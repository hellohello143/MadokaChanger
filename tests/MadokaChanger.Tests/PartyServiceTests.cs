using MadokaChanger.Core;
using Xunit;

namespace MadokaChanger.Tests;

public sealed class PartyServiceTests
{
    [Fact]
    public async Task PublishesOnlyDistinctLocalStateAndNeverEchoesPeerState()
    {
        var transport = new FakeTransport();
        await using var party = new PartyService(transport, pollInterval: TimeSpan.FromMilliseconds(20));
        await party.CreateAsync(CancellationToken.None);
        var local = State(103);
        var peer = State(22);

        await party.PublishAsync(local, CancellationToken.None);
        await party.PublishAsync(local, CancellationToken.None);
        transport.AddPeer(peer);
        await Task.Delay(100);

        Assert.Equal(1, transport.PublishCount);
        Assert.Equal(local, transport.LastPublished);
        Assert.Contains(party.Snapshot!.Members, m => m.MemberId == "peer" && m.State == peer);
        Assert.Equal(local, transport.LastPublished);
        Assert.True(party.IsConnected);
    }

    [Fact]
    public async Task LeaveClearsMembershipBeforeRelayCompletes()
    {
        var transport = new FakeTransport { BlockLeave = true };
        await using var party = new PartyService(transport, pollInterval: TimeSpan.FromHours(1));
        await party.CreateAsync(CancellationToken.None);
        var leave = party.LeaveAsync(CancellationToken.None);

        Assert.Null(party.Membership);
        Assert.Null(party.Snapshot);
        transport.CompleteLeave();
        await leave;
    }

