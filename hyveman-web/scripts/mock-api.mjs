/**
 * Playwright mock API (FRONTEND.md §14 browser tests): a controlled test
 * environment standing in for hyveman-api. It implements the web API surface
 * the e2e flows touch, issues the same session/CSRF cookies, and never
 * contains production credentials. Responses are raw DTO bodies — the real
 * API serializes the DTO directly with no outer `data` envelope (see
 * docs/FRONTEND-CONFORMANCE.md B1).
 *
 * Run: node scripts/mock-api.mjs [port]   (default 5099)
 */
import { createServer } from 'node:http';

const PORT = Number(process.argv[2] ?? 5099);

// ── Control state ──────────────────────────────────────────────────────────
const state = {
  setupRequired: true,
  sessionToken: null,
  passkeys: [{ id: 'pk_1', name: 'test-key', created: '2025-01-01T00:00:00Z' }],
  /** Credential id (base64url) registered by the last ceremony, echoed in
   *  login options so the virtual authenticator can match it (CDP virtual
   *  authenticators do not enumerate resident keys). */
  credentialId: null,
  invitations: [],
};
const SESSION_COOKIE = 'hyveman_session';
const CSRF_COOKIE = 'hyveman_csrf';
const CSRF_HEADER = 'X-CSRF-Token';

// ── Fixtures ───────────────────────────────────────────────────────────────
const now = () => new Date().toISOString();
const minutesAgo = (m) => new Date(Date.now() - m * 60_000).toISOString();

const hosts = [
  {
    id: 'hst_1',
    name: 'dc01',
    kind: 'windows-server',
    sourceId: 'src_1',
    idracUrl: 'https://idrac-dc01.internal',
    idracCredentialSet: true,
    enabled: true,
    notes: 'domain controller',
    createdAt: '2025-01-01T00:00:00Z',
    updatedAt: '2025-01-01T00:00:00Z',
  },
  {
    id: 'hst_2',
    name: 'web01',
    kind: 'linux-server',
    sourceId: 'src_2',
    idracUrl: null,
    idracCredentialSet: false,
    enabled: true,
    notes: null,
    createdAt: '2025-01-02T00:00:00Z',
    updatedAt: '2025-01-02T00:00:00Z',
  },
];

const sources = [
  {
    id: 'src_1',
    kind: 'windows-agent',
    name: 'dc01',
    createdAt: '2025-01-01T00:00:00Z',
    agent: { status: 'online', lastReceived: minutesAgo(1), agentVersion: '1.0.0' },
    tokens: [{ id: 'tok_1', prefix: 'agt_abcd', scopes: ['ingest'], created: '2025-01-01T00:00:00Z', revoked: false }],
  },
  {
    id: 'src_2',
    kind: 'linux-agent',
    name: 'web01',
    createdAt: '2025-01-02T00:00:00Z',
    agent: { status: 'silent', lastReceived: minutesAgo(40) },
    tokens: [],
  },
];

const overview = {
  generatedAt: now(),
  hosts: [
    {
      id: 'hst_1',
      name: 'dc01',
      kind: 'windows-server',
      sourceId: 'src_1',
      rollupState: 'ok',
      rollupAt: minutesAgo(2),
      hardwareState: 'ok',
      osState: 'ok',
      hyperVState: 'ok',
      agent: { status: 'online', lastReceived: minutesAgo(1), agentVersion: '1.0.0', vmCount: 3 },
      idrac: { configured: true, lastPoll: minutesAgo(1), lastPollOk: true },
      activeAlertCount: 0,
    },
    {
      id: 'hst_2',
      name: 'web01',
      kind: 'linux-server',
      sourceId: 'src_2',
      rollupState: 'warning',
      rollupAt: minutesAgo(3),
      hardwareState: 'unknown',
      osState: 'warning',
      agent: { status: 'silent', lastReceived: minutesAgo(40) },
      idrac: { configured: false },
      activeAlertCount: 1,
    },
  ],
  summary: { total: 2, ok: 1, warning: 1, critical: 0, unknown: 0, silentAgents: 1, activeAlerts: 1, unacknowledgedAlerts: 1 },
  recentAlerts: [
    { id: 'a1', title: 'Agent silent: web01', hostName: 'web01', severity: 'warning', status: 'active', firstSeen: minutesAgo(30), lastSeen: minutesAgo(10), count: 4 },
  ],
};

