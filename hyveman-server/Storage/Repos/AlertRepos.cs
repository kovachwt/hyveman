using Dapper;
using Hyveman.Server.Auth;
using Microsoft.Data.Sqlite;

namespace Hyveman.Server.Storage.Repos;

public sealed record RuleRow(string Id, string Name, string Type, string MatchJson, string Severity,
    int Cooldown, bool Enabled, string Created);

public sealed record AlertRow(string Id, string RuleId, string? HostId, string? SourceId, string Severity,
    string Signature, string FirstSeen, string LastSeen, int Count, string Status, string? DetailJson,
    string? LastNotifiedAt);

public sealed class AlertRepository
{
    private readonly SqliteFactory _factory;

    public AlertRepository(SqliteFactory factory) => _factory = factory;

    // ── rules ──────────────────────────────────────────────────────────────
    public async Task<List<RuleRow>> ListRulesAsync(bool enabledOnly = false)
    {
        await using var conn = _factory.OpenReadOnly();
        var sql = "SELECT id, name, type, match_json, severity, cooldown, enabled, created FROM rules";
        if (enabledOnly) sql += " WHERE enabled=1";
        sql += " ORDER BY name";
        var rows = await conn.QueryAsync<RuleRow>(sql);
        return rows.ToList();
    }

    public async Task<RuleRow?> GetRuleAsync(string id)
    {
        await using var conn = _factory.OpenReadOnly();
        return await conn.QueryFirstOrDefaultAsync<RuleRow>(
            "SELECT id, name, type, match_json, severity, cooldown, enabled, created FROM rules WHERE id=@id", new { id });
    }

    public async Task<string> InsertRuleAsync(SqliteConnection conn, string id, string name, string type,
        string matchJson, string severity, int cooldown)
    {
        await conn.ExecuteAsync("""
            INSERT INTO rules(id, name, type, match_json, severity, cooldown)
            VALUES (@id, @name, @type, @matchJson, @severity, @cooldown)
            """, new { id, name, type, matchJson, severity, cooldown });
        return id;
    }

    public async Task<bool> UpdateRuleAsync(SqliteConnection conn, string id, string name, string type,
        string matchJson, string severity, int cooldown, bool enabled)
        => await conn.ExecuteAsync("""
            UPDATE rules SET name=@name, type=@type, match_json=@matchJson, severity=@severity,
                   cooldown=@cooldown, enabled=@enabled
            WHERE id=@id
            """, new { id, name, type, matchJson, severity, cooldown, enabled = enabled ? 1 : 0 }) > 0;

    public async Task<bool> DeleteRuleAsync(SqliteConnection conn, string id)
        => await conn.ExecuteAsync("DELETE FROM rules WHERE id=@id", new { id }) > 0;

    // ── alerts ─────────────────────────────────────────────────────────────
    /// <summary>
    /// Find an active alert matching the dedup key (rule_id, host_id, source_id, signature).
    /// Exactly one target column is populated (host rules vs source rules — §9.2).
    /// </summary>
    public async Task<AlertRow?> FindActiveAsync(string ruleId, string? hostId, string? sourceId, string signature)
    {
        await using var conn = _factory.OpenReadOnly();
        return await conn.QueryFirstOrDefaultAsync<AlertRow>(
            """
            SELECT id, rule_id, host_id, source_id, severity, signature, first_seen, last_seen, count, status, detail_json, last_notified_at
            FROM alerts
            WHERE rule_id=@ruleId AND signature=@signature AND status='active'
              AND ((@hostId IS NOT NULL AND host_id=@hostId) OR (@sourceId IS NOT NULL AND source_id=@sourceId))
            LIMIT 1
            """,
            new { ruleId, hostId, sourceId, signature });
    }

    public async Task<AlertRow?> GetAsync(string id)
    {
        await using var conn = _factory.OpenReadOnly();
        return await conn.QueryFirstOrDefaultAsync<AlertRow>(
            "SELECT id, rule_id, host_id, source_id, severity, signature, first_seen, last_seen, count, status, detail_json, last_notified_at FROM alerts WHERE id=@id",
            new { id });
    }

