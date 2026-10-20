using MadokaChanger.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MadokaChanger.Tests;

public sealed class DuoOverlayTests : IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"Madoka-duo-"+Guid.NewGuid().ToString("N"));
    private sealed class Game : IGameEnvironment { public bool IsOpen {get;set;} public bool LeagueRunsAsAdmin=>false;public bool AppIsElevated=>false; }
    private async Task<(StateStore State,LibraryStore Library,DuoOverlay Overlay,PatcherController Patcher,Game Game,OperationGate Gate)> FixtureAsync()
    {
        var paths=new AppPaths(root);var state=new StateStore(paths,NullLogger<StateStore>.Instance);await state.LoadAsync();
        state.State.Selections[103]=new(103,103001);state.State.Selections[99]=new(99,99001);
        var library=new LibraryStore(state);var dir=Path.Combine(library.Root,"duo");Directory.CreateDirectory(dir);
        var packages=new[]{new Selection(103,103001),new Selection(103,103002),new Selection(99,99001),new Selection(99,99002),new Selection(99,99003,99005,"Dark")}.Select(s=>new PackageRecord(s.Key.Replace(':','_')+".fantome","sha",1,s.Key,"Author","1",s.ChampionId,s.SkinId,s.ChromaId,s.Form,null,["champion:"+s.ChampionId],null)).ToList();
        await library.ActivateAsync(dir,new("same","main",DateTimeOffset.UtcNow,"16.20",packages,[]),CancellationToken.None);
        var backend=Path.Combine(root,"backend");Directory.CreateDirectory(backend);
        foreach(var file in Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory,"TestBackend")))File.Copy(file,Path.Combine(backend,Path.GetFileName(file)));
        var exe=Path.Combine(backend,"MadokaChanger.TestBackend.exe");File.Copy(exe,Path.Combine(backend,"ltk_patcher_host.exe"));File.WriteAllText(Path.Combine(backend,"ltk_patcher_dll.dll"),"fixture");
        var gameDir=Path.Combine(root,"game");Directory.CreateDirectory(Path.Combine(gameDir,"DATA","FINAL"));File.WriteAllText(Path.Combine(gameDir,"League of Legends.exe"),"fixture");state.State.Settings.GameFolder=gameDir;
        var overlay=new DuoOverlay(library);var game=new Game();var gate=new OperationGate();
        var patcher=new PatcherController(new(NullLogger<NativeBridge>.Instance,exe),state,library,paths,gate,NullLogger<PatcherController>.Instance,game,overlay);
        return(state,library,overlay,patcher,game,gate);
    }
    private static DuoSelectionState Peer(Selection? selection,int champion=99,string commit="same")=>new(champion,selection,null,"ChampSelect","stopped",commit,"16.20");

    [Fact] public async Task Exact_peer_variant_replaces_unrelated_saved_preference_and_leave_restores_it()
    {
        var (state,_,overlay,patcher,_,_)=await FixtureAsync();var original=patcher.ConfigurationHash();
        var variant=new Selection(99,99003,99005,"Dark");overlay.Update(103,Peer(variant));
        Assert.Equal(variant,patcher.EffectiveSelections()[99]);Assert.Equal(new(103,103001),patcher.EffectiveSelections()[103]);
        Assert.NotEqual(original,patcher.ConfigurationHash());Assert.Equal(new(99,99001),state.State.Selections[99]);Assert.Equal(2,patcher.Sources().Count);
        overlay.Update(null,null);Assert.Equal(original,patcher.ConfigurationHash());Assert.Equal(new(99,99001),state.State.Selections[99]);
    }
    [Fact] public async Task Missing_or_incompatible_peer_package_never_substitutes_saved_skin()
    {
        var (_,_,overlay,patcher,_,_)=await FixtureAsync();
        overlay.Update(103,Peer(new(99,99004)));Assert.DoesNotContain(99,patcher.EffectiveSelections().Keys);Assert.Contains("missing",overlay.Problem!);
        overlay.Update(103,Peer(new(99,99002),commit:"different"));Assert.DoesNotContain(99,patcher.EffectiveSelections().Keys);Assert.Contains("differs",overlay.Problem!);
    }
    [Fact] public async Task Default_peer_and_same_champion_conflict_have_deterministic_policy()
    {
        var (_,_,overlay,patcher,_,_)=await FixtureAsync();
        overlay.Update(103,Peer(null));Assert.DoesNotContain(99,patcher.EffectiveSelections().Keys);
        overlay.Update(103,Peer(new(103,103002),103));Assert.Equal(new(103,103001),patcher.EffectiveSelections()[103]);Assert.Contains("Same-champion",overlay.Problem!);
        overlay.Update(null,Peer(new(99,99002)));Assert.Equal(new(99,99001),patcher.EffectiveSelections()[99]);Assert.Contains("detected",overlay.Problem!);
    }
    [Fact] public async Task Ambiguous_peer_packages_and_file_conflicts_are_not_silently_accepted()
    {
        var (state,library,overlay,patcher,_,_)=await FixtureAsync();var manifest=library.Manifest!;
        var peerPackage=manifest.Packages.Single(p=>p.Selection==new Selection(99,99002));
        await library.ActivateAsync(library.ActiveDirectory!,manifest with{Packages=[..manifest.Packages,peerPackage with{Path="duplicate.fantome"}]},CancellationToken.None);
        overlay.Update(103,Peer(new(99,99002)));Assert.DoesNotContain(99,patcher.EffectiveSelections().Keys);Assert.Contains("ambiguous",overlay.Problem!);
        await library.ActivateAsync(library.ActiveDirectory!,manifest with{Packages=manifest.Packages.Select(p=>p==peerPackage?p with{Overrides=["champion:103"]}:p).ToList()},CancellationToken.None);
        state.State.ConflictChoice=ConflictChoice.PreferRepository;
        Assert.Throws<InvalidOperationException>(()=>ConflictResolver.Order(patcher.Sources(),state.State.ConflictChoice));
    }
    [Fact] public async Task Peer_updates_rebuild_running_session_only_when_game_closed_and_keep_gate()
    {
        var (_,_,overlay,patcher,game,gate)=await FixtureAsync();
        overlay.Update(103,Peer(new(99,99002)));Assert.False(await patcher.RebuildIfSafeAsync(CancellationToken.None));Assert.Equal(PatcherState.Stopped,patcher.Status.State);
        await patcher.StartAsync(null,CancellationToken.None);
        try
        {
            Assert.Equal(new(99,99002),patcher.PreparedSelections[99]);var active=patcher.ActiveConfiguration;
            game.IsOpen=true;overlay.Update(103,Peer(new(99,99003,99005,"Dark")));
            Assert.False(await patcher.RebuildIfSafeAsync(CancellationToken.None));Assert.Equal(active,patcher.ActiveConfiguration);Assert.Equal(new(99,99002),patcher.PreparedSelections[99]);
            game.IsOpen=false;Assert.True(await patcher.RebuildIfSafeAsync(CancellationToken.None));Assert.True(gate.IsBusy);
            Assert.Equal(patcher.ConfigurationHash(),patcher.ActiveConfiguration);Assert.Equal(new(99,99003,99005,"Dark"),patcher.PreparedSelections[99]);
            game.IsOpen=true;overlay.Update(null,null);Assert.False(await patcher.RebuildIfSafeAsync(CancellationToken.None));
            game.IsOpen=false;Assert.True(await patcher.RebuildIfSafeAsync(CancellationToken.None));Assert.Equal(new(99,99001),patcher.PreparedSelections[99]);
        }
        finally {await patcher.StopAsync();}
        Assert.False(gate.IsBusy);
    }
    [Fact] public async Task Stop_serializes_with_rebuild_and_leaves_no_session()
    {
        var (_,_,overlay,patcher,_,gate)=await FixtureAsync();await patcher.StartAsync(null,CancellationToken.None);overlay.Update(103,Peer(new(99,99002)));
        var rebuild=patcher.RebuildIfSafeAsync(CancellationToken.None);var stop=patcher.StopAsync();
        try {await rebuild;}catch(OperationCanceledException){}await stop;
        Assert.Equal(PatcherState.Stopped,patcher.Status.State);Assert.Null(patcher.ActiveConfiguration);Assert.False(gate.IsBusy);
    }
    public void Dispose()
    {
        for(int attempt=0;attempt<20;attempt++)
        {
            try{if(Directory.Exists(root))Directory.Delete(root,true);return;}
            catch(Exception e)when(attempt<19 && e is IOException or UnauthorizedAccessException){Thread.Sleep(50);}
        }
    }
}