const events = {
  items: [
    {
      id: 1,
      sourceId: 'src_1',
      sourceName: 'dc01',
      hostId: 'hst_1',
      hostName: 'dc01',
      dedupScope: 'winlog',
      recordId: '1',
      time: minutesAgo(5),
      severity: 2,
      channel: 'System',
      eventId: 6008,
      message: 'The previous system shutdown was unexpected.',
      fieldsJson: '{"param1":"value"}',
      rawJson: '<Event><System/></Event>',
    },
    {
      id: 2,
      sourceId: 'src_2',
      sourceName: 'web01',
      hostId: 'hst_2',
      hostName: 'web01',
      dedupScope: 'syslog',
      recordId: '2',
      time: minutesAgo(12),
      severity: 3,
      channel: 'kernel',
      eventId: null,
      message: 'device eth0 entered promiscuous mode',
    },
  ],
  nextCursor: null,
  hasMore: false,
};

function json(res, body, status = 200) {
  const payload = typeof body === 'string' ? body : JSON.stringify(body);
  // Preserve headers set earlier (e.g. Set-Cookie from setHeader) while
  // enforcing the JSON content type.
  const headers = {
    ...res.getHeaders(),
    'Content-Type': 'application/json',
    'Cache-Control': 'no-store',
  };
  res.writeHead(status, headers);
  res.end(payload);
}

function problem(res, status, code, detail) {
  json(res, { type: 'about:blank', title: code, status, code, detail }, status);
}

function cookies(req) {
  const out = {};
  for (const part of (req.headers.cookie ?? '').split(';')) {
    const [k, ...v] = part.trim().split('=');
    if (k) out[k] = decodeURIComponent(v.join('='));
  }
  return out;
}

const authenticated = (req) => {
  const c = cookies(req);
  return state.sessionToken !== null && c[SESSION_COOKIE] === state.sessionToken;
};

async function readBody(req) {
  const chunks = [];
  for await (const chunk of req) chunks.push(chunk);
  const text = Buffer.concat(chunks).toString('utf8');
  try {
    return text ? JSON.parse(text) : undefined;
  } catch {
    return undefined;
  }
}

function requireCsrf(req, res) {
  const c = cookies(req);
  // Node lowercases incoming header names.
  const header = req.headers[CSRF_HEADER.toLowerCase()];
  if (!c[CSRF_COOKIE] || header !== c[CSRF_COOKIE]) {
    problem(res, 403, 'csrf_mismatch', 'Missing or mismatched CSRF token.');
    return false;
  }
  return true;
}

