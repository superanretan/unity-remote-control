// Environment → configuration. Pure function so tests can pass their own env object.
//
//   REDIS_URL             TCP connection string: redis://… or rediss://…
//                         Also accepted, in this order: KV_URL, REDIS_TLS_URL, UPSTASH_REDIS_URL —
//                         the names the Vercel Marketplace Redis integrations inject. A REST endpoint
//                         (https://…) is REJECTED: the REST API cannot SUBSCRIBE, which the
//                         cross-instance doorbell needs. Copy the TCP/RESP string from the provider console.
//                         Unset locally → in-memory store (single instance). Unset on Vercel → fatal
//                         misconfiguration (see `misconfigured`), because two instances would not see each other.
//   KEY_PREFIX            Redis key namespace (default "rc")
//   DEVICE_TIMEOUT        seconds without heartbeat before a host leaves the device list (default 15)
//   PAIR_LEASE_SECONDS    controller ↔ host pairing lease TTL (default 30); refreshed every TICK_MS
//   TICK_MS               per-instance maintenance tick: ping/pong, lease refresh, stale checks (default 10000)
//   MAILBOX_TTL_SECONDS   how long undelivered relay messages survive (default 45)
//   MAILBOX_MAX           max queued messages per recipient (default 200, oldest dropped)
//   ALLOWED_ORIGINS       comma list of browser origins allowed to connect; unset/"*" = any
//   ROOM_TOKEN            single shared token → room "default"
//   ROOM_TOKENS           multi-tenant: "presentation=tokA,test=tokB"
//   TURN_URLS             comma list of turn:/turns: URLs → ephemeral TURN credentials issued on register
//   TURN_SECRET           coturn static-auth-secret used to sign the ephemeral credentials
//   TURN_TTL_SECONDS      lifetime of issued TURN credentials (default 3600)
//   LOG=verbose           log every message
//   DEBUG_FORCE_CLOSE_MS  TEST ONLY: force-close every socket after N ms to simulate the Vercel max duration
//
// Set by the platform, read here for diagnostics: VERCEL, VERCEL_ENV, VERCEL_REGION.

/** Env var names that may carry the Redis TCP connection string, in priority order. */
export const REDIS_URL_VARS = ["REDIS_URL", "KV_URL", "REDIS_TLS_URL", "UPSTASH_REDIS_URL"];

export function loadConfig(env = process.env) {
  const rooms = parseRooms(env.ROOM_TOKEN, env.ROOM_TOKENS);
  const redis = resolveRedisUrl(env);
  const onVercel = !!env.VERCEL;

  let misconfigured = null;
  if (redis.error) {
    misconfigured = redis.error;
  } else if (!redis.url && onVercel) {
    misconfigured = {
      slug: "no-redis-url",
      detail: `No Redis connection string found (looked for ${REDIS_URL_VARS.join(", ")}). ` +
              "On Vercel every WebSocket may land on a different function instance, so without Redis the host " +
              "and the controller cannot see each other. Add a Redis store (Storage → Marketplace) and make sure " +
              "a rediss:// URL is exposed as REDIS_URL, then redeploy.",
    };
  }

  return {
    redisUrl: redis.url,
    redisUrlSource: redis.source,
    keyPrefix: (env.KEY_PREFIX || "rc").trim(),
    onVercel,
    vercelEnv: (env.VERCEL_ENV || "").trim(),
    region: (env.VERCEL_REGION || "").trim(),
    /** null, or { slug, detail } — the server then refuses to pretend it works (see src/signaling.js). */
    misconfigured,
    deviceTimeoutMs: clamp(num(env.DEVICE_TIMEOUT, 15), 3, 600) * 1000,
    pairLeaseSec: clamp(num(env.PAIR_LEASE_SECONDS, 30), 5, 3600),
    // 10 s keeps a 30 s lease refreshed three times per period while roughly halving the Redis command
    // count of an idle-but-connected session (relevant for a free provider quota).
    tickMs: clamp(num(env.TICK_MS, 10000), 500, 60000),
    mailboxTtlSec: clamp(num(env.MAILBOX_TTL_SECONDS, 45), 5, 3600),
    mailboxMax: clamp(num(env.MAILBOX_MAX, 200), 10, 10000),
    allowedOrigins: parseOrigins(env.ALLOWED_ORIGINS),
    rooms,
    verbose: env.LOG === "verbose",
    maxMessageBytes: 256 * 1024,
    maxDeviceNameLength: 64,
    turn: {
      urls: list(env.TURN_URLS),
      secret: (env.TURN_SECRET || "").trim(),
      ttlSec: clamp(num(env.TURN_TTL_SECONDS, 3600), 60, 86400),
    },
    debugForceCloseMs: num(env.DEBUG_FORCE_CLOSE_MS, 0),
  };
}

/**
 * First usable Redis URL among {@link REDIS_URL_VARS}.
 * → `{ url, source, error: null }` when a redis://|rediss:// URL was found (url "" when none is set),
 * → `{ url: "", source, error: { slug, detail } }` when a variable is set but unusable.
 */
export function resolveRedisUrl(env = process.env) {
  for (const name of REDIS_URL_VARS) {
    const raw = String(env[name] ?? "").trim();
    if (!raw) continue;

    const scheme = (raw.match(/^([a-zA-Z][a-zA-Z0-9+.-]*):\/\//) || [])[1]?.toLowerCase();
    if (scheme === "redis" || scheme === "rediss") return { url: raw, source: name, error: null };

    if (scheme === "http" || scheme === "https") {
      return {
        url: "", source: name,
        error: {
          slug: "redis-url-is-rest",
          detail: `${name} points at a REST endpoint (${scheme}://…). The Redis REST API cannot SUBSCRIBE, which ` +
                  "this server needs. Open the provider console, copy the TCP/RESP connection string " +
                  "(rediss://default:…@….upstash.io:6379) and set it as REDIS_URL.",
        },
      };
    }

    return {
      url: "", source: name,
      error: {
        slug: "redis-url-bad-scheme",
        detail: `${name} has an unsupported scheme (${scheme ?? "none"}). Expected redis:// or rediss://.`,
      },
    };
  }
  return { url: "", source: null, error: null };
}

/** `{ open: boolean, byToken: Map<token, roomName> }` — open means "no token configured, everybody lands in 'default'". */
export function parseRooms(single, multi) {
  const byToken = new Map();
  if (multi) {
    for (const part of list(multi)) {
      const eq = part.indexOf("=");
      if (eq <= 0) continue;
      const room = part.slice(0, eq).trim();
      const token = part.slice(eq + 1).trim();
      if (room && token && /^[A-Za-z0-9_-]{1,32}$/.test(room)) byToken.set(token, room);
    }
  }
  if (single && single.trim()) byToken.set(single.trim(), "default");
  return { open: byToken.size === 0, byToken };
}

function parseOrigins(raw) {
  const items = list(raw);
  if (items.length === 0 || items.includes("*")) return null;         // null = allow any
  return new Set(items.map(o => o.toLowerCase().replace(/\/+$/, "")));
}

function list(raw) {
  return String(raw || "").split(",").map(s => s.trim()).filter(Boolean);
}

function num(raw, fallback) {
  const n = Number(raw);
  return Number.isFinite(n) && raw !== undefined && raw !== "" ? n : fallback;
}

function clamp(n, min, max) {
  return Math.min(max, Math.max(min, n));
}
