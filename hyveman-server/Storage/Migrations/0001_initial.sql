-- Hyveman server schema, v1 (SERVER.md Appendix A). Applied by DbMigrator inside a transaction.
-- schema_migrations is created/managed by the migrator itself (§6.5).

CREATE TABLE sources (
  id          TEXT PRIMARY KEY,                      -- src_<ulid>
  kind        TEXT NOT NULL,                          -- windows-agent|linux-agent|syslog-feed
  name        TEXT NOT NULL,                          -- hostname (agents) / feed name
  boot_id     TEXT,                                   -- optional opaque host fingerprint from registration
  created     TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
  UNIQUE(kind, name)
);

CREATE TABLE hosts (
  id               TEXT PRIMARY KEY,                  -- host_<ulid>
  source_id        TEXT,
  name             TEXT NOT NULL,
  kind             TEXT,                              -- dell-poweredge|generic|...
  idrac_url        TEXT,
  idrac_cred_ref   TEXT,
  poll_enabled     INTEGER NOT NULL DEFAULT 1,
  last_poll_at     TEXT,
  last_poll_ok     INTEGER,
  created          TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
  FOREIGN KEY (source_id) REFERENCES sources(id) ON DELETE SET NULL
);

CREATE TABLE tokens (
  id           TEXT PRIMARY KEY,                      -- tok_<ulid>
  source_id    TEXT,
  token_hash   TEXT NOT NULL,
  scopes       TEXT NOT NULL DEFAULT '[]',            -- JSON array
  created      TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
  last_used    TEXT,
  revoked      INTEGER NOT NULL DEFAULT 0,
  consumed_at  TEXT,
  expires_at   TEXT,
  bound_kind   TEXT,
  FOREIGN KEY (source_id) REFERENCES sources(id) ON DELETE SET NULL
);
CREATE UNIQUE INDEX idx_tokens_hash ON tokens(token_hash);

CREATE TABLE events (
  id           INTEGER PRIMARY KEY AUTOINCREMENT,
  source_id    TEXT NOT NULL,
  dedup_scope  TEXT NOT NULL DEFAULT '',
  record_id    TEXT NOT NULL,
  time         TEXT NOT NULL,
  severity     INTEGER,
  facility     TEXT,
  message      TEXT NOT NULL,
  fields_json  TEXT,
  raw_json     TEXT,
  channel      TEXT,
  event_id     INTEGER,
  task         INTEGER,
  opcode       INTEGER,
  keywords     TEXT,
  ingested_at  TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
  UNIQUE(source_id, dedup_scope, record_id),
  FOREIGN KEY (source_id) REFERENCES sources(id) ON DELETE CASCADE
);
CREATE INDEX idx_events_src_time ON events(source_id, time);
CREATE INDEX idx_events_channel  ON events(channel);
CREATE INDEX idx_events_eventid  ON events(event_id);
CREATE INDEX idx_events_time     ON events(time);

CREATE VIRTUAL TABLE events_fts USING fts5(
  message,
  content='events', content_rowid='id',
  tokenize='unicode61 remove_diacritics 1'
);
CREATE TRIGGER events_ai AFTER INSERT ON events BEGIN
  INSERT INTO events_fts(rowid, message) VALUES (new.id, new.message);
END;
CREATE TRIGGER events_ad AFTER DELETE ON events BEGIN
  INSERT INTO events_fts(events_fts, rowid, message) VALUES('delete', old.id, old.message);
END;
CREATE TRIGGER events_au AFTER UPDATE ON events BEGIN
  INSERT INTO events_fts(events_fts, rowid, message) VALUES('delete', old.id, old.message);
  INSERT INTO events_fts(rowid, message) VALUES (new.id, new.message);
END;

CREATE TABLE components (
  id         TEXT PRIMARY KEY,
  host_id    TEXT NOT NULL,
  type       TEXT NOT NULL,
  name       TEXT NOT NULL,
  state      TEXT NOT NULL,                            -- ok|warning|critical|unknown
  detail     TEXT,
  last_seen  TEXT NOT NULL,
  UNIQUE(host_id, type, name),
  FOREIGN KEY (host_id) REFERENCES hosts(id) ON DELETE CASCADE
);
CREATE INDEX idx_components_host ON components(host_id);

CREATE TABLE health_snapshots (
  host_id         TEXT NOT NULL,
  time            TEXT NOT NULL,
  rollup_state    TEXT NOT NULL,
  components_json TEXT NOT NULL,
  PRIMARY KEY(host_id, time),
  FOREIGN KEY (host_id) REFERENCES hosts(id) ON DELETE CASCADE
);

CREATE TABLE metrics (
  host_id  TEXT NOT NULL,
  time     TEXT NOT NULL,
  name     TEXT NOT NULL,
  value    REAL NOT NULL,
  unit     TEXT,
  FOREIGN KEY (host_id) REFERENCES hosts(id) ON DELETE CASCADE
);
CREATE INDEX idx_metrics_host_time ON metrics(host_id, time, name);

CREATE TABLE vms (
  id            TEXT PRIMARY KEY,
  host_id       TEXT NOT NULL,
  name          TEXT NOT NULL,
  state         TEXT,
  heartbeat_ok  INTEGER,
  last_seen     TEXT,
  cpu_pct       REAL,
  mem_mb        INTEGER,
  UNIQUE(host_id, name),
  FOREIGN KEY (host_id) REFERENCES hosts(id) ON DELETE CASCADE
);

