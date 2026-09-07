// Vercel Function: GET /api/devices[?token=…] → current device registry (from Redis), for debugging.
// `/devices` is rewritten here by vercel.json so the documented `curl …/devices` keeps working.

import { bootstrap } from "../src/bootstrap.js";

export default async function handler(req, res) {
  const { signaling } = bootstrap();
  await signaling.handleHttp(req, res);
}
