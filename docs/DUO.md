# Duo Mode

Duo Mode shares committed Madoka catalog selections between two computers. The relay carries champion/skin/chroma/form IDs, library compatibility, phase, desired/prepared state, revisions and freshness. Packages, LCU credentials and player account identifiers remain on each computer. There is no file transfer, account registration, or Live Client/port-2999 dependency.

## Run locally

Install .NET 10 SDK, Node.js supported by Wrangler, and the existing native/backend requirements in [BUILDING.md](BUILDING.md). From the repository:

```powershell
Push-Location relay
npm ci
npm test
npx tsc --noEmit
npm run dev -- --ip 127.0.0.1 --port 8787
```

In another terminal:

```powershell
$env:MADOKACHANGER_DUO_URL='http://127.0.0.1:8787'
dotnet run --project src/MadokaChanger.App
```

The deployed relay is the default, so a normal install needs no relay setup. Both PCs need the same relay (the default or the same custom HTTPS URL), installed catalog commit and metadata patch, and exact selected packages. HTTP is accepted for loopback development only. Invalid custom configuration disables Duo without preventing normal app startup. Set `MADOKACHANGER_DUO_URL` in the desktop process environment only to use a different relay; restart after changing it. See [the example environment file](../.env.example); the desktop does not load `.env` files automatically.

## Deployment

The public relay is deployed on Cloudflare Workers Free with one SQLite-backed Durable Object at `https://madokachanger-duo-relay.intogummybear.workers.dev` (Worker version `11ef3f78-abbd-4515-b9fd-bcfc606aaea4`). Desktop builds use this address by default. To deploy a new version, authenticate to the Cloudflare account with Workers Free and run:

```powershell
Push-Location relay
npm ci
npx wrangler login
npx wrangler deploy
Pop-Location
# Set this only when testing a different deployment. Normal installs use the public default.
$env:MADOKACHANGER_DUO_URL='https://your-worker.your-subdomain.workers.dev'
```

Check the account is on Workers Free before deploying; this feature does not require enabling a paid plan. `wrangler.toml` contains the SQLite migration and binding. No database secrets or tokens belong in desktop configuration. Resource quotas are shared with other Workers in that account. Recheck quotas before deployment and monitor actual usage; abusive traffic can exhaust free quotas and make the relay unavailable.