    [Fact]
    public async Task RejectsForeignRoomSnapshotAndCanceledJoinCannotInstallMembership()
    {
        var transport = new FakeTransport();
        await using var party = new PartyService(transport, pollInterval: TimeSpan.FromMilliseconds(20));
        await party.CreateAsync(CancellationToken.None);
        transport.NextSnapshot = Snapshot("other-room", "other-generation");
        await Task.Delay(50);
        Assert.Equal("room-1", party.Snapshot!.RoomId);
        await party.LeaveAsync(CancellationToken.None);

        using var cts = new CancellationTokenSource();
        var cleanedBeforeJoin = transport.CleanedMemberships;
        transport.BlockJoin = true;
        var join = party.JoinAsync("0007", cts.Token);
        cts.Cancel();
        transport.CompleteJoin();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => join);
        Assert.Null(party.Membership);
        Assert.Equal(cleanedBeforeJoin + 1, transport.CleanedMemberships);
    }

    [Fact]
    public async Task TransientRelayFailureReconnectsWithTheSameMembershipTokenAndFreshSnapshot()
    {
        var transport = new FakeTransport { SnapshotFailuresRemaining = 1 };
        await using var party = new PartyService(transport, pollInterval: TimeSpan.FromMilliseconds(20));
        var sawReconnect = false;
        party.Changed += () => sawReconnect |= party.Status == "Reconnecting";
        await party.CreateAsync(CancellationToken.None);
        var token = party.Membership!.Token;

        await transport.SuccessfulSnapshot.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (!party.IsConnected && DateTime.UtcNow < deadline) await Task.Delay(20);

        Assert.True(sawReconnect);
        Assert.True(party.IsConnected);
        Assert.True(transport.SnapshotCount >= 2);
        Assert.All(transport.SnapshotTokens, seen => Assert.Equal(token, seen));
        Assert.Equal("room-1", party.Snapshot!.RoomId);
    }

    [Fact]
    public async Task StaleMemberRevisionCannotReplaceTheLastValidSnapshot()
    {
        var transport = new FakeTransport();
        await using var party = new PartyService(transport, pollInterval: TimeSpan.FromMilliseconds(20));
        await party.CreateAsync(CancellationToken.None);
        var lastGood = party.Snapshot;
        using var disconnected = new ManualResetEventSlim();
        party.Changed += () => { if (party.Status == "Reconnecting") disconnected.Set(); };
        var staleMember = lastGood!.Members.Single(m => m.MemberId == "local") with { State = State(22) };
        transport.NextSnapshot = lastGood with { Revision = lastGood.Revision + 1, Members = [staleMember] };

        Assert.True(disconnected.Wait(TimeSpan.FromSeconds(2)));
        Assert.Equal("Reconnecting", party.Status);
        Assert.NotNull(party.Snapshot);
        Assert.Equal(lastGood!.RoomId, party.Snapshot.RoomId);
        Assert.Equal(lastGood.Generation, party.Snapshot.Generation);
        Assert.Equal(lastGood.Revision, party.Snapshot.Revision);
        var retainedLocal = party.Snapshot.Members.Single(m => m.MemberId == "local");
        Assert.Equal(103, retainedLocal.State.ChampionId);
        Assert.DoesNotContain(party.Snapshot.Members, m => m.State.ChampionId == 22);
    }

    [Fact]
    public async Task ExpiredRoomResponseClearsLocalMembership()
    {
        var transport = new FakeTransport
        {
            SnapshotFailuresRemaining = 1,
            SnapshotFailureStatusCode = System.Net.HttpStatusCode.NotFound
        };
        await using var party = new PartyService(transport, pollInterval: TimeSpan.FromMilliseconds(20));
        var expired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        party.Changed += () => { if (party.Status.Contains("expired", StringComparison.OrdinalIgnoreCase)) expired.TrySetResult(); };
        await party.CreateAsync(CancellationToken.None);

        await expired.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Null(party.Membership);
        Assert.Null(party.Snapshot);
        Assert.False(party.IsConnected);
    }

    private static DuoSelectionState State(int champion) => new(champion, null, null, "ChampSelect", "stopped", null, null);
    private static DuoSnapshot Snapshot(string room = "room-1", string generation = "generation-1", long revision = 0,
        params DuoMember[] members) => new(1, room, generation, "0007", revision, DateTimeOffset.UtcNow.AddMinutes(1), members.ToList());
    private static DuoMembership Membership() => new(Snapshot(members: [new DuoMember("local", 0, State(103), DateTimeOffset.UtcNow)]), "local", Convert.ToBase64String(new byte[32]));

    private sealed class FakeTransport : IDuoTransport
    {
        private readonly TaskCompletionSource leaveGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource joinGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private DuoSnapshot current = Snapshot(members: [new DuoMember("local", 0, State(103), DateTimeOffset.UtcNow)]);
        public bool BlockLeave { get; set; }
        public bool BlockJoin { get; set; }
        public int PublishCount { get; private set; }
        public int CleanedMemberships { get; private set; }
        public DuoSelectionState? LastPublished { get; private set; }
        public DuoSnapshot? NextSnapshot { get; set; }
        public int SnapshotFailuresRemaining { get; set; }
        public System.Net.HttpStatusCode SnapshotFailureStatusCode { get; set; } = System.Net.HttpStatusCode.ServiceUnavailable;
        public int SnapshotCount { get; private set; }
        public List<string> SnapshotTokens { get; } = [];
        public TaskCompletionSource SuccessfulSnapshot { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<DuoMembership> CreateAsync(CancellationToken ct) => Task.FromResult(Membership());
        public async Task<DuoMembership> JoinAsync(string code, CancellationToken ct)
        {
            if (BlockJoin) await joinGate.Task.ConfigureAwait(false);
            return Membership();
        }
        public Task<DuoSnapshot> SnapshotAsync(DuoMembership membership, CancellationToken ct)
        {
            SnapshotCount++;
            SnapshotTokens.Add(membership.Token);
            if (SnapshotFailuresRemaining > 0)
            {
                SnapshotFailuresRemaining--;
                throw new HttpRequestException("temporary relay outage", null, SnapshotFailureStatusCode);
            }
            if (NextSnapshot is { } next) { NextSnapshot = null; return Task.FromResult(next); }
            SuccessfulSnapshot.TrySetResult();
            return Task.FromResult(current);
        }
        public Task<DuoSnapshot> PublishAsync(DuoMembership membership, DuoPublish update, CancellationToken ct)
        {
            PublishCount++;
            LastPublished = update.State;
            var members = current.Members.Select(m => m.MemberId == membership.MemberId
                ? m with { Revision = update.Revision, State = update.State, LastSeen = DateTimeOffset.UtcNow } : m).ToList();
            current = current with { Revision = update.Revision, Members = members };
            return Task.FromResult(current);
        }
        public async Task LeaveAsync(DuoMembership membership, CancellationToken ct)
        {
            if (BlockLeave) await leaveGate.Task.WaitAsync(ct).ConfigureAwait(false);
            if (membership.MemberId == "local") CleanedMemberships++;
        }
        public void AddPeer(DuoSelectionState state)
        {
            current = current with { Revision = current.Revision + 1,
                Members = [.. current.Members, new DuoMember("peer", current.Revision + 1, state, DateTimeOffset.UtcNow)] };
        }
        public void CompleteLeave() => leaveGate.TrySetResult();
        public void CompleteJoin() => joinGate.TrySetResult();
        public void Dispose() { }
    }
}
