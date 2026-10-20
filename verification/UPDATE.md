# App update verification

The headless harness in `tools/MadokaChanger.UpdateVerify` loads the saved release's actual `MadokaChanger.Core.dll` in an isolated assembly context and calls that version's `UpdateService.CheckAsync` and `DownloadAppUpdateAsync`. It checks the parsed current and latest versions, update availability, selected setup asset and SHA-256 digest, then hashes the downloaded setup. The harness also inspects the saved UI assembly's `UpdateCommand` async closure IL and requires routes to `CheckAsync`, `DownloadAppUpdateAsync`, and `LaunchAppSetup`.

Run `scripts/test-app-update.ps1` after v1.2.0 is published. It installs the old released setup and the downloaded new setup into a fresh workspace using `/VERYSILENT /SMOKETEST=1 /SKIPLTK=1 /DIR=<workspace>`. `-SeedFromPublish` copies a saved development payload instead of installing an old setup. `SMOKETEST` disables shortcut creation and uninstall registration, and the explicit directory keeps the payload in the workspace. The script checks the installed app version and required payload files, and compares settings, favorites, selections and library fixtures before and after the upgrade. A separate `-CheckOnly` mode exercises real GitHub release discovery without downloading or installing a setup.

The test does not instantiate the WPF application, invoke `UpdateCommand`, display its confirmation dialog, or restart MadokaChanger. The IL check establishes that the saved button command calls the expected updater methods; it does not claim the button was clicked. The installer run uses the same silent and LTK-skip flags as the updater, adds `SMOKETEST` and an isolated `/DIR`, and omits relaunch so it cannot start the application.

The public old release is v1.1.0. Its local installer SHA-256 matches GitHub's release asset digest (`7e2058c96ca9ac0053754197f6dbde6f1ff37bf4309cc3d5ed1fecb6f8ff5710`), and the verifier pins its Core DLL hash. A separate local development payload at `artifacts/duo-ready` reports v1.1.1; it was installed by copying published files and was never a public release. That copy can be tested with `-ExpectedOldVersion 1.1.1 -OldPublishDirectory artifacts/duo-ready -SeedFromPublish`. The script checks old Core hashes before and after each run.

## Results

Pre-publication check-only run: **PASS**. The unmodified v1.1.0 updater reported GitHub latest v1.1.0 and no update, as expected before v1.2.0 publication. It selected the published v1.1.0 setup and its SHA-256 matched GitHub metadata. The saved UpdateCommand IL contained all three expected updater calls. No download, installer run, UI interaction, or restart occurred.

Full v1.1.0-to-v1.2.0 upgrade against the public latest release: **Pending publication and run**.
