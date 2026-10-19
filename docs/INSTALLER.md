# Installer and releases

MadokaChanger uses Inno Setup 6.7.3 or later (7.x works, including a per-user install under `%LOCALAPPDATA%\Programs`) to build a single Windows x64 installer. The source is [installer/MadokaChanger.iss](../installer/MadokaChanger.iss).

## Installation behavior

- Installs for the current user under `%LOCALAPPDATA%\Programs\MadokaChanger`, with a selectable destination.
- Bundles the self-contained .NET desktop runtime, native adapter, patcher host/DLL and license texts. No backend setup or runtime download is needed during installation.
- Adds a Start menu shortcut and offers a desktop shortcut, both using the chibi icon.
- Uses a dark wizard and a stable application ID so later installers upgrade the same application.
- Registers a Windows uninstaller. Settings, libraries and custom imports are stored separately and preserved on uninstall. Untracked files within the installation are left alone.
- Does not close running applications or restart Windows. Close MadokaChanger before upgrading.
- Launching the app on the final page is optional. Silent installations never launch it.

## Build

```powershell
./scripts/publish.ps1
./scripts/build-installer.ps1 -Version 1.1.0
```

Pass `-CompilerPath` or set `INNO_SETUP_COMPILER` when `ISCC.exe` is outside its standard location. `-PublishDirectory` and `-OutputDirectory` can override the build folders.

The build script verifies backend files against their provenance manifest. The installer excludes PDB files and embeds every remaining published file. Outputs are `artifacts/installer/MadokaChanger-Setup-<version>.exe`, the existing standalone old-copy cleanup tool `MadokaChanger-Uninstaller.exe`, and `SHA256SUMS.txt` covering both executables. The standalone tool is built from `tools/MadokaChanger.Uninstaller`; it is separate from the installer's registered per-install uninstaller.

For a fresh release directory, use `publish.ps1 -OutputDirectory <publish-path>` and pass that same path to `build-installer.ps1 -PublishDirectory <publish-path> -OutputDirectory <assets-path>`.

## Verify

```powershell
./scripts/test-installer.ps1 -Installer ./artifacts/installer/MadokaChanger-Setup-1.1.0.exe
```

Runs on Windows PowerShell 5.1 and PowerShell 7.

This installs into a unique `artifacts/installer-smoke/` folder, checks every payload file, runs an upgrade, then exercises the generated uninstaller. It verifies preservation of untracked files and separate data. `/SMOKETEST=1` disables shortcuts and the uninstall registry entry for this check; payload extraction is unchanged. The application is never launched. Logs remain in the test folder.

## Publish

Source belongs in Git; installers and checksums belong in Releases. Compiled dependencies are prepared by the build scripts. Local logs and diagnostic screenshots containing machine paths are excluded from Git. The README preview has no personal paths.

```powershell
git tag -a v1.1.0 -m "MadokaChanger 1.1.0"
git push origin main --tags
gh release create v1.1.0 ./artifacts/installer/MadokaChanger-Setup-1.1.0.exe ./artifacts/installer/MadokaChanger-Uninstaller.exe ./artifacts/installer/SHA256SUMS.txt --title "MadokaChanger 1.1.0" --notes-file ./docs/releases/v1.1.0.md --verify-tag
```

The installer is unsigned. Signing requires the distributor's own certificate. Upstream patcher signatures are removed as required by the LTK Patcher License and must not be reused for this distribution.
