// Input validation for everything that comes off the wire. Anything that fails is rejected or
// replaced by a safe default; nothing user-controlled reaches the controller dropdown unfiltered.

const ID_RE = /^[A-Za-z0-9_-]{8,64}$/;
const SESSION_RE = /^[A-Za-z0-9_-]{1,128}$/;
const CONTROL_RE = /[\x00-\x1f\x7f]/g;

/** deviceId / clientId supplied by a client. */
export function isValidId(value) {
  return typeof value === "string" && ID_RE.test(value);
}

export function isValidSessionId(value) {
  return typeof value === "string" && SESSION_RE.test(value);
}

/** Human-readable device name: string only, control chars stripped, length-capped. */
export function sanitizeName(value, maxLength, fallback = "Vision Pro") {
  if (typeof value !== "string") return fallback;
  const clean = value.replace(CONTROL_RE, "").trim();
  if (!clean) return fallback;
  return clean.length > maxLength ? clean.slice(0, maxLength) : clean;
}

/** Short token-like strings (platform, reason, status …). */
export function sanitizeShort(value, maxLength, fallback = "") {
  if (typeof value !== "string") return fallback;
  const clean = value.replace(CONTROL_RE, "").trim();
  if (!clean) return fallback;
  return clean.length > maxLength ? clean.slice(0, maxLength) : clean;
}

export function normalizeStatus(value) {
  return value === "busy" ? "busy" : "available";
}

/** Per-device heartbeat timeout announced by the host (seconds) → ms, clamped, or null. */
export function normalizeDeviceTimeout(value) {
  const n = Number(value);
  if (!Number.isFinite(n) || n <= 0) return null;
  return Math.min(600, Math.max(3, n)) * 1000;
}
