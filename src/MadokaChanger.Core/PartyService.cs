namespace MadokaChanger.Core;

/// <summary>Owns a room membership, local state publication, polling, and reconnects.</summary>
public sealed class PartyService : IDisposable, IAsyncDisposable
{
    private static readonly TimeSpan NetworkTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PublishDebounce = TimeSpan.FromMilliseconds(500);
    private readonly IDuoTransport transport;
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan pollInterval;
    private readonly SemaphoreSlim networkGate = new(1, 1);
    private readonly SemaphoreSlim pollWake = new(0, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly Task polling;
    private readonly object sync = new();
    private readonly Dictionary<string, (long Revision, DuoSelectionState State)> memberRevisions = new(StringComparer.Ordinal);
    private DuoMembership? membership;
    private DuoSnapshot? snapshot;
    private DuoSelectionState? localState;
    private CancellationTokenSource? membershipCancellation;
    private long operationVersion;
    private long localRevision;
    private bool connected;
    private bool disposed;
    private string status = "Ready";

    public PartyService(IDuoTransport transport, TimeProvider? timeProvider = null, TimeSpan? pollInterval = null)
    {
        this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.pollInterval = pollInterval ?? TimeSpan.FromSeconds(10);
        if (this.pollInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(pollInterval));
        polling = PollLoopAsync(lifetime.Token);
    }

    public DuoMembership? Membership { get { lock (sync) return membership; } }
    public DuoSnapshot? Snapshot { get { lock (sync) return snapshot; } }
    public bool IsConnected { get { lock (sync) return connected; } }
    public string Status { get { lock (sync) return status; } }
    public event Action? Changed;

    public Task CreateAsync(CancellationToken ct) => StartMembershipAsync((token) => transport.CreateAsync(token), ct);

    public Task JoinAsync(string code, CancellationToken ct)
    {
        if (!DuoProtocol.IsCode(code)) throw new ArgumentException("Enter a four-digit room code.", nameof(code));
        return StartMembershipAsync(token => transport.JoinAsync(code, token), ct);
    }

    private async Task StartMembershipAsync(Func<CancellationToken, Task<DuoMembership>> create, CancellationToken ct)
    {
        ThrowIfDisposed();
        long version;
        lock (sync)
        {
            if (membership is not null) throw new InvalidOperationException("Leave the current room before joining another.");
            version = ++operationVersion;
            status = "Connecting";
            connected = false;
        }
        RaiseChanged();

        var gateAcquired = false;
        try
        {
            await networkGate.WaitAsync(ct).ConfigureAwait(false);
            gateAcquired = true;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token);
            timeout.CancelAfter(NetworkTimeout);
            DuoMembership returned;
            try { returned = await create(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && !lifetime.IsCancellationRequested)
            {
                SetStatusIfCurrent(version, "Couldn't reach the duo relay");
                throw new TimeoutException("The duo relay did not respond in time.");
            }

            ValidateMembership(returned);
            var installed = false;
            lock (sync)
            {
                if (!disposed && version == operationVersion && !ct.IsCancellationRequested && !lifetime.IsCancellationRequested)
                {
                    membership = returned;
                    snapshot = returned.Snapshot;
                    memberRevisions.Clear();
                    foreach (var member in returned.Snapshot.Members) memberRevisions[member.MemberId] = (member.Revision, member.State);
                    localRevision = Math.Max(returned.Snapshot.Revision, returned.Snapshot.Members.Max(m => m.Revision));
                    localState = null;
                    membershipCancellation?.Dispose();
                    membershipCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                    connected = true;
                    status = "Connected";
                    installed = true;
                }
            }
            if (!installed)
            {
                await TryCleanupReturnedMembershipAsync(returned).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                throw new OperationCanceledException("The room operation is no longer current.", ct);
            }
            WakePoller();
            RaiseChanged();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            SetStatusIfCurrent(version, "Ready");
            throw;
        }
        catch
        {
            SetStatusIfCurrent(version, "Connection failed");
            throw;
        }
        finally { if (gateAcquired) networkGate.Release(); }
    }

    public async Task LeaveAsync(CancellationToken ct)
    {
        DuoMembership? previous;
        lock (sync)
        {
            ThrowIfDisposedLocked();
            ++operationVersion;
            previous = membership;
            membership = null;
            snapshot = null;
            memberRevisions.Clear();
            localState = null;
            localRevision = 0;
            connected = false;
            status = "Ready";
            membershipCancellation?.Cancel();
            membershipCancellation?.Dispose();
            membershipCancellation = null;
        }
        WakePoller();
        RaiseChanged(); // Logical leave is immediate even if the relay is unavailable.
        if (previous is null) return;

        await networkGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token);
            timeout.CancelAfter(NetworkTimeout);
            await transport.LeaveAsync(previous, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && !lifetime.IsCancellationRequested) { }
        catch (HttpRequestException) { }
        catch (IOException) { }
        finally { networkGate.Release(); }
    }

