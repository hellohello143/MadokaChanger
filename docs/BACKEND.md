# Backend integration

## Components

1. **Metadata and asset paths**: CommunityDragon JSON and mapped URLs, read by C#.
2. **Package validation**: C# ZIP/path/WAD checks, or C# checks against the upstream modpkg extraction plan and chunk table exposed by the native shim. Untrusted native decoding is memory-bounded by a Windows Job Object. The shim is killed when cancellation closes its operation or the application exits.
3. **Package preparation**: upstream `ProjectImporter` with `FantomeImporter` or `ModpkgImporter` writes projects into application-owned storage. Arbitrary ZIPs are not handed to a guessed patcher CLI.
4. **Overlay construction**: upstream `ltk_overlay::OverlayBuilder` reads the installed game's WADs and produces an overlay elsewhere. First-party configuration, priority, transaction and session decisions stay in C#.
5. **Patcher host**: the C# adapter launches `ltk_patcher_host.exe` from an installed LTK Manager when one is found and signed, else from the local backend directory, uses its verified stdin/stdout protocol, monitors status and DLL logs, and owns only that process session.

There is no invented upstream C# patching API. `madokachanger-ltk.exe` is an application-owned serialization shim with a documented JSON contract. League Mod Toolkit's current deprecated authoring CLI has no overlay-build command, so MadokaChanger does not invent one.

## Included backend

The Windows distribution includes the native adapter, host and DLL under `backend/` beside MadokaChanger.exe. The application resolves the host and DLL relative to its native adapter, independently of the current working directory or previously saved external backend paths. There is no backend picker and no first-run backend download. An installed LTK Manager is optional but preferred: its signed host and DLL are used when present, because the bundled unsigned copies can fail to install their hook (`SetWindowsHookEx failed`).

For developers, `scripts/prepare-backend.ps1` obtains both files from the pinned upstream commit, checks their SHA-256 hashes, removes upstream code signatures using Windows ImageHlp, and verifies that every executable section is unchanged. `backend/backend-manifest.json` records source URLs and before/after hashes. `scripts/publish.ps1` runs this preparation automatically; normal builds use the prepared copies included with the source package.

The adapter was written against LTK Manager commit `1a3d4f900054a739f833b6a028e87caa58455fbb`. Protocol changes, patcher expiry and game compatibility changes can require a backend/adapter update. Merely finding two files is a dependency-presence check, not a compatibility certification. Errors are surfaced and logged.

## Protocol

The process has redirected stdin, stdout and stderr and runs with its own folder as working directory. When LTK Manager is installed, its signed `ltk_patcher_host.exe` and `ltk_patcher_dll.dll` are run in place (found through the uninstall registry or `Program Files\LTK Manager`, and accepted only when WinVerifyTrust validates both); otherwise the bundled unsigned copies are used. `--elevate` follows ltk-manager's rule: only when **Run patcher as administrator** is on or League carries a `RUNASADMIN` compatibility layer, and never when MadokaChanger is already elevated. It sends, in order:

```text
config loglevel 16
config flags 4
config prefix <absolute-overlay-root-with-trailing-separator>
start scan
```

`flags 4` is `CSLOL_HOOK_OPT_OUT_AH_V1`, the same bit ltk-manager sets when its skinhack scan enforcement is off. Library packages are repacked official skins, which the anti-skinhack scan always flags (`skin0 is another skin`), so enforcement is off by default; **Enforce anti-skinhack scan** in Settings sends `flags 0` instead. Opted-out scan results arrive as WARN lines and are only logged. Startup remains `Starting` until the host reports an `injecting`, `injected` or `waiting` status. Each `redirected wad` line from the DLL updates the status to `In game · skins applied (N archives redirected)`, which is the in-game confirmation. Host errors, failed injection, ERROR-level scan failures, expired backends and disabled overlays produce an error state and stop the session; `joined too late` and per-archive lazy-verification skips are shown as warnings and keep the session running.

Stop sends `stop`, closes stdin, waits up to five seconds and terminates only the owned host process if graceful exit fails. The process Job Object prevents an orphaned owned host after application exit. A host cannot be forcibly started against an open game by the normal app workflow; close the game first. Original WADs are never overwritten by the application.

## Shim contract, protocol 1

One JSON request on stdin, then EOF. Progress events are JSON lines on stdout; a terminal response is `{"ok":true,"result":...}` or `{"ok":false,"error":"..."}`. Nonzero exit is always failure. Supported operations:

- `version`: protocol and pinned league-mod revision.
- `probe {package}`: modpkg metadata, extraction paths, limits, override hashes and compressed chunk records, allowing C# to validate before extraction.
- `import {package,output}`: verified upstream package import.
- `build {game,overlay,state,mods:[{id,path}]}`: verified upstream overlay build. Input mods are already ordered by the C# conflict resolver; upstream priority is first-mod-wins.
- `pack {project,output}`: upstream modpkg packing, used by the reproducible native verification fixture.

## Redistribution

Read `licenses/LTK-PATCHER-LICENSE.md` before packaging host/DLL binaries. When distributed outside an unmodified official LTK Manager release artifact, the license requires removing League Toolkit's code signature, and using your own certificate if signing. It permits a written exception from League Toolkit. Do not imply endorsement. This distribution includes unsigned copies of both binaries under those terms, with the upstream signatures removed. Executable sections, integrity checks, expiry behavior and the host protocol are unchanged. MadokaChanger is an independent application and is not endorsed by League Toolkit.

The native libraries use Apache-2.0 and their transitive dependency licenses. The C# asset library and Manager source were inspected as references, not linked or copied into this application. The separate GPL license of those repositories is recorded in UPSTREAM.md. Third-party notices include the dependency inventory and available license texts; retain them with any binary distribution.
