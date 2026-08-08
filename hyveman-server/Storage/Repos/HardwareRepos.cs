using Dapper;
using Microsoft.Data.Sqlite;

namespace Hyveman.Server.Storage.Repos;

public sealed record HostRow(string Id, string? SourceId, string Name, string? Kind,
    string? IdracUrl, string? IdracCredRef, bool PollEnabled, string? LastPollAt, bool? LastPollOk, string Created);

public sealed class HostRepository
{
    private readonly SqliteFactory _factory;

    public HostRepository(SqliteFactory factory) => _factory = factory;

    public async Task<List<HostRow>> ListAsync(bool pollEnabledOnly = false)
    {
        await using var conn = _factory.OpenReadOnly();
        var sql = "SELECT id, source_id, name, kind, idrac_url, idrac_cred_ref, poll_enabled, last_poll_at, last_poll_ok, created FROM hosts";
        if (pollEnabledOnly) sql += " WHERE poll_enabled=1";
        sql += " ORDER BY name";
        var rows = await conn.QueryAsync<HostRow>(sql);
        return rows.ToList();
    }

    public async Task<HostRow?> GetAsync(string id)
    {
        await using var conn = _factory.OpenReadOnly();
        return await conn.QueryFirstOrDefaultAsync<HostRow>(
            "SELECT id, source_id, name, kind, idrac_url, idrac_cred_ref, poll_enabled, last_poll_at, last_poll_ok, created FROM hosts WHERE id=@id",
            new { id });
    }

    public async Task<HostRow?> GetBySourceIdAsync(string sourceId)
    {
        await using var conn = _factory.OpenReadOnly();
        return await conn.QueryFirstOrDefaultAsync<HostRow>(
            "SELECT id, source_id, name, kind, idrac_url, idrac_cred_ref, poll_enabled, last_poll_at, last_poll_ok, created FROM hosts WHERE source_id=@sourceId",
            new { sourceId });
    }

    public Task InsertAsync(SqliteConnection conn, string id, string? sourceId, string name, string? kind,
        string? idracUrl, string? idracCredRef)
        => conn.ExecuteAsync("""
            INSERT INTO hosts(id, source_id, name, kind, idrac_url, idrac_cred_ref)
            VALUES (@id, @sourceId, @name, @kind, @idracUrl, @idracCredRef)
            """, new { id, sourceId, name, kind, idracUrl, idracCredRef });

    public async Task<bool> UpdateAsync(SqliteConnection conn, string id, string? sourceId, string name,
        string? kind, string? idracUrl, string? idracCredRef, bool pollEnabled)
        => await conn.ExecuteAsync("""
            UPDATE hosts SET source_id=@sourceId, name=@name, kind=@kind, idrac_url=@idracUrl,
                   idrac_cred_ref=@idracCredRef, poll_enabled=@pollEnabled
            WHERE id=@id
            """, new { id, sourceId, name, kind, idracUrl, idracCredRef, pollEnabled = pollEnabled ? 1 : 0 }) > 0;

    public Task MarkPollAsync(SqliteConnection conn, string id, string at, bool ok)
        => conn.ExecuteAsync("UPDATE hosts SET last_poll_at=@at, last_poll_ok=@ok WHERE id=@id",
            new { id, at, ok = ok ? 1 : 0 });
}

public sealed record ComponentRow(string Id, string HostId, string Type, string Name, string State,
    string? Detail, string LastSeen);

public sealed record MetricRow(string HostId, string Time, string Name, double Value, string? Unit);

public sealed class ComponentRepository
{
    private readonly SqliteFactory _factory;

    public ComponentRepository(SqliteFactory factory) => _factory = factory;

