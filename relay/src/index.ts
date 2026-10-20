export interface Env {
  DUO_DIRECTORY: DurableObjectNamespace;
}

const VERSION = 1;
const INVITE_TTL_MS = 10 * 60_000;
const ROOM_IDLE_TTL_MS = 2 * 60 * 60_000;
const ROOM_EXPIRY_BUCKET_MS = 10 * 60_000;
const PRESENCE_TTL_MS = 35_000;
const HEARTBEAT_MIN_INTERVAL_MS = 3_000;
const PUBLISH_MIN_INTERVAL_MS = 1_000;
const MAX_BODY_BYTES = 16 * 1024;
const CREATE_LIMIT = 5;
const JOIN_LIMIT = 10;

type State = {
  championId: number | null;
  desired: Selection | null;
  prepared: Selection | null;
  phase: string;
  preparation: "stopped" | "preparing" | "prepared" | "pending" | "unavailable" | "error";
  libraryCommit: string | null;
  metadataPatch: string | null;
};

type Selection = { championId: number; skinId: number; chromaId: number | null; form: string | null };
type Room = { room_id: string; generation: string; code: string; revision: number; expires_at: number; active: number };
type Member = { member_id: string; revision: number; state_json: string; last_seen: number; last_publish: number };

export default {
  async fetch(request: Request, env: Env): Promise<Response> {
    const id = env.DUO_DIRECTORY.idFromName("duo-directory-v1");
    return env.DUO_DIRECTORY.get(id).fetch(request);
  },
};

export class DuoDirectory {
  private readonly sql: SqlStorage;

  constructor(private readonly ctx: DurableObjectState) {
    this.sql = ctx.storage.sql;
    this.sql.exec(`CREATE TABLE IF NOT EXISTS rooms (
        room_id TEXT PRIMARY KEY,
        generation TEXT NOT NULL,
        code TEXT NOT NULL UNIQUE,
        revision INTEGER NOT NULL,
        expires_at INTEGER NOT NULL,
        active INTEGER NOT NULL DEFAULT 0
      )`);
    this.sql.exec("CREATE INDEX IF NOT EXISTS rooms_expiry ON rooms(expires_at)");
    this.sql.exec(`CREATE TABLE IF NOT EXISTS members (
        room_id TEXT NOT NULL REFERENCES rooms(room_id) ON DELETE CASCADE,
        member_id TEXT NOT NULL,
        token_hash TEXT NOT NULL UNIQUE,
        revision INTEGER NOT NULL,
        state_json TEXT NOT NULL,
        last_seen INTEGER NOT NULL,
        last_publish INTEGER NOT NULL DEFAULT 0,
        PRIMARY KEY(room_id, member_id)
      )`);
    this.sql.exec(`CREATE TABLE IF NOT EXISTS rate_limits (
        bucket_key TEXT PRIMARY KEY,
        count INTEGER NOT NULL,
        expires_at INTEGER NOT NULL
      )`);
    this.sql.exec("CREATE INDEX IF NOT EXISTS rate_limits_expiry ON rate_limits(expires_at)");
  }

