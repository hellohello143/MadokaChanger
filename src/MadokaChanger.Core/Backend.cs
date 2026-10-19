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
    public string HostExecutable => Path.Combine(BackendDirectory,"ltk_patcher_host.exe");
    public bool BackendAvailable => Available && File.Exists(HostExecutable) && File.Exists(Path.Combine(BackendDirectory,"ltk_patcher_dll.dll"));
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

public sealed record HostEvent(string Kind,string State,string Message)
{
    public static HostEvent? Parse(string line)
    {
        var p=line.Split(' ',4,StringSplitOptions.None);
        if(p.Length<3) return null;
        return p[0] switch
        {
            "status"=>new("status",p[2],p.Length>3?p[3]:""),
            "ok" or "error"=>new(p[0],"",string.Join(' ',p.Skip(2))),
            "dll"=>new("dll","",string.Join(' ',p.Skip(2))),
            _=>null
        };
    }
    public static string[] Configuration(string prefix)
    {
        if(prefix.Contains('\n') || prefix.Contains('\r')) throw new ArgumentException("Overlay path contains a protocol newline.");
        return ["config loglevel 16","config flags 0","config prefix "+prefix.TrimEnd('\\','/')+Path.DirectorySeparatorChar,"start scan"];
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

public interface IGameEnvironment { bool IsOpen { get; } }
public sealed class SystemGameEnvironment : IGameEnvironment { public bool IsOpen=>GameLocator.IsGameOpen(); }

public sealed class PatcherController(NativeBridge native,StateStore state,LibraryStore library,AppPaths paths,OperationGate gate,ILogger<PatcherController> log,IGameEnvironment gameEnvironment)
{
    public event Action<PatcherStatus>? StatusChanged;
    public PatcherStatus Status { get; private set; } = new(PatcherState.Stopped,"Stopped");
    public string? ActiveConfiguration { get; private set; }
    private readonly SemaphoreSlim lifecycle=new(1,1);
    private Process? host;
    private ProcessJob? job;
    private IDisposable? lease;
    private Task? monitor;
    private bool stopping;
    public bool IsActive => Status.State is PatcherState.Running or PatcherState.Building or PatcherState.Starting;
    public string ConfigurationHash()
    {
        var config=new { selections=state.State.Selections.Values.OrderBy(s=>s.ChampionId),custom=state.State.CustomSkins.Where(c=>c.Enabled).OrderBy(c=>c.Id).Select(c=>c.Sha256),priority=state.State.ConflictChoice,library=library.Manifest?.Commit };
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(config,JsonFile.Options)));
    }
    public List<OverrideSource> Sources()
    {
        var result=new List<OverrideSource>();
        foreach(var selection in state.State.Selections.Values)
        {
            var matches=library.Find(selection);
            if(matches.Count!=1) throw new InvalidOperationException($"Saved selection for champion {selection.ChampionId} is unavailable or ambiguous. Reset it or install its package.");
            var p=matches[0]; result.Add(new("repo:"+p.Path,p.Name,false,p.Overrides));
        }
        result.AddRange(state.State.CustomSkins.Where(c=>c.Enabled).Select(c=>new OverrideSource("custom:"+c.Id,c.Name,true,c.Overrides)));
        return result;
    }
    public async Task StartAsync(IProgress<string>? progress,CancellationToken ct)
    {
        await lifecycle.WaitAsync(ct);
        try
        {
            if(host is not null || IsActive) throw new InvalidOperationException("Stop the current session before rebuilding.");
            if(!native.BackendAvailable) throw new FileNotFoundException("The built-in backend is incomplete. Extract the full MadokaChanger download again, keeping its backend folder beside the app.");
            var backend=native.BackendDirectory;
            var exe=native.HostExecutable;
            var game=state.State.Settings.GameFolder;
            if(!GameLocator.Validate(game)) throw new DirectoryNotFoundException("Choose a League Game folder containing League of Legends.exe and DATA/FINAL.");
            if(gameEnvironment.IsOpen) throw new InvalidOperationException("Close the League game before building a new overlay.");
            lease=await gate.EnterAsync(ct); stopping=false;
            var config=ConfigurationHash(); var sources=ConflictResolver.Order(Sources(),state.State.ConflictChoice);
            if(sources.Count==0) throw new InvalidOperationException("Apply a skin selection or enable a custom skin before starting.");
            Set(PatcherState.Building,"Preparing selected packages");
            var session=Path.Combine(paths.Overlay,Guid.NewGuid().ToString("N")); Directory.CreateDirectory(session);
            var mods=new List<object>();
            foreach(var source in sources)
            {
                ct.ThrowIfCancellationRequested(); string project;
                if(source.IsCustom) project=state.State.CustomSkins.Single(c=>"custom:"+c.Id==source.Id).ProjectPath;
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
            ct.ThrowIfCancellationRequested(); Set(PatcherState.Starting,"Waiting for the LTK host to confirm scanning");
            var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var process=new Process { StartInfo=new(exe) { WorkingDirectory=backend,UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true } };
            // --elevate is the verified upstream host argument. Elevation is handled by that official host.
            process.StartInfo.ArgumentList.Add("--elevate");
            if(!process.Start()) throw new IOException("LTK host could not start."); host=process;
            job=ProcessJob.Attach(process,0);
            monitor=MonitorAsync(process,started);
            foreach(var line in HostEvent.Configuration(overlay)) await process.StandardInput.WriteLineAsync(line.AsMemory(),ct);
            await process.StandardInput.FlushAsync(ct);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(30),ct);
            ActiveConfiguration=config; Set(PatcherState.Running,"Running · scanning for League (in-game effects are not confirmed)");
            await JsonFile.WriteAsync(Path.Combine(session,"configuration.json"),new{config,state.State.Selections,mods},ct);
        }
        catch(Exception e)
        {
            await StopHostAsync(); lease?.Dispose(); lease=null; ActiveConfiguration=null;
            Set(e is OperationCanceledException ? PatcherState.Stopped : PatcherState.Error,e is OperationCanceledException ? "Build cancelled" : e.Message); throw;
        }
        finally { lifecycle.Release(); }
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
                if(e.Kind=="error" || e.State=="failed" || (e.Kind=="dll" && (e.Message.Contains("WAD scan failed",StringComparison.OrdinalIgnoreCase) || e.Message.Contains("overlay verification failed",StringComparison.OrdinalIgnoreCase) || e.Message.Contains("AH init failed",StringComparison.OrdinalIgnoreCase) || e.Message.Contains("not overlaying",StringComparison.OrdinalIgnoreCase) || e.Message.Contains("end of life reached",StringComparison.OrdinalIgnoreCase))))
                {
                    var error=new IOException("LTK rejected the session: "+e.Message); started.TrySetException(error); Set(PatcherState.Error,error.Message);
                    await process.StandardInput.WriteLineAsync("stop"); process.StandardInput.Close(); break;
                }
                if(e.Kind=="status" && e.State is "injecting" or "injected" or "waiting")
                {
                    started.TrySetResult();
                    if(ActiveConfiguration is not null) Set(PatcherState.Running,e.State=="injecting"?"Running · scanning for League":$"Host {e.State} · {e.Message}");
                }
                if(e.State=="exited" && ActiveConfiguration is not null) Set(PatcherState.Running,"Game exited · scanning for the next game");
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
            if(!stopping) { lease?.Dispose(); lease=null; ActiveConfiguration=null; job?.Dispose(); job=null; if(host==process) host=null; process.Dispose(); }
        }
    }
    public async Task StopAsync()
    {
        await lifecycle.WaitAsync();
        try { await StopHostAsync(); ActiveConfiguration=null; lease?.Dispose(); lease=null; Set(PatcherState.Stopped,"Stopped"); }
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
    private void Set(PatcherState value,string message) { Status=new(value,message); StatusChanged?.Invoke(Status); }
}
