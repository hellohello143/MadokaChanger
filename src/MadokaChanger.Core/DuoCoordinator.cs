namespace MadokaChanger.Core;

/// <summary>Connects local LCU detection, committed preferences, relay snapshots and safe overlay preparation.</summary>
public sealed class DuoCoordinator : IAsyncDisposable, IDisposable
{
    private readonly ILeagueStateSource league;
    private readonly StateStore store;
    private readonly LibraryStore library;
    private readonly PatcherController patcher;
    private readonly DuoOverlay overlay;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim wake = new(0,1);
    private readonly object sync = new();
    private Task? loop;
    private string? observedHash;
    private DateTimeOffset stableSince;
    private string? attemptedHash;
    private string? preparationError;
    private readonly SemaphoreSlim tickGate = new(1,1);
    private int disposed;
    public PartyService Party { get; }
    public string RelayUrl { get; }
    public LeagueState LocalState { get; private set; } = new(false,"Offline",null,false);
    public DuoSelectionState LocalSelection { get; private set; } = new(null,null,null,"Offline","stopped",null,null);
    public DuoMember? Peer => Party.Snapshot?.Members.FirstOrDefault(m => m.MemberId != Party.Membership?.MemberId);
    public string Status { get; private set; } = "Create or join a private two-person party.";
    public string Preparation => LocalSelection.Preparation;
    public event Action? Changed;
    public DuoCoordinator(PartyService party,ILeagueStateSource league,StateStore store,LibraryStore library,PatcherController patcher,DuoOverlay overlay,string? relayUrl=null)
    {
        Party=party;this.league=league;this.store=store;this.library=library;this.patcher=patcher;this.overlay=overlay;
        RelayUrl=DuoConfiguration.ResolveRelayUrl(relayUrl);
        party.Changed+=OnChanged;patcher.StatusChanged+=OnPatcherChanged;
    }
    public async Task CreateAsync(CancellationToken ct)
    {
        RequireRelay();
        await Party.CreateAsync(ct).ConfigureAwait(false);EnsureLoop();Signal();
    }
    public async Task JoinAsync(string code,CancellationToken ct)
    {
        RequireRelay();
        await Party.JoinAsync(code,ct).ConfigureAwait(false);EnsureLoop();Signal();
    }
    private void RequireRelay()
    {
        if(!DuoConfiguration.IsValidRelayUrl(RelayUrl))throw new InvalidOperationException("Duo relay URL is invalid. Use HTTPS, or HTTP only for a loopback address.");
    }
    public async Task LeaveAsync(CancellationToken ct)
    {
        // Logical removal is immediate; physical changes still obey the game guard.
        overlay.Update(LocalState.ChampionId,null);attemptedHash=null;
        try { await Party.LeaveAsync(ct).ConfigureAwait(false); }
        finally { EnsureLoop();Signal();Changed?.Invoke(); }
    }
    /// <summary>Monitor the local champion in solo sessions as well as Duo. Does not start the patcher.</summary>
    public void StartMonitoring() { EnsureLoop();Signal(); }
    public Task RefreshLocalStateAsync(CancellationToken ct)=>TickAsync(ct);
    public void NotifyCommittedSelectionChanged() { attemptedHash=null;preparationError=null;Signal(); }
    private void OnChanged() { EnsureLoop();Signal();Changed?.Invoke(); }
    private void OnPatcherChanged(PatcherStatus _) { Signal();Changed?.Invoke(); }
    private void Signal() { if(wake.CurrentCount==0)try{wake.Release();}catch(SemaphoreFullException){} }
    private void EnsureLoop()
    {
        lock(sync) if(Volatile.Read(ref disposed)==0 && !lifetime.IsCancellationRequested && (loop is null || loop.IsCompleted))loop=Task.Run(RunAsync);
    }
    private async Task RunAsync()
    {
        var ct=lifetime.Token;
        try
        {
            while(!ct.IsCancellationRequested)
            {
                await wake.WaitAsync(TimeSpan.FromSeconds(1),ct).ConfigureAwait(false);
                // Coalesce committed selections and rapid room snapshots.
                await Task.Delay(250,ct).ConfigureAwait(false);
                try { await TickAsync(ct).ConfigureAwait(false); }
                catch(OperationCanceledException) when(ct.IsCancellationRequested) { break; }
                catch(Exception e) { Status="Skin preparation failed: "+e.Message;Changed?.Invoke(); }
            }
        }
        catch(OperationCanceledException) when(ct.IsCancellationRequested) { }
    }
    public async Task TickAsync(CancellationToken ct)
    {
        await tickGate.WaitAsync(ct).ConfigureAwait(false);
        try { await TickCoreAsync(ct).ConfigureAwait(false); }
        finally { tickGate.Release(); }
    }
    private async Task TickCoreAsync(CancellationToken ct)
    {
        var joined=Party.Membership is not null;
        try { LocalState=await league.ReadAsync(store.State.Settings.GameFolder,ct).ConfigureAwait(false); }
        catch(Exception e) when(e is not OperationCanceledException) { LocalState=new(false,"Offline",null,false);Status="League client unavailable: "+e.Message; }
        var peer=joined && Peer is { } candidate && DateTimeOffset.UtcNow-candidate.LastSeen<TimeSpan.FromSeconds(35) ? candidate.State : null;
        overlay.Update(LocalState.ChampionId,peer);
        var desiredHash=patcher.ConfigurationHash();
        if(observedHash!=desiredHash) { observedHash=desiredHash;stableSince=DateTimeOffset.UtcNow;attemptedHash=null;preparationError=null; }
        var pending=patcher.IsActive && patcher.ActiveConfiguration!=desiredHash;
        if(pending && !LocalState.IsInGame && !patcher.IsGameOpen && DateTimeOffset.UtcNow-stableSince>=TimeSpan.FromMilliseconds(500) && attemptedHash!=desiredHash)
        {
            attemptedHash=desiredHash;Status="Preparing current champion overrides…";Changed?.Invoke();
            try { if(!await patcher.RebuildIfSafeAsync(ct).ConfigureAwait(false))attemptedHash=null; }
            catch(Exception e) when(e is not OperationCanceledException) { preparationError="Automatic rebuild failed: "+e.Message; }
            pending=patcher.IsActive && patcher.ActiveConfiguration!=patcher.ConfigurationHash();
        }
        Selection? desired=null,prepared=null;
        var champion=LocalState.ChampionId;
        if(champion is { } id)
        {
            lock(store.State.Selections) store.State.Selections.TryGetValue(id,out desired);
            patcher.PreparedSelections.TryGetValue(id,out prepared);
            if(desired?.ChampionId!=id)desired=null;
            if(prepared?.ChampionId!=id)prepared=null;
        }
        var unsupported=champion is { } ch && store.State.CustomSkins.Any(c=>c.Enabled && c.ChampionId==ch);
        var unavailable=joined && unsupported || desired is not null && library.Find(desired).Count!=1;
        var preparation=patcher.Status.State==PatcherState.Error || preparationError is not null?"error":unavailable || overlay.Problem is not null || !LocalState.Available?"unavailable":
            patcher.Status.State is PatcherState.Building or PatcherState.Starting?"preparing":pending?"pending":
            patcher.Status.State!=PatcherState.Running?"stopped":"prepared";
        LocalSelection=new(champion,unsupported?null:desired,prepared,LocalState.Phase,preparation,library.Manifest?.Commit,library.Manifest?.MetadataPatch);
        Status=preparationError??overlay.Problem??(!LocalState.Available?"League client unavailable · open League and log in to detect your locked-in champion.":joined && unsupported?"Imported custom packages cannot be synchronized. Choose an installed catalog selection.":
            unavailable?"Your exact saved package is unavailable. Install it before preparing.":
            patcher.Status.State==PatcherState.Waiting?patcher.Status.Message:
            champion is null?"Waiting for your champion to be locked in. Other saved champions are not patched.":
            preparation switch
            {
                "pending"=>"Skin changes pending · the patcher restarts automatically when League is closed.",
                "prepared"=>"Current champion configuration prepared · in-game appearance requires successful patcher application.",
                "preparing"=>"Preparing current champion overrides…",
                "error"=>patcher.Status.Message,
                _=>"Current champion detected · press Start to prepare its saved skin."
            });
        if(joined)
        {
            try { await Party.PublishAsync(LocalSelection,ct).ConfigureAwait(false); }
            catch(Exception e) when(e is not OperationCanceledException) { Status="Relay unavailable: "+e.Message; }
        }
        Changed?.Invoke();
    }
    public async ValueTask DisposeAsync()
    {
        if(Interlocked.Exchange(ref disposed,1)!=0)return;
        Party.Changed-=OnChanged;patcher.StatusChanged-=OnPatcherChanged;lifetime.Cancel();
        if(loop is not null)await loop.ConfigureAwait(false);
        await Party.DisposeAsync().ConfigureAwait(false);overlay.Update(null,null);
        lifetime.Dispose();
    }
    public void Dispose()=>DisposeAsync().AsTask().GetAwaiter().GetResult();
}
