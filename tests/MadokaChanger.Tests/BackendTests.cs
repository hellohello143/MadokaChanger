using Microsoft.Extensions.Logging.Abstractions;
using MadokaChanger.Core;
using Xunit;
using System.Diagnostics;

namespace MadokaChanger.Tests;
public sealed class BackendTests : IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"MadokaChanger-backend-tests-"+Guid.NewGuid().ToString("N"));
    private async Task<(PatcherController Controller,OperationGate Gate,StateStore State)> CreateAsync(string mode="normal",bool gameOpen=false,string backendRelative="backend",bool leagueAdmin=false,bool appElevated=false)
    {
        var paths=new AppPaths(root);var state=new StateStore(paths,NullLogger<StateStore>.Instance);await state.LoadAsync();
        var backend=Path.Combine(root,backendRelative);Directory.CreateDirectory(backend);
        foreach(var file in Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory,"TestBackend")))File.Copy(file,Path.Combine(backend,Path.GetFileName(file)));
        var fixture=Path.Combine(backend,"MadokaChanger.TestBackend.exe");File.Copy(fixture,Path.Combine(backend,"ltk_patcher_host.exe"));
        await File.WriteAllTextAsync(Path.Combine(backend,"ltk_patcher_dll.dll"),"fixture");await File.WriteAllTextAsync(Path.Combine(backend,"test-mode.txt"),mode);
        var game=Path.Combine(root,"game");Directory.CreateDirectory(Path.Combine(game,"DATA","FINAL"));await File.WriteAllTextAsync(Path.Combine(game,"League of Legends.exe"),"fixture");
        state.State.Settings.GameFolder=game;state.State.Selections[103]=new(103,103001);
        var library=new LibraryStore(state);var generation=Path.Combine(library.Root,"versions","fixture");Directory.CreateDirectory(generation);
        await library.ActivateAsync(generation,new("test","main",DateTimeOffset.UtcNow,"16.20",[new("fixture.fantome","sha",1,"Fixture","Author","1",103,103001,null,null,null,["a"],null)],[]),CancellationToken.None);
        var gate=new OperationGate();var native=new NativeBridge(NullLogger<NativeBridge>.Instance,fixture);
        return(new(native,state,library,paths,gate,NullLogger<PatcherController>.Instance,new FakeGameEnvironment(gameOpen,leagueAdmin,appElevated)),gate,state);
    }
    [Theory]
    [InlineData("backend")]
    [InlineData("portable app files/backend")]
    public async Task Confirmed_host_start_pending_configuration_and_graceful_stop(string backendRelative)
    {
        var (controller,gate,state)=await CreateAsync(backendRelative:backendRelative);var states=new List<PatcherState>();controller.StatusChanged+=s=>states.Add(s.State);
        await controller.StartAsync(null,CancellationToken.None);
        Assert.Equal(PatcherState.Running,controller.Status.State);Assert.True(gate.IsBusy);Assert.Contains(PatcherState.Building,states);Assert.Contains(PatcherState.Starting,states);
        Assert.Equal(controller.ActiveConfiguration,controller.ConfigurationHash());state.State.Selections[103]=new(103,103002);Assert.NotEqual(controller.ActiveConfiguration,controller.ConfigurationHash());
        await controller.StopAsync();Assert.Equal(PatcherState.Stopped,controller.Status.State);Assert.False(gate.IsBusy);Assert.Null(controller.ActiveConfiguration);
        var commands=await File.ReadAllLinesAsync(Path.Combine(root,backendRelative,"commands.log"));Assert.Contains("config flags 4",commands);Assert.Contains("start scan",commands);Assert.Contains("stop",commands);
        Assert.DoesNotContain("--elevate",await File.ReadAllTextAsync(Path.Combine(root,backendRelative,"args.log")));
    }
    [Theory]
    [InlineData(false,false,false,false)]
    [InlineData(true,false,false,true)]
    [InlineData(false,true,false,true)]
    [InlineData(true,true,true,false)]
    public async Task Host_elevates_only_when_asked_or_when_League_runs_as_admin(bool optIn,bool leagueAdmin,bool appElevated,bool expected)
    {
        var (controller,_,state)=await CreateAsync(leagueAdmin:leagueAdmin,appElevated:appElevated);state.State.Settings.ElevatePatcher=optIn;
        await controller.StartAsync(null,CancellationToken.None);await controller.StopAsync();
        Assert.Equal(expected,(await File.ReadAllTextAsync(Path.Combine(root,"backend","args.log"))).Contains("--elevate"));
    }
    [Fact] public async Task Enforcing_the_skinhack_scan_sends_flags_0()
    {
        var (controller,_,state)=await CreateAsync();state.State.Settings.EnforceSkinhackScan=true;
        await controller.StartAsync(null,CancellationToken.None);await controller.StopAsync();
        Assert.Contains("config flags 0",await File.ReadAllLinesAsync(Path.Combine(root,"backend","commands.log")));
    }
    [Fact] public async Task Backend_errors_cannot_be_reported_as_success_and_release_the_lease()
    {
        var (controller,gate,_)=await CreateAsync("error");await Assert.ThrowsAsync<IOException>(()=>controller.StartAsync(null,CancellationToken.None));
        Assert.Equal(PatcherState.Error,controller.Status.State);Assert.False(gate.IsBusy);Assert.Null(controller.ActiveConfiguration);
    }
    [Fact] public async Task Cancelled_start_does_not_leave_a_running_session()
    {
        var (controller,gate,_)=await CreateAsync("no-ack");using var cancel=new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>controller.StartAsync(null,cancel.Token));Assert.False(gate.IsBusy);Assert.False(controller.IsActive);
        var pidFile=Path.Combine(root,"backend","host.pid");
        if(File.Exists(pidFile))
        {
            var pid=int.Parse(await File.ReadAllTextAsync(pidFile));
            try{using var process=Process.GetProcessById(pid);Assert.True(process.HasExited);}catch(ArgumentException){ }
        }
    }
    [Fact] public async Task Open_game_prevents_patch_build_before_any_native_operation()
    {
        var (controller,gate,_)=await CreateAsync(gameOpen:true);await Assert.ThrowsAsync<InvalidOperationException>(()=>controller.StartAsync(null,CancellationToken.None));Assert.False(gate.IsBusy);Assert.False(File.Exists(Path.Combine(root,"backend","commands.log")));
    }
    [Theory]
    [InlineData("ltk_patcher_host.exe")]
    [InlineData("ltk_patcher_dll.dll")]
    public async Task Missing_bundled_files_stop_before_any_native_operation(string file)
    {
        var (controller,gate,_)=await CreateAsync();File.Delete(Path.Combine(root,"backend",file));
        var error=await Assert.ThrowsAsync<FileNotFoundException>(()=>controller.StartAsync(null,CancellationToken.None));
        Assert.Contains("Extract the full MadokaChanger download",error.Message);Assert.False(gate.IsBusy);
        Assert.False(File.Exists(Path.Combine(root,"backend","commands.log")));
    }
    [Fact] public async Task Legacy_backend_setting_is_ignored_and_removed_without_losing_preferences()
    {
        var paths=new AppPaths(root);
        await File.WriteAllTextAsync(Path.Combine(root,"settings.json"),"""{"settings":{"backendFolder":"Z:\\obsolete-backend","language":"en_us","reducedMotion":true},"favorites":[103]}""");
        var state=new StateStore(paths,NullLogger<StateStore>.Instance);await state.LoadAsync();await state.SaveAsync();
        Assert.Equal("en_us",state.State.Settings.Language);Assert.True(state.State.Settings.ReducedMotion);Assert.Contains(103,state.State.Favorites);
        Assert.DoesNotContain("backendFolder",await File.ReadAllTextAsync(Path.Combine(root,"settings.json")));
    }
    private sealed class FakeGameEnvironment(bool isOpen,bool leagueAdmin=false,bool appElevated=false):IGameEnvironment {public bool IsOpen=>isOpen;public bool LeagueRunsAsAdmin=>leagueAdmin;public bool AppIsElevated=>appElevated;}
    public void Dispose()
    {
        for(int attempt=0;attempt<20;attempt++)
        {
            try{if(Directory.Exists(root))Directory.Delete(root,true);return;}
            catch(Exception e) when(attempt<19&&e is IOException or UnauthorizedAccessException){Thread.Sleep(50);}
        }
    }
}
