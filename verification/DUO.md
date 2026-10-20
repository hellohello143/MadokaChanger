# Duo verification — October 20, 2026

Implementation and independent review were delegated to three `gpt-6-luna` workers for relay, desktop/LCU and WPF. Integration, overlay lifecycle changes and final checks were performed in the main session. Existing unrelated working-tree changes were preserved.

| Check | Result |
|---|---|
| `dotnet build MadokaChanger.sln -c Release --no-restore` | PASS, 0 warnings, 0 errors; includes DuoVerify |
| `dotnet test tests/MadokaChanger.Tests -c Release --no-build` | PASS, 178/178, no skips with the available native adapter configured |
| `npm test` in `relay/` | PASS, 10 Workerd integration tests |
| `npx tsc --noEmit` in `relay/` | PASS |
| `npx wrangler deploy --dry-run` | PASS, Worker bundled and SQLite binding/migration validated; no publish |
| DuoVerify against a fresh local Wrangler relay | PASS with the production ten-second polling interval |
| UiVerify with isolated copied metadata/cache and backend files | PASS, full existing layouts and Duo at 1360×870 and 720×500 under both themes; no binding errors |
| `git diff --check` | PASS |

The machine's default `dotnet` executable had no SDK. Verification used an existing .NET SDK 10.0.402 and runtime 10.0.13 from the local development tools directory, setting `DOTNET_ROOT` for fake backend apphosts. `MADOKACHANGER_TEST_NATIVE` was set to the available native adapter for the three existing package round-trip tests; without that variable those existing tests are skipped. No Rust source changed.

Duo tests cover exact variants and saved-preference preservation; default, missing and ambiguous peer packages; library compatibility; local-first same-champion conflicts; existing file conflict checks; no unsolicited patcher Start; running-session rebuild serialization and Stop intent; active-game deferral; safe leave cleanup; committed local publishing without peer echo; reconnect with the same token; stale member/room snapshots; membership expiry; LCU swaps, dodge, disappearance and endpoint-unavailable transitions; and disposal.

Relay tests run against actual Worker/Durable Object code and SQLite in Workerd. They cover unbiased four-digit generation with zero prefixes/collisions; concurrent admission and third-member rejection; fixed unused-invite expiry; two-hour abandonment; ongoing match heartbeats and reconnect; token isolation; explicit leave/replacement; IP and member throttles; revisions; malformed/oversized data; and generation isolation. The local Workerd harness emits a non-fatal request-stream diagnostic for the deliberately oversized request and occasional Windows temporary-storage cleanup warnings. The assertions pass and the process exits successfully. A local development server was restarted after dependency updates before the final end-to-end run.

DuoVerify uses two real `DuoHttpTransport`, `PartyService` and `DuoCoordinator` instances talking to the local relay. Isolated temporary roots hold fixture libraries and the process-based fake backend. Its simulated LCU/game environment proves bidirectional state, temporary merge, stopped-state control, pending mid-game changes, automatic safe rebuild, physical leave cleanup and unchanged saved selections. It does not attach to League.

Screenshots (generated locally, ignored by Git): [Madoka](screenshots/duo/18-duo-madoka.png), [Madoka compact](screenshots/duo/19-duo-madoka-small.png), [Homura](screenshots/duo/20-duo-homura.png), [Homura compact](screenshots/duo/21-duo-homura-small.png).

The public Cloudflare Workers Free relay is deployed at `https://madokachanger-duo-relay.intogummybear.workers.dev` (Worker version `11ef3f78-abbd-4515-b9fd-bcfc606aaea4`). DuoVerify passed against the live relay as well as the local relay. A real two-PC session, live LCU session, and live-game skin appearance remain untested; the simulated checks never attach to League or inject into the game. Use the [two-PC checklist](../docs/DUO.md) for the remaining manual checks. Mid-game hot reload is unsupported; different appearances for two players on the same champion are unsupported; imported custom-package transfer is unsupported.