Official documentation checked October 19, 2026: [Durable Objects pricing](https://developers.cloudflare.com/durable-objects/platform/pricing/) permits SQLite Durable Objects on Free, with 100,000 requests/day, 13,000 GB-s/day, 5 million SQL rows read/day, 100,000 rows written/day and 5 GB storage. [Workers limits](https://developers.cloudflare.com/workers/platform/limits/) describes the Free request/CPU limits. Four clients polling every ten seconds create 34,560 requests/day, plus state changes. Each ordinary heartbeat changes one unindexed member row. A single continuously resident 128 MB object would consume about 11,059 GB-s/day; eligibility for hibernation reduces this. This design uses short HTTP requests, so it has no long-lived connection duration dependency. Reconnect uses bounded requests and snapshots with the existing member token. See [relay details](../relay/README.md) for limits, expiration and quota estimates.

## Architecture and protocol

`LcuLeagueStateSource` reads the local LCU phase and local champion; [LCU schema/source notes](DUO-LCU.md) identify endpoints and matching rules. `DuoCoordinator` starts its one-second local LCU monitor after app state and the library are loaded, whether or not a party is joined. It only publishes local metadata while joined. `PartyService` serializes network work, debounces changed local state, polls every ten seconds, validates snapshots and reconnects with backoff/jitter. The coordinator combines committed preferences with the locally locked champion and peer state, and schedules safe preparation. `DuoOverlay` is ephemeral and never writes to `StateStore.Selections`.

One durable SQLite coordinator owns all rooms, code allocation, membership, revisions and abuse counters. Four-digit codes use cryptographic rejection sampling and atomic collision handling. Invite codes expire after ten minutes without a second member. After admission each member receives a separate 256-bit bearer token; only its hash is stored. A used room expires after two hours without any member activity. Stale presence is shown after 35 seconds; stale membership still reserves its slot for reconnect. An explicit Leave releases the slot.

Protocol v1 uses camelCase JSON. `POST /v1/rooms` with `{ "version": 1 }` creates; `POST /v1/rooms/join` also accepts a four-digit `code` string (leading zeroes retained). Both return `{snapshot,memberId,token}`. Authenticated requests use `Authorization: Bearer …`: `GET /v1/rooms/{roomId}?generation=…` obtains a snapshot/heartbeat; `PUT /v1/rooms/{roomId}/state` publishes `{version,roomId,generation,revision,state}`; `DELETE /v1/rooms/{roomId}?generation=…` leaves. Snapshots contain `version,roomId,generation,code,revision,expiresAt,members`; each member contains `memberId,revision,state,lastSeen`. State contains `championId,desired,prepared,phase,preparation,libraryCommit,metadataPatch`. Selection shape is `{championId,skinId,chromaId,form}`. Errors contain `error,message`. The relay authenticates the token's member; clients cannot choose another member to update. Old room generations and revisions are rejected. Tokens are memory-only, so app restarts require creating/joining again.

## Overlay lifecycle and limits

The patcher redirects champion archives/files rather than individual player entities. Only the local locked-in champion's catalog selection and enabled custom packages for that same champion can enter the local build. Your saved preferences for other champions remain intact and are ignored until you lock in one of them. Enabled custom packages for unrelated champions are ignored automatically and do not need to be disabled. A valid exact peer catalog selection may be added for a different champion; it temporarily replaces that champion's saved local catalog preference. A peer default removes that unrelated override. Missing/ambiguous packages and differing library versions produce an unavailable state; no substitute skin is chosen. Imported packages are local only and are never transferred or applied on behalf of the peer.

Preparation waits for the local champion to lock in, preserving all other saved preferences while League is unavailable or no local champion is known. Pressing **Start** before lock-in arms the session in a waiting state without launching a host. When a valid configuration becomes available, the patcher starts automatically. **Stop** cancels the waiting intent.

If both players use the same champion with different choices, each computer retains its local player's choice and displays a conflict. This backend cannot give two instances of one champion different appearances. Imported custom packages cannot be synchronized because they have no shared stable catalog identity.

Desired means committed Madoka state. Prepared means the exact selection captured in the current successfully started overlay, which may still differ from desired. Prepared does not claim visible in-game rendering; the existing patcher status reports actual archive redirections when available.

Changes to the effective configuration trigger a debounced rebuild for any active patcher session, with or without a Duo party. This includes committed catalog choices and applicable enabled custom packages. Rebuilds serialize with Start/Stop and retain the operation gate across host restart; package operations cannot interleave. The game-process guard is checked before build and again before host startup; LCU active phases also defer scheduling. A stopped patcher stays stopped. Mid-game updates remain pending until the game closes. **Stop** cancels waiting and pending automatic work; a later **Start** arms the session again. A failed rebuild leaves an actionable error. No hot reload is simulated. Leave clears logical peer state immediately; physical removal waits for a safe point if a game is active. Once cleanup finishes, automatic peer updates stop until Duo is joined again, while solo local-champion monitoring continues.

## Automated verification

```powershell
dotnet build MadokaChanger.sln -c Release
dotnet test tests/MadokaChanger.Tests -c Release
dotnet run --project tools/MadokaChanger.DuoVerify -c Release -- http://127.0.0.1:8787
```

Run the final command with a fresh local `npm run dev` relay for a repeatable local check. It uses two real HTTP transports/coordinators, isolated temporary data roots, simulated LCU state and the process-based fake patcher. The deployed public relay has also passed this end-to-end check. Both checks verify bidirectional metadata, temporary merging, no unsolicited Start, pending active-game changes, safe automatic rebuild and leave cleanup. They never attach to a game. WPF checks use the existing `MadokaChanger.UiVerify` tool with an isolated metadata fixture and complete backend files; Duo layouts cover both palettes and small/large viewports. See [verification results](../verification/DUO.md).

## Two-PC manual checklist

1. Install the same library on A and B. Configure the same HTTPS relay and restart both apps. Confirm that no patcher starts merely by opening Duo; a local-only champion monitor may run, but a patcher host starts only after the user presses Start.
2. A creates a party. Copy its four-digit code (including any zero prefix); B joins. Confirm a third client gets a full-room message and that invalid/expired codes produce useful messages.
3. Enter champion select with different champions. Confirm each app detects its own champion. Commit different skin/chroma/form selections using Apply; previews alone must not publish. Swap champions and confirm detection follows the new local champion.
4. Press Start on each computer before launching the game. Confirm each effective build contains both exact selections and the UI separately reports desired/prepared status. Verify both appearances visually in the actual game; record patcher redirection/error evidence.
5. Commit a different peer skin while in game. Confirm pending status and unchanged current prepared selection. End/close the game; confirm automatic rebuild at the next safe opportunity, then verify the new appearance in the next game.
6. Disconnect/reconnect a network connection briefly; confirm fresh snapshots recover the same member without adding a third slot. Restart League, dodge a lobby, and create another lobby; confirm no old champion survives an unrelated session.
7. Try differing library versions, a missing exact chroma/form package, and an imported package. Confirm unavailable messages without substitute skins. Try the same champion with different choices and confirm the explicit per-champion limitation.
8. Leave during a game. Confirm temporary peer state is cleared and physical changes wait for game exit. Confirm saved preferences are unchanged. Stop the patcher, change the partner's selection, and confirm it stays stopped. Exit both apps and confirm background work/host processes terminate.
