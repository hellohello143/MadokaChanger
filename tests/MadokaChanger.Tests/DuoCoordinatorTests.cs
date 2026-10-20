using MadokaChanger.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MadokaChanger.Tests;

public sealed class DuoCoordinatorTests
{
    private sealed class GameEnvironment : IGameEnvironment
    {
        public bool IsOpen { get; set; }
        public bool LeagueRunsAsAdmin => false;
        public bool AppIsElevated => false;
    }

    private sealed class LeagueSource(LeagueState state) : ILeagueStateSource
    {
        public Task<LeagueState> ReadAsync(string? gameFolder, CancellationToken ct) => Task.FromResult(state);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public required string Root { get; init; }
        public required StateStore State { get; init; }
        public required LibraryStore Library { get; init; }
        public required DuoOverlay Overlay { get; init; }
        public required PatcherController Patcher { get; init; }
        public required GameEnvironment Game { get; init; }
        public required OperationGate Gate { get; init; }
        public required FakeTransport Transport { get; init; }
        public required PartyService Party { get; init; }
        public required DuoCoordinator Coordinator { get; init; }

        public async ValueTask DisposeAsync()
        {
            await Coordinator.DisposeAsync();
            await Patcher.StopAsync();
            await DeleteRootAsync(Root);
        }
    }

    private static async Task<Fixture> CreateFixtureAsync(bool startPatcher = false, bool gameOpen = false)
    {
        var root = Path.Combine(Path.GetTempPath(), "Madoka-duo-coordinator-" + Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(root);
        var state = new StateStore(paths, NullLogger<StateStore>.Instance);
        await state.LoadAsync();
        state.State.Selections[103] = new(103, 103001);
        state.State.Selections[99] = new(99, 99001);

        var library = new LibraryStore(state);
        var libraryDirectory = Path.Combine(library.Root, "duo");
        Directory.CreateDirectory(libraryDirectory);
        var packages = new[]
        {
            new Selection(103, 103001), new Selection(103, 103002),
            new Selection(99, 99001), new Selection(99, 99002)
        }.Select(selection => new PackageRecord(selection.Key.Replace(':', '_') + ".fantome", "sha", 1,
            selection.Key, "Author", "1", selection.ChampionId, selection.SkinId, selection.ChromaId,
            selection.Form, null, ["champion:" + selection.ChampionId], null)).ToList();
        await library.ActivateAsync(libraryDirectory,
            new("same", "main", DateTimeOffset.UtcNow, "16.20", packages, []), CancellationToken.None);

        var backend = Path.Combine(root, "backend");
        Directory.CreateDirectory(backend);
        foreach (var file in Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "TestBackend")))
            File.Copy(file, Path.Combine(backend, Path.GetFileName(file)));
        var executable = Path.Combine(backend, "MadokaChanger.TestBackend.exe");
        File.Copy(executable, Path.Combine(backend, "ltk_patcher_host.exe"));
        File.WriteAllText(Path.Combine(backend, "ltk_patcher_dll.dll"), "fixture");

        var gameFolder = Path.Combine(root, "game");
        Directory.CreateDirectory(Path.Combine(gameFolder, "DATA", "FINAL"));
        File.WriteAllText(Path.Combine(gameFolder, "League of Legends.exe"), "fixture");
        state.State.Settings.GameFolder = gameFolder;