  async fetch(request: Request): Promise<Response> {
    const now = Date.now();
    this.prune(now);
    const url = new URL(request.url);
    const path = url.pathname;

    try {
      if (request.method === "POST" && path === "/v1/rooms") {
        const body = await readJson(request);
        if (!exactKeys(body, ["version"]) || body.version !== VERSION) return error(400, "unsupported_version", "This relay version is not supported.");
        const limited = await this.takeRateLimit(request, "create", CREATE_LIMIT, now);
        if (limited) return limited;
        return await this.createRoom(now);
      }
      if (request.method === "POST" && path === "/v1/rooms/join") {
        const body = await readJson(request);
        if (!exactKeys(body, ["version", "code"]) || body.version !== VERSION) return error(400, "unsupported_version", "This relay version is not supported.");
        if (typeof body.code !== "string" || !/^\d{4}$/.test(body.code)) return error(400, "invalid_code", "Enter a four digit room code.");
        const limited = await this.takeRateLimit(request, "join", JOIN_LIMIT, now);
        if (limited) return limited;
        return await this.joinRoom(body.code, now);
      }

      const match = path.match(/^\/v1\/rooms\/([0-9a-f-]{36})(?:\/(state))?$/i);
      if (!match) return error(404, "not_found", "That relay endpoint was not found.");
      const [, roomId, statePath] = match;
      const generation = url.searchParams.get("generation");
      const token = bearer(request);
      if (!token) return error(401, "unauthorized", "This room membership is no longer valid.");
      const tokenHash = await sha256(token);

      if (request.method === "GET" && !statePath) {
        if (!generation) return error(400, "invalid_request", "Room generation is required.");
        return this.withMember(roomId, generation, tokenHash, now, (room) => json(this.snapshot(room, now)));
      }
      if (request.method === "PUT" && statePath === "state") {
        const body = await readJson(request);
        if (!exactKeys(body, ["version", "roomId", "generation", "revision", "state"]) || body.version !== VERSION) return error(400, "unsupported_version", "This relay version is not supported.");
        if (body.roomId !== roomId || typeof body.generation !== "string" || !Number.isSafeInteger(body.revision) || body.revision < 1 || !validState(body.state)) {
          return error(400, "invalid_request", "The state update is not valid.");
        }
        return this.withMember(roomId, body.generation, tokenHash, now, (room, member) => {
          if (body.revision <= member.revision) return error(409, "stale_revision", "A newer state update was already received.");
          if (now - member.last_publish < PUBLISH_MIN_INTERVAL_MS) return error(429, "rate_limited", "Please wait before publishing another state update.");
          this.sql.exec("UPDATE members SET revision = ?, state_json = ?, last_seen = ?, last_publish = ? WHERE room_id = ? AND member_id = ?", body.revision, JSON.stringify(body.state), now, now, roomId, member.member_id);
          const scheduledExpiry = scheduledRoomExpiry(now);
          this.sql.exec("UPDATE rooms SET revision = MAX(revision + 1, ?), expires_at = CASE WHEN active = 1 THEN MAX(expires_at, ?) ELSE expires_at END WHERE room_id = ?", body.revision, scheduledExpiry, roomId);
          const updated = this.room(roomId)!;
          return json(this.snapshot(updated, now));
        }, false);
      }
      if (request.method === "DELETE" && !statePath) {
        if (!generation) return error(400, "invalid_request", "Room generation is required.");
        return this.withMember(roomId, generation, tokenHash, now, (_room, member) => {
          this.sql.exec("DELETE FROM members WHERE room_id = ? AND member_id = ?", roomId, member.member_id);
          this.sql.exec("UPDATE rooms SET revision = revision + 1 WHERE room_id = ?", roomId);
          this.removeEmptyRooms();
          return new Response(null, { status: 204 });
        }, false);
      }
      return error(404, "not_found", "That relay endpoint was not found.");
    } catch (e) {
      if (e instanceof HttpFailure) return error(e.status, e.code, e.message);
      return error(400, "invalid_request", "The request could not be processed.");
    }
  }

  private async createRoom(now: number): Promise<Response> {
    for (let attempt = 0; attempt < 12; attempt++) {
      const code = chooseAvailableCode((candidate) => this.sql.exec<{ found: number }>("SELECT 1 AS found FROM rooms WHERE code = ?", candidate).toArray().length > 0, undefined, 1);
      if (!code) continue;
      const roomId = crypto.randomUUID();
      const generation = crypto.randomUUID();
      const memberId = crypto.randomUUID();
      const token = randomToken();
      const tokenHash = await sha256(token);
      try {
        this.ctx.storage.transactionSync(() => {
          this.sql.exec("INSERT INTO rooms(room_id,generation,code,revision,expires_at) VALUES(?,?,?,?,?)", roomId, generation, code, 1, now + INVITE_TTL_MS);
          this.sql.exec("INSERT INTO members(room_id,member_id,token_hash,revision,state_json,last_seen) VALUES(?,?,?,?,?,?)", roomId, memberId, tokenHash, 0, JSON.stringify(initialState()), now);
        });
        const room = this.room(roomId)!;
        return json({ snapshot: this.snapshot(room, now), memberId, token }, 201);
      } catch (e) {
        if (!isUniqueError(e)) throw e;
      }
    }
    return error(503, "room_unavailable", "A room could not be created. Please try again.");
  }

