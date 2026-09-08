// Vercel Function: GET /api/health[?token=…] → is this deployment actually wired up?
//
// Answers with the store verdict (see handleHttp in src/signaling.js): Redis reachable, pub/sub doorbell
// ringing, every Lua script accepted. On a cold instance the self-test runs on demand, so the answer always
// reflects this deployment rather than a cached guess. `/health` is rewritten here by vercel.json.
//
// Each path needs its own file on Vercel — the local server.js serves every path from one process, but a
// Function only receives the path its file maps to.

import { bootstrap } from "../src/bootstrap.js";

export default async function handler(req, res) {
  const { signaling } = bootstrap();
  await signaling.handleHttp(req, res);
}
