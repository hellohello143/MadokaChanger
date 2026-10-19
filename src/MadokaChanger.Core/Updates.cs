using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace MadokaChanger.Core;

public sealed record ReleaseAsset(string Name,string Url,long Size,string? Sha256);
public sealed record ReleaseInfo(string Tag,Version Version,string Notes,List<ReleaseAsset> Assets)
{
    public ReleaseAsset? Find(Func<string,bool> match)=>Assets.FirstOrDefault(a=>match(a.Name));
}

/// <summary>Reads a GitHub "latest release" response. Pure, so it is unit-tested without the network.</summary>
public static partial class ReleaseParser
{
    public static ReleaseInfo Parse(JsonElement release)
    {
        var tag=MetadataProvider.Text(release,"tag_name");
        var version=ParseVersion(tag) ?? throw new InvalidDataException($"Release tag '{tag}' is not a version number.");
        var assets=new List<ReleaseAsset>();
        if(release.TryGetProperty("assets",out var list) && list.ValueKind==JsonValueKind.Array)
            foreach(var a in list.EnumerateArray())
            {
                var digest=MetadataProvider.Text(a,"digest");
                var sha=digest.StartsWith("sha256:",StringComparison.OrdinalIgnoreCase) ? digest[7..].ToLowerInvariant() : null;
                assets.Add(new(MetadataProvider.Text(a,"name"),MetadataProvider.Text(a,"browser_download_url"),a.TryGetProperty("size",out var s)?s.GetInt64():0,sha));
            }
        return new(tag,version,MetadataProvider.Text(release,"body"),assets);
    }

    /// <summary>"v1.28.1", "1.0.1" and "LTK Manager 1.28.1" all give 1.28.1. Missing parts are zero so 1.1 equals 1.1.0.</summary>
    public static Version? ParseVersion(string? text)
    {
        if(string.IsNullOrWhiteSpace(text)) return null;
        var m=VersionRegex().Match(text); if(!m.Success) return null;
        // A long digit run (a date-stamped tag, say) overflows Int32: that is "not a version", not a crash.
        var parts=new int[3];
        for(int i=0;i<3;i++)
            if(m.Groups[i+1].Success && !int.TryParse(m.Groups[i+1].ValueSpan,System.Globalization.NumberStyles.None,System.Globalization.CultureInfo.InvariantCulture,out parts[i])) return null;
        return new(parts[0],parts[1],parts[2]);
    }

    /// <summary>Normalises to three parts so an assembly's 1.1.0.0 compares equal to a 1.1.0 tag.</summary>
    public static Version Normalize(Version v)=>new(v.Major,Math.Max(0,v.Minor),Math.Max(0,v.Build));

    [GeneratedRegex(@"(\d+)(?:\.(\d+))?(?:\.(\d+))?")] private static partial Regex VersionRegex();
}

