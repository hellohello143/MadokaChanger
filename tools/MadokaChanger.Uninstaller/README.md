# MadokaChanger Uninstaller

A small standalone tool that removes old MadokaChanger copies (1.0.0 and 1.0.1, installed or portable) so the new version, which updates itself, can take over cleanly.

It matches by name, not version: any MadokaChanger it finds is removed, including 1.1.0 and later. Run it **before** installing the new version, not after.

It needs no admin rights and no extra downloads: it targets .NET Framework 4.8, which ships with Windows 10 and 11.

## What it removes

- Running MadokaChanger windows (it asks before closing them).
- Installed copies listed in Windows' installed apps (names containing "MadokaChanger" or "SoulGem"), using their own uninstaller.
- Leftover program folders, including unzipped portable copies found through shortcuts or up to two folders deep in Desktop, Downloads and Documents.
- MadokaChanger shortcuts on the Desktop and in the Start Menu, plus the empty Start Menu group folder.

## What it keeps

Your skins and settings in `%LOCALAPPDATA%\MadokaChanger` stay unless you tick **Also delete my skins and settings**.

It only deletes a folder that contains `MadokaChanger.exe`, and refuses drive roots, your user folder, Desktop, Documents, Downloads, AppData and Program Files.

## Command line

| Option | Effect |
| --- | --- |
| *(none)* | Opens the window. |
| `/list` | Prints what it would remove, deletes nothing. |
| `/silent` | Removes everything except your skins and settings. |
| `/silent /deletedata` | Removes everything, including skins and settings. |
| `/?` | Shows help. |

Exit code 0 means everything was removed; 1 means at least one item failed (for example an all-users shortcut that needs an administrator).

## Build

```powershell
dotnet build tools/MadokaChanger.Uninstaller -c Release
```

The exe lands in `tools/MadokaChanger.Uninstaller/bin/Release/net48/MadokaChanger-Uninstaller.exe`.
