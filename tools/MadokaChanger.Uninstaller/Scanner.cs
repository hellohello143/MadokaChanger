using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace MadokaChanger.Uninstaller;

internal enum ItemKind { RunningApp, UninstallEntry, ProgramFolder, Shortcut, DataFolder }

internal sealed class Finding
{
    public ItemKind Kind { get; set; }
    public string Title { get; set; } = "";
    public string Location { get; set; } = "";
    public int ProcessId { get; set; }
    // Uninstall entries
    public string UninstallExecutable { get; set; } = "";
    public string UninstallArguments { get; set; } = "";
    public RegistryHive Hive { get; set; }
    public RegistryView View { get; set; }
    public string RegistryKeyName { get; set; } = "";
    // A folder that failed the safety checks is listed but never deleted.
    public string? RefusedBecause { get; set; }

    public string KindLabel => Kind switch
    {
        ItemKind.RunningApp => "Running app",
        ItemKind.UninstallEntry => "Installed program",
        ItemKind.ProgramFolder => "Program folder",
        ItemKind.Shortcut => "Shortcut",
        _ => "Skins and settings",
    };

    public override string ToString() => $"{KindLabel}: {Title}" + (Location.Length > 0 && Location != Title ? $" [{Location}]" : "") + (RefusedBecause is null ? "" : $" (will not delete: {RefusedBecause})");
}

internal sealed class ScanResult
{
    public List<Finding> Items { get; } = new();
    public IEnumerable<Finding> Removable => Items.Where(i => i.Kind != ItemKind.DataFolder && i.RefusedBecause is null);
    public IEnumerable<Finding> DataFolders => Items.Where(i => i.Kind == ItemKind.DataFolder);
    public bool HasProgramItems => Removable.Any();
}

internal static class Scanner
{
    public const string ExeName = "MadokaChanger.exe";
    private const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    private static readonly string[] NameMarkers = { "MadokaChanger", "SoulGem" };

    public static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    public static string DefaultInstallFolder => Path.Combine(LocalAppData, "Programs", "MadokaChanger");

    // The data folders the app uses (current and pre-rename). Only ever deleted on explicit opt-in.
    public static IEnumerable<string> DataRoots => new[] { Path.Combine(LocalAppData, "MadokaChanger"), Path.Combine(LocalAppData, "SoulGem") };

    public static ScanResult Scan()
    {
        var result = new ScanResult();
        var folders = new List<string>();

        foreach (var p in FindProcesses()) result.Items.Add(p);

        foreach (var entry in FindUninstallEntries(folders)) result.Items.Add(entry);

        folders.Add(DefaultInstallFolder);
        var shortcuts = FindShortcuts(folders);
        foreach (var dir in ShallowPortableSearch()) folders.Add(dir);

        foreach (var folder in folders.Select(Normalize).Where(f => f.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(folder)) continue;
            var refusal = CheckProgramFolder(folder);
            // Silently skip non-program folders we only guessed at, but show refusals for real candidates.
            if (refusal == NotAProgramFolder && !string.Equals(folder, Normalize(DefaultInstallFolder), StringComparison.OrdinalIgnoreCase)) continue;
            result.Items.Add(new Finding { Kind = ItemKind.ProgramFolder, Title = folder, Location = folder, RefusedBecause = refusal });
        }

        result.Items.AddRange(shortcuts);

        foreach (var data in DataRoots.Where(Directory.Exists))
            result.Items.Add(new Finding { Kind = ItemKind.DataFolder, Title = data, Location = data });
        return result;
    }

    // ---------- processes ----------
    private static IEnumerable<Finding> FindProcesses()
    {
        var self = Process.GetCurrentProcess().Id;
        foreach (var name in new[] { "MadokaChanger", "SoulGem" })
        {
            Process[] processes;
            try { processes = Process.GetProcessesByName(name); } catch { continue; }
            foreach (var p in processes)
            {
                if (p.Id == self) continue;
                var path = ProcessPath(p.Id) ?? name + ".exe";
                yield return new Finding { Kind = ItemKind.RunningApp, Title = $"{name}.exe (process {p.Id})", Location = path, ProcessId = p.Id };
            }
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int id);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);