/// <summary>What is installed of LTK Manager, read from Windows' installed-apps list and its patcher's signature.</summary>
public sealed record LtkInstall(Version? Version,string? Directory,bool PatcherSigned)
{
    public bool Installed=>Version is not null || PatcherSigned;
    public string Display=>!Installed?"not installed":PatcherSigned?$"{(Version is null?"installed":"v"+Version.ToString(3))} · signed patcher ready":$"v{Version!.ToString(3)} · patcher missing or unsigned";
    public static LtkInstall Detect()
    {
        var patcher=PatcherLocator.FindInstalledLtkManager();
        Version? version=null; string? dir=patcher?.Directory;
        if(OperatingSystem.IsWindows())
            foreach(var hive in new[]{Registry.LocalMachine,Registry.CurrentUser})
            foreach(var view in new[]{@"Software\Microsoft\Windows\CurrentVersion\Uninstall",@"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"})
            {
                try
                {
                    using var root=hive.OpenSubKey(view); if(root is null) continue;
                    foreach(var name in root.GetSubKeyNames())
                    {
                        using var app=root.OpenSubKey(name);
                        if(app?.GetValue("DisplayName") is not string display || !display.Contains("LTK Manager",StringComparison.OrdinalIgnoreCase)) continue;
                        var found=ReleaseParser.ParseVersion(app.GetValue("DisplayVersion") as string);
                        if(found is not null && (version is null || found>version)) { version=found; dir??=(app.GetValue("InstallLocation") as string)?.Trim('"'); }
                    }
                }
                catch(Exception e) when(e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
            }
        return new(version,dir,patcher is not null);
    }
}

public sealed record UpdateReport(Version AppCurrent,ReleaseInfo? AppLatest,LtkInstall Ltk,ReleaseInfo? LtkLatest,string? Problem)
{
    public bool AppUpdateAvailable=>AppLatest is not null && ReleaseParser.Normalize(AppLatest.Version)>ReleaseParser.Normalize(AppCurrent) && UpdateService.AppSetupAsset(AppLatest) is not null;
    /// <summary>Missing, or present without a signed patcher League will accept.</summary>
    public bool LtkMissing=>!Ltk.PatcherSigned;
    public bool LtkUpdateAvailable=>Ltk.PatcherSigned && Ltk.Version is not null && LtkLatest is not null && LtkLatest.Version>Ltk.Version;
    public bool LtkActionNeeded=>(LtkMissing||LtkUpdateAvailable) && LtkLatest is not null && UpdateService.LtkSetupAsset(LtkLatest) is not null;
    public string Summary=>
        $"MadokaChanger v{ReleaseParser.Normalize(AppCurrent).ToString(3)}"+(AppLatest is null?" · latest unknown":AppUpdateAvailable?$" → v{AppLatest.Version.ToString(3)} available":" · up to date")+
        $"\nLTK Manager {Ltk.Display}"+(LtkLatest is null?"":LtkMissing?$" · v{LtkLatest.Version.ToString(3)} will be installed":LtkUpdateAvailable?$" → v{LtkLatest.Version.ToString(3)} available":" · up to date")+
        (Problem is null?"":"\n"+Problem);
}

/// <summary>
/// Checks MadokaChanger's and LTK Manager's GitHub releases and installs what is missing or outdated.
/// Every download is checked against the SHA-256 GitHub publishes for the asset before it runs, and LTK Manager's
/// installer must also carry a valid Authenticode signature, because it installs the patcher League verifies.
/// </summary>
public sealed class UpdateService(RemoteClient remote,AppPaths paths,NativeBridge native,ILogger<UpdateService> log)
{
    public const string AppRepository="hellohello143/MadokaChanger";
    public const string LtkRepository="LeagueToolkit/ltk-manager";
    public static Version CurrentVersion=>ReleaseParser.Normalize(typeof(UpdateService).Assembly.GetName().Version ?? new Version(0,0,0));

    public static ReleaseAsset? AppSetupAsset(ReleaseInfo r)=>r.Find(n=>n.StartsWith("MadokaChanger-Setup-",StringComparison.OrdinalIgnoreCase) && n.EndsWith(".exe",StringComparison.OrdinalIgnoreCase));
    public static ReleaseAsset? LtkSetupAsset(ReleaseInfo r)=>
        r.Find(n=>n.Equals("LTK.Manager_x64-setup.exe",StringComparison.OrdinalIgnoreCase)) ??
        r.Find(n=>n.StartsWith("LTK.Manager_",StringComparison.OrdinalIgnoreCase) && n.EndsWith("_x64-setup.exe",StringComparison.OrdinalIgnoreCase));

    public async Task<ReleaseInfo> LatestAsync(string repository,CancellationToken ct)
    {
        using var doc=await remote.JsonAsync($"https://api.github.com/repos/{repository}/releases/latest",ct);
        return ReleaseParser.Parse(doc.RootElement);
    }

    public async Task<UpdateReport> CheckAsync(CancellationToken ct)
    {
        ReleaseInfo? app=null,ltk=null; var problems=new List<string>();
        try { app=await LatestAsync(AppRepository,ct); } catch(Exception e) when(e is HttpRequestException or InvalidDataException or JsonException or KeyNotFoundException) { log.LogWarning(e,"MadokaChanger release check failed"); problems.Add("Couldn't check for a new MadokaChanger: "+e.Message); }
        try { ltk=await LatestAsync(LtkRepository,ct); } catch(Exception e) when(e is HttpRequestException or InvalidDataException or JsonException or KeyNotFoundException) { log.LogWarning(e,"LTK Manager release check failed"); problems.Add("Couldn't check LTK Manager's releases: "+e.Message); }
        var report=new UpdateReport(CurrentVersion,app,LtkInstall.Detect(),ltk,problems.Count==0?null:string.Join(" ",problems));
        log.LogInformation("Update check: {Summary}",report.Summary.Replace('\n',' '));
        return report;
    }

