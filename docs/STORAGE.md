# Storage and transactions

Default root for a new installation: `%LOCALAPPDATA%/MadokaChanger/`. Upgrades reuse an existing `%LOCALAPPDATA%/SoulGem/` settings folder if the new folder has no settings, without moving files.

| Directory/file | Purpose |
|---|---|
| `settings.json` | Settings, favorites, desired variants, custom enabled states and explicit conflict priority |
| `metadata/catalog-<locale>.json` | Complete metadata snapshot with one CommunityDragon patch and locale |
| `images/` | URL-keyed SHA-256 image cache; removable using Settings |
| `library/active.json` | Atomic pointer to one fully validated generation and its installed SHA |
| `library/versions/<sha>-<guid>/` | Full repository-managed packages, form previews and manifest |
| `custom/<guid>/` | An imported package and upstream-prepared project; outside the managed library |
| `temporary/import-<guid>/` | Isolated imports before successful activation |
| `patches/<guid>/` | Session-only selected repository projects, overlay, builder state and configuration snapshot |
| `logs/` | Daily structured JSONL logs; rotated at 20 MB |

`LibraryFolder` can select another dedicated library root. It does not move the current generation. Keep a library directory separate from `custom`, caches, logs and patches. Metadata, images, settings, temporary files and custom imports remain under the data root.

## Update commit boundary

The source adapter discovers the default branch and resolves its SHA once. Git tree requests enumerate the pinned SHA; if the recursive response is truncated, non-recursive trees are walked. Git Trees has no pagination; a truncated single-directory result is refused rather than accepted as complete. Downloads always use the resolved SHA. Git LFS pointers are verified as Git blobs before their pinned media payload is downloaded and checked against its declared SHA-256/size.

Every supported package and form preview is downloaded into a fresh generation. The Classic category is excluded from both downloads and existing manifests as they are loaded. Every downloaded ordinary blob is checked against its Git SHA-1. Fantome/ZIP paths, special files, duplicates, bounds, CRCs and supported WAD descriptors are validated; modpkg uses the actual upstream plan and checks compressed chunk hashes. A catalog is rebuilt with explicit mapping errors for unknown or ambiguous variants.

The manifest is flushed and replaced first, inside the staged generation. `active.json` is flushed and atomically replaced only after the entire transaction succeeds. The installed SHA is the pointer's SHA. On cancellation or failure before that replacement, only the owned stage is deleted. Settings are not part of the library transaction. Old generations are retained for rollback and interrupted-process recovery.

If a machine loses power before cleanup, an unreferenced generation or `.tmp` file can remain. It is not activated. Inspect `active.json` before removing any unreferenced generation. Do not remove or edit the active generation or a current session's overlay while the game or patcher is running.

To roll back with the app and game closed, back up `active.json`, then point it at a previously successful generation using its manifest's SHA and relative directory. Keep the original pointer for recovery. The next start validates pointer/manifest agreement. Saved selections whose packages do not exist in that generation remain saved and are flagged as unavailable.

## Archive and import policy

Maximum package: 2 GB. ZIP maximum: 100,000 entries, 512 MB per entry, 4 GB total expanded, 1000:1 compression ratio for entries over 1 MB. Packed WAD v3 descriptors are checked before extraction, including chunk count, ranges and expanded sizes. Paths with parent traversal, roots, drives, reserved Windows names, ambiguous trailing dots/spaces, case collisions, links and special files are rejected.

modpkg header counts are preflighted before native parsing. Its declared paths and chunk table are inspected before extraction; compressed payload hashes and sizes are verified in C#. The native process has a kernel memory bound. Imports occur only in owned staging directories, with final file/reparse-point/expanded-size checks before activation. Upstream import handles actual decompression and format conversion.

Custom archives without a champion declaration are assigned to the champion selected at import. Imports start disabled. Uninstall removes the state entry before deleting its owned directory. Failed settings writes restore the in-memory state; a crash between state removal and cleanup can leave an inactive orphan directory. A duplicate has the same package SHA-256, independent of its filename.
