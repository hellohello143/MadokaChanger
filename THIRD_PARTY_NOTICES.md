# Third-party notices

Retain this file and the accompanying `licenses/` directory with binary distributions.

MadokaChanger first-party source and its serialization shim are licensed under Apache-2.0; see LICENSE. This build links the League Mod Toolkit libraries at commit acaf19e5e1e32ff11376f0af1bccfbaecc28026d. Their upstream lockfile and Cargo.lock pin transitive versions.

LTK Manager source is GPL-3.0-or-later and was inspected as a protocol reference. LeagueToolkit C# is GPL-3.0 and was inspected for asset format facts. Neither source nor assembly is included in this application. The GPL reference text and separate LTK Patcher License are included. The patcher host and DLL from commit 1a3d4f900054a739f833b6a028e87caa58455fbb are bundled under the LTK Patcher License. Their upstream code signatures have been removed as required; executable sections are unchanged. Provenance and SHA-256 hashes are recorded in backend/backend-manifest.json. MadokaChanger is independent and not endorsed by League Toolkit.

LeagueSkins packages and Riot artwork are downloaded at runtime and not included in the Windows package. LeagueSkins exposes no standard asset redistribution license. CommunityDragon is credited for metadata and asset hosting; Riot Games owns its game art and trademarks. The theme is an independent fan design inspired by Puella Magi Madoka Magica. The app includes an AI-generated Madoka Kaname chibi illustration for its icon and sidebar. The character belongs to its respective rights holders. Official anime images, audio and fonts are not bundled.

The self-contained .NET runtime and WindowsDesktop runtime retain their upstream license and third-party notices in the published directory. Rust standard-library and GCC runtime exception notices are under licenses/runtime. MIT is chosen where offered as an alternative to LGPL; Apache-2.0 is chosen where offered as an alternative to another license and no MIT alternative is present. Additional conjunctive BSD/Unicode terms remain applicable.

The following is a conservative dependency inventory, including build-time and target-specific crates from Cargo metadata. Some are not linked into the Windows executable. `licenses/dependencies.json` lists bundled text paths. Vendored native library texts are included beneath each crate.