    public async Task PublishAsync(DuoSelectionState state, CancellationToken ct)
    {
        if (!DuoProtocol.ValidState(state)) throw new ArgumentException("The duo state is invalid.", nameof(state));
        ThrowIfDisposed();
        DuoMembership? current;
        lock (sync)
        {
            current = membership;
            if (current is null) return;
            if (Equals(localState, state)) return;
            localState = state;
        }
        RaiseChanged();
        await Task.Delay(PublishDebounce, timeProvider, ct).ConfigureAwait(false);
        if (!ReferenceEquals(Membership, current) || !Equals(LocalState(), state)) return;
        using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token);
        requestTimeout.CancelAfter(NetworkTimeout);
        await networkGate.WaitAsync(requestTimeout.Token).ConfigureAwait(false);
        try
        {
            if (!ReferenceEquals(Membership, current)) return;
            await PublishCurrentAsync(current, state, requestTimeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (HttpRequestException ex) when (IsMembershipGone(ex)) { ClearGoneMembership(current); }
        catch { MarkDisconnected(current); }
        finally { networkGate.Release(); }
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        var backoff = TimeSpan.FromSeconds(1);
        while (!ct.IsCancellationRequested)
        {
            var current = Membership;
            if (current is null)
            {
                try { await pollWake.WaitAsync(ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                continue;
            }

            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, MembershipToken());
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
                timeout.CancelAfter(NetworkTimeout);
                await networkGate.WaitAsync(timeout.Token).ConfigureAwait(false);
                try
                {
                    if (ReferenceEquals(Membership, current))
                    {
                        var fresh = await transport.SnapshotAsync(current, timeout.Token).ConfigureAwait(false);
                        AcceptSnapshot(current, fresh);
                        var own = fresh.Members.Single(m => m.MemberId == current.MemberId);
                        var local = LocalState();
                        if (local is not null && !Equals(local, own.State))
                            await PublishCurrentAsync(current, local, timeout.Token).ConfigureAwait(false);
                        MarkConnected(current);
                    }
                }
                finally { networkGate.Release(); }
                backoff = TimeSpan.FromSeconds(1);
                try { await Task.Delay(pollInterval, timeProvider, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (OperationCanceledException) when (MembershipToken().IsCancellationRequested) { }
            catch (HttpRequestException ex) when (IsMembershipGone(ex)) { ClearGoneMembership(current); backoff = TimeSpan.FromSeconds(1); }
            catch
            {
                MarkDisconnected(current);
                var jitter = 0.8 + Random.Shared.NextDouble() * 0.4;
                var delay = TimeSpan.FromMilliseconds(Math.Min(backoff.TotalMilliseconds, 30000) * jitter);
                try { await Task.Delay(delay, timeProvider, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, 30));
            }
        }
    }

    private async Task PublishCurrentAsync(DuoMembership current, DuoSelectionState state, CancellationToken ct)
    {
        var currentSnapshot = Snapshot;
        if (currentSnapshot is null || !ReferenceEquals(Membership, current)) return;
        var own = currentSnapshot.Members.SingleOrDefault(m => m.MemberId == current.MemberId);
        var revision = Math.Max(Math.Max(localRevision, own?.Revision ?? 0), currentSnapshot.Revision) + 1;
        var update = new DuoPublish(DuoProtocol.Version, currentSnapshot.RoomId, currentSnapshot.Generation, revision, state);
        var fresh = await transport.PublishAsync(current, update, ct).ConfigureAwait(false);
        AcceptSnapshot(current, fresh);
        localRevision = revision;
    }

    private void AcceptSnapshot(DuoMembership current, DuoSnapshot fresh)
    {
        ValidateSnapshot(fresh, current);
        lock (sync)
        {
            if (!ReferenceEquals(membership, current)) return;
            if (snapshot is not null && fresh.Revision < snapshot.Revision)
                throw new InvalidDataException("The duo relay returned an out-of-date room snapshot.");
            foreach (var member in fresh.Members)
            {
                if (memberRevisions.TryGetValue(member.MemberId, out var previous)
                    && (member.Revision < previous.Revision || member.Revision == previous.Revision && member.State != previous.State))
                    throw new InvalidDataException("The duo relay returned a stale member revision.");
            }
            foreach (var member in fresh.Members) memberRevisions[member.MemberId] = (member.Revision, member.State);
            snapshot = fresh;
            var own = fresh.Members.Single(m => m.MemberId == current.MemberId);
            localRevision = Math.Max(Math.Max(localRevision, own.Revision), fresh.Revision);
        }
        RaiseChanged();
    }

    private static void ValidateMembership(DuoMembership value)
    {
        if (value is null || !HasTokenEntropy(value.Token) || value.MemberId is not { Length: > 0 and <= 128 })
            throw new InvalidDataException("The duo relay returned invalid membership data.");
        ValidateSnapshot(value.Snapshot, value);
        if (!value.Snapshot.Members.Any(m => m.MemberId == value.MemberId))
            throw new InvalidDataException("The duo relay omitted the current member.");
    }

    private static bool HasTokenEntropy(string? token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 128) return false;
        var standard = token.Replace('-', '+').Replace('_', '/');
        standard = standard.PadRight((standard.Length + 3) / 4 * 4, '=');
        try { return Convert.FromBase64String(standard).Length == 32; }
        catch (FormatException) { return false; }
    }

    private static void ValidateSnapshot(DuoSnapshot value, DuoMembership membership)
    {
        if (value is null || value.Version != DuoProtocol.Version || value.RoomId is not { Length: > 0 and <= 128 }
            || value.Generation is not { Length: > 0 and <= 128 } || !DuoProtocol.IsCode(value.Code)
            || value.Revision < 0 || value.Members is null || value.Members.Count is < 1 or > 2
            || value.ExpiresAt == default)
            throw new InvalidDataException("The duo relay returned an invalid room snapshot.");
        if (membership is not null && (value.RoomId != membership.Snapshot.RoomId || value.Generation != membership.Snapshot.Generation
            || value.Code != membership.Snapshot.Code))
            throw new InvalidDataException("The duo relay returned a snapshot for another room generation.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in value.Members)
        {
            if (member is null || member.MemberId is not { Length: > 0 and <= 128 } || !ids.Add(member.MemberId)
                || member.Revision < 0 || member.Revision > value.Revision || member.LastSeen == default || !DuoProtocol.ValidState(member.State))
                throw new InvalidDataException("The duo relay returned invalid member data.");
        }
        if (membership is not null && !ids.Contains(membership.MemberId))
            throw new InvalidDataException("The duo relay omitted the current member.");
    }

    private CancellationToken MembershipToken()
    {
        lock (sync) return membershipCancellation?.Token ?? new CancellationToken(canceled: true);
    }
    private DuoSelectionState? LocalState() { lock (sync) return localState; }

    private void MarkConnected(DuoMembership current)
    {
        lock (sync)
        {
            if (!ReferenceEquals(membership, current) || connected) return;
            connected = true;
            status = "Connected";
        }
        RaiseChanged();
    }

    private void MarkDisconnected(DuoMembership current)
    {
        lock (sync)
        {
            if (!ReferenceEquals(membership, current)) return;
            connected = false;
            status = "Reconnecting";
        }
        RaiseChanged();
    }

    private void ClearGoneMembership(DuoMembership current)
    {
        lock (sync)
        {
            if (!ReferenceEquals(membership, current)) return;
            membership = null;
            snapshot = null;
            memberRevisions.Clear();
            localState = null;
            localRevision = 0;
            connected = false;
            status = "Room expired or membership ended";
            membershipCancellation?.Cancel();
            membershipCancellation?.Dispose();
            membershipCancellation = null;
        }
        RaiseChanged();
    }

    private static bool IsMembershipGone(HttpRequestException ex) => ex.StatusCode is System.Net.HttpStatusCode.Unauthorized
        or System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Gone;

    private void WakePoller()
    {
        if (pollWake.CurrentCount == 0) try { pollWake.Release(); } catch (SemaphoreFullException) { }
    }

    private void SetStatusIfCurrent(long version, string value)
    {
        lock (sync) { if (version != operationVersion || disposed) return; status = value; connected = false; }
        RaiseChanged();
    }

    private async Task TryCleanupReturnedMembershipAsync(DuoMembership returned)
    {
        try { using var timeout = new CancellationTokenSource(NetworkTimeout); await transport.LeaveAsync(returned, timeout.Token).ConfigureAwait(false); }
        catch { }
    }

    private void ThrowIfDisposed() { lock (sync) ThrowIfDisposedLocked(); }
    private void ThrowIfDisposedLocked() { if (disposed) throw new ObjectDisposedException(nameof(PartyService)); }

    private void RaiseChanged()
    {
        var handlers = Changed;
        if (handlers is null) return;
        foreach (Action handler in handlers.GetInvocationList()) try { handler(); } catch { }
    }

    public async ValueTask DisposeAsync()
    {
        DuoMembership? previous;
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
            ++operationVersion;
            previous = membership;
            membership = null;
            snapshot = null;
            memberRevisions.Clear();
            localState = null;
            connected = false;
            membershipCancellation?.Cancel();
        }
        lifetime.Cancel();
        RaiseChanged();
        try { await polling.ConfigureAwait(false); } catch { }
        var gateAcquired = false;
        try { gateAcquired = await networkGate.WaitAsync(NetworkTimeout).ConfigureAwait(false); }
        catch { }
        if (gateAcquired)
        {
            try
            {
                if (previous is not null)
                {
                    using var timeout = new CancellationTokenSource(NetworkTimeout);
                    await transport.LeaveAsync(previous, timeout.Token).ConfigureAwait(false);
                }
            }
            catch { }
            finally { networkGate.Release(); }
        }
        membershipCancellation?.Dispose();
        lifetime.Dispose();
        pollWake.Dispose();
        // A custom injected transport may ignore cancellation. Avoid disposing it while an operation still owns the gate.
        if (gateAcquired) transport.Dispose();
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}
