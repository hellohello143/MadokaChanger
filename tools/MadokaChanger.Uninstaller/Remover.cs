using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Win32;

namespace MadokaChanger.Uninstaller;

internal sealed class RemovalReport
{
    public List<string> Lines { get; } = new();
    public int Removed { get; set; }
    public int Failed { get; set; }
    public bool Cancelled { get; set; }
    public void Ok(string line) { Removed++; Lines.Add("Removed: " + line); }
    public void Fail(string line) { Failed++; Lines.Add("Failed: " + line); }
    public void Note(string line) => Lines.Add(line);
}

internal static class Remover
{
    public static RemovalReport Run(ScanResult scan, bool deleteData, Action<string>? progress = null)
    {
        var report = new RemovalReport();
        void Step(string text) { progress?.Invoke(text); }

        // 1. Running copies hold files open; close them first.
        foreach (var app in scan.Items.Where(i => i.Kind == ItemKind.RunningApp))
        {
            Step("Closing " + app.Title + "...");
            if (StopProcess(app.ProcessId)) report.Ok(app.Title); else report.Fail(app.Title + " is still running");
        }

        // 2. Registered installs: let their own uninstaller remove them (it also removes the Windows entry).
        foreach (var entry in scan.Items.Where(i => i.Kind == ItemKind.UninstallEntry))
        {
            Step("Uninstalling " + entry.Title + "...");
            RunUninstaller(entry, report);
        }

        // 3. Leftover and portable program folders, all verified at scan time to contain MadokaChanger.exe.
        foreach (var folder in scan.Items.Where(i => i.Kind == ItemKind.ProgramFolder))
        {
            if (folder.RefusedBecause != null) { report.Note($"Kept {folder.Location}: {folder.RefusedBecause}"); continue; }
            if (!Directory.Exists(folder.Location)) { report.Ok(folder.Location + " (already gone)"); continue; }
            // Re-check everything except the exe, which the uninstaller in step 2 may already have removed.
            var refusal = Scanner.CheckProgramFolder(folder.Location);
            if (refusal != null && refusal != Scanner.NotAProgramFolder) { report.Note($"Kept {folder.Location}: {refusal}"); continue; }
            Step("Deleting " + folder.Location + "...");
            if (DeleteFolder(folder.Location, out var error)) report.Ok(folder.Location); else report.Fail(folder.Location + ": " + error);
        }

        // 4. Shortcuts.
        foreach (var lnk in scan.Items.Where(i => i.Kind == ItemKind.Shortcut))
        {
            if (!File.Exists(lnk.Title)) { report.Ok(lnk.Title + " (already gone)"); continue; }
            try
            {
                File.SetAttributes(lnk.Title, FileAttributes.Normal); File.Delete(lnk.Title); report.Ok(lnk.Title);
                // The installer's Start Menu group folder: remove it once it's empty, never anything else.
                var group = Path.GetDirectoryName(lnk.Title);
                if (group != null && Path.GetFileName(group).Equals("MadokaChanger", StringComparison.OrdinalIgnoreCase) &&
                    Directory.Exists(group) && !Directory.EnumerateFileSystemEntries(group).Any())
                    Directory.Delete(group, false);
            }
            catch (UnauthorizedAccessException) { report.Fail(lnk.Title + ": an administrator account is needed to remove this all-users shortcut"); }
            catch (Exception e) { report.Fail(lnk.Title + ": " + e.Message); }
        }

        // 5. Skins and settings: only when the user explicitly asked, and only the app's own data folders.
        foreach (var data in scan.DataFolders)
        {
            if (!deleteData) { report.Note("Kept your skins and settings: " + data.Location); continue; }
            if (!Scanner.DataRoots.Any(d => string.Equals(Scanner.Normalize(d), Scanner.Normalize(data.Location), StringComparison.OrdinalIgnoreCase)))
            { report.Note("Kept " + data.Location + ": not a MadokaChanger data folder"); continue; }
            Step("Deleting skins and settings...");
            if (DeleteFolder(data.Location, out var error)) report.Ok(data.Location); else report.Fail(data.Location + ": " + error);
        }
        return report;
    }