| Ecosystem | Package | Version | Declared license |
|---|---|---|---|
| Cargo | adler2 | 2.0.1 | 0BSD OR MIT OR Apache-2.0 |
| Cargo | aes | 0.8.4 | MIT OR Apache-2.0 |
| Cargo | aho-corasick | 1.1.4 | Unlicense OR MIT |
| Cargo | annotate-snippets | 0.12.16 | MIT OR Apache-2.0 |
| Cargo | anstyle | 1.0.14 | MIT OR Apache-2.0 |
| Cargo | arbitrary | 1.4.2 | MIT OR Apache-2.0 |
| Cargo | array-init | 2.1.0 | MIT OR Apache-2.0 |
| Cargo | arraydeque | 0.5.1 | MIT/Apache-2.0 |
| Cargo | autocfg | 1.5.1 | Apache-2.0 OR MIT |
| Cargo | base64 | 0.22.1 | MIT OR Apache-2.0 |
| Cargo | base64ct | 1.8.3 | Apache-2.0 OR MIT |
| Cargo | binrw | 0.14.2 | MIT |
| Cargo | binrw_derive | 0.14.2 | MIT |
| Cargo | bitflags | 2.13.1 | MIT OR Apache-2.0 |
| Cargo | block-buffer | 0.10.4 | MIT OR Apache-2.0 |
| Cargo | bstr | 1.13.0 | MIT OR Apache-2.0 |
| Cargo | bumpalo | 3.20.3 | MIT OR Apache-2.0 |
| Cargo | bytemuck | 1.25.1 | Zlib OR Apache-2.0 OR MIT |
| Cargo | byteorder | 1.5.0 | Unlicense OR MIT |
| Cargo | byteorder-lite | 0.1.0 | Unlicense OR MIT |
| Cargo | bzip2 | 0.5.2 | MIT OR Apache-2.0 |
| Cargo | bzip2-sys | 0.1.13+1.0.8 | MIT/Apache-2.0 |
| Cargo | camino | 1.2.4 | MIT OR Apache-2.0 |
| Cargo | cc | 1.2.67 | MIT OR Apache-2.0 |
| Cargo | cfg-if | 1.0.4 | MIT OR Apache-2.0 |
| Cargo | cipher | 0.4.4 | MIT OR Apache-2.0 |
| Cargo | color_quant | 1.1.0 | MIT |
| Cargo | const-oid | 0.9.6 | Apache-2.0 OR MIT |
| Cargo | constant_time_eq | 0.3.1 | CC0-1.0 OR MIT-0 OR Apache-2.0 |
| Cargo | core_detect | 1.0.0 | MIT/Apache-2.0 |
| Cargo | cpufeatures | 0.2.17 | MIT OR Apache-2.0 |
| Cargo | crc | 3.4.0 | MIT OR Apache-2.0 |
| Cargo | crc-catalog | 2.5.0 | MIT OR Apache-2.0 |
| Cargo | crc32fast | 1.5.0 | MIT OR Apache-2.0 |
| Cargo | crossbeam-deque | 0.8.7 | MIT OR Apache-2.0 |
| Cargo | crossbeam-epoch | 0.9.20 | MIT OR Apache-2.0 |
| Cargo | crossbeam-utils | 0.8.22 | MIT OR Apache-2.0 |
| Cargo | crunchy | 0.2.4 | MIT |
| Cargo | crypto-common | 0.1.7 | MIT OR Apache-2.0 |
| Cargo | deflate64 | 0.1.12 | MIT |
| Cargo | der | 0.7.10 | Apache-2.0 OR MIT |
| Cargo | deranged | 0.5.8 | MIT OR Apache-2.0 |
| Cargo | derive_arbitrary | 1.4.2 | MIT OR Apache-2.0 |
| Cargo | deunicode | 1.6.2 | BSD-3-Clause |
| Cargo | digest | 0.10.7 | MIT OR Apache-2.0 |
| Cargo | displaydoc | 0.2.6 | MIT OR Apache-2.0 |
| Cargo | either | 1.16.0 | MIT OR Apache-2.0 |
| Cargo | encoding_rs | 0.8.41 | (Apache-2.0 OR MIT) AND BSD-3-Clause |
| Cargo | encoding_rs_io | 0.1.8 | MIT OR Apache-2.0 |
| Cargo | enum_dispatch | 0.3.13 | MIT OR Apache-2.0 |
| Cargo | equivalent | 1.0.2 | Apache-2.0 OR MIT |
| Cargo | errno | 0.3.14 | MIT OR Apache-2.0 |
| Cargo | fastrand | 2.4.1 | Apache-2.0 OR MIT |
| Cargo | fax | 0.2.7 | MIT |
| Cargo | fdeflate | 0.3.7 | MIT OR Apache-2.0 |
| Cargo | find-msvc-tools | 0.1.9 | MIT OR Apache-2.0 |
| Cargo | flate2 | 1.1.9 | MIT OR Apache-2.0 |
| Cargo | generic-array | 0.14.7 | MIT |
| Cargo | getrandom | 0.2.17 | MIT OR Apache-2.0 |
| Cargo | getrandom | 0.3.4 | MIT OR Apache-2.0 |
| Cargo | getrandom | 0.4.3 | MIT OR Apache-2.0 |
| Cargo | gif | 0.14.2 | MIT OR Apache-2.0 |
| Cargo | glam | 0.27.0 | MIT OR Apache-2.0 |
| Cargo | globset | 0.4.19 | Unlicense OR MIT |
| Cargo | granit-parser | 1.3.0 | MIT OR Apache-2.0 |
| Cargo | half | 2.7.1 | MIT OR Apache-2.0 |
| Cargo | hashbrown | 0.17.1 | MIT OR Apache-2.0 |
| Cargo | heck | 0.5.0 | MIT OR Apache-2.0 |
| Cargo | hmac | 0.12.1 | MIT OR Apache-2.0 |
| Cargo | ignore | 0.4.32 | Unlicense OR MIT |
| Cargo | image | 0.25.10 | MIT OR Apache-2.0 |
| Cargo | image-webp | 0.2.4 | MIT OR Apache-2.0 |
| Cargo | indexmap | 2.14.0 | Apache-2.0 OR MIT |
| Cargo | inout | 0.1.4 | MIT OR Apache-2.0 |
| Cargo | itertools | 0.14.0 | MIT OR Apache-2.0 |
| Cargo | itoa | 1.0.18 | MIT OR Apache-2.0 |
| Cargo | jobserver | 0.1.35 | MIT OR Apache-2.0 |
| Cargo | js-sys | 0.3.103 | MIT OR Apache-2.0 |
| Cargo | lazy_static | 1.5.0 | MIT OR Apache-2.0 |
| Cargo | libc | 0.2.186 | MIT OR Apache-2.0 |
| Cargo | libm | 0.2.16 | MIT |
| Cargo | libwebp-sys2 | 0.1.11 | BSD-3-Clause |
| Cargo | linux-raw-sys | 0.12.1 | Apache-2.0 WITH LLVM-exception OR Apache-2.0 OR MIT |
| Cargo | log | 0.4.33 | MIT OR Apache-2.0 |
| Cargo | ltk_fantome | 0.15.5 | Apache-2.0 |
| Cargo | ltk_file | 0.2.12 | Apache-2.0 |
| Cargo | ltk_game_data | 0.9.3 | Apache-2.0 |
| Cargo | ltk_game_index | 0.1.5 | Apache-2.0 |
| Cargo | ltk_hash | 0.5.0 | Apache-2.0 |
| Cargo | ltk_hashtable | 0.2.2 | Apache-2.0 |
| Cargo | ltk_io_ext | 0.4.8 | Apache-2.0 |
| Cargo | ltk_meta | 0.9.0 | Apache-2.0 |
| Cargo | ltk_mod_project | 0.16.7 | Apache-2.0 |
| Cargo | ltk_modpkg | 0.12.14 | Apache-2.0 |
| Cargo | ltk_overlay | 0.17.0 | Apache-2.0 |
| Cargo | ltk_primitives | 0.3.7 | Apache-2.0 |
| Cargo | ltk_rst | 0.2.2 | Apache-2.0 |
| Cargo | ltk_wad | 0.5.9 | Apache-2.0 |
| Cargo | lzma-rs | 0.3.0 | MIT |
| Cargo | lzma-sys | 0.1.20 | MIT/Apache-2.0 |
| Cargo | memchr | 2.8.3 | Unlicense OR MIT |
| Cargo | memmap2 | 0.9.11 | MIT OR Apache-2.0 |
| Cargo | miette | 7.6.0 | Apache-2.0 |
| Cargo | miette-derive | 7.6.0 | Apache-2.0 |
| Cargo | miniz_oxide | 0.8.9 | MIT OR Zlib OR Apache-2.0 |
| Cargo | moxcms | 0.8.1 | BSD-3-Clause OR Apache-2.0 |
| Cargo | multiversion | 0.9.0 | MIT OR Apache-2.0 |
| Cargo | multiversion-macros | 0.9.0 | MIT OR Apache-2.0 |
| Cargo | multiversion_no_op | 1.0.0 | Apache-2.0 OR MIT |
| Cargo | nohash-hasher | 0.2.0 | Apache-2.0 OR MIT |
| Cargo | num-bigint-dig | 0.8.6 | MIT/Apache-2.0 |
| Cargo | num-conv | 0.2.2 | MIT OR Apache-2.0 |
| Cargo | num-integer | 0.1.46 | MIT OR Apache-2.0 |
| Cargo | num-iter | 0.1.46 | MIT OR Apache-2.0 |
| Cargo | num-traits | 0.2.19 | MIT OR Apache-2.0 |
| Cargo | num_enum | 0.7.6 | BSD-3-Clause OR MIT OR Apache-2.0 |
| Cargo | num_enum_derive | 0.7.6 | BSD-3-Clause OR MIT OR Apache-2.0 |
| Cargo | once_cell | 1.21.4 | MIT OR Apache-2.0 |
| Cargo | owo-colors | 3.5.0 | MIT |
| Cargo | paste | 1.0.15 | MIT OR Apache-2.0 |
| Cargo | pbkdf2 | 0.12.2 | MIT OR Apache-2.0 |
| Cargo | pem-rfc7468 | 0.7.0 | Apache-2.0 OR MIT |
| Cargo | pin-project-lite | 0.2.17 | Apache-2.0 OR MIT |
| Cargo | pkcs1 | 0.7.5 | Apache-2.0 OR MIT |
| Cargo | pkcs8 | 0.10.2 | Apache-2.0 OR MIT |
| Cargo | pkg-config | 0.3.33 | MIT OR Apache-2.0 |
| Cargo | png | 0.18.1 | MIT OR Apache-2.0 |
| Cargo | powerfmt | 0.2.0 | MIT OR Apache-2.0 |
| Cargo | ppv-lite86 | 0.2.21 | MIT OR Apache-2.0 |
| Cargo | proc-macro-crate | 3.5.0 | MIT OR Apache-2.0 |
| Cargo | proc-macro2 | 1.0.106 | MIT OR Apache-2.0 |
| Cargo | pxfm | 0.1.30 | BSD-3-Clause OR Apache-2.0 |
| Cargo | quick-error | 2.0.1 | MIT/Apache-2.0 |
| Cargo | quote | 1.0.46 | MIT OR Apache-2.0 |
| Cargo | r-efi | 5.3.0 | MIT OR Apache-2.0 OR LGPL-2.1-or-later |
| Cargo | r-efi | 6.0.0 | MIT OR Apache-2.0 OR LGPL-2.1-or-later |
| Cargo | rand | 0.8.7 | MIT OR Apache-2.0 |
| Cargo | rand_chacha | 0.3.1 | MIT OR Apache-2.0 |
| Cargo | rand_core | 0.6.4 | MIT OR Apache-2.0 |
| Cargo | rayon | 1.12.0 | MIT OR Apache-2.0 |
| Cargo | rayon-core | 1.13.0 | MIT OR Apache-2.0 |
| Cargo | regex-automata | 0.4.16 | MIT OR Apache-2.0 |
| Cargo | regex-syntax | 0.8.11 | MIT OR Apache-2.0 |
| Cargo | rmp | 0.8.15 | MIT |
| Cargo | rmp-serde | 1.3.1 | MIT |
| Cargo | rsa | 0.9.10 | MIT OR Apache-2.0 |
| Cargo | rustix | 1.1.4 | Apache-2.0 WITH LLVM-exception OR Apache-2.0 OR MIT |
| Cargo | rustversion | 1.0.23 | MIT OR Apache-2.0 |
| Cargo | same-file | 1.0.6 | Unlicense/MIT |
| Cargo | scopeguard | 1.2.0 | MIT OR Apache-2.0 |
| Cargo | semver | 1.0.28 | MIT OR Apache-2.0 |
| Cargo | serde | 1.0.228 | MIT OR Apache-2.0 |
| Cargo | serde-saphyr | 1.3.0 | MIT OR Apache-2.0 |
| Cargo | serde_core | 1.0.228 | MIT OR Apache-2.0 |
| Cargo | serde_derive | 1.0.228 | MIT OR Apache-2.0 |
| Cargo | serde_json | 1.0.150 | MIT OR Apache-2.0 |
| Cargo | serde_spanned | 1.1.1 | MIT OR Apache-2.0 |
| Cargo | sha1 | 0.10.7 | MIT OR Apache-2.0 |
| Cargo | sha2 | 0.10.9 | MIT OR Apache-2.0 |
| Cargo | shlex | 2.0.1 | MIT OR Apache-2.0 |
| Cargo | signature | 2.2.0 | Apache-2.0 OR MIT |
| Cargo | simd-adler32 | 0.3.10 | MIT |
| Cargo | simdutf8 | 0.1.5 | MIT OR Apache-2.0 |
| Cargo | slug | 0.1.6 | MIT/Apache-2.0 |
| Cargo | smallvec | 1.15.2 | MIT OR Apache-2.0 |
| Cargo | spin | 0.9.9 | MIT |
| Cargo | spki | 0.7.3 | Apache-2.0 OR MIT |
| Cargo | strum | 0.27.2 | MIT |
| Cargo | strum_macros | 0.27.2 | MIT |
| Cargo | subtle | 2.6.1 | BSD-3-Clause |
| Cargo | syn | 1.0.109 | MIT OR Apache-2.0 |
| Cargo | syn | 2.0.119 | MIT OR Apache-2.0 |
| Cargo | syn | 3.0.5 | MIT OR Apache-2.0 |
| Cargo | tempfile | 3.27.0 | MIT OR Apache-2.0 |
| Cargo | thiserror | 1.0.69 | MIT OR Apache-2.0 |
| Cargo | thiserror | 2.0.18 | MIT OR Apache-2.0 |
| Cargo | thiserror-impl | 1.0.69 | MIT OR Apache-2.0 |
| Cargo | thiserror-impl | 2.0.18 | MIT OR Apache-2.0 |
| Cargo | tiff | 0.11.3 | MIT |
| Cargo | time | 0.3.53 | MIT OR Apache-2.0 |
| Cargo | time-core | 0.1.9 | MIT OR Apache-2.0 |
| Cargo | toml | 1.1.6+spec-1.1.0 | MIT OR Apache-2.0 |
| Cargo | toml_datetime | 1.1.1+spec-1.1.0 | MIT OR Apache-2.0 |
| Cargo | toml_edit | 0.25.13+spec-1.1.0 | MIT OR Apache-2.0 |
| Cargo | toml_parser | 1.1.3+spec-1.1.0 | MIT OR Apache-2.0 |
| Cargo | toml_writer | 1.1.2+spec-1.1.0 | MIT OR Apache-2.0 |
| Cargo | tracing | 0.1.44 | MIT |
| Cargo | tracing-attributes | 0.1.31 | MIT |
| Cargo | tracing-core | 0.1.36 | MIT |
| Cargo | typenum | 1.20.1 | MIT OR Apache-2.0 |
| Cargo | unicode-ident | 1.0.24 | (MIT OR Apache-2.0) AND Unicode-3.0 |
| Cargo | unicode-width | 0.1.14 | MIT OR Apache-2.0 |
| Cargo | unicode-width | 0.2.2 | MIT OR Apache-2.0 |
| Cargo | vcpkg | 0.2.15 | MIT/Apache-2.0 |
| Cargo | version_check | 0.9.5 | MIT/Apache-2.0 |
| Cargo | walkdir | 2.5.0 | Unlicense/MIT |
| Cargo | wasi | 0.11.1+wasi-snapshot-preview1 | Apache-2.0 WITH LLVM-exception OR Apache-2.0 OR MIT |
| Cargo | wasip2 | 1.0.4+wasi-0.2.12 | Apache-2.0 WITH LLVM-exception OR Apache-2.0 OR MIT |
| Cargo | wasm-bindgen | 0.2.126 | MIT OR Apache-2.0 |
| Cargo | wasm-bindgen-macro | 0.2.126 | MIT OR Apache-2.0 |
| Cargo | wasm-bindgen-macro-support | 0.2.126 | MIT OR Apache-2.0 |
| Cargo | wasm-bindgen-shared | 0.2.126 | MIT OR Apache-2.0 |
| Cargo | webp-animation | 0.9.0 | MIT OR Apache-2.0 |
| Cargo | weezl | 0.1.12 | MIT OR Apache-2.0 |
| Cargo | winapi-util | 0.1.11 | Unlicense OR MIT |
| Cargo | windows-link | 0.2.1 | MIT OR Apache-2.0 |
| Cargo | windows-sys | 0.61.2 | MIT OR Apache-2.0 |
| Cargo | winnow | 1.0.4 | MIT |
| Cargo | wit-bindgen | 0.57.1 | Apache-2.0 WITH LLVM-exception OR Apache-2.0 OR MIT |
| Cargo | xxhash-rust | 0.8.17 | BSL-1.0 |
| Cargo | xz2 | 0.1.7 | MIT/Apache-2.0 |
| Cargo | zerocopy | 0.8.54 | BSD-2-Clause OR Apache-2.0 OR MIT |
| Cargo | zerocopy-derive | 0.8.54 | BSD-2-Clause OR Apache-2.0 OR MIT |
| Cargo | zeroize | 1.9.0 | Apache-2.0 OR MIT |
| Cargo | zeroize_derive | 1.5.0 | Apache-2.0 OR MIT |
| Cargo | zip | 2.4.2 | MIT |
| Cargo | zmij | 1.0.23 | MIT |
| Cargo | zopfli | 0.8.3 | Apache-2.0 |
| Cargo | zstd | 0.13.3 | MIT |
| Cargo | zstd-safe | 7.2.4 | MIT OR Apache-2.0 |
| Cargo | zstd-sys | 2.0.16+zstd.1.5.7 | MIT/Apache-2.0 |
| Cargo | zune-core | 0.5.1 | MIT OR Apache-2.0 OR Zlib |
| Cargo | zune-jpeg | 0.5.15 | MIT OR Apache-2.0 OR Zlib |
| NuGet | CommunityToolkit.Mvvm | 8.4.0 | MIT |
| NuGet | Microsoft.Extensions.DependencyInjection | 10.0.0 | MIT |
| NuGet | Microsoft.Extensions.DependencyInjection.Abstractions | 10.0.0 | MIT |
| NuGet | Microsoft.Extensions.Logging | 10.0.0 | MIT |
| NuGet | Microsoft.Extensions.Logging.Abstractions | 10.0.0 | MIT |
| NuGet | Microsoft.Extensions.Options | 10.0.0 | MIT |
| NuGet | Microsoft.Extensions.Primitives | 10.0.0 | MIT |
| NuGet | System.IO.Hashing | 10.0.0 | MIT |

