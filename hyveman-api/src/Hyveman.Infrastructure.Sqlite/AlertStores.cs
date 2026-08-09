using Dapper;
using Hyveman.Application;
using Hyveman.Domain;

namespace Hyveman.Infrastructure.Sqlite;

public sealed class AlertStore(SqliteDb db) : IAlertStore
{
    public async Task<AlertRecord?> FindLiveAsync(string key, CancellationToken ct)
    {
        using var conn = StoreHelpers.Open(db);
        var r = await conn.QuerySingleOrDefaultAsync(new CommandDefinition("""
            SELECT * FROM alerts WHERE key = @key AND status IN ('active','acknowledged','silenced')
            ORDER BY first_seen DESC LIMIT 1
            """, new { key }, cancellationToken: ct));
        return r is null ? null : Map(r);
    }

    public async Task<AlertRecord?> GetLatestAsync(string key, CancellationToken ct)
    {
        using var conn = StoreHelpers.Open(db);
        var r = await conn.QuerySingleOrDefaultAsync(new CommandDefinition("""
            SELECT * FROM alerts WHERE key = @key ORDER BY last_seen DESC, id DESC LIMIT 1
            """, new { key }, cancellationToken: ct));
        return r is null ? null : Map(r);
    }

    public async Task<AlertRecord?> GetAsync(string id, CancellationToken ct)
    {
        using var conn = StoreHelpers.Open(db);
        var r = await conn.QuerySingleOrDefaultAsync(new CommandDefinition(
            "SELECT * FROM alerts WHERE id = @id", new { id }, cancellationToken: ct));
        return r is null ? null : Map(r);
    }

