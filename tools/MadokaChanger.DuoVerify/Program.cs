using MadokaChanger.Core;
using Microsoft.Extensions.Logging.Abstractions;

var allowRemote=args.Length==2 && args[1]=="--allow-remote";
if(args.Length is <1 or >2 || args.Length==2 && !allowRemote || !Uri.TryCreate(args[0],UriKind.Absolute,out var relay)
    || !relay.IsLoopback && (!allowRemote || relay.Scheme!=Uri.UriSchemeHttps))
    throw new ArgumentException("Usage: MadokaChanger.DuoVerify http://127.0.0.1:8787; add --allow-remote only for your own deployed HTTPS relay. Tests create temporary rooms with simulated metadata.");
var root=Path.Combine(Path.GetTempPath(),"Madoka-duo-e2e-"+Guid.NewGuid().ToString("N"));
await using var a=await Client.CreateAsync(Path.Combine(root,"a"),relay,103,new(103,103001));
await using var b=await Client.CreateAsync(Path.Combine(root,"b"),relay,99,new(99,99002));
try
{
    // Exercise the real background scheduler without a Duo party first.
    a.League.ChampionId=null;
    await a.Coordinator.RefreshLocalStateAsync(CancellationToken.None);
    a.Coordinator.StartMonitoring();
    await a.Patcher.StartAsync(null,CancellationToken.None);
    Check(a.Patcher.Status.State==PatcherState.Waiting && a.Patcher.PreparedSelections.Count==0,"Start before lock-in did not wait");
    a.League.ChampionId=103;
    await Until(()=>a.Patcher.Status.State==PatcherState.Running && a.Patcher.PreparedSelections.Count==1 && a.Patcher.PreparedSelections.GetValueOrDefault(103)==new Selection(103,103001),"solo lock-in auto start");
    lock(a.Store.State.Selections)a.Store.State.Selections[103]=new(103,103002);
    await a.Store.SaveAsync();a.Coordinator.NotifyCommittedSelectionChanged();
    await Until(()=>a.Patcher.PreparedSelections.GetValueOrDefault(103)==new Selection(103,103002),"solo saved skin auto restart");
    Check(a.Patcher.Sources().All(s=>s.Id.StartsWith("repo:",StringComparison.Ordinal)) && a.Patcher.EffectiveSelections().Count==1,"Unrelated saved/custom champion entered solo build");
    await a.Patcher.StopAsync();
    lock(a.Store.State.Selections)a.Store.State.Selections[103]=new(103,103001);
    await a.Store.SaveAsync();a.Coordinator.NotifyCommittedSelectionChanged();
    await a.Coordinator.CreateAsync(CancellationToken.None);
    await b.Coordinator.JoinAsync(a.Party.Membership!.Snapshot.Code,CancellationToken.None);
    await Until(()=>a.Party.Snapshot?.Members.Any(m=>m.State.Desired==new Selection(99,99002))==true && b.Party.Snapshot?.Members.Any(m=>m.State.Desired==new Selection(103,103001))==true,"bidirectional state");
    await Until(()=>a.Patcher.EffectiveSelections().GetValueOrDefault(99)==new Selection(99,99002) && b.Patcher.EffectiveSelections().GetValueOrDefault(103)==new Selection(103,103001),"temporary merge");
    Check(!a.Patcher.IsActive && !b.Patcher.IsActive,"Room messages started a stopped patcher");
    await a.Patcher.StartAsync(null,CancellationToken.None);await b.Patcher.StartAsync(null,CancellationToken.None);
    Check(a.Patcher.PreparedSelections[99]==new Selection(99,99002),"A peer build");
    Check(b.Patcher.PreparedSelections[103]==new Selection(103,103001),"B peer build");
    a.League.InGame=b.League.InGame=true;
    lock(b.Store.State.Selections)b.Store.State.Selections[99]=new(99,99003);
    await b.Store.SaveAsync();b.Coordinator.NotifyCommittedSelectionChanged();
    await Until(()=>a.Coordinator.Preparation=="pending" && b.Coordinator.Preparation=="pending","mid-game pending");
    Check(a.Patcher.PreparedSelections[99]==new Selection(99,99002),"Active game changed appearance");
    a.League.InGame=b.League.InGame=false;
    await Until(()=>a.Patcher.PreparedSelections.GetValueOrDefault(99)==new Selection(99,99003) && b.Patcher.PreparedSelections.GetValueOrDefault(99)==new Selection(99,99003),"automatic safe rebuild");
    await a.Coordinator.LeaveAsync(CancellationToken.None);
    await Until(()=>!a.Patcher.PreparedSelections.ContainsKey(99),"leave physical cleanup");
    Check(a.Store.State.Selections.Count==2 && a.Store.State.Selections[103]==new Selection(103,103001) && a.Store.State.Selections[22]==new Selection(22,22001),"Peer changed saved preferences");
    await b.Patcher.StopAsync();await b.Coordinator.LeaveAsync(CancellationToken.None);
    Check(!b.Patcher.IsActive,"Leave started stopped patcher");
    Console.WriteLine("PASS solo background lock-in/wait/automatic restart + actual relay with two desktop transports/coordinators + fake LCU/native patcher: champion-only sources, no unsolicited start, exact Duo merge, active-game deferral, safe rebuild, leave cleanup and unchanged saved preferences.");
}
finally
{
    await a.DisposeAsync();await b.DisposeAsync();
    if(Directory.Exists(root))Directory.Delete(root,true);
}

