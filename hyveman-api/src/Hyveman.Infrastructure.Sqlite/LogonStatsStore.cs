using Dapper;
using Hyveman.Application;
using Hyveman.Domain;

namespace Hyveman.Infrastructure.Sqlite;

/// <summary>Per-user/per-day security-logon counts (DESIGN §5.1 `logon_stats`).
/// NULL `logon_type` rows are account lockouts (4740); SQLite treats NULLs as
/// distinct in unique indexes, so those merge via update-then-insert rather
/// than ON CONFLICT, which would otherwise create duplicate rows per batch.</summary>
public sealed class LogonStatsStore(SqliteDb db) : ILogonStatsStore
{
    public async Task IncrementAsync(string sourceId, IReadOnlyList<LogonStatEntry> entries, CancellationToken ct)
    {
        using var conn = StoreHelpers.Open(db);
        using var tx = conn.BeginTransaction();
        foreach (var e in entries)
        {
            if (e.LogonType is { } lt)
            {
                await conn.ExecuteAsync(new CommandDefinition("""
                    INSERT INTO logon_stats(day, source_id, user, logon_type, success_count, failure_count)
                    VALUES (@Day, @SourceId, @User, @LogonType, @SuccessDelta, @FailureDelta)
                    ON CONFLICT(day, source_id, user, logon_type) DO UPDATE SET
                        success_count = success_count + @SuccessDelta,
                        failure_count = failure_count + @FailureDelta
                    """, new { e.Day, SourceId = sourceId, e.User, LogonType = lt, e.SuccessDelta, e.FailureDelta },
                    tx, cancellationToken: ct));
            }
            else
            {
                var affected = await conn.ExecuteAsync(new CommandDefinition("""
                    UPDATE logon_stats SET
                        success_count = success_count + @SuccessDelta,
                        failure_count = failure_count + @FailureDelta
                    WHERE day = @Day AND source_id = @SourceId AND user = @User AND logon_type IS NULL
                    """, new { e.Day, SourceId = sourceId, e.User, e.SuccessDelta, e.FailureDelta },
                    tx, cancellationToken: ct));
                if (affected == 0)
                {
                    await conn.ExecuteAsync(new CommandDefinition("""
                        INSERT INTO logon_stats(day, source_id, user, logon_type, success_count, failure_count)
                        VALUES (@Day, @SourceId, @User, NULL, @SuccessDelta, @FailureDelta)
                        """, new { e.Day, SourceId = sourceId, e.User, e.SuccessDelta, e.FailureDelta },
                        tx, cancellationToken: ct));
                }
            }
        }
        tx.Commit();
    }

    public async Task<IReadOnlyList<LogonStatRow>> QueryAsync(LogonStatsQuery q, CancellationToken ct)
    {
        var sql = """
            SELECT l.day, l.source_id, l.user, l.logon_type, l.success_count, l.failure_count,
                   s.name AS source_name
            FROM logon_stats l
            JOIN sources s ON s.id = l.source_id
            WHERE 1=1
            """;
        var p = new Dictionary<string, object?>();
        if (q.From is { } from) { sql += " AND l.day >= @FromDay"; p["FromDay"] = from.ToUniversalTime().ToString(TimeFormat.Day); }
        if (q.To is { } to) { sql += " AND l.day <= @ToDay"; p["ToDay"] = to.ToUniversalTime().ToString(TimeFormat.Day); }
        if (q.SourceId is { } sid) { sql += " AND l.source_id = @SourceId"; p["SourceId"] = sid; }
        if (!string.IsNullOrEmpty(q.User)) { sql += " AND l.user = @User"; p["User"] = q.User; }
        sql += " ORDER BY l.day DESC, l.source_id, l.user, l.logon_type LIMIT @Limit";
        p["Limit"] = q.Limit;

        using var conn = StoreHelpers.Open(db);
        var rows = await conn.QueryAsync(new CommandDefinition(sql, p, cancellationToken: ct));
        return rows.Select(r => new LogonStatRow(
            (string)r.day,
            (string)r.source_id,
            (string?)r.source_name,
            (string)r.user,
            r.logon_type is null ? null : (int?)StoreHelpers.ToLong(r.logon_type),
            StoreHelpers.ToLong(r.success_count),
            StoreHelpers.ToLong(r.failure_count))).ToList();
    }
}