    public static async Task<(string id, bool created)> UpsertAsync(SqliteConnection conn,
        string id, string ruleId, string? hostId, string? sourceId, string severity, string signature,
        string firstSeen, string detailJson)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO alerts(id, rule_id, host_id, source_id, severity, signature, first_seen, last_seen, count, detail_json)
            VALUES (@id, @ruleId, @hostId, @sourceId, @severity, @signature, @firstSeen, @firstSeen, 1, @detailJson)
            ON CONFLICT DO NOTHING
            RETURNING id;
            """;
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@ruleId", ruleId);
        cmd.Parameters.AddWithValue("@hostId", (object?)hostId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@sourceId", (object?)sourceId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@severity", severity);
        cmd.Parameters.AddWithValue("@signature", signature);
        cmd.Parameters.AddWithValue("@firstSeen", firstSeen);
        cmd.Parameters.AddWithValue("@detailJson", (object?)detailJson ?? DBNull.Value);
        var inserted = await cmd.ExecuteScalarAsync();
        return inserted is not null ? (id, true) : (id, false);
    }

    /// <summary>Bump an existing alert (last_seen=now, count++). Returns the new count.</summary>
    public static Task<int> BumpAsync(SqliteConnection conn, string alertId, string now)
        => conn.ExecuteScalarAsync<int>("UPDATE alerts SET last_seen=@now, count=count+1 WHERE id=@id RETURNING count", new { id = alertId, now });

    /// <summary>Persist an escalated severity / new detail on a bumped alert (§9.2 escalation).</summary>
    public static Task UpdateSeverityDetailAsync(SqliteConnection conn, string alertId, string severity, string? detailJson)
        => conn.ExecuteAsync("UPDATE alerts SET severity=@severity, detail_json=@detailJson WHERE id=@id",
            new { id = alertId, severity, detailJson });

    /// <summary>Record that a notification was sent for this alert (persistent cooldown, §9.2).</summary>
    public static Task MarkNotifiedAsync(SqliteConnection conn, string alertId, string now)
        => conn.ExecuteAsync("UPDATE alerts SET last_notified_at=@now WHERE id=@id", new { id = alertId, now });

    public static Task<int> ResolveAsync(SqliteConnection conn, string alertId, string now)
        => conn.ExecuteAsync("UPDATE alerts SET status='resolved', last_seen=@now WHERE id=@id AND status='active'", new { id = alertId, now });

    public async Task<List<AlertRow>> ListAsync(int limit = 200, string? status = null)
    {
        await using var conn = _factory.OpenReadOnly();
        var sql = "SELECT id, rule_id, host_id, source_id, severity, signature, first_seen, last_seen, count, status, detail_json, last_notified_at FROM alerts";
        if (!string.IsNullOrEmpty(status)) sql += $" WHERE status='{status.Replace("'", "''")}'";
        sql += " ORDER BY last_seen DESC LIMIT @limit";
        var rows = await conn.QueryAsync<AlertRow>(sql, new { limit });
        return rows.ToList();
    }

    public async Task<List<AlertRow>> ActiveForHostAsync(string hostId)
    {
        await using var conn = _factory.OpenReadOnly();
        var rows = await conn.QueryAsync<AlertRow>(
            "SELECT id, rule_id, host_id, source_id, severity, signature, first_seen, last_seen, count, status, detail_json, last_notified_at FROM alerts WHERE host_id=@hostId AND status='active' ORDER BY last_seen DESC",
            new { hostId });
        return rows.ToList();
    }

    public static Task SetStatusAsync(SqliteConnection conn, string alertId, string status, string now)
        => conn.ExecuteAsync("UPDATE alerts SET status=@status, last_seen=@now WHERE id=@id", new { id = alertId, status, now });

    // ── rule_channels ──────────────────────────────────────────────────────
    public static Task SetRuleChannelsAsync(SqliteConnection conn, string ruleId, IReadOnlyList<string> channelIds)
    {
        var tasks = new List<Task>
        {
            conn.ExecuteAsync("DELETE FROM rule_channels WHERE rule_id=@ruleId", new { ruleId }),
        };
        foreach (var ch in channelIds)
            tasks.Add(conn.ExecuteAsync("INSERT OR IGNORE INTO rule_channels(rule_id, channel_id) VALUES (@ruleId, @channelId)",
                new { ruleId, channelId = ch }));
        return Task.WhenAll(tasks);
    }

    public async Task<List<string>> ChannelsForRuleAsync(string ruleId)
    {
        await using var conn = _factory.OpenReadOnly();
        var rows = await conn.QueryAsync<string>("SELECT channel_id FROM rule_channels WHERE rule_id=@ruleId", new { ruleId });
        return rows.ToList();
    }

    // ── maintenance windows ────────────────────────────────────────────────
    public static Task InsertWindowAsync(SqliteConnection conn, string id, string hostId, string start, string end,
        string? reason, string? createdBy)
        => conn.ExecuteAsync("""
            INSERT INTO maintenance_windows(id, host_id, start, end, reason, created_by)
            VALUES (@id, @hostId, @start, @end, @reason, @createdBy)
            """, new { id, hostId, start, end, reason, createdBy });

    public async Task<List<MaintenanceWindowRow>> WindowsForHostAsync(string hostId, bool activeOnly = false)
    {
        await using var conn = _factory.OpenReadOnly();
        var sql = "SELECT id, host_id, start, end, reason, created_by FROM maintenance_windows WHERE host_id=@hostId";
        if (activeOnly) sql += " AND end >= strftime('%Y-%m-%dT%H:%M:%fZ','now')";
        sql += " ORDER BY start DESC";
        var rows = await conn.QueryAsync<MaintenanceWindowRow>(sql, new { hostId });
        return rows.ToList();
    }

    public static Task DeleteWindowAsync(SqliteConnection conn, string id)
        => conn.ExecuteAsync("DELETE FROM maintenance_windows WHERE id=@id", new { id });

    // ── notification queue ─────────────────────────────────────────────────
    public static Task EnqueueAsync(SqliteConnection conn, string alertId, string channelId, string nextAt)
        => conn.ExecuteAsync("INSERT INTO notification_queue(alert_id, channel_id, next_at) VALUES (@alertId, @channelId, @nextAt)",
            new { alertId, channelId, nextAt });

    public async Task<List<QueueRow>> DueAsync(string nowIso, int limit = 50)
    {
        await using var conn = _factory.OpenReadOnly();
        var rows = await conn.QueryAsync<QueueRow>(
            "SELECT id, alert_id, channel_id, attempts, next_at, last_error FROM notification_queue WHERE next_at <= @nowIso ORDER BY id LIMIT @limit",
            new { nowIso, limit });
        return rows.ToList();
    }

    public static Task MarkSentAsync(SqliteConnection conn, long id)
        => conn.ExecuteAsync("DELETE FROM notification_queue WHERE id=@id", new { id });

    public static Task MarkFailedAsync(SqliteConnection conn, long id, int attempts, string nextAt, string error)
        => conn.ExecuteAsync("UPDATE notification_queue SET attempts=@attempts, next_at=@nextAt, last_error=@error WHERE id=@id",
            new { id, attempts, nextAt, error });

    public async Task<long> QueueDepthAsync()
    {
        await using var conn = _factory.OpenReadOnly();
        return await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM notification_queue");
    }
}

public sealed record QueueRow(long Id, string AlertId, string ChannelId, int Attempts, string NextAt, string? LastError);
public sealed record MaintenanceWindowRow(string Id, string HostId, string Start, string End, string? Reason, string? CreatedBy);

public sealed class ChannelRepository
{
    private readonly SqliteFactory _factory;

    public ChannelRepository(SqliteFactory factory) => _factory = factory;

    public async Task<List<ChannelRow>> ListAsync()
    {
        await using var conn = _factory.OpenReadOnly();
        var rows = await conn.QueryAsync<ChannelRow>(
            "SELECT id, name, kind, config_ref, enabled, created FROM notification_channels ORDER BY name");
        return rows.ToList();
    }

    public async Task<ChannelRow?> GetAsync(string id)
    {
        await using var conn = _factory.OpenReadOnly();
        return await conn.QueryFirstOrDefaultAsync<ChannelRow>(
            "SELECT id, name, kind, config_ref, enabled, created FROM notification_channels WHERE id=@id", new { id });
    }

    public static Task InsertAsync(SqliteConnection conn, string id, string name, string kind, string configRef, bool enabled)
        => conn.ExecuteAsync("""
            INSERT INTO notification_channels(id, name, kind, config_ref, enabled)
            VALUES (@id, @name, @kind, @configRef, @enabled)
            """, new { id, name, kind, configRef, enabled = enabled ? 1 : 0 });

    public async Task<bool> UpdateAsync(SqliteConnection conn, string id, string name, string kind, string configRef, bool enabled)
        => await conn.ExecuteAsync("""
            UPDATE notification_channels SET name=@name, kind=@kind, config_ref=@configRef, enabled=@enabled
            WHERE id=@id
            """, new { id, name, kind, configRef, enabled = enabled ? 1 : 0 }) > 0;

    public static Task DeleteAsync(SqliteConnection conn, string id)
        => conn.ExecuteAsync("DELETE FROM notification_channels WHERE id=@id", new { id });
}

public sealed record ChannelRow(string Id, string Name, string Kind, string ConfigRef, bool Enabled, string Created);

public sealed class CredentialRepository
{
    private readonly SqliteFactory _factory;

    public CredentialRepository(SqliteFactory factory) => _factory = factory;

    public async Task<string?> GetBlobByLabelAsync(string label, Func<byte[], string?> decrypt)
    {
        await using var conn = _factory.OpenReadOnly();
        var blob = await conn.QueryFirstOrDefaultAsync<byte[]>(
            "SELECT blob_encrypted FROM credentials WHERE label=@label", new { label });
        return blob is null ? null : decrypt(blob);
    }

    public string? GetBlobByLabelSync(string label, Func<byte[], string?> decrypt)
    {
        using var conn = _factory.OpenReadOnly();
        var blob = conn.QueryFirstOrDefault<byte[]>(
            "SELECT blob_encrypted FROM credentials WHERE label=@label", new { label });
        return blob is null ? null : decrypt(blob);
    }

    public async Task<List<CredentialMeta>> ListAsync()
    {
        await using var conn = _factory.OpenReadOnly();
        var rows = await conn.QueryAsync<CredentialMetaRow>(
            "SELECT id, kind, label, created, rotated FROM credentials ORDER BY label");
        return rows.Select(r => new Auth.CredentialMeta(r.Id, r.Kind, r.Label,
            DateTimeOffset.Parse(r.Created), r.Rotated is null ? null : DateTimeOffset.Parse(r.Rotated))).ToList();
    }

    private sealed record CredentialMetaRow(string Id, string Kind, string Label, string Created, string? Rotated);
}

public sealed class PasskeyRepository
{
    private readonly SqliteFactory _factory;

    public PasskeyRepository(SqliteFactory factory) => _factory = factory;

    public async Task<int> CountAsync()
    {
        await using var conn = _factory.OpenReadOnly();
        return (int)await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM passkeys");
    }

    public async Task<List<PasskeyRow>> ListAsync()
    {
        await using var conn = _factory.OpenReadOnly();
        var rows = await conn.QueryAsync<PasskeyRow>(
            "SELECT id, name, credential_id, public_key, sign_count, created, last_used FROM passkeys ORDER BY created");
        return rows.ToList();
    }

    public async Task<PasskeyRow?> GetByCredentialIdAsync(string credentialIdB64Url)
    {
        await using var conn = _factory.OpenReadOnly();
        return await conn.QueryFirstOrDefaultAsync<PasskeyRow>(
            "SELECT id, name, credential_id, public_key, sign_count, created, last_used FROM passkeys WHERE credential_id=@credentialIdB64Url",
            new { credentialIdB64Url });
    }

    public static Task InsertAsync(SqliteConnection conn, string id, string name, string credentialIdB64Url,
        byte[] publicKey)
        => conn.ExecuteAsync("""
            INSERT INTO passkeys(id, name, credential_id, public_key)
            VALUES (@id, @name, @credentialIdB64Url, @publicKey)
            """, new { id, name, credentialIdB64Url, publicKey });

    public static Task UpdateSignCountAsync(SqliteConnection conn, string id, uint signCount, string lastUsed)
        => conn.ExecuteAsync("UPDATE passkeys SET sign_count=@signCount, last_used=@lastUsed WHERE id=@id",
            new { id, signCount, lastUsed });

    public static Task DeleteAsync(SqliteConnection conn, string id)
        => conn.ExecuteAsync("DELETE FROM passkeys WHERE id=@id", new { id });

    public static Task ClearAllAsync(SqliteConnection conn)
        => conn.ExecuteAsync("DELETE FROM passkeys");
}

public sealed record PasskeyRow(string Id, string Name, string CredentialId, byte[] PublicKey,
    long SignCount, string Created, string? LastUsed);

public sealed class AuditRepository
{
    private readonly SqliteFactory _factory;
    private readonly SqliteWriter _writer;

    public AuditRepository(SqliteFactory factory, SqliteWriter writer)
    {
        _factory = factory;
        _writer = writer;
    }

    public Task WriteAsync(string actor, string action, string? targetKind, string? targetId, string? detailJson)
        => _writer.WithTransactionAsync(async conn =>
        {
            await conn.ExecuteAsync("""
                INSERT INTO audit_log(actor, action, target_kind, target_id, detail_json)
                VALUES (@actor, @action, @targetKind, @targetId, @detailJson)
                """, new { actor, action, targetKind, targetId, detailJson });
        });

    public async Task<List<AuditRow>> ListAsync(int limit = 200)
    {
        await using var conn = _factory.OpenReadOnly();
        var rows = await conn.QueryAsync<AuditRow>(
            "SELECT id, time, actor, action, target_kind, target_id, detail_json FROM audit_log ORDER BY id DESC LIMIT @limit",
            new { limit });
        return rows.ToList();
    }
}

public sealed record AuditRow(long Id, string Time, string Actor, string Action, string? TargetKind, string? TargetId, string? DetailJson);