    /// <summary>Downloads an asset into the app's temp folder and refuses it unless its SHA-256 matches GitHub's digest.</summary>
    public async Task<string> DownloadVerifiedAsync(ReleaseAsset asset,IProgress<string>? progress,CancellationToken ct)
    {
        if(asset.Sha256 is not { Length:64 } expected) throw new InvalidDataException($"GitHub published no SHA-256 for {asset.Name}, so it won't be run.");
        if(!Uri.TryCreate(asset.Url,UriKind.Absolute,out var uri) || uri.Scheme!="https" || uri.Host!="github.com") throw new InvalidDataException($"Unexpected download address for {asset.Name}.");
        var folder=Path.Combine(paths.Temporary,"updates"); Directory.CreateDirectory(folder);
        var target=Path.Combine(folder,Path.GetFileName(asset.Name));
        long done=0; var last=DateTime.MinValue;
        await remote.DownloadAsync(asset.Url,target,ct,n=>
        {
            done+=n; if((DateTime.UtcNow-last).TotalMilliseconds<250) return; last=DateTime.UtcNow;
            progress?.Report(asset.Size>0?$"Downloading {asset.Name} · {done*100/asset.Size}%":$"Downloading {asset.Name} · {done/1048576d:N1} MB");
        },maxBytes:512L*1024*1024);
        await using(var s=File.OpenRead(target))
        {
            var actual=Convert.ToHexString(await SHA256.HashDataAsync(s,ct)).ToLowerInvariant();
            if(actual!=expected) { s.Close(); File.Delete(target); throw new InvalidDataException($"{asset.Name} failed its checksum (expected {expected[..12]}…, got {actual[..12]}…). Nothing was installed."); }
        }
        log.LogInformation("Downloaded and verified {Asset} sha256={Sha}",asset.Name,expected);
        return target;
    }

    /// <summary>
    /// Installs or updates LTK Manager silently. Its installer is per-machine (Program Files), so Windows shows one
    /// administrator prompt here. Patching afterwards never needs one: the signed patcher runs in place.
    /// </summary>
    public async Task<string> InstallLtkAsync(ReleaseInfo release,IProgress<string>? progress,CancellationToken ct)
    {
        var asset=LtkSetupAsset(release) ?? throw new InvalidDataException($"LTK Manager {release.Tag} has no Windows installer asset.");
        var setup=await DownloadVerifiedAsync(asset,progress,ct);
        if(!Authenticode.IsTrusted(setup)) throw new InvalidDataException("LTK Manager's installer isn't validly signed, so it won't be run.");
        progress?.Report("Installing LTK Manager · approve the Windows prompt");
        Process? p;
        try { p=Process.Start(new ProcessStartInfo(setup,"/S"){UseShellExecute=true,Verb="runas"}); }
        catch(Win32Exception e) when(e.NativeErrorCode==1223) { throw new OperationCanceledException("The administrator prompt was declined, so LTK Manager wasn't installed."); }
        if(p is null) throw new IOException("LTK Manager's installer did not start.");
        using(p)
        {
            await p.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromMinutes(10),ct);
            if(p.ExitCode!=0) throw new IOException($"LTK Manager's installer exited with code {p.ExitCode}.");
        }
        try { File.Delete(setup); } catch(IOException) { }
        var patcher=native.ResolvePatcher(refresh:true);
        if(patcher is not { Signed:true }) throw new IOException("LTK Manager installed, but its signed patcher wasn't found afterwards. Restart MadokaChanger and check Settings.");
        log.LogInformation("LTK Manager {Version} installed; using {Patcher}",release.Tag,patcher.Description);
        return $"LTK Manager {release.Version.ToString(3)} installed · using its signed patcher";
    }

    /// <summary>Downloads the new MadokaChanger setup. The caller closes the app and runs it with <see cref="LaunchAppSetup"/>.</summary>
    public async Task<string> DownloadAppUpdateAsync(ReleaseInfo release,IProgress<string>? progress,CancellationToken ct)
    {
        var asset=AppSetupAsset(release) ?? throw new InvalidDataException($"MadokaChanger {release.Tag} has no setup file attached.");
        return await DownloadVerifiedAsync(asset,progress,ct);
    }

    /// <summary>
    /// Runs the per-user setup silently. /RELAUNCH=1 tells it to start MadokaChanger again when it finishes,
    /// and /SKIPLTK=1 skips its dependency step because this app just handled LTK Manager itself.
    /// </summary>
    public static void LaunchAppSetup(string setup)=>
        Process.Start(new ProcessStartInfo(setup,"/SILENT /SUPPRESSMSGBOXES /NORESTART /RELAUNCH=1 /SKIPLTK=1"){UseShellExecute=true})?.Dispose();
}
