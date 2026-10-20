using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace MadokaChanger.Core;

public sealed class NativeBridge(ILogger<NativeBridge> log, string? executable=null)
{
    public string Executable { get; } = executable ?? Path.Combine(AppContext.BaseDirectory,"backend","madokachanger-ltk.exe");
    public bool Available => File.Exists(Executable);
    public string BackendDirectory => Path.GetDirectoryName(Path.GetFullPath(Executable))!;
    public string HostExecutable => Path.Combine(BackendDirectory,PatcherLocator.HostName);
    private string BundledDll => Path.Combine(BackendDirectory,PatcherLocator.DllName);
    // Only the shipped app looks for an installed LTK Manager; an explicit executable (tests, tools) stays self-contained.
    private readonly bool searchInstalled = executable is null;
    private PatcherBinaries? installed;
    private bool searched;
    public bool BackendAvailable => Available && ResolvePatcher() is not null;
    public string PatcherDescription => ResolvePatcher()?.Description ?? "missing";
    /// <summary>The signed patcher of an installed LTK Manager when there is one, else the bundled copy beside the adapter.</summary>
    public PatcherBinaries? ResolvePatcher(bool refresh=false)
    {
        if(searchInstalled && (refresh || !searched)) { installed=PatcherLocator.FindInstalledLtkManager(); searched=true; }
        if(installed is not null && File.Exists(installed.Host) && File.Exists(installed.Dll)) return installed;
        return File.Exists(HostExecutable) && File.Exists(BundledDll) ? new(HostExecutable,BundledDll,false,"MadokaChanger") : null;
    }
    public async Task<JsonDocument> InvokeAsync(object request,CancellationToken ct,IProgress<string>? progress=null)
    {
        if(!Available) throw new FileNotFoundException("The native LTK adapter is missing. Build native/ with Cargo and copy madokachanger-ltk.exe into backend/.",Executable);
        using var p=new Process { StartInfo=new(Executable) { RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,UseShellExecute=false,CreateNoWindow=true } };
        if(!p.Start()) throw new IOException("Could not launch the LTK adapter.");
        var operation=JsonSerializer.SerializeToElement(request,JsonFile.Options).GetProperty("operation").GetString();
        using var job=ProcessJob.Attach(p,operation=="build"?4L*1024*1024*1024:1536L*1024*1024);
        using var cancel=ct.Register(()=>{ try { if(!p.HasExited) p.Kill(true); } catch(InvalidOperationException) { } });
        try
        {
        var errors=p.StandardError.ReadToEndAsync(ct);
        await p.StandardInput.WriteAsync(JsonSerializer.Serialize(request,JsonFile.Options).AsMemory(),ct);
        p.StandardInput.Close(); string? result=null;
        while(await p.StandardOutput.ReadLineAsync(ct) is { } line)
        {
            using var j=JsonDocument.Parse(line);
            if(j.RootElement.TryGetProperty("progress",out _)) progress?.Report($"{MetadataProvider.Text(j.RootElement,"stage")} · {j.RootElement.GetProperty("current")}/{j.RootElement.GetProperty("total")}");
            else result=line;
        }
        await p.WaitForExitAsync(ct); ct.ThrowIfCancellationRequested();
        var stderr=await errors;
        if(result is null) throw new InvalidDataException($"LTK adapter exited {p.ExitCode} without a result. {stderr}");
        using var response=JsonDocument.Parse(result);
        if(p.ExitCode!=0 || !response.RootElement.GetProperty("ok").GetBoolean())
        {
            log.LogError("LTK adapter failed: {Error}",stderr);
            throw new InvalidDataException(MetadataProvider.Text(response.RootElement,"error")+" "+stderr);
        }
        return JsonDocument.Parse(response.RootElement.GetProperty("result").GetRawText());
        }
        finally
        {
            if(!p.HasExited) p.Kill(true);
            await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }
}

public sealed record HostEvent(string Kind,string State,string Message,string Level="")
{
    public static HostEvent? Parse(string line)
    {
        line=line.TrimEnd('\r','\n');
        if(line.StartsWith("dll ",StringComparison.Ordinal))
        {
            // dll <timestamp> <pid> <tid> <level> <message...>
            var d=line.Split(' ',6,StringSplitOptions.RemoveEmptyEntries);
            return d.Length>=6 ? new("dll","",d[5],d[4]) : d.Length>=3 ? new("dll","",string.Join(' ',d.Skip(2))) : null;
        }
        var p=line.Split(' ',4,StringSplitOptions.None);
        if(p.Length<3) return null;
        return p[0] switch
        {
            "status"=>new("status",p[2],p.Length>3?p[3]:""),
            "ok" or "error"=>new(p[0],"",string.Join(' ',p.Skip(2))),
            _=>null
        };
    }
    public static string[] Configuration(string prefix,int flags)
    {
        if(prefix.Contains('\n') || prefix.Contains('\r')) throw new ArgumentException("Overlay path contains a protocol newline.");
        return ["config loglevel 16","config flags "+flags,"config prefix "+prefix.TrimEnd('\\','/')+Path.DirectorySeparatorChar,"start scan"];
    }
}

public static class GameLocator
{
    public static string Normalize(string directory)
    {
        if(File.Exists(Path.Combine(directory,"Game","League of Legends.exe"))) directory=Path.Combine(directory,"Game");
        return Path.GetFullPath(directory);
    }
    public static bool Validate(string directory) => !string.IsNullOrWhiteSpace(directory) &&
        File.Exists(Path.Combine(directory,"League of Legends.exe")) && Directory.Exists(Path.Combine(directory,"DATA","FINAL"));
    public static string? Detect()
    {
        var candidates=new List<string> { @"C:\Riot Games\League of Legends\Game",@"D:\Riot Games\League of Legends\Game" };
        var installs=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"Riot Games","RiotClientInstalls.json");
        try
        {
            if(File.Exists(installs))
            {
                using var doc=JsonDocument.Parse(File.ReadAllText(installs));
                if(doc.RootElement.TryGetProperty("associated_client",out var a)) foreach(var key in a.EnumerateObject())
                    if(key.Name.EndsWith("LeagueClient.exe",StringComparison.OrdinalIgnoreCase)) candidates.Add(Path.Combine(Path.GetDirectoryName(key.Name)!,"Game"));
            }
        }
        catch(Exception e) when(e is IOException or JsonException) { }
        return candidates.FirstOrDefault(Validate);
    }
    public static bool IsGameOpen() => Process.GetProcessesByName("League of Legends").Any(p=>{p.Dispose();return true;});
}

