using Dapper;
using Hyveman.Application;
using Hyveman.Domain;

namespace Hyveman.Infrastructure.Sqlite;

/// <summary>Latest-wins agent status (API.md §6.4, §10). The receive time is
/// captured independently of sent_at and always updates on a valid heartbeat;
/// state payloads follow the §7.4 ordering rule (boot_time change or newer
/// sent_at).</summary>
public sealed class AgentStatusStore(SqliteDb db) : IAgentStatusStore
{
    public async Task<AgentStatusRow?> GetAsync(string sourceId, CancellationToken ct)
    {
        using var conn = StoreHelpers.Open(db);
        var r = await conn.QuerySingleOrDefaultAsync(new CommandDefinition(
            "SELECT * FROM agent_status WHERE source_id = @sourceId", new { sourceId }, cancellationToken: ct));
        return r is null ? null : Map(r);
    }

    public async Task<IReadOnlyList<AgentStatusRow>> ListAllAsync(CancellationToken ct)
    {
        using var conn = StoreHelpers.Open(db);
        var rows = await conn.QueryAsync(new CommandDefinition(
            "SELECT * FROM agent_status ORDER BY source_id", cancellationToken: ct));
        return rows.Select(Map).ToList();
    }

    public async Task<bool> ApplyHeartbeatAsync(string sourceId, HeartbeatPayload hb, DateTimeOffset receivedAt, CancellationToken ct)
    {
        var existing = await GetAsync(sourceId, ct);
        var storeState = existing is null
            || (hb.BootTime is { } bt && existing.BootTime is { } ebt && bt != ebt)
            || (hb.SentAt > (existing.LastSentAt ?? DateTimeOffset.MinValue));

        var now = DateTimeOffset.UtcNow;
        using var conn = StoreHelpers.Open(db);
        if (storeState)
        {
            await conn.ExecuteAsync(new CommandDefinition("""
                INSERT INTO agent_status(source_id, last_received, last_sent_at, agent_version, os_build,
                                         boot_time, uptime_s, degraded, config_hash, counters_json,
                                         heartbeat_json, updated_at)
                VALUES (@SourceId, @LastReceived, @LastSentAt, @AgentVersion, @OsBuild,
                        @BootTime, @UptimeS, @Degraded, @ConfigHash, @CountersJson,
                        @HeartbeatJson, @UpdatedAt)
                ON CONFLICT(source_id) DO UPDATE SET
                    last_received = @LastReceived, last_sent_at = @LastSentAt,
                    agent_version = @AgentVersion, os_build = @OsBuild, boot_time = @BootTime,
                    uptime_s = @UptimeS, degraded = @Degraded, config_hash = @ConfigHash,
                    counters_json = @CountersJson, heartbeat_json = @HeartbeatJson,
                    updated_at = @UpdatedAt
                """, new
            {
                SourceId = sourceId,
                LastReceived = StoreHelpers.Fmt(receivedAt),
                LastSentAt = StoreHelpers.Fmt(hb.SentAt),
                AgentVersion = hb.AgentVersion,
                OsBuild = hb.OsBuild,
                BootTime = hb.BootTime is { } b2 ? StoreHelpers.Fmt(b2) : null,
                UptimeS = hb.UptimeS,
                Degraded = hb.Degraded,
                ConfigHash = hb.ConfigHash,
                CountersJson = hb.CountersJson,
                HeartbeatJson = System.Text.Json.JsonSerializer.Serialize(hb),
                UpdatedAt = StoreHelpers.Fmt(now),
            }, cancellationToken: ct));
        }
        else
        {
            // Older payload in the same boot session: the state is not stored,
            // but the receive time still resets the silence timer (§7.4).
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE agent_status SET last_received = @LastReceived, updated_at = @UpdatedAt WHERE source_id = @SourceId",
                new { SourceId = sourceId, LastReceived = StoreHelpers.Fmt(receivedAt), UpdatedAt = StoreHelpers.Fmt(now) },
                cancellationToken: ct));
        }
        return storeState;
    }

    public async Task<bool> ApplyFactsAsync(string sourceId, FactsPayload facts, DateTimeOffset receivedAt, CancellationToken ct)
    {
        var existing = await GetAsync(sourceId, ct);
        var storeFacts = existing?.FactsCollectedAt is null || facts.CollectedAt > existing.FactsCollectedAt;
        if (!storeFacts) return false;

        var now = DateTimeOffset.UtcNow;
        using var conn = StoreHelpers.Open(db);
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO agent_status(source_id, last_received, facts_json, facts_collected_at, updated_at)
            VALUES (@SourceId, @LastReceived, @FactsJson, @FactsCollectedAt, @UpdatedAt)
            ON CONFLICT(source_id) DO UPDATE SET
                facts_json = @FactsJson, facts_collected_at = @FactsCollectedAt,
                updated_at = @UpdatedAt
            """, new
        {
            SourceId = sourceId,
            LastReceived = StoreHelpers.Fmt(receivedAt),
            FactsJson = System.Text.Json.JsonSerializer.Serialize(facts),
            FactsCollectedAt = StoreHelpers.Fmt(facts.CollectedAt),
            UpdatedAt = StoreHelpers.Fmt(now),
        }, cancellationToken: ct));
        return true;
    }

    private static AgentStatusRow Map(dynamic r) => new(
        (string)r.source_id,
        StoreHelpers.Parse((string)r.last_received),
        StoreHelpers.ParseOpt((string?)r.last_sent_at),
        (string?)r.agent_version,
        (string?)r.os_build,
        StoreHelpers.ParseOpt((string?)r.boot_time),
        r.uptime_s is null ? null : (long?)StoreHelpers.ToLong(r.uptime_s),
        (string?)r.degraded,
        (string?)r.config_hash,
        (string?)r.counters_json,
        (string?)r.heartbeat_json,
        (string?)r.facts_json,
        StoreHelpers.ParseOpt((string?)r.facts_collected_at),
        StoreHelpers.Parse((string)r.updated_at));
}

public sealed class HostStore(SqliteDb db) : IHostStore
{
    public async Task<IReadOnlyList<HostRecord>> ListAsync(CancellationToken ct)
    {
        using var conn = StoreHelpers.Open(db);
        var rows = await conn.QueryAsync(new CommandDefinition(
            "SELECT * FROM hosts ORDER BY name", cancellationToken: ct));
        return rows.Select(Map).ToList();
    }

    public async Task<HostRecord?> GetAsync(string id, CancellationToken ct)
    {
        using var conn = StoreHelpers.Open(db);
        var r = await conn.QuerySingleOrDefaultAsync(new CommandDefinition(
            "SELECT * FROM hosts WHERE id = @id", new { id }, cancellationToken: ct));
        return r is null ? null : Map(r);
    }

    public async Task<HostRecord?> GetBySourceAsync(string sourceId, CancellationToken ct)
    {
        using var conn = StoreHelpers.Open(db);
        var r = await conn.QuerySingleOrDefaultAsync(new CommandDefinition(
            "SELECT * FROM hosts WHERE source_id = @sourceId", new { sourceId }, cancellationToken: ct));
        return r is null ? null : Map(r);
    }

    public async Task<HostRecord> CreateAsync(HostRecord host, CancellationToken ct)
    {
        using var conn = StoreHelpers.Open(db);
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO hosts(id, name, kind, source_id, idrac_url, idrac_cred_ref, enabled, notes, updated_at, created_at)
            VALUES (@Id, @Name, @Kind, @SourceId, @IdracUrl, @IdracCredRef, @Enabled, @Notes, @UpdatedAt, @CreatedAt)
            """, new
        {
            host.Id, host.Name, host.Kind, host.SourceId, host.IdracUrl, host.IdracCredRef,
            Enabled = host.Enabled ? 1 : 0, host.Notes,
            UpdatedAt = StoreHelpers.Fmt(host.UpdatedAt), CreatedAt = StoreHelpers.Fmt(host.CreatedAt),
        }, cancellationToken: ct));
        return host;
    }

    public async Task<bool> UpdateAsync(HostRecord host, DateTimeOffset expectedUpdatedAt, CancellationToken ct)
    {
        using var conn = StoreHelpers.Open(db);
        var affected = await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE hosts SET name = @Name, kind = @Kind, source_id = @SourceId, idrac_url = @IdracUrl,
                   idrac_cred_ref = @IdracCredRef, enabled = @Enabled, notes = @Notes, updated_at = @UpdatedAt
            WHERE id = @Id AND updated_at = @ExpectedUpdatedAt
            """, new
        {
            host.Id, host.Name, host.Kind, host.SourceId, host.IdracUrl, host.IdracCredRef,
            Enabled = host.Enabled ? 1 : 0, host.Notes, UpdatedAt = StoreHelpers.Fmt(host.UpdatedAt),
            ExpectedUpdatedAt = StoreHelpers.Fmt(expectedUpdatedAt),
        }, cancellationToken: ct));
        return affected > 0;
    }

    public async Task DeleteAsync(string id, CancellationToken ct)
    {
        using var conn = StoreHelpers.Open(db);
        using var tx = conn.BeginTransaction();
        await conn.ExecuteAsync(new CommandDefinition("DELETE FROM vms WHERE host_id = @id", new { id }, tx, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition("DELETE FROM components WHERE host_id = @id", new { id }, tx, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition("DELETE FROM health_snapshots WHERE host_id = @id", new { id }, tx, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition("DELETE FROM metrics WHERE host_id = @id", new { id }, tx, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition("DELETE FROM hosts WHERE id = @id", new { id }, tx, cancellationToken: ct));
        tx.Commit();
    }

    private static HostRecord Map(dynamic r) => new(
        (string)r.id, (string)r.name, (string)r.kind, (string?)r.source_id, (string?)r.idrac_url,
        (string?)r.idrac_cred_ref, (long)r.enabled == 1, (string?)r.notes,
        StoreHelpers.Parse((string)r.updated_at), StoreHelpers.Parse((string)r.created_at));
}