  private async joinRoom(code: string, now: number): Promise<Response> {
    const memberId = crypto.randomUUID();
    const token = randomToken();
    const tokenHash = await sha256(token);
    const outcome: { value: "missing" | "expired" | "full" | "joined" } = { value: "missing" };
    this.ctx.storage.transactionSync(() => {
      const rows = this.sql.exec<Room>("SELECT * FROM rooms WHERE code = ?", code).toArray();
      const room = rows[0];
      if (!room) return;
      if (room.active ? this.latestActivity(room.room_id) + ROOM_IDLE_TTL_MS <= now : room.expires_at <= now) {
        this.deleteRoom(room.room_id);
        outcome.value = "expired";
        return;
      }
      const count = this.sql.exec<{ n: number }>("SELECT COUNT(*) AS n FROM members WHERE room_id = ?", room.room_id).toArray()[0]?.n ?? 0;
      if (count >= 2) { outcome.value = "full"; return; }
      this.sql.exec("INSERT INTO members(room_id,member_id,token_hash,revision,state_json,last_seen) VALUES(?,?,?,?,?,?)", room.room_id, memberId, tokenHash, 0, JSON.stringify(initialState()), now);
      this.sql.exec("UPDATE rooms SET revision = revision + 1, active = 1, expires_at = ? WHERE room_id = ?", scheduledRoomExpiry(now), room.room_id);
      outcome.value = "joined";
    });
    if (outcome.value === "expired") return error(410, "invite_expired", "That room invitation has expired.");
    if (outcome.value === "full") return error(409, "room_full", "That room already has two members.");
    if (outcome.value !== "joined") return error(404, "room_not_found", "That room code was not found.");
    const room = this.sql.exec<Room>("SELECT r.* FROM rooms r JOIN members m ON m.room_id = r.room_id WHERE m.member_id = ?", memberId).toArray()[0];
    return json({ snapshot: this.snapshot(room, now), memberId, token }, 201);
  }

  private withMember(roomId: string, generation: string, tokenHash: string, now: number,
    action: (room: Room, member: Member) => Response, heartbeat = true): Response {
    const result = this.ctx.storage.transactionSync(() => {
      const room = this.room(roomId);
      if (!room || room.generation !== generation) return { response: error(404, "room_not_found", "This room membership is no longer valid.") };
      const member = this.sql.exec<Member>("SELECT member_id,revision,state_json,last_seen,last_publish FROM members WHERE room_id = ? AND token_hash = ?", roomId, tokenHash).toArray()[0];
      if (!member) return { response: error(401, "unauthorized", "This room membership is no longer valid.") };
      if (room.active ? this.latestActivity(roomId) + ROOM_IDLE_TTL_MS <= now : room.expires_at <= now) {
        this.deleteRoom(roomId);
        return { response: error(404, "room_not_found", "This room membership is no longer valid.") };
      }
      if (heartbeat) {
        if (now - member.last_seen < HEARTBEAT_MIN_INTERVAL_MS) return { response: error(429, "rate_limited", "Please wait before sending another room heartbeat.") };
        this.sql.exec("UPDATE members SET last_seen = ? WHERE room_id = ? AND member_id = ?", now, roomId, member.member_id);
        member.last_seen = now;
        const scheduledExpiry = scheduledRoomExpiry(now);
        if (room.active && scheduledExpiry > room.expires_at) {
          this.sql.exec("UPDATE rooms SET expires_at = ? WHERE room_id = ?", scheduledExpiry, roomId);
          room.expires_at = scheduledExpiry;
        }
      }
      return { response: action(room, member) };
    });
    return result.response;
  }

  private snapshot(room: Room, now: number) {
    const members = this.sql.exec<Member>("SELECT member_id,revision,state_json,last_seen,last_publish FROM members WHERE room_id = ? AND last_seen > ? ORDER BY rowid", room.room_id, now - PRESENCE_TTL_MS).toArray().map((m) => ({
      memberId: m.member_id, revision: m.revision, state: JSON.parse(m.state_json), lastSeen: new Date(m.last_seen).toISOString(),
    }));
    const expiresAt = room.active ? this.latestActivity(room.room_id) + ROOM_IDLE_TTL_MS : room.expires_at;
    return { version: VERSION, roomId: room.room_id, generation: room.generation, code: room.code,
      revision: room.revision, expiresAt: new Date(expiresAt).toISOString(), members };
  }