    /// <summary>Diff-merge component states: upsert each, resolve ones no longer present to 'unknown'.</summary>
    public static async Task MergeComponentsAsync(SqliteConnection conn, string hostId, IReadOnlyList<ComponentState> states, string seenAt)
    {
        foreach (var c in states)
        {
            var id = "cmp_" + Common.Ulid.New();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO components(id, host_id, type, name, state, detail, last_seen)
                VALUES (@id, @hostId, @type, @name, @state, @detail, @seenAt)
                ON CONFLICT(host_id, type, name) DO UPDATE SET
                  state=excluded.state, detail=excluded.detail, last_seen=excluded.last_seen
                """;
            cmd.Parameters.AddWithValue("@id", id);
            cmd.Parameters.AddWithValue("@hostId", hostId);
            cmd.Parameters.AddWithValue("@type", c.Type);
            cmd.Parameters.AddWithValue("@name", c.Name);
            cmd.Parameters.AddWithValue("@state", c.State);
            cmd.Parameters.AddWithValue("@detail", (object?)c.Detail ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@seenAt", seenAt);
            await cmd.ExecuteNonQueryAsync();
        }
        // Components previously present but absent from this poll → unknown (transient loss), not deleted (§8.3).
        var names = states.Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
        if (names.Count > 0)
        {
            foreach (var type in states.Select(s => s.Type).Distinct())
            {
                var existing = await conn.QueryAsync<(string Name, string Type)>(
                    "SELECT name, type FROM components WHERE host_id=@hostId AND type=@type", new { hostId, type });
                foreach (var row in existing)
                {
                    if (!names.Contains(row.Name))
                    {
                        await conn.ExecuteAsync("""
                            UPDATE components SET state='unknown', last_seen=@seenAt
                            WHERE host_id=@hostId AND type=@type AND name=@name
                            """, new { hostId, type = row.Type, name = row.Name, seenAt });
                    }
                }
            }
        }
    }

    /// <summary>Mark all components of a host unknown (poller unreachable path). Never deletes rows.</summary>
    public static Task MarkAllUnknownAsync(SqliteConnection conn, string hostId, string seenAt)
        => conn.ExecuteAsync("UPDATE components SET state='unknown', last_seen=@seenAt WHERE host_id=@hostId",
            new { hostId, seenAt });

    public async Task<List<ComponentRow>> ListForHostAsync(string hostId)
    {
        await using var conn = _factory.OpenReadOnly();
        var rows = await conn.QueryAsync<ComponentRow>(
            "SELECT id, host_id, type, name, state, detail, last_seen FROM components WHERE host_id=@hostId ORDER BY type, name",
            new { hostId });
        return rows.ToList();
    }

    public async Task<List<(string Type, string Name, string State)>> CurrentStatesAsync(string hostId)
    {
        await using var conn = _factory.OpenReadOnly();
        var rows = await conn.QueryAsync<(string Type, string Name, string State)>(
            "SELECT type, name, state FROM components WHERE host_id=@hostId", new { hostId });
        return rows.ToList();
    }

    public static Task InsertMetricAsync(SqliteConnection conn, string hostId, string time, string name, double value, string? unit)
        => conn.ExecuteAsync("INSERT INTO metrics(host_id, time, name, value, unit) VALUES (@hostId, @time, @name, @value, @unit)",
            new { hostId, time, name, value, unit });

    public static Task InsertSnapshotAsync(SqliteConnection conn, string hostId, string time, string rollup, string componentsJson)
        => conn.ExecuteAsync("""
            INSERT INTO health_snapshots(host_id, time, rollup_state, components_json)
            VALUES (@hostId, @time, @rollup, @componentsJson)
            ON CONFLICT(host_id, time) DO UPDATE SET rollup_state=excluded.rollup_state, components_json=excluded.components_json
            """, new { hostId, time, rollup, componentsJson });

    public async Task<List<MetricRow>> MetricsAsync(string hostId, string name, int limit = 300)
    {
        await using var conn = _factory.OpenReadOnly();
        var rows = await conn.QueryAsync<MetricRow>(
            "SELECT host_id, time, name, value, unit FROM metrics WHERE host_id=@hostId AND name=@name ORDER BY time DESC LIMIT @limit",
            new { hostId, name, limit });
        return rows.ToList();
    }

    public async Task<List<(string Time, string Rollup)>> SnapshotsAsync(string hostId, int limit = 200)
    {
        await using var conn = _factory.OpenReadOnly();
        var rows = await conn.QueryAsync<(string Time, string Rollup)>(
            "SELECT time, rollup_state AS Rollup FROM health_snapshots WHERE host_id=@hostId ORDER BY time DESC LIMIT @limit",
            new { hostId });
        return rows.ToList();
    }

    /// <summary>Replace a host's VM set in one transaction (delete-then-insert, §7.7).</summary>
    public static Task ReplaceVmsAsync(SqliteConnection conn, string hostId, IReadOnlyList<VmState> vms)
    {
        var tasks = new List<Task> { conn.ExecuteAsync("DELETE FROM vms WHERE host_id=@hostId", new { hostId }) };
        foreach (var vm in vms)
        {
            var id = "vm_" + Common.Ulid.New();
            tasks.Add(conn.ExecuteAsync("""
                INSERT INTO vms(id, host_id, name, state, heartbeat_ok, last_seen, cpu_pct, mem_mb)
                VALUES (@id, @hostId, @name, @state, @hb, @lastSeen, @cpu, @mem)
                ON CONFLICT(host_id, name) DO UPDATE SET
                  state=excluded.state, heartbeat_ok=excluded.heartbeat_ok, last_seen=excluded.last_seen,
                  cpu_pct=excluded.cpu_pct, mem_mb=excluded.mem_mb
                """, new { id, hostId, name = vm.Name, state = vm.State, hb = vm.HeartbeatOk.HasValue ? (vm.HeartbeatOk.Value ? 1 : 0) : (int?)null,
                    lastSeen = vm.LastSeen, cpu = vm.CpuPct, mem = vm.MemMb }));
        }
        return Task.WhenAll(tasks);
    }

    public async Task<List<VmRow>> VmsAsync(string hostId)
    {
        await using var conn = _factory.OpenReadOnly();
        var rows = await conn.QueryAsync<VmRow>(
            "SELECT id, host_id, name, state, heartbeat_ok, last_seen, cpu_pct, mem_mb FROM vms WHERE host_id=@hostId ORDER BY name",
            new { hostId });
        return rows.ToList();
    }
}

public sealed record ComponentState(string Type, string Name, string State, string? Detail = null);
public sealed record VmState(string Name, string State, bool? HeartbeatOk, string? LastSeen, double? CpuPct, int? MemMb);
public sealed record VmRow(string Id, string HostId, string Name, string? State, bool? HeartbeatOk, string? LastSeen, double? CpuPct, int? MemMb);

public sealed class HeartbeatRepository
{
    private readonly SqliteFactory _factory;

    public HeartbeatRepository(SqliteFactory factory) => _factory = factory;

    public static Task UpsertAsync(SqliteConnection conn, HeartbeatRow hb)
        => conn.ExecuteAsync("""
            INSERT INTO agent_heartbeats
              (source_id, sent_at, received_at, agent_version, protocol_version, os_build,
               boot_time, uptime_s, degraded, config_hash, counters_json, free_disk_json)
            VALUES
              (@sourceId, @sentAt, @receivedAt, @agentVersion, @protocolVersion, @osBuild,
               @bootTime, @uptimeS, @degraded, @configHash, @countersJson, @freeDiskJson)
            ON CONFLICT(source_id) DO UPDATE SET
              sent_at=excluded.sent_at, received_at=excluded.received_at, agent_version=excluded.agent_version,
              protocol_version=excluded.protocol_version, os_build=excluded.os_build, boot_time=excluded.boot_time,
              uptime_s=excluded.uptime_s, degraded=excluded.degraded, config_hash=excluded.config_hash,
              counters_json=excluded.counters_json, free_disk_json=excluded.free_disk_json
            """, new
        {
            sourceId = hb.SourceId, sentAt = hb.SentAt, receivedAt = hb.ReceivedAt, agentVersion = hb.AgentVersion,
            protocolVersion = hb.ProtocolVersion, osBuild = hb.OsBuild, bootTime = hb.BootTime, uptimeS = hb.UptimeS,
            degraded = hb.Degraded, configHash = hb.ConfigHash, countersJson = hb.CountersJson, freeDiskJson = hb.FreeDiskJson,
        });

    public async Task<List<HeartbeatRow>> AllAsync()
    {
        await using var conn = _factory.OpenReadOnly();
        var rows = await conn.QueryAsync<HeartbeatRow>("SELECT * FROM agent_heartbeats");
        return rows.ToList();
    }

    public async Task<HeartbeatRow?> GetAsync(string sourceId)
    {
        await using var conn = _factory.OpenReadOnly();
        return await conn.QueryFirstOrDefaultAsync<HeartbeatRow>(
            "SELECT * FROM agent_heartbeats WHERE source_id=@sourceId", new { sourceId });
    }
}

public sealed record HeartbeatRow(string SourceId, string SentAt, string ReceivedAt, string? AgentVersion,
    int? ProtocolVersion, string? OsBuild, string? BootTime, long? UptimeS, string Degraded,
    string? ConfigHash, string? CountersJson, string? FreeDiskJson);