static void Check(bool value,string message){if(!value)throw new Exception(message);}
static async Task Until(Func<bool> condition,string stage)
{
    using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(45));
    try {while(!condition())await Task.Delay(100,timeout.Token);}
    catch(OperationCanceledException){throw new TimeoutException("E2E timed out: "+stage);}
}
sealed class League(int champion) : ILeagueStateSource,IGameEnvironment
{
    public int? ChampionId {get;set;}=champion;
    public bool InGame {get;set;}
    public bool IsOpen=>InGame;
    public bool LeagueRunsAsAdmin=>false;public bool AppIsElevated=>false;
    public Task<LeagueState> ReadAsync(string? gameFolder,CancellationToken ct)=>Task.FromResult(new LeagueState(true,InGame?"InProgress":"ChampSelect",ChampionId,InGame));
}
sealed class Client : IAsyncDisposable
{
    public required StateStore Store {get;init;}
    public required PatcherController Patcher {get;init;}
    public required PartyService Party {get;init;}
    public required DuoCoordinator Coordinator {get;init;}
    public required League League {get;init;}
    public static async Task<Client> CreateAsync(string root,Uri relay,int champion,Selection desired)
    {
        var paths=new AppPaths(root);var store=new StateStore(paths,NullLogger<StateStore>.Instance);await store.LoadAsync();store.State.Selections[champion]=desired;
        store.State.Selections[22]=new(22,22001); // Deliberately unavailable; unrelated preferences must never reach a build.
        store.State.CustomSkins.Add(new("unrelated",22,"Unrelated custom","test","1","","test","unused.fantome",Path.Combine(root,"missing-unrelated-project"),["champion:22"],true));
        var library=new LibraryStore(store);var dir=Path.Combine(library.Root,"e2e");Directory.CreateDirectory(dir);
        var selections=new[]{new Selection(103,103001),new Selection(103,103002),new Selection(99,99002),new Selection(99,99003)};
        await library.ActivateAsync(dir,new("e2e","main",DateTimeOffset.UtcNow,"16.20",selections.Select(s=>new PackageRecord(s.SkinId+".fantome","sha",1,s.Key,"test","1",s.ChampionId,s.SkinId,s.ChromaId,s.Form,null,[s.ChampionId.ToString()],null)).ToList(),[]),CancellationToken.None);
        var backend=Path.Combine(root,"backend");Directory.CreateDirectory(backend);foreach(var file in Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory,"TestBackend")))File.Copy(file,Path.Combine(backend,Path.GetFileName(file)));
        var exe=Path.Combine(backend,"MadokaChanger.TestBackend.exe");File.Copy(exe,Path.Combine(backend,"ltk_patcher_host.exe"));File.WriteAllText(Path.Combine(backend,"ltk_patcher_dll.dll"),"test");
        var game=Path.Combine(root,"game");Directory.CreateDirectory(Path.Combine(game,"DATA","FINAL"));File.WriteAllText(Path.Combine(game,"League of Legends.exe"),"test");store.State.Settings.GameFolder=game;
        var league=new League(champion);var overlay=new DuoOverlay(library);var patcher=new PatcherController(new(NullLogger<NativeBridge>.Instance,exe),store,library,paths,new(),NullLogger<PatcherController>.Instance,league,overlay);
        var party=new PartyService(new DuoHttpTransport(relay));
        return new(){Store=store,Patcher=patcher,Party=party,League=league,Coordinator=new(party,league,store,library,patcher,overlay,relay.AbsoluteUri)};
    }
    public async ValueTask DisposeAsync(){await Coordinator.DisposeAsync();await Patcher.StopAsync();}
}