  private room(id: string): Room | undefined { return this.sql.exec<Room>("SELECT * FROM rooms WHERE room_id = ?", id).toArray()[0]; }
  private latestActivity(id: string): number { return this.sql.exec<{ latest: number }>("SELECT COALESCE(MAX(last_seen),0) AS latest FROM members WHERE room_id = ?", id).toArray()[0]?.latest ?? 0; }

  private async takeRateLimit(request: Request, kind: string, limit: number, now: number): Promise<Response | null> {
    const ip = request.headers.get("cf-connecting-ip") ?? "unknown";
    const ipHash = await sha256(ip);
    const minute = Math.floor(now / 60_000);
    const key = `${kind}:${ipHash}:${minute}`;
    const allowed = this.ctx.storage.transactionSync(() => {
      const count = this.sql.exec<{ count: number }>("SELECT count FROM rate_limits WHERE bucket_key = ?", key).toArray()[0]?.count ?? 0;
      if (count >= limit) return false;
      this.sql.exec("INSERT INTO rate_limits(bucket_key,count,expires_at) VALUES(?,?,?) ON CONFLICT(bucket_key) DO UPDATE SET count = excluded.count", key, count + 1, (minute + 2) * 60_000);
      return true;
    });
    return allowed ? null : error(429, "rate_limited", "Too many room requests. Please wait a minute and try again.");
  }

  private prune(now: number): void {
    this.ctx.storage.transactionSync(() => {
      const ids = this.sql.exec<{ room_id: string }>("SELECT room_id FROM rooms WHERE expires_at <= ? LIMIT 64", now - 60_000).toArray();
      for (const { room_id } of ids) this.deleteRoom(room_id);
      this.sql.exec("DELETE FROM rate_limits WHERE bucket_key IN (SELECT bucket_key FROM rate_limits WHERE expires_at <= ? LIMIT 64)", now);
    });
  }

  private deleteRoom(id: string): void {
    this.sql.exec("DELETE FROM members WHERE room_id = ?", id);
    this.sql.exec("DELETE FROM rooms WHERE room_id = ?", id);
  }

  private removeEmptyRooms(): void {
    const ids = this.sql.exec<{ room_id: string }>("SELECT r.room_id FROM rooms r WHERE NOT EXISTS (SELECT 1 FROM members m WHERE m.room_id = r.room_id) LIMIT 64").toArray();
    for (const { room_id } of ids) this.deleteRoom(room_id);
  }
}

function initialState(): State {
  return { championId: null, desired: null, prepared: null, phase: "Unknown", preparation: "stopped", libraryCommit: null, metadataPatch: null };
}

function scheduledRoomExpiry(now: number): number {
  return Math.ceil((now + ROOM_IDLE_TTL_MS) / ROOM_EXPIRY_BUCKET_MS) * ROOM_EXPIRY_BUCKET_MS;
}

function validState(value: unknown): value is State {
  if (!isRecord(value) || !exactKeys(value, ["championId", "desired", "prepared", "phase", "preparation", "libraryCommit", "metadataPatch"])) return false;
  const champion = value.championId;
  if (!(champion === null || Number.isInteger(champion) && champion > 0 && champion <= 10000)) return false;
  if (!validSelection(value.desired) || !validSelection(value.prepared)) return false;
  if (value.desired && value.desired.championId !== champion || value.prepared && value.prepared.championId !== champion) return false;
  if (typeof value.phase !== "string" || value.phase.length < 1 || value.phase.length > 40) return false;
  if (!("stopped preparing prepared pending unavailable error".split(" ").includes(value.preparation as string))) return false;
  if (!(value.libraryCommit === null || typeof value.libraryCommit === "string" && value.libraryCommit.length <= 128)) return false;
  if (!(value.metadataPatch === null || typeof value.metadataPatch === "string" && value.metadataPatch.length <= 64)) return false;
  return true;
}

