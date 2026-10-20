# Changelog

## 1.2.0

- Added Duo Mode: private two-person parties with four-digit codes, local League champion detection and supported skin/chroma/form selection synchronization. The deployed HTTPS relay is included by default.
- Partner selections are temporary. Existing patcher sessions rebuild at safe points; active-game changes wait until the game closes, and stopped sessions remain stopped.
- Matching exact packages and library versions are required. Same-champion conflicts keep the local selection; imported custom packages are not shared.
- App updates now explicitly target the running installation folder and use a fully silent installer before reopening the app.

- Made the sidebar chibi a little smaller in normal, short and compact layouts.
- Clicking the chibi switches between Madoka's pink/plum appearance and a matching Homura chibi with purple accents and grey surfaces. Clicking again switches back.
- The accessible mascot button supports keyboard use; the chosen theme is saved immediately, restored at startup and retained when saving other settings.
- Theme changes update existing controls without resetting the current page, search, preview, favorites or saved selections.

Validation includes automated Core and relay tests, off-screen WPF layouts, real-relay end-to-end verification and isolated installer checks. Live two-PC game rendering remains unverified. See `verification/DUO.md` and the release notes.

## 1.1.0

- Downloaded library packages now use opaque local filenames and package name metadata, while original skin names, previews, chromas, forms, favorites and saved selections remain unchanged in the app.
- Source Git/LFS integrity is verified before rewriting; final package SHA-256 and size are stored separately. Payload bytes and override sets are checked before activation.
- Existing libraries migrate transactionally at their installed commit, with no new upstream commit required. Already transformed libraries are reused on restart.
- The release includes the existing standalone **MadokaChanger-Uninstaller.exe** for cleaning up old installed or portable copies before installing the new version. Skins and settings are kept by default.

Local storage obfuscation does not provide encryption or a user-security guarantee. Signed or unsupported modpkg packages are refused without replacing the working library.

Validation: 150 automated tests, native package round-trip checks, off-screen WPF checks, and an isolated installer/upgrade/uninstall smoke test.

## 1.0.1

- Starting a session no longer asks for administrator access every time. The patcher is elevated only when League runs as administrator or **Run patcher as administrator** is turned on in Settings.
- Uses the signed patcher from an installed LTK Manager when one is found; the bundled patcher is the fallback.
- Library skins now apply in game: the anti-skinhack scan is relaxed by default (hook flag 4, as in LTK Manager), with an **Enforce anti-skinhack scan** setting to turn it back on.
- Harmless "wad scan failed" warnings no longer end the session; only real blocks do.
- The status bar shows **In game · skins applied** once League loads a modded archive.

Validation: 59 automated tests and an isolated installer/upgrade/uninstall smoke test. In-game rendering is not yet verified for this build.

## 1.0.0

- First public Windows x64 installer.
- Bundled skin backend with automatic configuration.
- Dark Madoka theme, chibi application icon and responsive windowed layouts.
- Searchable champion library with favorites and saved selections.
- Always-visible skin preview, horizontal skin strip and chroma color swatches.
- Custom skin import, conflict handling, persistent settings and local diagnostics.
- Per-user installation, optional desktop shortcut and standard uninstaller.

Validation: 39 automated tests, off-screen WPF layout checks and an isolated installer/uninstaller smoke test. Live game attachment and rendering remain unverified.
