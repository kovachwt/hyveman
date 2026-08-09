// Fetches the pinned hyveman-api OpenAPI document into openapi/openapi.json.
//
// Expects a running hyveman-api instance (the API publishes the document at
// /openapi/v1.json in Development). Usage:
//
//   node scripts/fetch-openapi.mjs [baseUrl]
//
// The document is the build input for the Orval-generated client; CI fails the
// build if src/api/generated is stale relative to it (npm run api:check).

import { writeFileSync, mkdirSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const base = process.argv[2] ?? 'http://127.0.0.1:5080';
const url = `${base.replace(/\/$/, '')}/openapi/v1.json`;

console.log(`Fetching OpenAPI document from ${url} ...`);
const res = await fetch(url, { headers: { Accept: 'application/json' } });
if (!res.ok) {
  console.error(`Failed to fetch ${url}: HTTP ${res.status}`);
  console.error('Start hyveman-api in Development first:');
  console.error('  dotnet run --project ../hyveman-api/src/Hyveman.Api -- --WebAuthnRpId=localhost --WebAuthnExpectedOrigin=http://localhost:5080');
  process.exit(1);
}
const doc = await res.json();
if (!doc?.paths) {
  console.error('Response does not look like an OpenAPI document.');
  process.exit(1);
}

// The frontend always talks to the API through its own origin (/api/v1), so
// the pinned document must not contain absolute server URLs: Orval would
// otherwise hard-code the generator's host into the client.
delete doc.servers;
// Operational endpoints (/health/live, /health/ready) belong to the agent/
// infra surface, not the web API contract the browser consumes.
for (const p of Object.keys(doc.paths)) {
  if (!p.startsWith('/api/v1/')) delete doc.paths[p];
}
const out = resolve(root, 'openapi/openapi.json');
mkdirSync(dirname(out), { recursive: true });
writeFileSync(out, JSON.stringify(doc, null, 2) + '\n');
console.log(`Saved ${out} (${doc.paths ? Object.keys(doc.paths).length : 0} paths).`);