function validSelection(value: unknown): value is Selection | null {
  if (value === null) return true;
  if (!isRecord(value) || !exactKeys(value, ["championId", "skinId", "chromaId", "form"])) return false;
  const { championId, skinId, chromaId, form } = value;
  return Number.isInteger(championId) && championId > 0 && championId <= 10000
    && Number.isInteger(skinId) && skinId > 0 && Math.floor(skinId / 1000) === championId
    && (chromaId === null || Number.isInteger(chromaId) && chromaId > 0)
    && (form === null || typeof form === "string" && form.length > 0 && form.length <= 64 && /^[A-Za-z0-9_ -]+$/.test(form));
}

function exactKeys(value: unknown, keys: string[]): value is Record<string, any> {
  return isRecord(value) && Object.keys(value).length === keys.length && keys.every((key) => Object.hasOwn(value, key));
}
function isRecord(value: unknown): value is Record<string, any> { return typeof value === "object" && value !== null && !Array.isArray(value); }

async function readJson(request: Request): Promise<any> {
  const length = Number(request.headers.get("content-length") ?? 0);
  if (length > MAX_BODY_BYTES) {
    await request.body?.cancel();
    throw new HttpFailure(413, "request_too_large", "The request is too large.");
  }
  if (!request.headers.get("content-type")?.toLowerCase().startsWith("application/json")) {
    await request.body?.cancel();
    throw new HttpFailure(400, "invalid_request", "A JSON request body is required.");
  }
  const reader = request.body?.getReader();
  const chunks: Uint8Array[] = [];
  let size = 0;
  if (reader) {
    while (true) {
      const { done, value } = await reader.read();
      if (done) break;
      size += value.byteLength;
      if (size > MAX_BODY_BYTES) {
        await reader.cancel();
        throw new HttpFailure(413, "request_too_large", "The request is too large.");
      }
      chunks.push(value);
    }
  }
  const bytes = new Uint8Array(size);
  let offset = 0;
  for (const chunk of chunks) { bytes.set(chunk, offset); offset += chunk.byteLength; }
  try { return JSON.parse(new TextDecoder("utf-8", { fatal: true }).decode(bytes)); }
  catch { throw new HttpFailure(400, "invalid_request", "The request body is not valid JSON."); }
}

function bearer(request: Request): string | null {
  const value = request.headers.get("authorization");
  const match = value && /^Bearer ([A-Za-z0-9_-]{40,64})$/.exec(value);
  return match?.[1] ?? null;
}

export function generateCode(random: (buffer: Uint32Array) => void = (buffer) => crypto.getRandomValues(buffer)): string {
  const bound = Math.floor(0x1_0000_0000 / 10_000) * 10_000;
  const values = new Uint32Array(1);
  do { random(values); } while (values[0] >= bound);
  return String(values[0] % 10_000).padStart(4, "0");
}

export function chooseAvailableCode(isTaken: (code: string) => boolean,
  random: (buffer: Uint32Array) => void = (buffer) => crypto.getRandomValues(buffer), attempts = 12): string | null {
  for (let attempt = 0; attempt < attempts; attempt++) {
    const code = generateCode(random);
    if (!isTaken(code)) return code;
  }
  return null;
}

function randomToken(): string {
  const bytes = crypto.getRandomValues(new Uint8Array(32));
  let binary = "";
  for (const byte of bytes) binary += String.fromCharCode(byte);
  return btoa(binary).replaceAll("+", "-").replaceAll("/", "_").replace(/=+$/, "");
}
async function sha256(value: string): Promise<string> {
  const digest = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(value));
  return [...new Uint8Array(digest)].map((b) => b.toString(16).padStart(2, "0")).join("");
}

function json(value: unknown, status = 200): Response {
  return new Response(JSON.stringify(value), { status, headers: { "content-type": "application/json; charset=utf-8", "cache-control": "no-store" } });
}
function error(status: number, code: string, message: string): Response { return json({ error: code, message }, status); }
class HttpFailure extends Error { constructor(readonly status: number, readonly code: string, message: string) { super(message); } }
function isUniqueError(error: unknown): boolean { return error instanceof Error && /UNIQUE constraint failed/i.test(error.message); }
