namespace Hyveman.Infrastructure.Sqlite;

/// <summary>Versioned schema migrations (API.md §10). Exact columns may adjust
/// across migrations; the invariants — UNIQUE(kind,name) on sources,
/// UNIQUE(source_id, dedup_scope, record_id) on events, enforced foreign keys,
/// hashed/ciphertext secrets, UTC times — never weaken.</summary>
public static class Migrations
{
    public static readonly (int Version, string Sql)[] All =
    [
        (1, V1),
        (2, V2),
        (3, V3),
        (4, V4),
        (5, V5),
        (6, V6),
        (7, V7),
    ];

    private const string V1 = """
        CREATE TABLE sources(
            id TEXT PRIMARY KEY,
            kind TEXT NOT NULL,
            name TEXT NOT NULL,
            created_at TEXT NOT NULL,
            UNIQUE(kind, name));

        CREATE TABLE hosts(
            id TEXT PRIMARY KEY,
            name TEXT NOT NULL,
            kind TEXT NOT NULL DEFAULT 'windows-server',
            source_id TEXT NULL REFERENCES sources(id),
            idrac_url TEXT NULL,
            idrac_cred_ref TEXT NULL REFERENCES credentials(id),
            enabled INTEGER NOT NULL DEFAULT 1,
            notes TEXT NULL,
            updated_at TEXT NOT NULL,
            created_at TEXT NOT NULL);

        CREATE TABLE tokens(
            id TEXT PRIMARY KEY,
            source_id TEXT NOT NULL REFERENCES sources(id),
            token_hash TEXT NOT NULL UNIQUE,
            prefix TEXT NOT NULL,
            scopes TEXT NOT NULL,
            created TEXT NOT NULL,
            last_used TEXT NULL,
            revoked INTEGER NOT NULL DEFAULT 0,
            expires_at TEXT NULL);

        CREATE TABLE registration_tokens(
            id TEXT PRIMARY KEY,
            token_hash TEXT NOT NULL UNIQUE,
            kind TEXT NOT NULL,
            created TEXT NOT NULL,
            expires_at TEXT NULL,
            consumed_at TEXT NULL,
            revoked INTEGER NOT NULL DEFAULT 0,
            created_by TEXT NULL);

        CREATE TABLE events(
            id INTEGER PRIMARY KEY,
            source_id TEXT NOT NULL REFERENCES sources(id),
            dedup_scope TEXT NOT NULL DEFAULT '',
            record_id TEXT NOT NULL,
            time TEXT NOT NULL,
            severity INTEGER NULL,
            facility TEXT NULL,
            message TEXT NULL,
            fields_json TEXT NOT NULL DEFAULT '{}',
            raw_json TEXT NULL,
            channel TEXT NULL,
            event_id INTEGER NULL,
            task INTEGER NULL,
            opcode INTEGER NULL,
            keywords TEXT NULL,
            created_at TEXT NOT NULL,
            UNIQUE(source_id, dedup_scope, record_id));

        CREATE INDEX idx_events_source_time ON events(source_id, time);
        CREATE INDEX idx_events_time ON events(time);
        CREATE INDEX idx_events_channel ON events(channel);
        CREATE INDEX idx_events_event_id ON events(event_id);
        CREATE INDEX idx_events_severity ON events(severity);

        CREATE VIRTUAL TABLE events_fts USING fts5(
            message,
            content='events',
            content_rowid='id',
            tokenize='unicode61');

        CREATE TABLE agent_status(
            source_id TEXT PRIMARY KEY REFERENCES sources(id),
            last_received TEXT NOT NULL,
            last_sent_at TEXT NULL,
            agent_version TEXT NULL,
            os_build TEXT NULL,
            boot_time TEXT NULL,
            uptime_s INTEGER NULL,
            degraded TEXT NULL,
            config_hash TEXT NULL,
            counters_json TEXT NULL,
            heartbeat_json TEXT NULL,
            facts_json TEXT NULL,
            facts_collected_at TEXT NULL,
            updated_at TEXT NOT NULL);

        CREATE TABLE vms(
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            host_id TEXT NOT NULL REFERENCES hosts(id),
            name TEXT NOT NULL,
            state TEXT NOT NULL,
            heartbeat_ok INTEGER NULL,
            cpu_pct REAL NULL,
            mem_mb INTEGER NULL,
            last_seen TEXT NULL,
            stale INTEGER NOT NULL DEFAULT 0,
            collected_at TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            UNIQUE(host_id, name));

        CREATE TABLE components(
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            host_id TEXT NOT NULL REFERENCES hosts(id),
            type TEXT NOT NULL,
            name TEXT NOT NULL,
            state TEXT NOT NULL,
            detail TEXT NULL,
            last_seen TEXT NOT NULL,
            UNIQUE(host_id, type, name));

        CREATE TABLE health_snapshots(
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            host_id TEXT NOT NULL REFERENCES hosts(id),
            time TEXT NOT NULL,
            rollup_state TEXT NOT NULL,
            components_json TEXT NULL);

        CREATE INDEX idx_snapshots_host_time ON health_snapshots(host_id, time);

        CREATE TABLE metrics(
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            host_id TEXT NOT NULL REFERENCES hosts(id),
            time TEXT NOT NULL,
            name TEXT NOT NULL,
            value REAL NOT NULL,
            unit TEXT NULL);

        CREATE INDEX idx_metrics_host_time ON metrics(host_id, time);

        CREATE TABLE rules(
            id TEXT PRIMARY KEY,
            name TEXT NOT NULL,
            type TEXT NOT NULL,
            match_json TEXT NOT NULL,
            severity TEXT NOT NULL,
            cooldown_s INTEGER NOT NULL DEFAULT 0,
            enabled INTEGER NOT NULL DEFAULT 1,
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL);

        CREATE TABLE notification_channels(
            id TEXT PRIMARY KEY,
            name TEXT NOT NULL,
            kind TEXT NOT NULL,
            config_ref TEXT NULL,
            enabled INTEGER NOT NULL DEFAULT 1,
            created TEXT NOT NULL,
            rotated TEXT NULL,
            last_test_at TEXT NULL,
            last_test_ok INTEGER NULL,
            updated_at TEXT NOT NULL);

        CREATE TABLE rule_channels(
            rule_id TEXT NOT NULL REFERENCES rules(id),
            channel_id TEXT NOT NULL REFERENCES notification_channels(id),
            PRIMARY KEY(rule_id, channel_id));

        CREATE TABLE alerts(
            id TEXT PRIMARY KEY,
            rule_id TEXT NULL REFERENCES rules(id),
            host_id TEXT NULL REFERENCES hosts(id),
            source_id TEXT NULL REFERENCES sources(id),
            key TEXT NOT NULL,
            fingerprint TEXT NOT NULL,
            severity TEXT NOT NULL,
            status TEXT NOT NULL,
            title TEXT NOT NULL,
            detail TEXT NULL,
            first_seen TEXT NOT NULL,
            last_seen TEXT NOT NULL,
            count INTEGER NOT NULL DEFAULT 1,
            ack_at TEXT NULL,
            ack_reason TEXT NULL,
            silence_until TEXT NULL,
            resolved_at TEXT NULL,
            updated_at TEXT NOT NULL,
            UNIQUE(key, status));

        CREATE INDEX idx_alerts_status ON alerts(status);
        CREATE INDEX idx_alerts_host ON alerts(host_id);
        CREATE INDEX idx_alerts_last_seen ON alerts(last_seen);

        CREATE TABLE notification_outbox(
            id TEXT PRIMARY KEY,
            alert_id TEXT NULL,
            channel_id TEXT NOT NULL,
            status TEXT NOT NULL DEFAULT 'pending',
            attempt_count INTEGER NOT NULL DEFAULT 0,
            next_attempt_at TEXT NOT NULL,
            last_error TEXT NULL,
            created_at TEXT NOT NULL,
            sent_at TEXT NULL);

        CREATE INDEX idx_outbox_due ON notification_outbox(status, next_attempt_at);

        CREATE TABLE passkeys(
            id TEXT PRIMARY KEY,
            name TEXT NOT NULL,
            credential_id TEXT NOT NULL UNIQUE,
            public_key TEXT NOT NULL,
            sign_count INTEGER NOT NULL DEFAULT 0,
            created TEXT NOT NULL,
            last_used TEXT NULL);

        CREATE TABLE credentials(
            id TEXT PRIMARY KEY,
            kind TEXT NOT NULL,
            label TEXT NOT NULL,
            blob_encrypted BLOB NOT NULL,
            key_version INTEGER NOT NULL DEFAULT 1,
            created TEXT NOT NULL,
            rotated TEXT NULL);

        CREATE TABLE maintenance_windows(
            id TEXT PRIMARY KEY,
            host_id TEXT NULL REFERENCES hosts(id),
            start TEXT NOT NULL,
            end TEXT NOT NULL,
            reason TEXT NULL,
            created_by TEXT NULL,
            created_at TEXT NOT NULL);

        CREATE INDEX idx_windows_range ON maintenance_windows(start, end);

        CREATE TABLE audit_log(
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            time TEXT NOT NULL,
            actor TEXT NULL,
            action TEXT NOT NULL,
            target_kind TEXT NULL,
            target_id TEXT NULL,
            detail_json TEXT NULL);

        CREATE INDEX idx_audit_time ON audit_log(time);
        CREATE INDEX idx_audit_action ON audit_log(action);

        CREATE TABLE web_sessions(
            id_hash TEXT PRIMARY KEY,
            created_at TEXT NOT NULL,
            expires_at TEXT NOT NULL,
            last_seen TEXT NOT NULL,
            revoked_at TEXT NULL);

        CREATE TABLE webauthn_challenges(
            challenge_hash TEXT PRIMARY KEY,
            operation TEXT NOT NULL,
            options_json TEXT NOT NULL,
            created_at TEXT NOT NULL,
            expires_at TEXT NOT NULL,
            origin_context TEXT NULL);

        CREATE TABLE saved_searches(
            id TEXT PRIMARY KEY,
            name TEXT NOT NULL,
            filter_json TEXT NOT NULL,
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL);

        CREATE TABLE settings(
            key TEXT PRIMARY KEY,
            value TEXT NOT NULL,
            updated_at TEXT NOT NULL);

        CREATE TABLE logon_stats(
            day TEXT NOT NULL,
            source_id TEXT NOT NULL REFERENCES sources(id),
            user TEXT NOT NULL,
            logon_type INTEGER NULL,
            success_count INTEGER NOT NULL DEFAULT 0,
            failure_count INTEGER NOT NULL DEFAULT 0,
            PRIMARY KEY(day, source_id, user, logon_type));
        """;

