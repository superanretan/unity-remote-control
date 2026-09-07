// One-shot wiring of config + store + signaling, memoised per process (per Vercel Function instance).
// Both api/*.js adapters and server.js call this so they share exactly the same behaviour.

import { loadConfig } from "./config.js";
import { MemoryStore } from "./store/memory.js";
import { RedisStore } from "./store/redis.js";
import { createSignaling } from "./signaling.js";

let cached = null;

export function log(line) {
  console.log(`${new Date().toISOString()} ${line}`);
}

export function createStore(config) {
  if (config.misconfigured) {
    // Deliberately still return a store so HTTP health answers and every registration can be told WHY.
    log(`[FATAL] ${config.misconfigured.slug}: ${config.misconfigured.detail}`);
    return new MemoryStore();
  }
  if (config.redisUrl) {
    log(`[store] Redis via ${config.redisUrlSource}${config.region ? ` (function region ${config.region})` : ""}`);
    return new RedisStore(config.redisUrl, { keyPrefix: config.keyPrefix, log });
  }
  log("[store] no Redis connection string → in-memory store (single instance only; fine for local LAN dev, NOT for Vercel)");
  return new MemoryStore();
}

export function bootstrap(env = process.env) {
  if (cached) return cached;
  const config = loadConfig(env);
  const store = createStore(config);
  const signaling = createSignaling({ store, config, log });
  if (config.rooms.open) log("[auth] ROOM_TOKEN not set → anyone who knows the URL can connect");
  cached = { config, store, signaling };
  return cached;
}