const server = createServer(async (req, res) => {
  const url = new URL(req.url ?? '/', `http://${req.headers.host}`);
  const method = req.method ?? 'GET';
  const path = url.pathname;
  if (process.env.HYVEMAN_MOCK_LOG) {
    const c = cookies(req);
    console.log(`[mock] ${method} ${path} session=${c[SESSION_COOKIE] ? 'yes' : 'no'}`);
  }

  // ── Test-control endpoints (never part of the real API) ────────────────
  if (path === '/__mock/state') {
    if (method === 'GET') return json(res, { state });
    const body = await readBody(req);
    if (body?.setupRequired !== undefined) state.setupRequired = Boolean(body.setupRequired);
    if (body?.authenticated !== undefined) state.sessionToken = body.authenticated ? 'mock-session' : null;
    return json(res, { state });
  }
  if (path === '/__mock/reset') {
    state.setupRequired = true;
    state.sessionToken = null;
    state.credentialId = null;
    return json(res, { ok: true });
  }

  // CSRF cookie issuance for every /api/v1 request (mirrors the API).
  const c = cookies(req);
  if (path.startsWith('/api/v1/') && (!c[CSRF_COOKIE] || c[CSRF_COOKIE].length < 16)) {
    res.setHeader('Set-Cookie', `${CSRF_COOKIE}=mock-csrf-token-1234567890; Path=/; SameSite=Strict`);
  }
  if (['POST', 'PUT', 'PATCH', 'DELETE'].includes(method) && path.startsWith('/api/v1/')) {
    if (!requireCsrf(req, res)) return;
  }

  // ── Auth ────────────────────────────────────────────────────────────────
  if (path === '/api/v1/auth/session') {
    return json(res, {
      authenticated: authenticated(req),
      setupRequired: state.setupRequired && !authenticated(req),
      user: authenticated(req) ? { id: 'usr_admin', name: 'admin', displayName: 'Hyveman Administrator' } : null,
    });
  }
  if (path === '/api/v1/auth/passkeys/login/options') {
    return json(res, {
      challenge: 'A' + 'a'.repeat(42),
      rpId: 'localhost',
      timeout: 60000,
      userVerification: 'preferred',
      allowCredentials: state.credentialId ? [{ type: 'public-key', id: state.credentialId }] : [],
    });
  }
  if (path === '/api/v1/auth/passkeys/login/verify') {
    await readBody(req);
    state.sessionToken = 'mock-session';
    res.setHeader('Set-Cookie', `${SESSION_COOKIE}=${state.sessionToken}; Path=/; SameSite=Strict; HttpOnly`);
    return json(res, { ok: true });
  }
  if (path === '/api/v1/auth/passkeys/register/options') {
    return json(res, {
      challenge: 'R' + 'r'.repeat(42),
      rp: { name: 'Hyveman', id: 'localhost' },
      user: { id: 'dXNlcjE', name: 'admin', displayName: 'admin' },
      pubKeyCredParams: [{ type: 'public-key', alg: -7 }],
      timeout: 60000,
      attestation: 'none',
      authenticatorSelection: { userVerification: 'preferred' },
      excludeCredentials: [],
    });
  }
  if (path === '/api/v1/auth/passkeys/register/verify') {
    const body = await readBody(req);
    // Envelope: { response, inviteToken?, username?, displayName? } (docs/MULTI-USER.md).
    const credential = body?.response ?? body;
    if (credential?.id) state.credentialId = credential.id;
    state.sessionToken = 'mock-session';
    state.setupRequired = false;
    state.passkeys.push({ id: 'pk_new', name: 'test-key', created: now() });
    res.setHeader('Set-Cookie', `${SESSION_COOKIE}=${state.sessionToken}; Path=/; SameSite=Strict; HttpOnly`);
    return json(res, { id: 'pk_new' });
  }
  if (path === '/api/v1/auth/invitations/inspect' && method === 'POST') {
    const body = await readBody(req);
    const valid = typeof body?.token === 'string' && body.token.startsWith('inv_');
    return json(res, { valid, createdBy: 'admin', expiresAt: null });
  }
  if (path === '/api/v1/auth/logout') {
    state.sessionToken = null;
    res.setHeader('Set-Cookie', `${SESSION_COOKIE}=; Path=/; Max-Age=0`);
    return json(res, null, 204);
  }
  if (path === '/api/v1/auth/passkeys' && method === 'GET') {
    return json(res, state.passkeys);
  }

  // ── Users & invitations (docs/MULTI-USER.md) ───────────────────────────
  if (path === '/api/v1/users' && method === 'GET') {
    return json(res, [
      { id: 'usr_admin', name: 'admin', displayName: 'Hyveman Administrator', disabled: false, created: now(), createdBy: 'setup', passkeyCount: state.passkeys.length, lastActive: null },
    ]);
  }
  if (path === '/api/v1/users/invitations' && method === 'GET') {
    return json(res, state.invitations ?? []);
  }
  if (path === '/api/v1/users/invitations' && method === 'POST') {
    const body = await readBody(req);
    const created = {
      id: 'invite_1',
      token: 'inv_' + 'a'.repeat(48),
      url: `http://localhost:5173/accept-invite#token=${'inv_' + 'a'.repeat(48)}`,
      created: now(),
      expiresAt: body?.expiresInMinutes ? new Date(Date.now() + body.expiresInMinutes * 60000).toISOString() : null,
    };
    state.invitations = [...(state.invitations ?? []), { id: created.id, createdBy: 'usr_admin', createdByDisplayName: 'admin', created: created.created, expiresAt: created.expiresAt, consumedAt: null, revoked: false }];
    return json(res, created, 201);
  }
  if (path.startsWith('/api/v1/users/invitations/') && path.endsWith('/revoke') && method === 'POST') {
    state.invitations = (state.invitations ?? []).map((i) => (i.id === path.split('/')[4] ? { ...i, revoked: true } : i));
    return json(res, null, 204);
  }
  if (path.startsWith('/api/v1/users/') && method === 'POST' && path.endsWith('/disable')) {
    return json(res, null, 204);
  }
  if (path.startsWith('/api/v1/users/') && method === 'POST' && path.endsWith('/enable')) {
    return json(res, null, 204);
  }

  // ── Auth gate for everything else ───────────────────────────────────────
  if (!authenticated(req)) return problem(res, 401, 'unauthorized', 'Sign in required.');

  // ── Resources ───────────────────────────────────────────────────────────
  if (path === '/api/v1/overview') return json(res, overview);
  if (path === '/api/v1/hosts' && method === 'GET') return json(res, hosts);
  if (path === '/api/v1/hosts' && method === 'POST') {
    const body = await readBody(req);
    return json(res, { id: 'hst_new', createdAt: now(), updatedAt: now(), ...body });
  }
  const hostMatch = path.match(/^\/api\/v1\/hosts\/([^/]+)$/);
  if (hostMatch && method === 'GET') {
    const host = hosts.find((h) => h.id === hostMatch[1]);
    if (!host) return problem(res, 404, 'not_found', 'Host not found.');
    return json(res, {
      ...host,
      rollupState: 'ok',
      rollupAt: minutesAgo(2),
      components: [
        { type: 'cpu', name: 'CPU 1', state: 'ok', detail: 'Xeon', lastSeen: minutesAgo(1) },
        { type: 'memory', name: 'DIMM A1', state: 'ok', lastSeen: minutesAgo(1) },
        { type: 'disk', name: 'Virtual Disk 0', state: 'ok', lastSeen: minutesAgo(1) },
      ],
      latestMetrics: [
        { name: 'temperature_max_c', value: 41.2, unit: 'C', time: minutesAgo(1) },
        { name: 'power_watts', value: 210, unit: 'W', time: minutesAgo(1) },
      ],
      recentAlerts: [],
      recentEvents: events.items.slice(0, 1),
      agent: { status: 'online', lastReceived: minutesAgo(1), agentVersion: '1.0.0', osBuild: '10.0.20348', vmCount: 3 },
    });
  }
  if (path.startsWith('/api/v1/hosts/') && path.endsWith('/health')) {
    return json(res, {
      hostId: 'hst_1',
      rollupState: 'ok',
      rollupAt: minutesAgo(2),
      components: [
        { type: 'cpu', name: 'CPU 1', state: 'ok', detail: 'Xeon', lastSeen: minutesAgo(1) },
        { type: 'memory', name: 'DIMM A1', state: 'ok', lastSeen: minutesAgo(1) },
      ],
      latestMetrics: [],
      recentSnapshots: [],
    });
  }
  if (path.startsWith('/api/v1/hosts/') && path.endsWith('/health-history')) {
    const points = Array.from({ length: 24 }, (_, i) => ({
      time: new Date(Date.now() - (23 - i) * 3_600_000).toISOString(),
      rollupState: i % 7 === 3 ? 'warning' : 'ok',
      temperatureMaxC: 40 + (i % 5),
      powerWatts: 200 + (i % 4) * 10,
    }));
    return json(res, { hostId: 'hst_1', from: points[0].time, to: points[23].time, resolution: '1h', points });
  }
  if (path.startsWith('/api/v1/hosts/') && path.endsWith('/vms')) {
    return json(res, [
      { name: 'sql01', state: 'on', heartbeatOk: true, cpuPct: 12, memMb: 8192, lastSeen: minutesAgo(1), stale: false },
      { name: 'app01', state: 'on', heartbeatOk: true, cpuPct: 34, memMb: 4096, lastSeen: minutesAgo(1), stale: false },
    ]);
  }
  if (path === '/api/v1/events') return json(res, events);
  if (path.startsWith('/api/v1/events/')) {
    const id = Number(path.split('/').pop());
    const item = events.items.find((e) => e.id === id);
    return item ? json(res, item) : problem(res, 404, 'not_found', 'Event not found.');
  }
  if (path === '/api/v1/saved-searches') return json(res, []);
  if (path === '/api/v1/sources') return json(res, sources);
  if (path === '/api/v1/registration-tokens') {
    return method === 'POST'
      ? json(res, { id: 'rt_1', kind: 'windows-agent', token: 'reg_mocktoken123456', created: now(), expiresAt: null, showOnce: true })
      : json(res, []);
  }
  if (path === '/api/v1/alerts') {
    return json(res, {
      items: [
        { id: 'a1', ruleId: 'r1', ruleName: 'Heartbeat silence', hostId: 'hst_2', hostName: 'web01', severity: 'warning', status: 'active', title: 'Agent silent: web01', detail: 'No heartbeat for 40 minutes', firstSeen: minutesAgo(30), lastSeen: minutesAgo(10), count: 4 },
      ],
      nextCursor: null,
      hasMore: false,
    });
  }
  if (path === '/api/v1/rules') {
    return json(res, [
      { id: 'r1', name: 'Agent silent', type: 'heartbeat', match: { silenceAfterS: 300 }, severity: 'warning', cooldownS: 300, enabled: true, channelIds: [], createdAt: '2025-01-01T00:00:00Z', updatedAt: '2025-01-01T00:00:00Z' },
    ]);
  }
  if (path === '/api/v1/notification-channels') {
    return json(res, [
      { id: 'c1', name: 'Ops telegram', kind: 'telegram', enabled: true, created: '2025-01-01T00:00:00Z', updatedAt: '2025-06-01T00:00:00Z', configSummary: { chatId: '••••••' } },
    ]);
  }
  if (path === '/api/v1/maintenance-windows') return json(res, []);
  if (path === '/api/v1/settings/retention') {
    return json(res, { eventDays: 365, metricDays: 180, snapshotDays: 180 });
  }
  if (path === '/api/v1/audit-log') {
    return json(res, {
      items: [{ id: 1, time: minutesAgo(60), actor: 'admin', action: 'rule.updated', targetKind: 'rule', targetId: 'r1', detailJson: null }],
      nextCursor: null,
      hasMore: false,
    });
  }
  if (path === '/api/v1/logon-stats') {
    return json(res, {
      items: [
        { day: '2025-08-09', sourceId: 'src_1', sourceName: 'dc01', user: 'alice', logonType: 2, successCount: 5, failureCount: 1 },
      ],
      hasMore: false,
    });
  }

  problem(res, 404, 'not_found', `No mock route for ${method} ${path}`);
});

server.listen(PORT, '127.0.0.1', () => {
  console.log(`Mock hyveman API listening on http://127.0.0.1:${PORT}`);
  console.log('State: setupRequired=%s authenticated=%s', state.setupRequired, state.sessionToken !== null);
});
