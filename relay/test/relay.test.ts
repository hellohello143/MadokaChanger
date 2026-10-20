import { afterEach, describe, expect, it, vi } from "vitest";
import { SELF } from "cloudflare:test";
import { chooseAvailableCode, generateCode } from "../src/index";

const base = "https://relay.test";
const headers = { "content-type": "application/json", "cf-connecting-ip": "192.0.2.11" };
const initialState = {
  championId: null, desired: null, prepared: null, phase: "Unknown", preparation: "stopped",
  libraryCommit: null, metadataPatch: null,
};

async function create(ip = "192.0.2.11") {
  const response = await SELF.fetch(`${base}/v1/rooms`, { method: "POST", headers: { ...headers, "cf-connecting-ip": ip }, body: JSON.stringify({ version: 1 }) });
  expect(response.status).toBe(201);
  return await response.json() as any;
}
async function join(code: string, ip = "192.0.2.12") {
  return SELF.fetch(`${base}/v1/rooms/join`, { method: "POST", headers: { ...headers, "cf-connecting-ip": ip }, body: JSON.stringify({ version: 1, code }) });
}
function auth(token: string) { return { authorization: `Bearer ${token}` }; }
function advanceClock(ms: number) {
  if (!vi.isFakeTimers()) vi.useFakeTimers();
  vi.setSystemTime(new Date(Date.now() + ms));
}

afterEach(() => { vi.useRealTimers(); });