    private static bool StopProcess(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            if (p.HasExited) return true;
            try { p.CloseMainWindow(); } catch { }
            if (p.WaitForExit(5000)) return true;
            p.Kill();
            return p.WaitForExit(10000);
        }
        catch (ArgumentException) { return true; } // already exited
        catch (Exception) { return false; }
    }

    private static void RunUninstaller(Finding entry, RemovalReport report)
    {
        var exe = entry.UninstallExecutable;
        var isMsi = Path.GetFileName(exe).Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase);
        if (exe.Length == 0 || (!isMsi && !File.Exists(exe)))
        {
            // The uninstaller is gone (folder deleted by hand): just clear the stale Windows entry.
            if (DeleteRegistryEntry(entry, out var why)) report.Ok(entry.Title + " (stale entry, uninstaller was missing)");
            else report.Fail(entry.Title + ": stale entry could not be removed (" + why + ")");
            return;
        }
        try
        {
            // ShellExecute lets a machine-wide uninstaller show its own administrator prompt.
            using var p = Process.Start(new ProcessStartInfo(exe, entry.UninstallArguments) { UseShellExecute = true, WorkingDirectory = Path.GetTempPath() });
            if (p is null) { report.Fail(entry.Title + ": the uninstaller did not start"); return; }
            if (!p.WaitForExit((int)TimeSpan.FromMinutes(5).TotalMilliseconds)) { report.Fail(entry.Title + ": the uninstaller is still running after 5 minutes"); return; }
            // Inno's uninstaller hands off to a temp copy; give it a moment to finish deleting.
            for (var i = 0; i < 30 && EntryExists(entry); i++) Thread.Sleep(500);
            if (p.ExitCode == 0 || !EntryExists(entry)) report.Ok(entry.Title);
            else report.Fail($"{entry.Title}: the uninstaller exited with code {p.ExitCode}");
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223) { report.Fail(entry.Title + ": the administrator prompt was declined"); }
        catch (Exception e) { report.Fail(entry.Title + ": " + e.Message); }
    }

    private static bool EntryExists(Finding entry)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(entry.Hive, entry.View);
            using var key = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + entry.RegistryKeyName);
            return key != null;
        }
        catch { return false; }
    }

    private static bool DeleteRegistryEntry(Finding entry, out string error)
    {
        error = "";
        try
        {
            using var root = RegistryKey.OpenBaseKey(entry.Hive, entry.View);
            using var uninstall = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", true);
            uninstall?.DeleteSubKeyTree(entry.RegistryKeyName, false);
            return true;
        }
        catch (UnauthorizedAccessException) { error = "an administrator account is needed"; return false; }
        catch (Exception e) { error = e.Message; return false; }
    }

    private static bool DeleteFolder(string path, out string error)
    {
        error = "";
        for (var attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                if (!Directory.Exists(path)) return true;
                if (Scanner.IsReparsePoint(path)) { Directory.Delete(path, false); return true; } // remove the link, never its target
                ClearReadOnly(path);
                Directory.Delete(path, true);
                return true;
            }
            catch (Exception e) { error = e.Message; Thread.Sleep(700); }
        }
        return false;
    }

    private static void ClearReadOnly(string path)
    {
        // Walks only real subfolders: links inside the folder are never followed.
        string[] files = Array.Empty<string>(), dirs = Array.Empty<string>();
        try { files = Directory.GetFiles(path); dirs = Directory.GetDirectories(path); } catch { }
        foreach (var file in files)
        {
            try { var a = File.GetAttributes(file); if ((a & FileAttributes.ReadOnly) != 0) File.SetAttributes(file, a & ~FileAttributes.ReadOnly); } catch { }
        }
        foreach (var dir in dirs.Where(d => !Scanner.IsReparsePoint(d))) ClearReadOnly(dir);
    }
}