CREATE TABLE agent_heartbeats (
  source_id        TEXT PRIMARY KEY,
  sent_at          TEXT NOT NULL,
  received_at      TEXT NOT NULL,
  agent_version    TEXT,
  protocol_version INTEGER,
  os_build         TEXT,
  boot_time        TEXT,
  uptime_s         INTEGER,
  degraded         TEXT NOT NULL DEFAULT '',
  config_hash      TEXT,
  counters_json    TEXT,
  free_disk_json   TEXT,
  FOREIGN KEY (source_id) REFERENCES sources(id) ON DELETE CASCADE
);

CREATE TABLE rules (
  id        TEXT PRIMARY KEY,
  name      TEXT NOT NULL,
  type      TEXT NOT NULL,                             -- health|event|heartbeat|threshold
  match_json TEXT NOT NULL,
  severity  TEXT NOT NULL,                             -- info|warning|critical
  cooldown  INTEGER NOT NULL DEFAULT 300,
  enabled   INTEGER NOT NULL DEFAULT 1,
  created   TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))
);

CREATE TABLE alerts (
  id         TEXT PRIMARY KEY,
  rule_id    TEXT NOT NULL,
  host_id    TEXT,
  source_id  TEXT,
  severity   TEXT NOT NULL,
  signature  TEXT NOT NULL,
  first_seen TEXT NOT NULL,
  last_seen  TEXT NOT NULL,
  count      INTEGER NOT NULL DEFAULT 1,
  status     TEXT NOT NULL DEFAULT 'active',
  detail_json TEXT,
  FOREIGN KEY (rule_id) REFERENCES rules(id) ON DELETE CASCADE,
  FOREIGN KEY (host_id) REFERENCES hosts(id) ON DELETE SET NULL,
  FOREIGN KEY (source_id) REFERENCES sources(id) ON DELETE SET NULL
);
CREATE INDEX idx_alerts_status ON alerts(status);
CREATE INDEX idx_alerts_rule   ON alerts(rule_id, status);
CREATE INDEX idx_alerts_dedup  ON alerts(rule_id, host_id, source_id, signature, status);

CREATE TABLE rule_channels (
  rule_id    TEXT NOT NULL,
  channel_id TEXT NOT NULL,
  PRIMARY KEY(rule_id, channel_id),
  FOREIGN KEY (rule_id)    REFERENCES rules(id)                ON DELETE CASCADE,
  FOREIGN KEY (channel_id) REFERENCES notification_channels(id) ON DELETE CASCADE
);

CREATE TABLE notification_channels (
  id          TEXT PRIMARY KEY,
  name        TEXT NOT NULL,
  kind        TEXT NOT NULL,                            -- telegram|webhook|smtp
  config_ref  TEXT NOT NULL,
  enabled     INTEGER NOT NULL DEFAULT 1,
  created     TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))
);

CREATE TABLE notification_queue (
  id          INTEGER PRIMARY KEY AUTOINCREMENT,
  alert_id    TEXT NOT NULL,
  channel_id  TEXT NOT NULL,
  attempts    INTEGER NOT NULL DEFAULT 0,
  next_at     TEXT NOT NULL,
  last_error  TEXT,
  FOREIGN KEY (alert_id)    REFERENCES alerts(id)                ON DELETE CASCADE,
  FOREIGN KEY (channel_id)  REFERENCES notification_channels(id) ON DELETE CASCADE
);

CREATE TABLE logon_stats (
  day            TEXT NOT NULL,
  source_id      TEXT NOT NULL,
  user           TEXT NOT NULL,
  logon_type     INTEGER,
  success_count  INTEGER NOT NULL DEFAULT 0,
  failure_count  INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY(day, source_id, user, logon_type),
  FOREIGN KEY (source_id) REFERENCES sources(id) ON DELETE CASCADE
);

CREATE TABLE maintenance_windows (
  id         TEXT PRIMARY KEY,
  host_id    TEXT NOT NULL,
  start      TEXT NOT NULL,
  end        TEXT NOT NULL,
  reason     TEXT,
  created_by TEXT,
  FOREIGN KEY (host_id) REFERENCES hosts(id) ON DELETE CASCADE
);

CREATE TABLE passkeys (
  id              TEXT PRIMARY KEY,
  name            TEXT NOT NULL,
  credential_id   TEXT NOT NULL UNIQUE,                -- base64url
  public_key      BLOB NOT NULL,
  sign_count      INTEGER NOT NULL DEFAULT 0,
  created         TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
  last_used       TEXT
);

CREATE TABLE credentials (
  id             TEXT PRIMARY KEY,
  kind           TEXT NOT NULL,
  label          TEXT NOT NULL UNIQUE,
  blob_encrypted BLOB NOT NULL,
  created        TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
  rotated        TEXT
);

CREATE TABLE audit_log (
  id           INTEGER PRIMARY KEY AUTOINCREMENT,
  time         TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
  actor        TEXT NOT NULL,
  action       TEXT NOT NULL,
  target_kind  TEXT,
  target_id    TEXT,
  detail_json  TEXT
);
CREATE INDEX idx_audit_time ON audit_log(time);
