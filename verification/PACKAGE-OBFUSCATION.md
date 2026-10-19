# Downloaded library package obfuscation

Verified 2026-10-19 with .NET SDK 10.0.402 and the native adapter reporting league-mod revision `acaf19e5e1e32ff11376f0af1bccfbaecc28026d`.

This is local storage obfuscation, not encryption or a user-security guarantee. Original frontend identities, source paths, attribution and rollback generations remain readable. Custom imports retain their existing storage and behavior.

## Behavior

The install transaction verifies all upstream downloads, validates packages and maps original repository paths before rewriting. Two workers create opaque package filenames and replace only Fantome `Name` or modpkg `name`/`display_name`. The resulting packages are validated again; protected bytes and override sets must match. Final local SHA-256 and size are recorded separately from Git blob and LFS integrity information. Activation commits the complete manifest and then the atomic active pointer.

Legacy manifests migrate into a new stage at their installed commit. Verified local source files can be reused offline; unrecoverable source integrity information requires a pinned re-download. Migration preserves existing mapping and display identities. Current versioned manifests are reused on subsequent launches. A migration failure retains the prior generation and leaves the populated frontend available.

## Verification

- Release solution build: passed, zero warnings/errors.
- Complete xUnit suite: 150 passed, zero failures/skips, including 20 package transformation cases.
- Real native integration: upstream-produced modpkg packs, validates and imports after rewriting; author roles, license/readme files, layers, string overrides and gameplay payloads are preserved.
- Independent modpkg fixtures: raw/Zstd metadata, both growing and shrinking identities, metadata before/after gameplay chunks, distributor attribution, original schema/generator, unknown binary metadata and compressed gameplay bytes are preserved. The native parser validates the results.
- Fantome and recognized ZIPs: original names and all protected metadata, RAW asset paths/bytes, packed WAD bytes, previews and override sets are retained; supported extension casing is preserved.
- Identity/path tests: base, chroma and explicit form selection mappings; original metadata; preview paths; favorites/saved choices; manifest-based path resolution; stable filenames after restart.
- Integrity/migration tests: distinct Git/LFS/local hashes and sizes, no rewriting after failed upstream verification, same-commit Update migration, offline startup migration, legacy LFS pinned recovery, restart idempotence.
- Transaction tests: operation lease through transformation, cancellation before/during rewriting, failed validation/override comparisons, signed-modpkg refusal and failed final pointer replacement all preserve the previous generation and remove incomplete staging/temp files.
- Off-screen WPF verification in an isolated fixture: startup migration and identity/path assertions passed; all 13 layouts, champion search, skin/chroma selection, browser state and binding checks passed. The first layout attempt encountered a transient `DeferredAppResourceReference` resource exception; the unchanged baseline and subsequent updated run both passed. The verifier's stale dependency-label assertion was aligned with the existing application label. Production XAML/styles were untouched.
- `git diff --check` and dependency-license path checks passed.

The native integration cases require `MADOKACHANGER_TEST_NATIVE` to point to the pinned adapter. They were enabled for these checks. No user's installed library was migrated during verification; no game or patcher session was launched.

Reproduce the build and tests from PowerShell with a complete .NET 10 SDK and built native adapter:

```powershell
$env:MADOKACHANGER_TEST_NATIVE = (Resolve-Path ./native/bin/madokachanger-ltk.exe).Path
dotnet build MadokaChanger.sln -c Release
dotnet test tests/MadokaChanger.Tests/MadokaChanger.Tests.csproj -c Release
```

## Format limitations

- Fantome requires the existing recognized ZIP structure (`META/info.json` plus WAD/RAW content); packed WAD validation remains limited to v3.
- modpkg supports container v1, one named MessagePack metadata map, raw/Zstd metadata up to 16 MB stored/expanded, and nonoverlapping metadata/content ranges. Signed containers are refused because rewriting would invalidate their signatures. Unsupported or ambiguous inputs fail the transaction without activation.
- Existing package/path/size/extraction limits still apply. ZIP container compression can change; every protected entry's uncompressed bytes are compared. modpkg compressed gameplay bytes are preserved exactly, with table offsets adjusted when metadata size changes.

## Changed files

Implementation:

- `src/MadokaChanger.Core/PackageTransformer.cs` — versioned opaque naming, Fantome rewriting, byte/override preservation checks and final local integrity.
- `src/MadokaChanger.Core/ModpkgRewriter.cs` — format-aware MessagePack/chunk-table rewriting with protected-range comparisons.
- `src/MadokaChanger.Core/LibraryInstaller.cs` — bounded transformation phase and staged pinned migration under the existing operation lease.
- `src/MadokaChanger.Core/Models.cs` — separate persisted source/local integrity, original display metadata, storage paths and versions.
- `src/MadokaChanger.Core/Network.cs` — return verified LFS pointer information and share source-integrity verification with migration.
- `src/MadokaChanger.Core/Packages.cs` — retain original metadata during mapping and support structured modpkg author credits.
- `src/MadokaChanger.Core/Storage.cs` — resolve the current package's storage path through the manifest, with legacy fallback.
- `src/MadokaChanger.Core/MadokaChanger.Core.csproj` — pinned MessagePack and ZstdSharp dependencies.
- `src/MadokaChanger.App/ViewModels/MainViewModel.cs` — request versioned migration after loading/populating the existing collection.

Verification:

- `tests/MadokaChanger.Tests/PackageTransformationTests.cs` — focused transformation/migration/transaction/native integration coverage.
- `tests/MadokaChanger.Tests/RepositoryTests.cs` — assert persisted verified Git/LFS identities remain distinct.
- `tests/MadokaChanger.Tests/CoreTests.cs`, `tests/MadokaChanger.Tests/ClassicRemovalTests.cs` — adapt repository fixtures to return source-integrity records.
- `tools/MadokaChanger.UiVerify/Program.cs` — startup migration, frontend identity and renamed-path assertions.

Documentation and dependency notices:

- `README.md`, `docs/STORAGE.md`, `docs/UPSTREAM.md`, this report.
- `THIRD_PARTY_NOTICES.md`, `licenses/dependencies.json`.
- `licenses/managed/MessagePack-3.1.12/LICENSE`.
- `licenses/managed/ZstdSharp.Port-0.8.8/LICENSE`.
- `licenses/managed/Microsoft.NET.StringTools-17.11.4/LICENSE`.
