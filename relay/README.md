# MadokaChanger Duo relay

This Cloudflare Worker hosts the v1 Duo room API. Every request is routed to one SQLite-backed Durable Object named `duo-directory-v1`; its SQL tables are the source of truth for room codes, membership, state revisions, rate limits, and expiry. The Worker keeps no process-local room state.

## Local development

```sh
npm ci
npm test
npx tsc --noEmit
npm run dev
```

The integration tests run against the Worker and Durable Object in Workerd. Local development does not create Cloudflare resources.

## Deploy to a free Cloudflare account

Cloudflare offers SQLite-backed Durable Objects on Workers Free. Its current free allocations include 100,000 Worker requests per day at 10 ms CPU per request; 100,000 Durable Object requests, 13,000 GB-s, five million SQLite rows read, 100,000 rows written, and 5 GB of stored SQL data per day or account as documented. Confirm the target account is on Workers Free in the dashboard before deploying. Do not enable Workers Paid or add billing resources for this relay.

From PowerShell, in the `relay` folder:

```powershell
npm ci
npx wrangler login
npx wrangler whoami
$env:CLOUDFLARE_ACCOUNT_ID = "<the Workers Free account ID>"
npx wrangler whoami --account $env:CLOUDFLARE_ACCOUNT_ID
npm run deploy
```

Wrangler opens a browser for login. Use the account selector in the dashboard to verify the account's plan, then set `CLOUDFLARE_ACCOUNT_ID` to that account's ID for the deployment. Enable the account's `workers.dev` subdomain in Workers & Pages if it has not been configured. The deploy output gives the relay URL, usually `https://madokachanger-duo-relay.<account-subdomain>.workers.dev`.

Set the URL for MadokaChanger in the PowerShell session that launches the desktop app, then restart the app:

```powershell
$env:MADOKACHANGER_DUO_URL = "https://madokachanger-duo-relay.<account-subdomain>.workers.dev"
```

For a persistent Windows user setting, use `[Environment]::SetEnvironmentVariable("MADOKACHANGER_DUO_URL", "https://madokachanger-duo-relay.<account-subdomain>.workers.dev", "User")` and restart MadokaChanger. The Worker has observability disabled, and request or membership data is not logged by this project. No Cloudflare account is selected in source, and no resource is provisioned until the operator runs the deploy command.

## API and protections

The routes and JSON fields match `docs/DUO-CONTRACT.md` and `src/MadokaChanger.Core/DuoProtocol.cs`: create, join, snapshot, publish, and leave. Create and join are limited per hashed client IP to 5 and 10 attempts per minute. Room codes are generated with rejection sampling so each four digit code has equal probability, including codes beginning with zero; insertion retries collisions. Membership tokens contain 256 random bits and only SHA-256 token hashes are stored. Room and generation IDs are random UUIDs. Bodies are limited to 16 KiB and state fields follow the shared protocol's selection and revision rules.

An unused invitation expires ten minutes after creation even when its owner polls. Once a second member joins, the active room expires two hours after the latest member heartbeat. Polling updates only that member row, at most once every three seconds; publication is limited to one per second and updates the member row along with the room revision. The `last_seen` column has no secondary index, so ordinary ten second polling does not rewrite an index. Members disappear from snapshots after 35 seconds without a heartbeat, while a still-active peer keeps the room alive and the stale member continues to occupy capacity until the room expires or they leave.

At four users polling once every ten seconds, the relay handles 34,560 requests per day at both the Worker and Durable Object, about 35% of each free request allowance. Each poll writes one member row, or about 34,560 row writes per day. Member primary keys and token-hash indexes do not change during a heartbeat, and `last_seen` has no secondary index. The room expiry index changes at most once per ten minutes per active room; for two active rooms that adds at most 288 room-row and expiry-index updates each day. State publications, joins, leaves, and bounded cleanup add occasional writes. Even budgeting eight SQL rows read per poll gives roughly 276,480 reads per day, below the five million free daily SQLite read allowance. There is over 65,000 rows-written daily headroom in this four-user estimate for state publications and other room activity.

The SQLite Durable Object allocation is 13,000 GB-s per day. A deliberately conservative upper bound that holds its 128 MB allocation active for all 24 hours is 11,059.2 GB-s (`128 MB / 1 GB × 86,400 seconds`), below that daily allocation. Real polling requests finish and leave the object eligible to hibernate between requests. The Worker Free CPU cap is 10 ms per request. These estimates use current documented limits and assume four active clients without abusive traffic; review the [Durable Objects pricing](https://developers.cloudflare.com/durable-objects/platform/pricing/) and [Workers limits](https://developers.cloudflare.com/workers/platform/limits/) before deployment.

The Workerd test runtime currently uses compatibility date `2025-09-06`, the newest date supported by the installed local runtime. Update `compatibility_date` and rerun the suite when refreshing Wrangler and Workerd dependencies. No Worker, Durable Object, database, account, or billing resource has been provisioned by this project setup.
