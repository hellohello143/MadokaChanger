using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;

namespace MadokaChanger.Core;

/// <summary>Hook flag bits sent with <c>config flags N</c>, mirroring ltk-manager's <c>hook_flags</c>.</summary>
public static class HookFlags
{
    /// <summary>CSLOL_HOOK_OPT_OUT_AH_V1: a failed anti-skinhack WAD scan becomes a warning instead of blocking the overlay.</summary>
    public const int OptOutAntiSkinhack=4;
    /// <summary>
    /// The library is made of repacked official skins, which the anti-skinhack scan always flags
    /// ("skin0 is another skin"). ltk-manager only loads them with enforcement off, so that is the default here too.
    /// </summary>
    public static int For(AppSettings settings)=>settings.EnforceSkinhackScan?0:OptOutAntiSkinhack;
}

/// <summary>The patcher host and hook DLL a session runs with.</summary>
public sealed record PatcherBinaries(string Host,string Dll,bool Signed,string Source)
{
    public string Directory=>Path.GetDirectoryName(Host)!;
    public string Description=>Signed?$"signed patcher from {Source} ({Directory})":$"bundled patcher, unsigned ({Directory})";
}

public static class PatcherLocator
{
    public const string HostName="ltk_patcher_host.exe";
    public const string DllName="ltk_patcher_dll.dll";

    /// <summary>
    /// The official LTK Manager install ships the same host/DLL build with League Toolkit's signature intact.
    /// Running those files in place redistributes nothing, and they are the copy known to inject on this machine.
    /// </summary>
    public static PatcherBinaries? FindInstalledLtkManager()
    {
        if(!OperatingSystem.IsWindows()) return null;
        foreach(var directory in LtkManagerDirectories())
        {
            var host=Path.Combine(directory,HostName); var dll=Path.Combine(directory,DllName);
            if(File.Exists(host) && File.Exists(dll) && Authenticode.IsTrusted(host) && Authenticode.IsTrusted(dll))
                return new(host,dll,true,"LTK Manager");
        }
        return null;
    }

    private static IEnumerable<string> LtkManagerDirectories()
    {
        var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates=new List<string>();
        if(OperatingSystem.IsWindows())
        {
            foreach(var hive in new[]{Registry.CurrentUser,Registry.LocalMachine})
            foreach(var view in new[]{@"Software\Microsoft\Windows\CurrentVersion\Uninstall",@"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"})
            {
                try
                {
                    using var root=hive.OpenSubKey(view); if(root is null) continue;
                    foreach(var name in root.GetSubKeyNames())
                    {
                        using var app=root.OpenSubKey(name);
                        if(app?.GetValue("DisplayName") is not string display || !display.Contains("LTK Manager",StringComparison.OrdinalIgnoreCase)) continue;
                        if(app.GetValue("InstallLocation") is string location && location.Length>0) candidates.Add(location.Trim('"'));
                        if(app.GetValue("DisplayIcon") is string icon && icon.Length>0) candidates.Add(Path.GetDirectoryName(icon.Split(',')[0].Trim('"'))??"");
                    }
                }
                catch(Exception e) when(e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
            }
        }
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"LTK Manager"));
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Programs","LTK Manager"));
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"LTK Manager"));
        foreach(var c in candidates)
        {
            if(string.IsNullOrWhiteSpace(c)) continue;
            string full; try { full=Path.GetFullPath(c); } catch(Exception e) when(e is ArgumentException or NotSupportedException or PathTooLongException) { continue; }
            if(seen.Add(full)) yield return full;
        }
    }
}

/// <summary>
/// When the host needs <c>--elevate</c>, the same rule as ltk-manager's <c>elevation_decision</c>: only when the user opts in
/// or League is configured to run as administrator, and never when this app is already elevated.
/// </summary>
public static class Elevation
{
    public static bool Decide(bool optIn,bool leagueRunsAsAdmin,bool appIsElevated)=>!appIsElevated && (optIn||leagueRunsAsAdmin);