    private const string V2 = """
        -- Default agent-silent heartbeat rule so silence alerting works out of
        -- the box (DESIGN §4.4 rule type 3; operator-editable in the UI).
        INSERT INTO rules(id, name, type, match_json, severity, cooldown_s, enabled, created_at, updated_at)
        VALUES ('rul_seed_agent_silent', 'Agent silent', 'heartbeat',
                '{"silenceAfterS":300,"sourceKinds":["windows-agent","linux-agent","syslog-feed"]}',
                'warning', 0, 1,
                '2026-01-01T00:00:00.0000000Z', '2026-01-01T00:00:00.0000000Z');
        """;

    private const string V3 = """
        -- Last iDRAC poll status per host (API.md §9.1): a failed poll records
        -- the failure without erasing the last known component state.
        CREATE TABLE poll_status(
            host_id TEXT PRIMARY KEY REFERENCES hosts(id),
            last_poll TEXT NOT NULL,
            last_success TEXT NULL,
            last_error TEXT NULL,
            failures INTEGER NOT NULL DEFAULT 0);
        """;

    private const string V4 = """
        -- Accepted-on-first-use iDRAC certificate pins (API.md §9.1): one pin
        -- per host; a changed certificate is refused until the pin is cleared.
        CREATE TABLE idrac_trusted_certs(
            host_id TEXT PRIMARY KEY REFERENCES hosts(id),
            fingerprint TEXT NOT NULL,
            cert_der BLOB NOT NULL,
            accepted_at TEXT NOT NULL);
        """;