        var overlay = new DuoOverlay(library);
        var game = new GameEnvironment { IsOpen = gameOpen };
        var gate = new OperationGate();
        var patcher = new PatcherController(new(NullLogger<NativeBridge>.Instance, executable), state, library,
            paths, gate, NullLogger<PatcherController>.Instance, game, overlay);
        var transport = new FakeTransport();
        transport.AddPeer(PeerState());
        var party = new PartyService(transport, pollInterval: TimeSpan.FromHours(1));
        await party.CreateAsync(CancellationToken.None);
        var coordinator = new DuoCoordinator(party, new LeagueSource(new(true, "ChampSelect", 103, false)),
            state, library, patcher, overlay, "https://relay.example");
        if (startPatcher) await patcher.StartAsync(null, CancellationToken.None);
        return new Fixture
        {
            Root = root, State = state, Library = library, Overlay = overlay, Patcher = patcher,
            Game = game, Gate = gate, Transport = transport, Party = party, Coordinator = coordinator
        };
    }

    private static DuoSelectionState PeerState() => new(99, new(99, 99002), null, "ChampSelect", "prepared", "same", "16.20");

    [Fact]
    public async Task Peer_change_stays_pending_in_game_then_leave_restores_saved_configuration_when_safe()
    {
        await using var fixture = await CreateFixtureAsync(startPatcher: true);
        fixture.Game.IsOpen = true;
        var initialConfiguration = fixture.Patcher.ActiveConfiguration;

        await fixture.Coordinator.TickAsync(CancellationToken.None);

        Assert.Equal("pending", fixture.Coordinator.Preparation);
        Assert.Equal(initialConfiguration, fixture.Patcher.ActiveConfiguration);
        Assert.Equal(new(99, 99001), fixture.Patcher.PreparedSelections[99]);
        Assert.Equal(new(99, 99002), fixture.Patcher.EffectiveSelections()[99]);

        fixture.Game.IsOpen = false;
        await Task.Delay(550);
        await fixture.Coordinator.TickAsync(CancellationToken.None);
        Assert.Equal(new(99, 99002), fixture.Patcher.PreparedSelections[99]);

        fixture.Game.IsOpen = true;
        await fixture.Coordinator.LeaveAsync(CancellationToken.None);
        Assert.Null(fixture.Coordinator.Peer);
        Assert.Equal(new(99, 99001), fixture.Patcher.EffectiveSelections()[99]);
        Assert.Equal("Offline", fixture.Coordinator.LocalState.Phase);

        fixture.Game.IsOpen = false;
        // Let the coordinator observe the transition from joined to left and
        // begin the guarded cleanup window before checking its safe rebuild.
        await fixture.Coordinator.TickAsync(CancellationToken.None);
        await Task.Delay(550);
        await fixture.Coordinator.TickAsync(CancellationToken.None);
        Assert.Equal(new(99, 99001), fixture.Patcher.PreparedSelections[99]);
        Assert.Equal(PatcherState.Running, fixture.Patcher.Status.State);
    }

    [Fact]
    public async Task Committed_saved_change_is_published_as_local_state_without_peer_writes()
    {
        await using var fixture = await CreateFixtureAsync();
        fixture.State.State.Selections[103] = new(103, 103002);
        await fixture.State.SaveAsync();
        fixture.Coordinator.NotifyCommittedSelectionChanged();

        await fixture.Coordinator.TickAsync(CancellationToken.None);

        Assert.Equal(new(103, 103002), fixture.Coordinator.LocalSelection.Desired);
        Assert.Equal(new(99, 99001), fixture.State.State.Selections[99]);
        Assert.Equal(new(99, 99002), fixture.Patcher.EffectiveSelections()[99]);
        Assert.Equal(103, fixture.Transport.LastPublished?.ChampionId);
        Assert.Equal(new(103, 103002), fixture.Transport.LastPublished?.Desired);
        Assert.Equal(PatcherState.Stopped, fixture.Patcher.Status.State);
        Assert.Null(fixture.Patcher.ActiveConfiguration);
    }

    [Fact]
    public async Task Stopped_coordinator_disposal_removes_subscriptions_and_never_starts_patcher()
    {
        var fixture = await CreateFixtureAsync();
        var changes = 0;
        fixture.Coordinator.Changed += () => changes++;
        await fixture.Coordinator.TickAsync(CancellationToken.None);
        Assert.Equal(PatcherState.Stopped, fixture.Patcher.Status.State);
        Assert.Null(fixture.Patcher.ActiveConfiguration);
        Assert.False(fixture.Gate.IsBusy);

        await fixture.Coordinator.DisposeAsync();
        var afterDispose = changes;
        await fixture.Patcher.StopAsync();

        Assert.Equal(afterDispose, changes);
        Assert.Equal(PatcherState.Stopped, fixture.Patcher.Status.State);
        Assert.Null(fixture.Patcher.ActiveConfiguration);
        Assert.False(fixture.Gate.IsBusy);
        await fixture.Patcher.StopAsync();
        await DeleteRootAsync(fixture.Root);
    }

    private static async Task DeleteRootAsync(string root)
    {
        for (var attempt = 0; attempt < 20 && Directory.Exists(root); attempt++)
        {
            try { Directory.Delete(root, recursive: true); return; }
            catch (UnauthorizedAccessException) when (attempt < 19) { await Task.Delay(100); }
        }
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private sealed class FakeTransport : IDuoTransport
    {
        private DuoSnapshot snapshot = BuildSnapshot([LocalMember()]);
        public DuoSelectionState? LastPublished { get; private set; }
        public Task<DuoMembership> CreateAsync(CancellationToken ct) => Task.FromResult(Membership());
        public Task<DuoMembership> JoinAsync(string code, CancellationToken ct) => Task.FromResult(Membership());
        public Task<DuoSnapshot> SnapshotAsync(DuoMembership membership, CancellationToken ct) => Task.FromResult(snapshot);
        public Task<DuoSnapshot> PublishAsync(DuoMembership membership, DuoPublish update, CancellationToken ct)
        {
            LastPublished = update.State;
            var members = snapshot.Members.Select(member => member.MemberId == membership.MemberId
                ? member with { Revision = update.Revision, State = update.State, LastSeen = DateTimeOffset.UtcNow }
                : member).ToList();
            snapshot = snapshot with { Revision = update.Revision, Members = members };
            return Task.FromResult(snapshot);
        }
        public Task LeaveAsync(DuoMembership membership, CancellationToken ct)
        {
            snapshot = snapshot with { Revision = snapshot.Revision + 1,
                Members = snapshot.Members.Where(member => member.MemberId != membership.MemberId).ToList() };
            return Task.CompletedTask;
        }
        public void AddPeer(DuoSelectionState state)
        {
            var revision = snapshot.Revision + 1;
            snapshot = snapshot with { Revision = revision, Members = [.. snapshot.Members,
                new DuoMember("peer", 0, state, DateTimeOffset.UtcNow)] };
        }
        public void Dispose() { }

        private DuoMembership Membership() => new(snapshot, "local", Convert.ToBase64String(new byte[32]));
        private static DuoMember LocalMember() => new("local", 0,
            new(null, null, null, "Lobby", "stopped", null, null), DateTimeOffset.UtcNow);
        private static DuoSnapshot BuildSnapshot(List<DuoMember> members) => new(1, "room", "generation", "0007",
            0, DateTimeOffset.UtcNow.AddMinutes(1), members);
    }
}
