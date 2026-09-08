// Configuration / provisioning behaviour: whatever name the Vercel Marketplace integration used, the
// server must find the Redis URL — and when it cannot, it must say so instead of running degraded.

import { test } from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { loadConfig, resolveRedisUrl, REDIS_URL_VARS } from "../src/config.js";

const root = path.join(path.dirname(fileURLToPath(import.meta.url)), "..");

test("the Redis TCP URL is accepted under every name the Marketplace integrations inject", () => {
  assert.deepEqual(resolveRedisUrl({ REDIS_URL: "rediss://default:pw@eu1.upstash.io:6379" }),
    { url: "rediss://default:pw@eu1.upstash.io:6379", source: "REDIS_URL", error: null });

  for (const name of REDIS_URL_VARS) {
    const r = resolveRedisUrl({ [name]: "rediss://h:6379" });
    assert.equal(r.source, name, name);
    assert.equal(r.error, null, name);
  }

  // REDIS_URL wins over the aliases, and surrounding whitespace is trimmed.
  assert.equal(resolveRedisUrl({ KV_URL: "redis://b:6379", REDIS_URL: "  redis://a:6379  " }).url, "redis://a:6379");
  assert.deepEqual(resolveRedisUrl({}), { url: "", source: null, error: null });
  // Purely-REST variables are not connection strings and must be ignored, not misread.
  assert.deepEqual(resolveRedisUrl({ KV_REST_API_URL: "https://x.upstash.io", KV_REST_API_TOKEN: "t" }),
    { url: "", source: null, error: null });
});

test("a REST endpoint or a nonsense URL is rejected with an actionable message", () => {
  const rest = resolveRedisUrl({ KV_URL: "https://x.upstash.io" });
  assert.equal(rest.url, "");
  assert.equal(rest.error.slug, "redis-url-is-rest");
  assert.match(rest.error.detail, /cannot SUBSCRIBE/);
  assert.match(rest.error.detail, /TCP\/RESP/);

  assert.equal(resolveRedisUrl({ REDIS_URL: "eu1.upstash.io:6379" }).error.slug, "redis-url-bad-scheme");
  assert.equal(resolveRedisUrl({ REDIS_URL: "postgres://h/db" }).error.slug, "redis-url-bad-scheme");
});

test("no Redis is fatal on Vercel and harmless locally", () => {
  const onVercel = loadConfig({ VERCEL: "1", VERCEL_REGION: "fra1" });
  assert.equal(onVercel.misconfigured.slug, "no-redis-url");
  assert.match(onVercel.misconfigured.detail, /Storage → Marketplace/);
  assert.equal(onVercel.onVercel, true);
  assert.equal(onVercel.region, "fra1");

  const local = loadConfig({});
  assert.equal(local.misconfigured, null, "in-memory store stays a documented local dev path");
  assert.equal(local.redisUrl, "");

  // A broken URL is fatal everywhere — it can only be a configuration mistake.
  assert.equal(loadConfig({ REDIS_URL: "https://x.upstash.io" }).misconfigured.slug, "redis-url-is-rest");
  assert.equal(loadConfig({ REDIS_URL: "rediss://h:6379" }).misconfigured, null);
});

// The local server answers every path from one process, a Vercel Function only receives the path its file
// maps to. A handled path without a file is a 404 that no behavioural test can see.
test("every HTTP path the server answers has a matching Vercel function file", () => {
  const vercel = JSON.parse(fs.readFileSync(path.join(root, "vercel.json"), "utf8"));
  const apiFiles = fs.readdirSync(path.join(root, "api"));

  for (const p of ["signaling.js", "devices.js", "health.js"])
    assert.ok(apiFiles.includes(p), `api/${p} is missing`);

  for (const key of Object.keys(vercel.functions))
    assert.ok(fs.existsSync(path.join(root, key)), `${key} is configured in vercel.json but does not exist`);

  for (const rewrite of vercel.rewrites) {
    const file = rewrite.destination.replace(/^\//, "") + ".js";
    assert.ok(fs.existsSync(path.join(root, file)), `${rewrite.destination} has no ${file}`);
  }

  // Vercel's Node detection must not capture a server entrypoint instead of the api/ functions.
  const pkg = JSON.parse(fs.readFileSync(path.join(root, "package.json"), "utf8"));
  assert.equal(pkg.main, undefined, 'package.json "main" makes Vercel look for a server entrypoint');
  assert.equal(vercel.framework, null, 'vercel.json needs "framework": null');
});

test("defaults match the documented timings", () => {
  const c = loadConfig({});
  assert.equal(c.tickMs, 10000);
  assert.equal(c.deviceTimeoutMs, 15000);
  assert.equal(c.pairLeaseSec, 30);
  assert.equal(c.mailboxTtlSec, 45);
  assert.equal(c.keyPrefix, "rc");
});
