# Integration assessment and pinned evidence

Inspected on **2026-10-18**. Remote repositories were fetched and read before implementation.

| Source | Revision/version | Findings and use |
|---|---|---|
| [LeagueToolkit organization](https://github.com/LeagueToolkit) | Repositories below | Authoritative upstream components |
| [ltk-manager](https://github.com/LeagueToolkit/ltk-manager) | `1a3d4f900054a739f833b6a028e87caa58455fbb`; latest release observed `v1.28.1` | GPL-3.0-or-later source; reference for host launch/config/status/shutdown. Not linked into this app |
| [league-mod](https://github.com/LeagueToolkit/league-mod) | `acaf19e5e1e32ff11376f0af1bccfbaecc28026d` | Apache-2.0 native import/build libraries, linked by the serialization shim |
| [LeagueToolkit C#](https://github.com/LeagueToolkit/LeagueToolkit) | `76bf57bf4fd195343dec610bf0f912a06b910914` | GPL-3.0 asset library; WAD descriptor structure inspected as a reference. No assembly/source reused |
| [LeagueSkins](https://github.com/Alban1911/LeagueSkins) | default branch `main`, commit `40573673466dd3fc250a9c2a8b945514ca119fe6` | No standard license file detected. 10,797 Fantome packages, 41 PNG previews. Fetched locally for validation; not redistributed |
| [CommunityDragon docs](https://www.communitydragon.org/docs) and [asset-path reference](https://github.com/CommunityDragon/Docs/blob/master/assets.md) | RAW patch `16.20`; content `16.20.8248524+branch.releases-16-20.content.release`; locale `default` | Live JSON schema and image resolution verified. Riot-owned assets, with CommunityDragon attribution |
| [LTK Patcher License](https://github.com/LeagueToolkit/ltk-manager/blob/1a3d4f900054a739f833b6a028e87caa58455fbb/LTK-PATCHER-LICENSE.md) | v1.0, effective 2026-07-14 | Separate host/DLL terms; signature-removal condition outside official release artifacts |
| [.NET lifecycle](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core) | .NET 10 LTS, SDK 10.0.402 used | WPF target `net10.0-windows` |

Native versions at the pinned revision: `ltk_mod_project 0.16.7`, `ltk_fantome 0.15.5`, `ltk_modpkg 0.12.14`, `ltk_overlay 0.17.0`, `ltk_game_data 0.9.3`, `ltk_game_index 0.1.5`. `native/Cargo.lock` was seeded from upstream's lockfile and committed. This matters: unconstrained dependency resolution selected ZIP 2.6.1, which does not compile with this upstream's ZipFile signatures. The verified lock uses ZIP 2.4.2.

## Exact source integration points

- `ltk-manager/crates/ltk-manager-runtime/src/patcher/host/process.rs`: redirected streams, backend working directory, `--elevate`, `stop`, stdin EOF and five-second shutdown grace.
- `.../host/protocol.rs`: `config loglevel`, `config flags`, `config prefix`, `start scan`, and timestamped `ok/status/error/dll` records. `flags 0` keeps default behavior.
- `.../patcher/injector.rs`: failure states, scan rejection, overlay-disabled and expired-backend diagnostics.
- `league-mod/crates/ltk_mod_project/src/import/`: `ProjectImporter`, extraction plans and format conversion.
- `.../fantome/import.rs`, `.../modpkg/import.rs`: actual supported imports, not extension-only assumptions.
- `league-mod/crates/ltk_overlay/src/lib.rs` and `builder/`: `OverlayBuilder`, `EnabledMod`, `FsModContent` and first-mod-wins ordering.
- `LeagueToolkit/src/LeagueToolkit/Core/Wad/{WadFile,WadChunk}.cs`: v3 header/descriptor layout reference.

## Metadata and package mapping

Resolve `latest/content-metadata.json` once to a numeric RAW patch, then request the champion summary and every champion JSON from that patch and locale. Image paths come from `squarePortraitPath`, `splashPath`, `tilePath` and `chromaPath`. Strip `/lol-game-data/assets/`, lowercase the remaining path and append it under the same patch's `plugins/rcp-be-lol-game-data/global/<locale>/` directory. No splash filenames or chroma IDs are generated.

The actual current repository differs from its older README's `.zip` examples:

```text
skins/103/103001/103001.fantome
skins/103/103001/103052/103052.fantome
skins/103/103085/103086/103086.fantome
skins/103/103085/103086/103086.png
classic/60001/1001/1001.fantome
```

A base package needs an exact champion ID, exact skin ID and matching filename. A chroma needs an exact ID in that parent skin's chroma array. A form needs a matching numeric variant path and explicit preview, with no conflicting parent-skin ID. Duplicate matches are all unavailable. `103052` maps to the Dynasty Ahri chroma because metadata declares it there; no division/modulo ID formula is used.

The representative Dynasty Ahri archive contains `META/info.json` and `WAD/Ahri.wad.client`, not an arbitrary ZIP of loose skins. That WAD is imported by the real upstream Fantome importer. `.modpkg` has `_modpkg_` magic, a binary chunk table and MsgPack metadata; it is not opened as a ZIP.

Local storage obfuscation parses modpkg container version 1 using the same pinned revision's [`read.rs`](https://github.com/LeagueToolkit/league-mod/blob/acaf19e5e1e32ff11376f0af1bccfbaecc28026d/crates/ltk_modpkg/src/read.rs), [`chunk.rs`](https://github.com/LeagueToolkit/league-mod/blob/acaf19e5e1e32ff11376f0af1bccfbaecc28026d/crates/ltk_modpkg/src/chunk.rs), and [`metadata.rs`](https://github.com/LeagueToolkit/league-mod/blob/acaf19e5e1e32ff11376f0af1bccfbaecc28026d/crates/ltk_modpkg/src/metadata.rs). The 61-byte records contain path hash, data offset, compression, stored/expanded lengths and XXH3 checksums, followed by path/layer/WAD indices. Only named MessagePack identity values are replaced; protected values and compressed payload ranges are copied and compared. This avoids the project import/pack conversion dropping distributor metadata or normalizing format fields.

The separate `classic/` category and CommunityDragon `Jade_` champion aliases are excluded by the current application. This policy applies to fresh metadata, previously cached metadata, new package downloads, and already installed manifests. It prevents duplicate Classic entries from reappearing after an update or restart. Ordinary official skins are still identified by their exact parent metadata; a word in an official skin name does not create a Classic champion category.

The original upstream snapshot had 10,797 packages. Excluding 1,734 Classic packages leaves 9,063 supported packages: 9,047 exact mappings, 15 unknown variants, and one missing parent. The earlier full repository validation covered these supported files as part of that snapshot. The current policy is separately covered by regression tests.
