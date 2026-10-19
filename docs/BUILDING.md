# Building and development

Install .NET **10 LTS SDK**, Rust stable, and a native Windows C/C++ toolchain. The normal MSVC route uses Visual Studio Build Tools with Desktop development with C++ and a Windows SDK.

```powershell
./scripts/build-native.ps1
./scripts/prepare-backend.ps1
dotnet build MadokaChanger.sln -c Release
dotnet test tests/MadokaChanger.Tests -c Release
dotnet run --project src/MadokaChanger.App
./scripts/publish.ps1
```

`native/Cargo.lock` is committed and must be used with `--locked`. The verified build used Rust 1.99.1, the GNU target, w64devkit 2.10.1 and `RUSTFLAGS='-C link-self-contained=yes'`. The MSVC recipe is supplied but was not exercised in this environment. `scripts/build-native.ps1 -Target x86_64-pc-windows-gnu` selects GNU explicitly when its toolchain is installed. The binary has no extra GNU DLL requirements in the verified build.

The complete backend is copied beside the app under `backend/`: `madokachanger-ltk.exe`, `ltk_patcher_host.exe`, and `ltk_patcher_dll.dll`. The publish script prepares pinned, hash-verified patcher files automatically, removes the upstream signatures as required by the LTK Patcher License, and records their provenance in `backend-manifest.json`. The supplied source package includes these prepared files. Publication fails if any required backend file is missing.

## Features and behavior

- Alphabetical, searchable, row-virtualized champion portraits; favorites and saved-selection badges. Browser search, filters and the browser control's scroll state remain when returning from detail. Classic/Jade champion entries are excluded from both champion lists and cached metadata. Champion search is case-insensitive.
- Metadata-derived splash and tile images, horizontal skin selection, real multi-color chroma swatches and previews. A chroma keeps its parent's splash. An explicit form package with its matching repository preview can use that preview.
- Exactly one desired variant per champion; safe JSON persistence. Missing or ambiguous packages are not replaced with a different variant.
- Full, commit-pinned installs of the supported skin library, excluding the `classic/` category, with six downloads at a time, Git blob verification, LFS materialization, ZIP CRC and WAD descriptor checks. A changed commit downloads **all** packages again, including unchanged ones. An unchanged commit returns “Already up to date.”
- Staged validation and atomic active-pointer replacement. Failures and cancellation retain the working library and do not change selections, favorites or custom packages. Old library generations remain for recovery; see storage documentation before removing unused generations.
- Separate custom imports for `.fantome`, recognized Fantome `.zip`, and upstream `.modpkg`. Duplicate detection uses SHA-256. Declared champion aliases are used when present; otherwise the selected champion is the explicit assignment. Multi-champion packages are refused with a useful error.
- File conflicts are shown in Custom Skins. `Reject` blocks conflicting configurations. `PreferRepository` or `PreferCustom` explicitly chooses cross-source priority. Same-source conflicts require disabling one conflicting package.
- The same native overlay builder handles repository and custom content. The installer, import/uninstall and a running patcher session share a mutation lease. Browsing and saving desired selections remain available.
- Dark reusable styles, visible keyboard focus, WPF scaling, resizing, asynchronous images and reduced-motion preferences. Ctrl+I imports custom skins; Escape returns to the browser; arrow keys navigate a focused skin carousel.
- Windowed layouts use a compact sidebar, flexible champion cards and stacked settings panels. The skin preview, horizontal skin strip, chroma color circles and Apply/Reset actions remain on-screen without vertical page scrolling. The initial window fits the available desktop work area; PerMonitorV2 DPI awareness is retained.

## Verified upstream snapshot and limits

See [UPSTREAM.md](UPSTREAM.md) for commits, formats and protocol details. MadokaChanger excludes Classic/Jade champions and all `classic/` packages. At the inspected revision, the supported inventory contains **9,063 packages**: **9,047** exact selectable mappings and **16** unavailable variants with explicit reasons. Existing cached libraries are filtered immediately, even when the upstream commit has not changed. Previously downloaded excluded files are left inactive on disk; future installations do not fetch them.

Dark mode is forced for the WPF application, in addition to the dark Madoka colors, controls and title bar. It does not change the Windows system theme.

The repository contains no standard redistribution license for its skin assets. Packages and Riot images are fetched on the user's machine and are not bundled with the executable. MadokaChanger is an independent fan project, not affiliated with Riot Games or League Toolkit. No particular server compatibility or in-game behavior is promised.

## Verification

See [VERIFICATION.md](../verification/VERIFICATION.md). Automated tests cover transactions, mapping, archive validation, conflicts, protocol parsing and persistence. Live tests exercised CommunityDragon assets, the full repository installation, unchanged-update handling, real Fantome/modpkg import and a native overlay build against a synthetic WAD. Off-screen WPF rendering checks views and bindings without showing a desktop window.

**Live patcher scanning, attachment, in-game rendering, interactive keyboard/mouse behavior and monitor-DPI transitions were not tested.** Desktop automation was stopped at the user's request while a game was active. No verification action attached to or changed that game.

Run diagnostics with an isolated data root:

```powershell
dotnet run --project tools/MadokaChanger.Verify -- catalog C:/temp/MadokaChangerQA native/bin/madokachanger-ltk.exe
dotnet run --project tools/MadokaChanger.Verify -- install C:/temp/MadokaChangerQA native/bin/madokachanger-ltk.exe
dotnet run --project tools/MadokaChanger.Verify -- custom C:/temp/MadokaChangerQA native/bin/madokachanger-ltk.exe
dotnet run --project tools/MadokaChanger.Verify -- native C:/temp/MadokaChangerQA native/bin/madokachanger-ltk.exe
dotnet run --project tools/MadokaChanger.UiVerify -- C:/temp/MadokaChangerQA verification/screenshots
```

The separate `overlay` diagnostic requires a closed game and an installed library. The optional `host` diagnostic configures and stops the host without sending `start scan`; it was deliberately not run during this task.

## Configuration, logging and recovery

New installations use `%LOCALAPPDATA%/MadokaChanger`. If the previous SoulGem data folder already contains settings and the new folder does not, the app reuses that existing folder to preserve selections, library paths and custom packages without moving files. Classic selections and favorites are removed; previously enabled Classic custom entries are disabled and hidden. See [STORAGE.md](STORAGE.md). `MADOKACHANGER_GITHUB_TOKEN` is an optional process environment variable for a higher GitHub API quota; no token is written to settings or logs. `MADOKACHANGER_DATA_ROOT` overrides the data root for isolated development runs.

For an interrupted or failed install, use **Retry** or **Update** again. An error before activation leaves the old active pointer unchanged. A cancelled install removes its owned staging directory. If a remote request is rate-limited, retry after GitHub's reset time. If disk space is low, free space outside the active library or choose a different library directory in Settings. The updater preflights the supported inventory size with a staging margin; real LFS payloads can require more space than their small pointer files advertise.

JSONL logs are under `logs/`. Settings offers View Log and Open Logs. Logs include paths and diagnostics; review them before sharing.
