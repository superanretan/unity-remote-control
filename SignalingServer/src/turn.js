// Ephemeral TURN credentials (coturn "TURN REST API" / static-auth-secret scheme):
//   username   = "<unix-expiry>:<clientId>"
//   credential = base64( HMAC-SHA1( TURN_SECRET, username ) )
// coturn: `use-auth-secret` + `static-auth-secret=<TURN_SECRET>`. The secret never leaves the server;
// the browser only ever sees a credential that expires after TURN_TTL_SECONDS.

import { createHmac } from "node:crypto";

/** Returns an RTCIceServer-shaped array or [] when TURN is not configured. */
export function issueIceServers(turnConfig, subject, nowMs = Date.now()) {
  if (!turnConfig || !turnConfig.urls.length || !turnConfig.secret) return [];
  const expiry = Math.floor(nowMs / 1000) + turnConfig.ttlSec;
  const username = `${expiry}:${String(subject || "anon").replace(/[^A-Za-z0-9_-]/g, "")}`;
  const credential = createHmac("sha1", turnConfig.secret).update(username).digest("base64");
  return [{ urls: [...turnConfig.urls], username, credential }];
}