    private const string V5 = """
        -- DEFECTS.md D2: UNIQUE(key, status) breaks the second fire→resolve
        -- cycle of an alert key — resolving the second occurrence collides
        -- with the first cycle's resolved row, which surfaced as 500s on the
        -- ordinary heartbeat-clear path. Replace the table-level constraint
        -- with a partial unique index over live statuses only: at most one
        -- live occurrence per key, unlimited resolved history (API.md §9.3).
        CREATE TABLE alerts_v5(
            id TEXT PRIMARY KEY,
            rule_id TEXT NULL REFERENCES rules(id),
            host_id TEXT NULL REFERENCES hosts(id),
            source_id TEXT NULL REFERENCES sources(id),
            key TEXT NOT NULL,
            fingerprint TEXT NOT NULL,
            severity TEXT NOT NULL,
            status TEXT NOT NULL,
            title TEXT NOT NULL,
            detail TEXT NULL,
            first_seen TEXT NOT NULL,
            last_seen TEXT NOT NULL,
            count INTEGER NOT NULL DEFAULT 1,
            ack_at TEXT NULL,
            ack_reason TEXT NULL,
            silence_until TEXT NULL,
            resolved_at TEXT NULL,
            updated_at TEXT NOT NULL);

        INSERT INTO alerts_v5(id, rule_id, host_id, source_id, key, fingerprint, severity, status,
                              title, detail, first_seen, last_seen, count, ack_at, ack_reason,
                              silence_until, resolved_at, updated_at)
            SELECT id, rule_id, host_id, source_id, key, fingerprint, severity, status,
                   title, detail, first_seen, last_seen, count, ack_at, ack_reason,
                   silence_until, resolved_at, updated_at
            FROM alerts;

        DROP TABLE alerts;
        ALTER TABLE alerts_v5 RENAME TO alerts;

        -- Live-uniqueness invariant, enforced at the schema level.
        CREATE UNIQUE INDEX ux_alerts_live_key ON alerts(key)
            WHERE status IN ('active','acknowledged','silenced');
        -- Plain key index serves the cooldown lookup (D3: cooldown keys off the
        -- most recent occurrence's last_seen, which can be a resolved row).
        CREATE INDEX idx_alerts_key ON alerts(key);
        CREATE INDEX idx_alerts_status ON alerts(status);
        CREATE INDEX idx_alerts_host ON alerts(host_id);
        CREATE INDEX idx_alerts_last_seen ON alerts(last_seen);
        """;

    private const string V6 = """
        -- Per-VM Hyper-V Replica facts (PROTOCOL §7.1, additive-optional):
        -- null/absent = VM not replicated. The vms table is delete+reinsert
        -- per scan (latest-wins), so new columns need no backfill.
        ALTER TABLE vms ADD COLUMN replication_state TEXT NULL;
        ALTER TABLE vms ADD COLUMN replication_health TEXT NULL;
        ALTER TABLE vms ADD COLUMN replication_last_apply_time TEXT NULL;
        """;

    private const string V7 = """
        -- Per-rule auto-resolve timeout (API.md §7.3): a live alert resolves
        -- automatically once no new occurrence arrives for this many seconds.
        -- NULL/absent = never auto-resolve (default). New column, so existing
        -- rules need no backfill.
        ALTER TABLE rules ADD COLUMN auto_resolve_after_s INTEGER NULL;
        """;
}