public interface IGameEnvironment
{
    bool IsOpen { get; }
    bool LeagueRunsAsAdmin => Elevation.LeagueRunsAsAdmin();
    bool AppIsElevated => Elevation.AppIsElevated();
}
public sealed class SystemGameEnvironment : IGameEnvironment { public bool IsOpen=>GameLocator.IsGameOpen(); }

public sealed class PatcherController(NativeBridge native,StateStore state,LibraryStore library,AppPaths paths,OperationGate gate,ILogger<PatcherController> log,IGameEnvironment gameEnvironment,DuoOverlay? duo=null)
{
    private readonly DuoOverlay selectionOverlay=duo??new(library);
    public event Action<PatcherStatus>? StatusChanged;
    public PatcherStatus Status { get; private set; } = new(PatcherState.Stopped,"Stopped");
    public string? ActiveConfiguration { get; private set; }
    public IReadOnlyDictionary<int,Selection> PreparedSelections { get; private set; } = new Dictionary<int,Selection>();
    public bool IsGameOpen => gameEnvironment.IsOpen;
    private long stopRevision;
    private readonly object preparationSync=new();
    private CancellationTokenSource? preparationCancellation;
    private readonly SemaphoreSlim lifecycle=new(1,1);
    private Process? host;
    private ProcessJob? job;
    private IDisposable? lease;
    private Task? monitor;
    private bool stopping;
    private PatcherBinaries? binaries;
    private readonly HashSet<string> redirected=new(StringComparer.OrdinalIgnoreCase);
    public bool IsActive => Status.State is PatcherState.Running or PatcherState.Building or PatcherState.Starting or PatcherState.Waiting;
    public string ConfigurationHash()
    {
        var input=selectionOverlay.Capture(state.State);
        return ConfigurationHash(input.Selections.Values,input.CustomSkins);
    }
    public IReadOnlyDictionary<int,Selection> EffectiveSelections()
    {
        return selectionOverlay.Merge(state.State);
    }
    private string ConfigurationHash(IEnumerable<Selection> selections,IEnumerable<CustomSkin> custom)
    {
        var config=new { selections=selections.OrderBy(s=>s.ChampionId),custom=custom.OrderBy(c=>c.Id).Select(c=>c.Sha256),priority=state.State.ConflictChoice,library=library.Manifest?.Commit };
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(config,JsonFile.Options)));
    }
    public List<OverrideSource> Sources()
    {
        var input=selectionOverlay.Capture(state.State);
        return Sources(input.Selections.Values,input.CustomSkins);
    }
    private List<OverrideSource> Sources(IEnumerable<Selection> selections,IEnumerable<CustomSkin> custom)
    {
        var result=new List<OverrideSource>();
        foreach(var selection in selections)
        {
            var matches=library.Find(selection);
            if(matches.Count!=1) throw new InvalidOperationException($"Saved selection for champion {selection.ChampionId} is unavailable or ambiguous. Reset it or install its package.");
            var p=matches[0]; result.Add(new("repo:"+p.Path,p.Name,false,p.Overrides));
        }
        result.AddRange(custom.Select(c=>new OverrideSource("custom:"+c.Id,c.Name,true,c.Overrides)));
        return result;
    }
    public async Task StartAsync(IProgress<string>? progress,CancellationToken ct)
    {
        var expectedStop=Interlocked.Read(ref stopRevision);
        await lifecycle.WaitAsync(ct);
        try
        {
            if(expectedStop!=Interlocked.Read(ref stopRevision))throw new OperationCanceledException("Start was superseded by Stop.");
            await StartCoreAsync(progress,ct,expectedStop:expectedStop);
        }
        finally { lifecycle.Release(); }
    }
    /// <summary>Rebuild only a session the user already started, holding its operation lease and lifecycle lock.</summary>
    public async Task<bool> RebuildIfSafeAsync(CancellationToken ct)
    {
        var expectedStop=Interlocked.Read(ref stopRevision);
        await lifecycle.WaitAsync(ct);
        try
        {
            if(expectedStop!=Interlocked.Read(ref stopRevision) || Status.State is not (PatcherState.Running or PatcherState.Waiting) || gameEnvironment.IsOpen || ActiveConfiguration==ConfigurationHash()) return false;
            // Validate before stopping a usable scanning session.
            ConflictResolver.Order(Sources(),state.State.ConflictChoice);
            await StopHostAsync(); ActiveConfiguration=null; PreparedSelections=new Dictionary<int,Selection>();
            if(expectedStop!=Interlocked.Read(ref stopRevision)) return false;
            await StartCoreAsync(null,ct,rebuilding:true,expectedStop);
            return true;
        }
        finally { lifecycle.Release(); }
    }
    private async Task StartCoreAsync(IProgress<string>? progress,CancellationToken ct,bool rebuilding=false,long? expectedStop=null)
    {
        using var preparation=CancellationTokenSource.CreateLinkedTokenSource(ct);
        lock(preparationSync) preparationCancellation=preparation;
        ct=preparation.Token;
        try
        {
            ct.ThrowIfCancellationRequested();
            if(expectedStop is { } beforeBuild && beforeBuild!=Interlocked.Read(ref stopRevision))throw new OperationCanceledException("Patcher stopped before preparation.");
            if(host is not null || !rebuilding && IsActive) throw new InvalidOperationException("Stop the current session before rebuilding.");
            var patcherFiles=native.Available ? native.ResolvePatcher(refresh:true) : null;
            if(patcherFiles is null) throw new FileNotFoundException("The built-in backend is incomplete. Extract the full MadokaChanger download again, keeping its backend folder beside the app.");
            binaries=patcherFiles;
            var game=state.State.Settings.GameFolder;
            if(!GameLocator.Validate(game)) throw new DirectoryNotFoundException("Choose a League Game folder containing League of Legends.exe and DATA/FINAL.");
            if(gameEnvironment.IsOpen) throw new InvalidOperationException("Close the League game before building a new overlay.");
            lease??=await gate.EnterAsync(ct); stopping=false;
            var (selections,customSkins)=selectionOverlay.Capture(state.State);
            var config=ConfigurationHash(selections.Values,customSkins); var sources=ConflictResolver.Order(Sources(selections.Values,customSkins),state.State.ConflictChoice);
            if(sources.Count==0)
            {
                ct.ThrowIfCancellationRequested();
                if(expectedStop is { } beforeWaiting && beforeWaiting!=Interlocked.Read(ref stopRevision))throw new OperationCanceledException("Patcher stopped before waiting.");
                ActiveConfiguration=config;PreparedSelections=new Dictionary<int,Selection>();
                Set(PatcherState.Waiting,"Waiting for a locked-in champion with a saved skin · Stop cancels automatic patching");return;
            }
            Set(PatcherState.Building,"Preparing selected packages");
            var session=Path.Combine(paths.Overlay,Guid.NewGuid().ToString("N")); Directory.CreateDirectory(session);
            var mods=new List<object>();
            foreach(var source in sources)
            {
                ct.ThrowIfCancellationRequested(); string project;
                if(source.IsCustom) project=customSkins.Single(c=>"custom:"+c.Id==source.Id).ProjectPath;
                else
                {
                    var p=library.Manifest!.Packages.Single(p=>"repo:"+p.Path==source.Id);
                    project=Path.Combine(session,"projects",mods.Count.ToString());
                    progress?.Report("Preparing "+p.Name);
                    using var result=await native.InvokeAsync(new{operation="import",package=library.GetPackagePath(p),output=project},ct);
                }
                mods.Add(new{id=source.Id,path=project});
            }
            var overlay=Path.Combine(session,"overlay");
            using(var result=await native.InvokeAsync(new{operation="build",game,overlay,state=Path.Combine(session,"state"),mods},ct,progress))
                log.LogInformation("Built overlay: {Result}",result.RootElement.ToString());
            ct.ThrowIfCancellationRequested();
            if(expectedStop is { } revision && revision!=Interlocked.Read(ref stopRevision))throw new OperationCanceledException("Patcher stopped during preparation.");
            if(gameEnvironment.IsOpen) throw new InvalidOperationException("League opened during preparation. Changes are pending; close the game and Start again.");
            Set(PatcherState.Starting,"Waiting for the LTK host to confirm scanning");
            var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var process=new Process { StartInfo=new(patcherFiles.Host) { WorkingDirectory=patcherFiles.Directory,UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true } };
            // Same rule as ltk-manager: --elevate (a UAC prompt) only when asked for or when League itself runs as admin.
            var settings=state.State.Settings;
            bool leagueAdmin=gameEnvironment.LeagueRunsAsAdmin,appElevated=gameEnvironment.AppIsElevated;
            var elevate=Elevation.Decide(settings.ElevatePatcher,leagueAdmin,appElevated);
            var flags=HookFlags.For(settings);
            log.LogInformation("Patcher: {Patcher}; elevate={Elevate} (opt_in={OptIn}, league_admin={LeagueAdmin}, app_elevated={AppElevated}); flags={Flags}",patcherFiles.Description,elevate,settings.ElevatePatcher,leagueAdmin,appElevated,flags);
            if(elevate) process.StartInfo.ArgumentList.Add("--elevate");
            redirected.Clear();
            if(expectedStop is { } beforeLaunch && beforeLaunch!=Interlocked.Read(ref stopRevision))throw new OperationCanceledException("Patcher stopped during preparation.");
            if(!process.Start()) throw new IOException("LTK host could not start."); host=process;
            job=ProcessJob.Attach(process,0);
            monitor=MonitorAsync(process,started);
            foreach(var line in HostEvent.Configuration(overlay,flags)) await process.StandardInput.WriteLineAsync(line.AsMemory(),ct);
            await process.StandardInput.FlushAsync(ct);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(30),ct);
            if(expectedStop is { } afterLaunch && afterLaunch!=Interlocked.Read(ref stopRevision))throw new OperationCanceledException("Patcher stopped during preparation.");
            ActiveConfiguration=config; PreparedSelections=new Dictionary<int,Selection>(selections); Set(PatcherState.Running,"Running · scanning for League (in-game effects are not confirmed)");
            await JsonFile.WriteAsync(Path.Combine(session,"configuration.json"),new{config,selections,mods},ct);
        }
        catch(Exception e)
        {
            await StopHostAsync(); lease?.Dispose(); lease=null; ActiveConfiguration=null; PreparedSelections=new Dictionary<int,Selection>();
            Set(e is OperationCanceledException ? PatcherState.Stopped : PatcherState.Error,e is OperationCanceledException ? "Build cancelled" : e.Message); throw;
        }
        finally {lock(preparationSync)if(ReferenceEquals(preparationCancellation,preparation))preparationCancellation=null;}
    }
    private async Task MonitorAsync(Process process,TaskCompletionSource started)
    {
        var stderr=Task.Run(async()=> { while(await process.StandardError.ReadLineAsync() is { } e) log.LogWarning("LTK host stderr: {Line}",e); });
        try
        {
            while(await process.StandardOutput.ReadLineAsync() is { } line)
            {
                log.LogInformation("LTK host: {Line}",line);
                var e=HostEvent.Parse(line); if(e is null) continue;
                var verdict=e.Kind=="dll" ? DllLines.Classify(e.Level,e.Message) : null;
                if(e.Kind=="error" || e.State=="failed" || verdict?.Kind==DllLineKind.Fatal)
                {
                    var error=new IOException("LTK rejected the session: "+(verdict?.Text ?? e.Message)+FailureHint(e)); started.TrySetException(error); Set(PatcherState.Error,error.Message);
                    await process.StandardInput.WriteLineAsync("stop"); process.StandardInput.Close(); break;
                }
                if(verdict?.Kind==DllLineKind.Warning && ActiveConfiguration is not null) { log.LogWarning("Patcher: {Warning}",verdict.Text); Set(PatcherState.Running,verdict.Text); }
                if(verdict?.Kind==DllLineKind.Redirected && ActiveConfiguration is not null && redirected.Add(verdict.Text))
                    Set(PatcherState.Running,$"In game · skins applied ({redirected.Count} archive{(redirected.Count==1?"":"s")} redirected)");
                if(e.Kind=="status" && e.State is "injecting" or "injected" or "waiting")
                {
                    started.TrySetResult();
                    if(ActiveConfiguration is not null) Set(PatcherState.Running,e.State=="injecting"?"Running · scanning for League":$"Host {e.State} · {e.Message}");
                }
                if(e.State=="exited" && ActiveConfiguration is not null) { redirected.Clear(); Set(PatcherState.Running,"Game exited · scanning for the next game"); }
            }
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); await stderr;
            if(!stopping) { started.TrySetException(new IOException("LTK host exited before confirming startup. Check logs and backend compatibility.")); if(Status.State!=PatcherState.Error) Set(PatcherState.Error,"LTK host exited unexpectedly; inspect logs."); }
        }
        catch(Exception e) when(e is IOException or InvalidOperationException or TimeoutException)
        {
            if(!stopping) { started.TrySetException(e); Set(PatcherState.Error,e.Message); }
            try { if(!process.HasExited) process.Kill(true);await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); } catch(InvalidOperationException) { }
        }
        finally
        {
            if(!stopping) { lease?.Dispose(); lease=null; ActiveConfiguration=null; PreparedSelections=new Dictionary<int,Selection>(); job?.Dispose(); job=null; if(host==process) host=null; process.Dispose(); }
        }
    }
    public async Task StopAsync()
    {
        Interlocked.Increment(ref stopRevision);
        lock(preparationSync)preparationCancellation?.Cancel();
        await lifecycle.WaitAsync();
        try { await StopHostAsync(); ActiveConfiguration=null; PreparedSelections=new Dictionary<int,Selection>(); lease?.Dispose(); lease=null; Set(PatcherState.Stopped,"Stopped"); }
        finally { lifecycle.Release(); }
    }
    private async Task StopHostAsync()
    {
        stopping=true; var p=host; host=null;
        if(p is not null)
        {
            try
            {
                if(!p.HasExited) { await p.StandardInput.WriteLineAsync("stop"); p.StandardInput.Close(); await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
            }
            catch(Exception e) when(e is IOException or InvalidOperationException or TimeoutException) { try { if(!p.HasExited) p.Kill(true); } catch(InvalidOperationException) { } }
            await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            if(monitor is not null) await monitor;
            p.Dispose();
        }
        monitor=null; job?.Dispose(); job=null;
    }
    private string FailureHint(HostEvent e)
    {
        if(!e.Message.Contains("SetWindowsHookEx",StringComparison.OrdinalIgnoreCase) && !e.Message.Contains("hook install",StringComparison.OrdinalIgnoreCase)) return "";
        return binaries is { Signed: false }
            ? " · MadokaChanger is using its bundled unsigned patcher. Install LTK Manager (github.com/LeagueToolkit/ltk-manager) and MadokaChanger will use its signed patcher automatically."
            : " · If League is set to run as administrator, turn on \"Run patcher as administrator\" in Settings.";
    }
    private void Set(PatcherState value,string message) { Status=new(value,message); StatusChanged?.Invoke(Status); }
}