## Packages without a separate text in their downloaded package

These packages declare their license in package metadata; the standard MIT/Apache texts are also supplied under licenses/runtime. Inspect the package source for any additional notices before altering the dependency graph.

- binrw 0.14.2: MIT (https://github.com/jam1garner/binrw)
- binrw_derive 0.14.2: MIT (https://github.com/jam1garner/binrw)
- crc-catalog 2.5.0: MIT OR Apache-2.0 (https://github.com/akhilles/crc-catalog.git)
- ltk_fantome 0.15.5: Apache-2.0 (https://github.com/LeagueToolkit/league-mod)
- ltk_file 0.2.12: Apache-2.0 (registry+https://github.com/rust-lang/crates.io-index)
- ltk_game_data 0.9.3: Apache-2.0 (https://github.com/LeagueToolkit/league-mod)
- ltk_game_index 0.1.5: Apache-2.0 (https://github.com/LeagueToolkit/league-mod)
- ltk_hash 0.5.0: Apache-2.0 (registry+https://github.com/rust-lang/crates.io-index)
- ltk_hashtable 0.2.2: Apache-2.0 (https://github.com/LeagueToolkit/league-mod)
- ltk_io_ext 0.4.8: Apache-2.0 (registry+https://github.com/rust-lang/crates.io-index)
- ltk_meta 0.9.0: Apache-2.0 (registry+https://github.com/rust-lang/crates.io-index)
- ltk_mod_project 0.16.7: Apache-2.0 (https://github.com/LeagueToolkit/league-mod)
- ltk_modpkg 0.12.14: Apache-2.0 (https://github.com/LeagueToolkit/league-mod)
- ltk_overlay 0.17.0: Apache-2.0 (https://github.com/LeagueToolkit/league-mod)
- ltk_primitives 0.3.7: Apache-2.0 (registry+https://github.com/rust-lang/crates.io-index)
- ltk_rst 0.2.2: Apache-2.0 (registry+https://github.com/rust-lang/crates.io-index)
- ltk_wad 0.5.9: Apache-2.0 (registry+https://github.com/rust-lang/crates.io-index)
- r-efi 5.3.0: MIT OR Apache-2.0 OR LGPL-2.1-or-later (https://github.com/r-efi/r-efi)
- r-efi 6.0.0: MIT OR Apache-2.0 OR LGPL-2.1-or-later (https://github.com/r-efi/r-efi)

The exact MIT copyright and permission text for binrw and binrw_derive 0.14.2 is additionally included in licenses/binrw-0.14.2-MIT.txt, retrieved from its upstream v0.14.2 tag.

Self-contained runtime packs: Microsoft.NETCore.App.Runtime.win-x64 10.0.13 and Microsoft.WindowsDesktop.App.Runtime.win-x64 10.0.13, under MIT. Their exact runtime license/notice files are bundled in licenses/managed/.
