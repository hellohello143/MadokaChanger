# MadokaChanger update verification

Changes requested: force dark mode, rename the application, remove Classic options entirely, and retain champion search.

- Release solution build and Windows x64 self-contained publish verified.
- 35 tests pass. The three new regressions cover cached Classic metadata, previously installed Classic packages at an unchanged revision, and exclusion from new library downloads.
- WPF dark mode is explicitly forced; custom dark Madoka resources remain in use. No Windows theme settings were changed.
- Executable, window title, sidebar, assemblies, solution/projects, log names, source package and native adapter are renamed MadokaChanger.
- Off-screen UI checks assert the exact window title, forced dark mode, absence of Classic champion entries in the shared browser/custom list, case-insensitive search returning exactly one Ahri, and restoration of search after returning from detail.
- At patch 16.20 the browser now contains 173 champions, with 77 duplicate Classic/Jade entries removed. The supported installed-library view contains 9,063 packages, excluding 1,734 Classic packages.
- Cached data and saved selections remain usable across the rename. Classic preferences are removed and Classic custom entries are disabled without deleting imported files.

Screenshots are produced without showing a window, sending desktop input, or interacting with the game. No live game attachment or real patcher scan was performed. Earlier native/package verification remains described in the project README and upstream notes.

## Included backend update

- The host and DLL are now bundled with the native adapter under the app's `backend/` directory; no external LTK installation or backend picker is required.
- Upstream files are pinned to 1a3d4f900054a739f833b6a028e87caa58455fbb and SHA-256 verified. The upstream signatures are removed under the LTK Patcher License using Windows ImageHlp. All executable sections are verified unchanged; provenance and original/distributed hashes are recorded in backend-manifest.json.
- The backend resolves beside the native adapter regardless of the working directory. Legacy backendFolder settings are ignored and dropped while other preferences remain intact.
- Release build and self-contained publish pass with zero warnings or errors. All 39 tests pass, including portable directories with spaces, missing bundled files, old settings, and the existing host lifecycle tests using a process test double.
- Off-screen WPF verification checks that the bundled backend is ready without configuration, the Settings screen has no backend picker, and existing dark mode/search/skin UI checks still pass.
- Real patcher processes and the running game were not launched or controlled during this update. Actual game compatibility remains unverified.

## Madoka chibi branding update

- A transparent Madoka chibi illustration replaces the sidebar gem and is embedded as the application/window icon.
- The Windows ICO contains 10 sizes from 16 to 256 pixels. The original transparent PNG and the exact built-in imagegen prompt are retained under src/MadokaChanger.App/Assets/.
- Release build and self-contained publish pass with no warnings or errors; the existing 39 tests pass. The published EXE icon is extracted for inspection without executing it, and off-screen WPF screenshots verify sidebar layout and resource loading.

## Larger preview and description removal

- Champion subtitles and skin lore are removed from the selection page, including their unused view-model bindings.
- The main artwork preview grows with the available space. The later responsive update below replaces the previous fixed minimum height and scrolling detail layout.
- Chroma previews are shown over the main artwork, with thumbnails sized to the current viewport.
- Release build and self-contained publish pass with no warnings or errors; all 39 existing tests pass. Off-screen WPF verification checks the updated layouts and binding errors without opening a window or interacting with the game.

## Responsive windowed layout

- The skin page has no vertical ScrollViewer. Artwork occupies the remaining Grid row; the horizontal skin strip, chroma color circles, form selector and Apply/Reset actions occupy fixed rows and remain visible. The selected chroma preview is overlaid on the artwork. Short windows reduce chrome and use a compact thumbnail strip.
- The sidebar becomes an icon rail below 1100 logical pixels. Champion rows fill the available width, custom-skin editors scroll within their own area, and Settings columns stack on narrow views. ComboBox templates retain readable selections and stretch to their container.
- Startup sizing fits the desktop work area, with nominal 720x500 minimum dimensions clamped to smaller work areas. PerMonitorV2 DPI awareness remains enabled.
- Off-screen render checks cover 720x500, 854x480, 900x620, 960x640, 1120x800 and larger layouts, including 125% and 150% raster scaling. Assertions check that preview, skin strip, chroma swatches and Apply/Reset/Start/Stop controls remain within the window; oversized carousel items are rejected. Settings Save is brought into view and checked.
- The existing 39 tests and Release build pass. No app window was shown, no desktop input was sent, and no real game or patcher process was controlled. Actual cross-monitor DPI transitions remain untested.

## Windows installer 1.0.0

- Inno Setup 6.7.3 compiles the dark per-user x64 installer with the chibi icon and bundled runtime/backend.
- The isolated smoke test verified all 854 non-PDB payload files by SHA-256, then performed an upgrade and ran the generated uninstaller.
- Untracked files and separate data were preserved. The existing desktop shortcut was unchanged and the application was never launched.
- See installer-smoke.json for the installer checksum and machine-independent result fields. The first-party source, installer scripts and public README are included in Git; local logs, compiled outputs and diagnostic screenshots with local paths are excluded.