    public static string? ProcessPath(int pid)
    {
        var handle = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var buffer = new StringBuilder(1024); var size = buffer.Capacity;
            return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString(0, size) : null;
        }
        finally { CloseHandle(handle); }
    }

    // ---------- Windows "Installed apps" entries ----------
    private static IEnumerable<Finding> FindUninstallEntries(List<string> folders)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var found = new List<Finding>();
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = root.OpenSubKey(UninstallPath);
                if (uninstall is null) continue;
                foreach (var keyName in uninstall.GetSubKeyNames())
                {
                    using var key = uninstall.OpenSubKey(keyName);
                    var display = key?.GetValue("DisplayName") as string;
                    if (key is null || display is null || !NameMarkers.Any(m => display.IndexOf(m, StringComparison.OrdinalIgnoreCase) >= 0)) continue;
                    var quiet = key.GetValue("QuietUninstallString") as string;
                    var normal = key.GetValue("UninstallString") as string;
                    var command = !string.IsNullOrWhiteSpace(quiet) ? quiet! : normal ?? "";
                    // HKCU is shared by both registry views; report each real entry once.
                    if (!seen.Add(hive + "|" + keyName + "|" + command)) continue;

                    var innoPath = key.GetValue("Inno Setup: App Path") as string;
                    var location = key.GetValue("InstallLocation") as string;
                    foreach (var dir in new[] { innoPath, location }) if (!string.IsNullOrWhiteSpace(dir)) folders.Add(dir!);

                    SplitCommand(command, out var exe, out var args);
                    var isInno = innoPath != null || Path.GetFileName(exe).StartsWith("unins", StringComparison.OrdinalIgnoreCase);
                    if (isInno) args = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART";
                    else if (Path.GetFileName(exe).Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase))
                        args = args.Replace("/I", "/X").Replace("/i", "/X") + " /qn /norestart";

                    var version = key.GetValue("DisplayVersion") as string;
                    found.Add(new Finding
                    {
                        Kind = ItemKind.UninstallEntry,
                        Title = display + (string.IsNullOrWhiteSpace(version) ? "" : " " + version),
                        Location = (hive == RegistryHive.CurrentUser ? "HKCU" : "HKLM") + "\\" + UninstallPath + "\\" + keyName,
                        UninstallExecutable = exe, UninstallArguments = args,
                        Hive = hive, View = view, RegistryKeyName = keyName,
                    });
                }
            }
            catch (Exception) { /* unreadable hive/view: nothing to report from it */ }
        }
        return found;
    }

    public static void SplitCommand(string command, out string exe, out string args)
    {
        command = (command ?? "").Trim();
        if (command.StartsWith("\""))
        {
            var end = command.IndexOf('"', 1);
            exe = end > 0 ? command.Substring(1, end - 1) : command.Trim('"');
            args = end > 0 ? command.Substring(end + 1).Trim() : "";
            return;
        }
        var dot = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        exe = dot >= 0 ? command.Substring(0, dot + 4) : command;
        args = dot >= 0 ? command.Substring(dot + 4).Trim() : "";
    }

    // ---------- shortcuts and the portable copies they point at ----------
    private static IEnumerable<string> ShortcutRoots()
    {
        yield return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms);
    }

    private static List<Finding> FindShortcuts(List<string> folders)
    {
        var list = new List<Finding>();
        foreach (var root in ShortcutRoots().Where(r => !string.IsNullOrEmpty(r) && Directory.Exists(r)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var lnk in SafeFiles(root, "*.lnk", root.IndexOf("Programs", StringComparison.OrdinalIgnoreCase) >= 0 ? 4 : 0))
            {
                var target = ResolveShortcut(lnk) ?? "";
                var pointsAtApp = Path.GetFileName(target).Equals(ExeName, StringComparison.OrdinalIgnoreCase);
                var named = Path.GetFileName(lnk).StartsWith("MadokaChanger", StringComparison.OrdinalIgnoreCase);
                if (!pointsAtApp && !named) continue;
                if (named && !pointsAtApp && target.Length > 0) continue; // named like us but launches something else: leave it
                if (pointsAtApp) { var dir = Path.GetDirectoryName(target); if (!string.IsNullOrEmpty(dir)) folders.Add(dir!); }
                list.Add(new Finding { Kind = ItemKind.Shortcut, Title = lnk, Location = target });
            }
        }
        return list;
    }

    private static IEnumerable<string> SafeFiles(string dir, string pattern, int depth)
    {
        string[] files = Array.Empty<string>();
        try { files = Directory.GetFiles(dir, pattern); } catch { }
        foreach (var f in files) yield return f;
        if (depth <= 0) yield break;
        string[] subs = Array.Empty<string>();
        try { subs = Directory.GetDirectories(dir); } catch { }
        foreach (var s in subs.Where(s => !IsReparsePoint(s)))
            foreach (var f in SafeFiles(s, pattern, depth - 1)) yield return f;
    }

    public static string? ResolveShortcut(string lnk)
    {
        object? shell = null, shortcut = null;
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type is null) return null;
            shell = Activator.CreateInstance(type);
            shortcut = type.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
            return shortcut?.GetType().InvokeMember("TargetPath", BindingFlags.GetProperty, null, shortcut, null) as string;
        }
        catch { return null; }
        finally
        {
            if (shortcut != null && Marshal.IsComObject(shortcut)) Marshal.ReleaseComObject(shortcut);
            if (shell != null && Marshal.IsComObject(shell)) Marshal.ReleaseComObject(shell);
        }
    }

    // Unzipped portable copies usually live a level or two under Desktop, Downloads or Documents.
    private static IEnumerable<string> ShallowPortableSearch()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roots = new[] { Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Path.Combine(profile, "Downloads"), Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) };
        foreach (var root in roots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
            foreach (var dir in Children(root))
            {
                if (File.Exists(Path.Combine(dir, ExeName))) { yield return dir; continue; }
                foreach (var inner in Children(dir)) if (File.Exists(Path.Combine(inner, ExeName))) yield return inner;
            }
    }

    private static IEnumerable<string> Children(string dir)
    {
        try { return Directory.GetDirectories(dir).Where(d => !IsReparsePoint(d)).ToArray(); } catch { return Array.Empty<string>(); }
    }

    public static bool IsReparsePoint(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; } catch { return true; }
    }

    // ---------- safety ----------
    public const string NotAProgramFolder = "it does not contain MadokaChanger.exe";

    public static string Normalize(string path)
    {
        try { return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'))).TrimEnd('\\'); }
        catch { return ""; }
    }

    private static IEnumerable<string> ProtectedFolders()
    {
        var special = new[]
        {
            Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.Desktop,
            Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.ApplicationData,
            Environment.SpecialFolder.CommonApplicationData, Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86,
            Environment.SpecialFolder.Windows, Environment.SpecialFolder.System, Environment.SpecialFolder.StartMenu, Environment.SpecialFolder.Programs,
            Environment.SpecialFolder.CommonStartMenu, Environment.SpecialFolder.CommonPrograms, Environment.SpecialFolder.CommonDesktopDirectory,
            Environment.SpecialFolder.MyPictures, Environment.SpecialFolder.MyMusic, Environment.SpecialFolder.MyVideos,
        };
        foreach (var s in special) { var p = Environment.GetFolderPath(s); if (!string.IsNullOrEmpty(p)) yield return p; }
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        yield return Path.Combine(profile, "Downloads");
        yield return Path.Combine(profile, "OneDrive");
        yield return Path.Combine(LocalAppData, "Programs");
        var users = Directory.GetParent(profile)?.FullName; if (users != null) yield return users;
        foreach (var data in DataRoots) yield return data;
    }

    /// <summary>Returns null when <paramref name="folder"/> is clearly a MadokaChanger program folder that is safe to delete.</summary>
    public static string? CheckProgramFolder(string folder)
    {
        var full = Normalize(folder);
        if (full.Length == 0) return "the path is not valid";
        var root = Path.GetPathRoot(full)?.TrimEnd('\\') ?? "";
        if (full.Length <= root.Length + 1 || string.Equals(full, root, StringComparison.OrdinalIgnoreCase)) return "it is a drive root";
        foreach (var p in ProtectedFolders().Select(Normalize).Where(p => p.Length > 0))
        {
            if (string.Equals(full, p, StringComparison.OrdinalIgnoreCase)) return "it is a system or personal folder";
            if (IsInside(p, full)) return "it contains a system or personal folder";
        }
        foreach (var data in DataRoots) if (IsInside(Normalize(data), full)) return "your skins and settings are inside it";
        if (IsReparsePoint(full)) return "it is a link to another folder";
        if (!File.Exists(Path.Combine(full, ExeName))) return NotAProgramFolder;
        var self = Normalize(Process.GetCurrentProcess().MainModule?.FileName ?? "");
        if (self.Length > 0 && IsInside(self, full)) return "this uninstaller is running from it; move the uninstaller somewhere else first";
        return null;
    }

    /// <summary>True when <paramref name="child"/> is strictly inside <paramref name="parent"/>.</summary>
    public static bool IsInside(string child, string parent)
    {
        if (child.Length == 0 || parent.Length == 0) return false;
        return child.StartsWith(parent.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
    }
}