    public async Task CreateAsync(AlertRecord alert, CancellationToken ct)
    {
        using var conn = StoreHelpers.Open(db);
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO alerts(id, rule_id, host_id, source_id, key, fingerprint, severity, status,
                               title, detail, first_seen, last_seen, count, ack_at, ack_reason,
                               silence_until, resolved_at, updated_at)
            VALUES (@Id, @RuleId, @HostId, @SourceId, @Key, @Fingerprint, @Severity, @Status,
                    @Title, @Detail, @FirstSeen, @LastSeen, @Count, @AckAt, @AckReason,
                    @SilenceUntil, @ResolvedAt, @UpdatedAt)
            """, Args(alert), cancellationToken: ct));
    }

    public async Task UpdateAsync(AlertRecord alert, CancellationToken ct)
    {
        using var conn = StoreHelpers.Open(db);
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE alerts SET severity = @Severity, status = @Status, title = @Title, detail = @Detail,
                   first_seen = @FirstSeen, last_seen = @LastSeen, count = @Count,
                   ack_at = @AckAt, ack_reason = @AckReason, silence_until = @SilenceUntil,
                   resolved_at = @ResolvedAt, updated_at = @UpdatedAt
            WHERE id = @Id
            """, Args(alert), cancellationToken: ct));
    }

    public async Task<IReadOnlyList<AlertRecord>> ListAsync(AlertQuery q, CancellationToken ct)
    {
        var sql = "SELECT * FROM alerts WHERE 1=1";
        var p = new Dictionary<string, object?>();
        if (q.Status is { } status) { sql += " AND status = @Status"; p["Status"] = status; }
        if (q.HostId is { } hid) { sql += " AND host_id = @HostId"; p["HostId"] = hid; }
        if (q.RuleId is { } rid) { sql += " AND rule_id = @RuleId"; p["RuleId"] = rid; }
        if (q.From is { } from) { sql += " AND last_seen >= @From"; p["From"] = StoreHelpers.Fmt(from); }
        if (q.To is { } to) { sql += " AND last_seen < @To"; p["To"] = StoreHelpers.Fmt(to); }
        if (AlertCursor.TryDecode(q.Cursor ?? "", out var ctime, out var cid))
        {
            sql += " AND (last_seen, id) < (@CTime, @CId)";
            p["CTime"] = StoreHelpers.Fmt(ctime);
            p["CId"] = cid;
        }
        sql += " ORDER BY last_seen DESC, id DESC LIMIT @Limit";
        p["Limit"] = q.Limit;

        using var conn = StoreHelpers.Open(db);
        var rows = await conn.QueryAsync(new CommandDefinition(sql, p, cancellationToken: ct));
        return rows.Select(r => (AlertRecord)Map(r)).ToList();
    }

    public async Task<IReadOnlyList<AlertRecord>> ListLiveAsync(CancellationToken ct)
    {
        using var conn = StoreHelpers.Open(db);
        var rows = await conn.QueryAsync(new CommandDefinition(
            "SELECT * FROM alerts WHERE status IN ('active','acknowledged','silenced') ORDER BY last_seen DESC",
            cancellationToken: ct));
        return rows.Select(r => (AlertRecord)Map(r)).ToList();
    }

    public async Task<long> CountLiveAsync(CancellationToken ct)
    {
        using var conn = StoreHelpers.Open(db);
        return await conn.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(*) FROM alerts WHERE status IN ('active','acknowledged','silenced')", cancellationToken: ct));
    }

    public async Task<long> CountUnacknowledgedAsync(CancellationToken ct)
    {
        using var conn = StoreHelpers.Open(db);
        return await conn.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(*) FROM alerts WHERE status = 'active'", cancellationToken: ct));
    }

    public async Task ResolveForHostAsync(string hostId, DateTimeOffset at, CancellationToken ct)
    {
        using var conn = StoreHelpers.Open(db);
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE alerts SET status = 'resolved', resolved_at = @At, updated_at = @At
            WHERE host_id = @HostId AND status IN ('active','acknowledged','silenced')
            """, new { HostId = hostId, At = StoreHelpers.Fmt(at) }, cancellationToken: ct));
    }

    private static dynamic Args(AlertRecord a) => new
    {
        a.Id, a.RuleId, a.HostId, a.SourceId, a.Key, a.Fingerprint, a.Severity, a.Status,
        a.Title, a.Detail, FirstSeen = StoreHelpers.Fmt(a.FirstSeen), LastSeen = StoreHelpers.Fmt(a.LastSeen),
        a.Count, AckAt = a.AckAt is { } x ? StoreHelpers.Fmt(x) : null, a.AckReason,
        SilenceUntil = a.SilenceUntil is { } s ? StoreHelpers.Fmt(s) : null,
        ResolvedAt = a.ResolvedAt is { } r ? StoreHelpers.Fmt(r) : null, UpdatedAt = StoreHelpers.Fmt(a.UpdatedAt),
    };

    private static AlertRecord Map(dynamic r) => new(
        (string)r.id, (string?)r.rule_id, (string?)r.host_id, (string?)r.source_id,
        (string)r.key, (string)r.fingerprint, (string)r.severity, (string)r.status,
        (string)r.title, (string?)r.detail, StoreHelpers.Parse((string)r.first_seen),
        StoreHelpers.Parse((string)r.last_seen), StoreHelpers.ToLong(r.count),
        StoreHelpers.ParseOpt((string?)r.ack_at), (string?)r.ack_reason,
        StoreHelpers.ParseOpt((string?)r.silence_until), StoreHelpers.ParseOpt((string?)r.resolved_at),
        StoreHelpers.Parse((string)r.updated_at));
}

public static class AlertCursor
{
    public static bool TryDecode(string cursor, out DateTimeOffset time, out string id)
    {
        time = default;
        id = "";
        try
        {
            var parts = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(cursor)).Split('|');
            if (parts.Length != 2 || !DateTimeOffset.TryParse(parts[0], null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out time) || parts[1].Length == 0)
                return false;
            id = parts[1];
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static string Encode(DateTimeOffset time, string id) =>
        Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{time.ToUniversalTime():O}|{id}"));
}

public sealed class RuleStore(SqliteDb db) : IRuleStore
{
    public async Task<IReadOnlyList<RuleRecord>> ListAsync(CancellationToken ct)
    {
        using var conn = StoreHelpers.Open(db);
        var rows = await conn.QueryAsync(new CommandDefinition(
            "SELECT * FROM rules ORDER BY name", cancellationToken: ct));
        return rows.Select(r => (RuleRecord)Map(r)).ToList();
    }

    public async Task<RuleRecord?> GetAsync(string id, CancellationToken ct)
    {
        using var conn = StoreHelpers.Open(db);
        var r = await conn.QuerySingleOrDefaultAsync(new CommandDefinition(
            "SELECT * FROM rules WHERE id = @id", new { id }, cancellationToken: ct));
        return r is null ? null : Map(r);
    }

    public async Task<RuleRecord> CreateAsync(RuleRecord rule, CancellationToken ct)
    {
        using var conn = StoreHelpers.Open(db);
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO rules(id, name, type, match_json, severity, cooldown_s, enabled, created_at, updated_at)
            VALUES (@Id, @Name, @Type, @MatchJson, @Severity, @CooldownS, @Enabled, @CreatedAt, @UpdatedAt)
            """, Args(rule), cancellationToken: ct));
        return rule;
    }

    public async Task<bool> UpdateAsync(RuleRecord rule, DateTimeOffset expectedUpdatedAt, CancellationToken ct)
    {
        using var conn = StoreHelpers.Open(db);
        var p = Args(rule);
        return await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE rules SET name = @Name, type = @Type, match_json = @MatchJson, severity = @Severity,
                   cooldown_s = @CooldownS, enabled = @Enabled, updated_at = @UpdatedAt
            WHERE id = @Id AND updated_at = @Expected
            """, new
        {
            p.Id, p.Name, p.Type, p.MatchJson, p.Severity, p.CooldownS, p.Enabled, p.UpdatedAt,
            Expected = StoreHelpers.Fmt(expectedUpdatedAt),
        }, cancellationToken: ct)) > 0;
    }

    public async Task DeleteAsync(string id, CancellationToken ct)
    {
        using var conn = StoreHelpers.Open(db);
        using var tx = conn.BeginTransaction();
        await conn.ExecuteAsync(new CommandDefinition("DELETE FROM rule_channels WHERE rule_id = @id", new { id }, tx, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition("DELETE FROM rules WHERE id = @id", new { id }, tx, cancellationToken: ct));
        tx.Commit();
    }

    public async Task SetChannelsAsync(string ruleId, IReadOnlyList<string> channelIds, CancellationToken ct)
    {
        using var conn = StoreHelpers.Open(db);
        using var tx = conn.BeginTransaction();
        await conn.ExecuteAsync(new CommandDefinition("DELETE FROM rule_channels WHERE rule_id = @ruleId", new { ruleId }, tx, cancellationToken: ct));
        foreach (var cid in channelIds.Distinct())
            await conn.ExecuteAsync(new CommandDefinition(
                "INSERT INTO rule_channels(rule_id, channel_id) VALUES (@ruleId, @cid)",
                new { ruleId, cid }, tx, cancellationToken: ct));
        tx.Commit();
    }

    public async Task<IReadOnlyList<string>> GetChannelIdsAsync(string ruleId, CancellationToken ct)
    {
        using var conn = StoreHelpers.Open(db);
        var rows = await conn.QueryAsync<string>(new CommandDefinition(
            "SELECT channel_id FROM rule_channels WHERE rule_id = @ruleId ORDER BY channel_id", new { ruleId }, cancellationToken: ct));
        return rows.ToList();
    }

    private static dynamic Args(RuleRecord r) => new
    {
        r.Id, r.Name, r.Type, MatchJson = r.MatchJson, r.Severity, r.CooldownS,
        Enabled = r.Enabled ? 1 : 0, CreatedAt = StoreHelpers.Fmt(r.CreatedAt), UpdatedAt = StoreHelpers.Fmt(r.UpdatedAt),
    };

    private static RuleRecord Map(dynamic r) => new(
        (string)r.id, (string)r.name, (string)r.type, (string)r.match_json, (string)r.severity,
        StoreHelpers.ToLong(r.cooldown_s), (long)r.enabled == 1,
        StoreHelpers.Parse((string)r.created_at), StoreHelpers.Parse((string)r.updated_at));
}

public sealed class ChannelStore(SqliteDb db) : INotificationChannelStore
{
    public async Task<IReadOnlyList<ChannelRecord>> ListAsync(CancellationToken ct)
    {
        using var conn = StoreHelpers.Open(db);
        var rows = await conn.QueryAsync(new CommandDefinition(
            "SELECT * FROM notification_channels ORDER BY name", cancellationToken: ct));
        return rows.Select(r => (ChannelRecord)Map(r)).ToList();
    }

    public async Task<ChannelRecord?> GetAsync(string id, CancellationToken ct)
    {
        using var conn = StoreHelpers.Open(db);
        var r = await conn.QuerySingleOrDefaultAsync(new CommandDefinition(
            "SELECT * FROM notification_channels WHERE id = @id", new { id }, cancellationToken: ct));
        return r is null ? null : Map(r);
    }

    public async Task<ChannelRecord> CreateAsync(ChannelRecord c, CancellationToken ct)
    {
        using var conn = StoreHelpers.Open(db);
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO notification_channels(id, name, kind, config_ref, enabled, created, rotated, updated_at)
            VALUES (@Id, @Name, @Kind, @ConfigRef, @Enabled, @Created, @Rotated, @UpdatedAt)
            """, Args(c), cancellationToken: ct));
        return c;
    }

    public async Task<bool> UpdateAsync(ChannelRecord c, DateTimeOffset expectedUpdatedAt, CancellationToken ct)
    {
        using var conn = StoreHelpers.Open(db);
        return await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE notification_channels SET name = @Name, kind = @Kind, config_ref = @ConfigRef,
                   enabled = @Enabled, rotated = @Rotated, updated_at = @UpdatedAt
            WHERE id = @Id AND updated_at = @Expected
            """, new
        {
            c.Id, c.Name, c.Kind, c.ConfigRef, Enabled = c.Enabled ? 1 : 0,
            Rotated = c.Rotated is { } r ? StoreHelpers.Fmt(r) : null, UpdatedAt = StoreHelpers.Fmt(c.UpdatedAt),
            Expected = StoreHelpers.Fmt(expectedUpdatedAt),
        }, cancellationToken: ct)) > 0;
    }

    public async Task DeleteAsync(string id, CancellationToken ct)
    {
        using var conn = StoreHelpers.Open(db);
        using var tx = conn.BeginTransaction();
        await conn.ExecuteAsync(new CommandDefinition("DELETE FROM rule_channels WHERE channel_id = @id", new { id }, tx, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition("DELETE FROM notification_channels WHERE id = @id", new { id }, tx, cancellationToken: ct));
        tx.Commit();
    }

    public async Task MarkTestResultAsync(string id, bool ok, DateTimeOffset at, CancellationToken ct)
    {
        using var conn = StoreHelpers.Open(db);
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE notification_channels SET last_test_at = @At, last_test_ok = @Ok, updated_at = @At WHERE id = @Id",
            new { Id = id, At = StoreHelpers.Fmt(at), Ok = ok ? 1 : 0 }, cancellationToken: ct));
    }

    private static dynamic Args(ChannelRecord c) => new
    {
        c.Id, c.Name, c.Kind, c.ConfigRef, Enabled = c.Enabled ? 1 : 0,
        Created = StoreHelpers.Fmt(c.Created), Rotated = c.Rotated is { } r ? StoreHelpers.Fmt(r) : null,
        UpdatedAt = StoreHelpers.Fmt(c.UpdatedAt),
    };

    private static ChannelRecord Map(dynamic r) => new(
        (string)r.id, (string)r.name, (string)r.kind, (string?)r.config_ref, (long)r.enabled == 1,
        StoreHelpers.Parse((string)r.created), StoreHelpers.ParseOpt((string?)r.rotated),
        StoreHelpers.ParseOpt((string?)r.last_test_at),
        r.last_test_ok is null ? null : (long)r.last_test_ok == 1,
        StoreHelpers.Parse((string)r.updated_at));
}

public sealed class OutboxStore(SqliteDb db) : IOutboxStore
{
    public async Task EnqueueAsync(string alertId, string channelId, DateTimeOffset now, CancellationToken ct)
    {
        using var conn = StoreHelpers.Open(db);
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO notification_outbox(id, alert_id, channel_id, status, attempt_count, next_attempt_at, created_at)
            VALUES (@Id, @AlertId, @ChannelId, 'pending', 0, @Next, @Created)
            ON CONFLICT DO NOTHING
            """, new
        {
            Id = StoreHelpers.RandomId("out_", 18),
            AlertId = alertId,
            ChannelId = channelId,
            Next = StoreHelpers.Fmt(now),
            Created = StoreHelpers.Fmt(now),
        }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<OutboxItem>> DequeueDueAsync(int max, DateTimeOffset now, CancellationToken ct)
    {
        using var conn = StoreHelpers.Open(db);
        using var tx = conn.BeginTransaction();
        var rows = await conn.QueryAsync(new CommandDefinition("""
            SELECT * FROM notification_outbox
            WHERE status = 'pending' AND next_attempt_at <= @Now
            ORDER BY next_attempt_at LIMIT @Max
            """, new { Now = StoreHelpers.Fmt(now), Max = max }, tx, cancellationToken: ct));
        var items = rows.Select(r => (OutboxItem)Map(r) with { Status = "sending" }).ToList();
        foreach (var item in items)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE notification_outbox SET status = 'sending' WHERE id = @Id",
                new { item.Id }, tx, cancellationToken: ct));
        }
        tx.Commit();
        return items;
    }

    public async Task MarkResultAsync(string id, bool success, string? error, DateTimeOffset now, CancellationToken ct)
    {
        using var conn = StoreHelpers.Open(db);
        if (success)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE notification_outbox SET status = 'sent', sent_at = @At, last_error = NULL WHERE id = @Id",
                new { Id = id, At = StoreHelpers.Fmt(now) }, cancellationToken: ct));
        }
        else
        {
            // Retry with exponential backoff (1m, 2m, 4m, ...); cap at 8 attempts.
            var attempt = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT attempt_count FROM notification_outbox WHERE id = @Id", new { Id = id }, cancellationToken: ct));
            await conn.ExecuteAsync(new CommandDefinition("""
                UPDATE notification_outbox SET
                    status = CASE WHEN attempt_count + 1 >= 8 THEN 'failed' ELSE 'pending' END,
                    attempt_count = attempt_count + 1,
                    next_attempt_at = @Next,
                    last_error = @Error
                WHERE id = @Id
                """, new
            {
                Id = id,
                Next = StoreHelpers.Fmt(now.AddMinutes(Math.Pow(2, Math.Min(6, attempt)))),
                Error = error is { Length: > 400 } ? error[..400] : error,
            }, cancellationToken: ct));
        }
    }

    public async Task<long> CountPendingAsync(CancellationToken ct)
    {
        using var conn = StoreHelpers.Open(db);
        return await conn.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(*) FROM notification_outbox WHERE status IN ('pending','sending')", cancellationToken: ct));
    }

    private static OutboxItem Map(dynamic r) => new(
        (string)r.id, (string?)r.alert_id, (string)r.channel_id, (string)r.status,
        (int)StoreHelpers.ToLong(r.attempt_count), StoreHelpers.Parse((string)r.next_attempt_at),
        (string?)r.last_error, StoreHelpers.Parse((string)r.created_at),
        StoreHelpers.ParseOpt((string?)r.sent_at));
}