    public static bool AppIsElevated()
    {
        if(!OperatingSystem.IsWindows()) return false;
        using var identity=WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>A RUNASADMIN AppCompatFlags layer on any League/Riot executable, in HKCU or HKLM.</summary>
    public static bool LeagueRunsAsAdmin()
    {
        if(!OperatingSystem.IsWindows()) return false;
        const string key=@"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers";
        foreach(var hive in new[]{Registry.CurrentUser,Registry.LocalMachine})
        {
            try
            {
                using var layers=hive.OpenSubKey(key); if(layers is null) continue;
                foreach(var exe in layers.GetValueNames())
                {
                    var name=Path.GetFileName(exe);
                    if(!name.StartsWith("League",StringComparison.OrdinalIgnoreCase) && !name.StartsWith("Riot",StringComparison.OrdinalIgnoreCase)) continue;
                    if(layers.GetValue(exe) is string value && value.Contains("RUNASADMIN",StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
            catch(Exception e) when(e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        }
        return false;
    }
}

public enum DllLineKind { Other, Fatal, Warning, Redirected, InitDone }
public sealed record DllVerdict(DllLineKind Kind,string Text);

/// <summary>Reads the DLL's own log lines the way ltk-manager's <c>dll_lines.rs</c> does.</summary>
public static class DllLines
{
    public static DllVerdict Classify(string level,string message)
    {
        var text=StripTarget(message).Trim();
        var isError=level.Equals("error",StringComparison.OrdinalIgnoreCase);
        // With OPT_OUT_AH_V1 the DLL still logs "AH wad scan failed ... (opted out)", at WARN. Only an ERROR blocks the overlay.
        if(message.Contains("WAD scan failed",StringComparison.OrdinalIgnoreCase))
            return isError
                ? new(DllLineKind.Fatal,"League's anti-skinhack scan blocked "+WadOf(message)+". Turn off \"Enforce anti-skinhack scan\" in Settings to load library skins.")
                : new(DllLineKind.Other,text);
        if(text.StartsWith("end of life reached",StringComparison.OrdinalIgnoreCase))
            return new(DllLineKind.Fatal,"This patcher build has expired ("+text+"). Update LTK Manager or MadokaChanger's backend.");
        if(text.StartsWith("overlay verification failed, disabling overlay",StringComparison.OrdinalIgnoreCase))
            return new(DllLineKind.Fatal,"The patcher disabled the overlay: "+text["overlay verification failed, disabling overlay".Length..].TrimStart(':',' '));
        if(text.Contains("AH init failed",StringComparison.OrdinalIgnoreCase))
            return new(DllLineKind.Fatal,"The patcher could not initialise: "+text);
        if(text.Equals("joined too late, not overlaying",StringComparison.OrdinalIgnoreCase))
            return new(DllLineKind.Warning,"Attached too late for this game, so it stays unmodded. Keep MadokaChanger running and your skins load from the next game.");
        if(text.StartsWith("lazy verification failed, not overlaying",StringComparison.OrdinalIgnoreCase))
            return new(DllLineKind.Warning,"Skipped one archive: "+text["lazy verification failed, not overlaying".Length..].TrimStart(':',' '));
        if(text.StartsWith("failed to install ",StringComparison.OrdinalIgnoreCase) && text.EndsWith(" hook",StringComparison.OrdinalIgnoreCase))
            return new(DllLineKind.Warning,"Patcher warning: "+text);
        if(text.StartsWith("redirected wad: ",StringComparison.OrdinalIgnoreCase))
            return new(DllLineKind.Redirected,LastSegment(text["redirected wad: ".Length..]));
        if(text.Equals("init done",StringComparison.OrdinalIgnoreCase))
            return new(DllLineKind.InitDone,text);
        return new(DllLineKind.Other,text);
    }

    private static string StripTarget(string message)
    {
        var m=message.TrimStart(); var i=m.IndexOf(": ",StringComparison.Ordinal);
        if(i>0 && m[..i].All(c=>char.IsAsciiLetterOrDigit(c)||c=='_'||c==':')) return m[(i+2)..];
        return m;
    }
    private static string LastSegment(string path)
    {
        var p=path.Trim(); var i=p.LastIndexOfAny(['/','\\']); return i>=0?p[(i+1)..]:p;
    }
    private static string WadOf(string message)
    {
        var i=message.LastIndexOf(" for ",StringComparison.Ordinal);
        return i>=0 && i+5<message.Length ? message[(i+5)..].Trim() : "a champion archive";
    }
}

internal static class Authenticode
{
    private static readonly Guid GenericVerifyV2=new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    /// <summary>WinVerifyTrust with no UI and no revocation lookup, the same call ltk-manager's diagnostics make.</summary>
    public static bool IsTrusted(string path)
    {
        if(!OperatingSystem.IsWindows()) return false;
        var file=Marshal.StringToHGlobalUni(path);
        var fileInfo=Marshal.AllocHGlobal(Marshal.SizeOf<FileInfo>());
        var data=Marshal.AllocHGlobal(Marshal.SizeOf<TrustData>());
        try
        {
            Marshal.StructureToPtr(new FileInfo { Size=(uint)Marshal.SizeOf<FileInfo>(),FilePath=file },fileInfo,false);
            var trust=new TrustData { Size=(uint)Marshal.SizeOf<TrustData>(),UiChoice=2,RevocationChecks=0,UnionChoice=1,File=fileInfo,StateAction=1 };
            Marshal.StructureToPtr(trust,data,false);
            var action=GenericVerifyV2;
            var result=WinVerifyTrust(IntPtr.Zero,ref action,data);
            trust=Marshal.PtrToStructure<TrustData>(data); trust.StateAction=2; Marshal.StructureToPtr(trust,data,true);
            WinVerifyTrust(IntPtr.Zero,ref action,data);
            return result==0;
        }
        catch(Exception e) when(e is DllNotFoundException or EntryPointNotFoundException) { return false; }
        finally { Marshal.FreeHGlobal(data); Marshal.FreeHGlobal(fileInfo); Marshal.FreeHGlobal(file); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct FileInfo { public uint Size; public IntPtr FilePath,FileHandle,KnownSubject; }
    [StructLayout(LayoutKind.Sequential)] private struct TrustData
    {
        public uint Size; public IntPtr PolicyCallbackData,SipClientData; public uint UiChoice,RevocationChecks,UnionChoice;
        public IntPtr File; public uint StateAction; public IntPtr StateData,UrlReference; public uint ProviderFlags,UiContext; public IntPtr SignatureSettings;
    }
    [DllImport("wintrust.dll",ExactSpelling=true,CharSet=CharSet.Unicode)] private static extern int WinVerifyTrust(IntPtr hwnd,ref Guid action,IntPtr data);
}
