using Dapper;
using Hyveman.Application;
using Hyveman.Domain;

namespace Hyveman.Infrastructure.Sqlite;

/// <summary>Vendor-neutral health store: components, snapshots, metrics, VMs.</summary>
public sealed class HealthStore(SqliteDb db) : IHealthStore
{
    public async Task ReplaceComponentsAsync(string hostId, IReadOnlyList<ComponentRecord> components, CancellationToken ct)
    {
        using var conn = StoreHelpers.Open(db);
        using var tx = conn.BeginTransaction();
        foreach (var c in components)
        {
            await conn.ExecuteAsync(new CommandDefinition("""
                INSERT INTO components(host_id, type, name, state, detail, last_seen)
                VALUES (@HostId, @Type, @Name, @State, @Detail, @LastSeen)
                ON CONFLICT(host_id, type, name) DO UPDATE SET
                    state = @State, detail = @Detail, last_seen = @LastSeen
                """, new
            {
                HostId = hostId, c.Type, c.Name, State = HealthStates.ToWire(c.State),
                c.Detail, LastSeen = StoreHelpers.Fmt(c.LastSeen),
            }, tx, cancellationToken: ct));
        }
        if (components.Count > 0)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                "DELETE FROM components WHERE host_id = @hostId AND last_seen < @seen",
                new { hostId, seen = StoreHelpers.Fmt(components.Max(c => c.LastSeen)) }, tx, cancellationToken: ct));
        }
        tx.Commit();
    }

    public async Task<IReadOnlyList<ComponentRecord>> GetComponentsAsync(string hostId, CancellationToken ct)
    {
        using var conn = StoreHelpers.Open(db);
        var rows = await conn.QueryAsync(new CommandDefinition(
            "SELECT host_id, type, name, state, detail, last_seen FROM components WHERE host_id = @hostId ORDER BY type, name",
            new { hostId }, cancellationToken: ct));
        return rows.Select(r => new ComponentRecord((string)r.host_id, (string)r.type, (string)r.name,
            HealthStates.FromWire((string)r.state), (string?)r.detail, StoreHelpers.Parse((string)r.last_seen))).ToList();
    }

    public async Task AddSnapshotAsync(string hostId, DateTimeOffset time, string rollupState, string componentsJson, CancellationToken ct)
    {
        using var conn = StoreHelpers.Open(db);
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO health_snapshots(host_id, time, rollup_state, components_json)
            VALUES (@HostId, @Time, @Rollup, @ComponentsJson)
            """, new { HostId = hostId, Time = StoreHelpers.Fmt(time), Rollup = rollupState, ComponentsJson = componentsJson },
            cancellationToken: ct));
    }

    public async Task<IReadOnlyList<HealthSnapshotRecord>> GetSnapshotsAsync(string hostId,
        DateTimeOffset? from, DateTimeOffset? to, int limit, CancellationToken ct)
    {
        using var conn = StoreHelpers.Open(db);
        var rows = await conn.QueryAsync(new CommandDefinition("""
            SELECT id, host_id, time, rollup_state, components_json FROM health_snapshots
            WHERE host_id = @hostId AND time >= @from AND time <= @to
            ORDER BY time DESC LIMIT @limit
            """, new
        {
            hostId, from = StoreHelpers.Fmt(from ?? DateTimeOffset.MinValue),
            to = StoreHelpers.Fmt(to ?? DateTimeOffset.MaxValue), limit,
        }, cancellationToken: ct));
        return rows.Select(r => new HealthSnapshotRecord(StoreHelpers.ToLong(r.id), (string)r.host_id,
            StoreHelpers.Parse((string)r.time), (string)r.rollup_state, (string?)r.components_json)).ToList();
    }

    public async Task AddMetricsAsync(string hostId, DateTimeOffset time, IReadOnlyList<MetricRecord> metrics, CancellationToken ct)
    {
        if (metrics.Count == 0) return;
        using var conn = StoreHelpers.Open(db);
        using var tx = conn.BeginTransaction();
        foreach (var m in metrics)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                "INSERT INTO metrics(host_id, time, name, value, unit) VALUES (@HostId, @Time, @Name, @Value, @Unit)",
                new { HostId = hostId, Time = StoreHelpers.Fmt(time), m.Name, m.Value, m.Unit }, tx, cancellationToken: ct));
        }
        tx.Commit();
    }

    public async Task<IReadOnlyList<MetricRecord>> GetLatestMetricsAsync(string hostId, int maxPerName, CancellationToken ct)
    {
        using var conn = StoreHelpers.Open(db);
        var rows = await conn.QueryAsync(new CommandDefinition("""
            SELECT m.host_id, m.time, m.name, m.value, m.unit FROM metrics m
            WHERE m.id IN (SELECT MAX(id) FROM metrics WHERE host_id = @hostId GROUP BY name)
            ORDER BY m.name LIMIT @max
            """, new { hostId, max = Math.Max(1, maxPerName * 100) }, cancellationToken: ct));
        return rows.Select(r => new MetricRecord((string)r.host_id, (string)r.name,
            StoreHelpers.ToDouble(r.value), (string?)r.unit, StoreHelpers.Parse((string)r.time))).ToList();
    }

    public async Task<IReadOnlyList<MetricRecord>> GetMetricsInRangeAsync(string hostId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        using var conn = StoreHelpers.Open(db);
        var rows = await conn.QueryAsync(new CommandDefinition("""
            SELECT host_id, time, name, value, unit FROM metrics
            WHERE host_id = @hostId AND time >= @from AND time <= @to
            ORDER BY time
            """, new { hostId, from = StoreHelpers.Fmt(from), to = StoreHelpers.Fmt(to) }, cancellationToken: ct));
        return rows.Select(r => new MetricRecord((string)r.host_id, (string)r.name,
            StoreHelpers.ToDouble(r.value), (string?)r.unit, StoreHelpers.Parse((string)r.time))).ToList();
    }

    public async Task UpsertVmsAsync(string hostId, IReadOnlyList<VmRecord> vms, bool stale, DateTimeOffset collectedAt, CancellationToken ct)
    {
        using var conn = StoreHelpers.Open(db);
        using var tx = conn.BeginTransaction();
        await conn.ExecuteAsync(new CommandDefinition("DELETE FROM vms WHERE host_id = @hostId", new { hostId }, tx, cancellationToken: ct));
        foreach (var v in vms)
        {
            await conn.ExecuteAsync(new CommandDefinition("""
                INSERT INTO vms(host_id, name, state, heartbeat_ok, cpu_pct, mem_mb, last_seen,
                                replication_state, replication_health, replication_last_apply_time,
                                stale, collected_at, updated_at)
                VALUES (@HostId, @Name, @State, @HeartbeatOk, @CpuPct, @MemMb, @LastSeen,
                        @ReplicationState, @ReplicationHealth, @ReplicationLastApplyTime,
                        @Stale, @CollectedAt, @UpdatedAt)
                """, new
            {
                HostId = hostId, v.Name, v.State, v.HeartbeatOk,
                CpuPct = v.CpuPct, MemMb = v.MemMb,
                LastSeen = v.LastSeen is { } ls ? StoreHelpers.Fmt(ls) : null,
                ReplicationState = v.ReplicationState,
                ReplicationHealth = v.ReplicationHealth,
                ReplicationLastApplyTime = v.ReplicationLastApplyTime is { } rl ? StoreHelpers.Fmt(rl) : null,
                Stale = v.Stale ? 1 : 0, CollectedAt = StoreHelpers.Fmt(collectedAt),
                UpdatedAt = StoreHelpers.Fmt(DateTimeOffset.UtcNow),
            }, tx, cancellationToken: ct));
        }
        tx.Commit();
    }

    public async Task<IReadOnlyList<VmRecord>> GetVmsAsync(string hostId, CancellationToken ct)
    {
        using var conn = StoreHelpers.Open(db);
        var rows = await conn.QueryAsync(new CommandDefinition(
            "SELECT * FROM vms WHERE host_id = @hostId ORDER BY name", new { hostId }, cancellationToken: ct));
        return rows.Select(r => new VmRecord(
            (string)r.host_id, (string)r.name, (string)r.state,
            r.heartbeat_ok is null ? null : (long)r.heartbeat_ok == 1,
            r.cpu_pct is null ? null : (double?)StoreHelpers.ToDouble(r.cpu_pct),
            r.mem_mb is null ? null : (long?)StoreHelpers.ToLong(r.mem_mb),
            StoreHelpers.ParseOpt((string?)r.last_seen),
            (long)r.stale == 1, StoreHelpers.Parse((string)r.collected_at),
            (string?)r.replication_state, (string?)r.replication_health,
            StoreHelpers.ParseOpt((string?)r.replication_last_apply_time))).ToList();
    }

    public async Task<long> PurgeMetricsOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct)
    {
        using var conn = StoreHelpers.Open(db);
        return await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM metrics WHERE time < @cutoff", new { cutoff = StoreHelpers.Fmt(cutoff) }, cancellationToken: ct));
    }

    public async Task<long> PurgeSnapshotsOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct)
    {
        using var conn = StoreHelpers.Open(db);
        return await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM health_snapshots WHERE time < @cutoff", new { cutoff = StoreHelpers.Fmt(cutoff) }, cancellationToken: ct));
    }

    public async Task<long> PurgeVmsAsync(DateTimeOffset cutoff, CancellationToken ct)
    {
        using var conn = StoreHelpers.Open(db);
        return await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM vms WHERE updated_at < @cutoff", new { cutoff = StoreHelpers.Fmt(cutoff) }, cancellationToken: ct));
    }
}