describe("Duo relay", () => {
  it("keeps the room revision at least as high as a valid member revision", async () => {
    const owner = await create();
    const revision = 100;
    const published = await SELF.fetch(`${base}/v1/rooms/${owner.snapshot.roomId}/state`, {
      method: "PUT", headers: { ...headers, ...auth(owner.token) }, body: JSON.stringify({
        version: 1, roomId: owner.snapshot.roomId, generation: owner.snapshot.generation, revision, state: initialState,
      }),
    });
    expect(published.status).toBe(200);
    const snapshot = await published.json() as any;
    expect(snapshot.revision).toBeGreaterThanOrEqual(revision);
    expect(snapshot.members.find((m: any) => m.memberId === owner.memberId).revision).toBe(revision);
  });
  it("generates four digits without modulo bias and preserves leading zeroes", () => {
    let draw = 0;
    const random = (buffer: Uint32Array) => { buffer[0] = draw++ === 0 ? 0xffff_ffff : 7; };
    expect(generateCode(random)).toBe("0007");
    const values = [7, 7, 8];
    expect(chooseAvailableCode((code) => code === "0007", (buffer) => { buffer[0] = values.shift()!; })).toBe("0008");
    expect(chooseAvailableCode(() => true, (buffer) => { buffer[0] = 5; }, 2)).toBeNull();
  });

  it("admits only one concurrent second member and rejects a third", async () => {
    const owner = await create();
    const [first, second] = await Promise.all([join(owner.snapshot.code), join(owner.snapshot.code)]);
    await Promise.all([first.clone().arrayBuffer(), second.clone().arrayBuffer()]);
    const admitted = [first, second].filter((r) => r.status === 201);
    const full = [first, second].filter((r) => r.status === 409);
    expect(admitted).toHaveLength(1);
    expect(full).toHaveLength(1);
    vi.useFakeTimers();
    vi.setSystemTime(new Date(Date.now() + 11 * 60_000));
    const third = await join(owner.snapshot.code, "192.0.2.13");
    expect(third.status).toBe(409);
    expect((await third.json() as any).error).toBe("room_full");
  });

  it("expires unused invitations after ten minutes", async () => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date("2026-10-19T12:00:00Z"));
    const owner = await create();
    vi.setSystemTime(new Date("2026-10-19T12:09:00Z"));
    const heartbeat = await SELF.fetch(`${base}/v1/rooms/${owner.snapshot.roomId}?generation=${owner.snapshot.generation}`, { headers: auth(owner.token) });
    expect(heartbeat.status).toBe(200);
    const refreshed = await heartbeat.json() as any;
    expect(refreshed.expiresAt).toBe(owner.snapshot.expiresAt);
    const publish = await SELF.fetch(`${base}/v1/rooms/${owner.snapshot.roomId}/state`, {
      method: "PUT", headers: { ...headers, ...auth(owner.token) }, body: JSON.stringify({ version: 1, roomId: owner.snapshot.roomId,
        generation: owner.snapshot.generation, revision: 1, state: initialState }),
    });
    expect(publish.status).toBe(200);
    expect((await publish.json() as any).expiresAt).toBe(owner.snapshot.expiresAt);
    vi.setSystemTime(new Date("2026-10-19T12:10:01Z"));
    const result = await join(owner.snapshot.code);
    expect(result.status).toBe(410);
    expect((await result.json() as any).error).toBe("invite_expired");
  });

  it("expires an abandoned active room after two hours", async () => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date("2026-10-19T12:00:00Z"));
    const owner = await create();
    const joined = await join(owner.snapshot.code);
    expect(joined.status).toBe(201);
    await joined.arrayBuffer();
    vi.setSystemTime(new Date("2026-10-19T14:00:02Z"));
    const expired = await SELF.fetch(`${base}/v1/rooms/${owner.snapshot.roomId}?generation=${owner.snapshot.generation}`, { headers: auth(owner.token) });
    await expired.arrayBuffer();
    expect(expired.status).toBe(404);
  });

  it("keeps a match alive on heartbeats and lets the same token reclaim its original slot", async () => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date("2026-10-19T12:00:00Z"));
    const owner = await create();
    const joined = await join(owner.snapshot.code);
    expect(joined.status).toBe(201);
    const guest = await joined.json() as any;

    vi.setSystemTime(new Date("2026-10-19T13:00:00Z"));
    const firstHeartbeat = await SELF.fetch(`${base}/v1/rooms/${owner.snapshot.roomId}?generation=${owner.snapshot.generation}`, { headers: auth(guest.token) });
    expect(firstHeartbeat.status).toBe(200);
    const alive = await firstHeartbeat.json() as any;
    expect(new Date(alive.expiresAt).getTime()).toBe(new Date("2026-10-19T15:00:00Z").getTime());

    vi.setSystemTime(new Date("2026-10-19T14:00:00Z"));
    const secondHeartbeat = await SELF.fetch(`${base}/v1/rooms/${owner.snapshot.roomId}?generation=${owner.snapshot.generation}`, { headers: auth(guest.token) });
    expect(secondHeartbeat.status).toBe(200);
    await secondHeartbeat.arrayBuffer();

    const reconnected = await SELF.fetch(`${base}/v1/rooms/${owner.snapshot.roomId}?generation=${owner.snapshot.generation}`, { headers: auth(owner.token) });
    expect(reconnected.status).toBe(200);
    const snapshot = await reconnected.json() as any;
    expect(snapshot.members).toHaveLength(2);
    expect(snapshot.members.map((member: any) => member.memberId)).toContain(owner.memberId);
    expect(snapshot.members.map((member: any) => member.memberId)).toContain(guest.memberId);
  });

  it("frees only the leaving member's slot so a new guest can join", async () => {
    const owner = await create();
    const guestResponse = await join(owner.snapshot.code);
    expect(guestResponse.status).toBe(201);
    const guest = await guestResponse.json() as any;

    const leave = await SELF.fetch(`${base}/v1/rooms/${owner.snapshot.roomId}?generation=${owner.snapshot.generation}`, {
      method: "DELETE", headers: auth(owner.token),
    });
    expect(leave.status).toBe(204);
    await leave.arrayBuffer();

    const replacementResponse = await join(owner.snapshot.code, "192.0.2.44");
    expect(replacementResponse.status).toBe(201);
    const replacement = await replacementResponse.json() as any;
    expect(replacement.snapshot.members).toHaveLength(2);
    expect(replacement.snapshot.members.map((member: any) => member.memberId)).toContain(guest.memberId);
    expect(replacement.snapshot.members.map((member: any) => member.memberId)).toContain(replacement.memberId);
  });

  it("limits room creation and join attempts by hashed client IP per minute", async () => {
    for (let i = 0; i < 5; i++) expect((await create("198.51.100.101")).token).toBeTruthy();
    const sixthCreate = await SELF.fetch(`${base}/v1/rooms`, {
      method: "POST", headers: { ...headers, "cf-connecting-ip": "198.51.100.101" }, body: JSON.stringify({ version: 1 }),
    });
    await sixthCreate.arrayBuffer();
    expect(sixthCreate.status).toBe(429);

    for (let i = 0; i < 10; i++) {
      const attempt = await join("0000", "198.51.100.102");
      expect(attempt.status).toBe(404);
      await attempt.arrayBuffer();
    }
    const eleventhJoin = await join("0000", "198.51.100.102");
    await eleventhJoin.arrayBuffer();
    expect(eleventhJoin.status).toBe(429);
  });

  it("isolates bearer tokens, publishes validated state, and returns fresh reconnect snapshots", async () => {
    const owner = await create();
    const joined = await join(owner.snapshot.code);
    expect(joined.status).toBe(201);
    const guest = await joined.json() as any;

    const stranger = await create();
    const denied = await SELF.fetch(`${base}/v1/rooms/${owner.snapshot.roomId}?generation=${owner.snapshot.generation}`, { headers: auth(stranger.token) });
    await denied.arrayBuffer();
    expect(denied.status).toBe(401);

    const url = `${base}/v1/rooms/${owner.snapshot.roomId}?generation=${owner.snapshot.generation}`;
    advanceClock(3_001);
    const before = await SELF.fetch(url, { headers: auth(owner.token) });
    expect(before.status).toBe(200);
    const snapshot = await before.json() as any;
    expect(snapshot.members).toHaveLength(2);
    const throttled = await SELF.fetch(url, { headers: auth(owner.token) });
    await throttled.arrayBuffer();
    expect(throttled.status).toBe(429);

    const update = {
      version: 1, roomId: owner.snapshot.roomId, generation: owner.snapshot.generation,
      revision: 1,
      state: { championId: 1, desired: { championId: 1, skinId: 1000, chromaId: null, form: null }, prepared: null,
        phase: "ChampSelect", preparation: "prepared", libraryCommit: "abc", metadataPatch: null },
    };
    const published = await SELF.fetch(`${base}/v1/rooms/${owner.snapshot.roomId}/state`, {
      method: "PUT", headers: { ...headers, ...auth(owner.token) }, body: JSON.stringify(update),
    });
    expect(published.status).toBe(200);
    const changed = await published.json() as any;
    expect(changed.revision).toBeGreaterThan(snapshot.revision);
    expect(changed.members.find((m: any) => m.memberId === owner.memberId).state.desired.skinId).toBe(1000);

    const stale = await SELF.fetch(`${base}/v1/rooms/${owner.snapshot.roomId}/state`, {
      method: "PUT", headers: { ...headers, ...auth(owner.token) }, body: JSON.stringify(update),
    });
    expect(stale.status).toBe(409);
    await stale.arrayBuffer();

    const tooSoon = await SELF.fetch(`${base}/v1/rooms/${owner.snapshot.roomId}/state`, {
      method: "PUT", headers: { ...headers, ...auth(owner.token) }, body: JSON.stringify({ ...update, revision: 2 }),
    });
    expect(tooSoon.status).toBe(429);
    await tooSoon.arrayBuffer();

    const malformed = await SELF.fetch(`${base}/v1/rooms/${owner.snapshot.roomId}/state`, {
      method: "PUT", headers: { ...headers, ...auth(owner.token) }, body: JSON.stringify({ ...update,
        revision: 2, state: { ...initialState, championId: 1, desired: { championId: 2, skinId: 2000, chromaId: null, form: null } } }),
    });
    expect(malformed.status).toBe(400);
    await malformed.arrayBuffer();

    advanceClock(3_001);
    const reconnected = await SELF.fetch(url, { headers: auth(owner.token) });
    expect((await reconnected.json() as any).revision).toBe(changed.revision);
  });

  it("rejects oversized bodies and wrong room generations", async () => {
    const owner = await create();
    const oversized = await SELF.fetch(`${base}/v1/rooms`, { method: "POST", headers, body: JSON.stringify({ version: 1, x: "a".repeat(20_000) }) });
    await oversized.arrayBuffer();
    expect(oversized.status).toBe(413);
    const wrong = await SELF.fetch(`${base}/v1/rooms/${owner.snapshot.roomId}?generation=wrong`, { headers: auth(owner.token) });
    await wrong.arrayBuffer();
    expect(wrong.status).toBe(404);
  });
});
