# Changelog

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
