using System.Text;
using Dapper;
using Hyveman.Application;
using Hyveman.Domain;

namespace Hyveman.Infrastructure.Sqlite;

/// <summary>Idempotent event store with an FTS5 rendered-message index
/// (DESIGN §5.1, API.md §7.2/§10). Inserts use ON CONFLICT DO NOTHING on
/// (source_id, dedup_scope, record_id); FTS rows are written atomically with
/// newly accepted rows only.</summary>
public sealed class EventStore(SqliteDb db) : IEventStore
{
    public async Task<IngestResult> InsertBatchAsync(string sourceId, IReadOnlyList<ValidatedLogItem> items, CancellationToken ct)
    {
        var accepted = 0;
        var deduped = 0;
        var acceptedItems = new List<ValidatedLogItem>();
        using var conn = StoreHelpers.Open(db);
        using var tx = conn.BeginTransaction();
        foreach (var item in items)
        {
            var affected = await conn.ExecuteAsync(new CommandDefinition("""
                INSERT INTO events(source_id, dedup_scope, record_id, time, severity, facility,
                                   message, fields_json, raw_json, channel, event_id, task, opcode, keywords, created_at)
                VALUES (@SourceId, @DedupScope, @RecordId, @Time, @Severity, @Facility,
                        @Message, @FieldsJson, @RawJson, @Channel, @EventId, @Task, @Opcode, @Keywords, @CreatedAt)
                ON CONFLICT(source_id, dedup_scope, record_id) DO NOTHING
                """, new
            {
                SourceId = sourceId,
                item.DedupScope,
                item.RecordId,
                Time = StoreHelpers.Fmt(item.Time),
                Severity = item.Severity,
                item.Facility,
                item.Message,
                item.FieldsJson,
                item.RawJson,
                item.Channel,
                item.EventId,
                item.Task,
                item.Opcode,
                item.Keywords,
                CreatedAt = StoreHelpers.Fmt(DateTimeOffset.UtcNow),
            }, tx, cancellationToken: ct));
            if (affected == 1)
            {
                accepted++;
                acceptedItems.Add(item);
                if (item.Message is { Length: > 0 })
                {
                    var rowid = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
                        "SELECT last_insert_rowid()", transaction: tx, cancellationToken: ct));
                    await conn.ExecuteAsync(new CommandDefinition(
                        "INSERT INTO events_fts(rowid, message) VALUES (@RowId, @Message)",
                        new { RowId = rowid, Message = item.Message }, tx, cancellationToken: ct));
                }
            }
            else
            {
                deduped++;
            }
        }
        tx.Commit();
        return new IngestResult(accepted, deduped, [], acceptedItems);
    }

    public async Task<EventSearchPage> SearchAsync(EventQuery q, CancellationToken ct)
    {
        var sql = new StringBuilder("""
            SELECT e.id, e.source_id, e.dedup_scope, e.record_id, e.time, e.severity, e.facility,
                   e.message, e.fields_json, e.raw_json, e.channel, e.event_id, e.task, e.opcode, e.keywords,
                   s.name AS source_name, h.id AS host_id, h.name AS host_name
            FROM events e
            LEFT JOIN sources s ON s.id = e.source_id
            LEFT JOIN hosts h ON h.source_id = e.source_id
            WHERE 1=1
            """);
        var p = new Dictionary<string, object?>();
        if (q.From is { } from) { sql.Append(" AND e.time >= @From"); p["From"] = StoreHelpers.Fmt(from); }
        if (q.To is { } to) { sql.Append(" AND e.time < @To"); p["To"] = StoreHelpers.Fmt(to); }
        if (q.SourceId is { } sid) { sql.Append(" AND e.source_id = @SourceId"); p["SourceId"] = sid; }
        if (q.HostId is { } hid)
        {
            sql.Append(" AND EXISTS (SELECT 1 FROM hosts hh WHERE hh.id = @HostId AND hh.source_id = e.source_id)");
            p["HostId"] = hid;
        }
        if (q.Channel is { } ch) { sql.Append(" AND e.channel = @Channel"); p["Channel"] = ch; }
        if (q.SeverityMin is { } sm) { sql.Append(" AND e.severity >= @SeverityMin"); p["SeverityMin"] = sm; }
        if (q.EventId is { } eid) { sql.Append(" AND e.event_id = @EventId"); p["EventId"] = eid; }
        if (!string.IsNullOrWhiteSpace(q.Q))
        {
            // FTS5 MATCH with a quoted, escaped user string (no injection, no syntax errors).
            var escaped = q.Q.Trim().Replace("\"", "\"\"");
            sql.Append(" AND e.id IN (SELECT rowid FROM events_fts WHERE events_fts MATCH @Q)");
            p["Q"] = $"\"{escaped}\"";
        }

        var desc = q.Sort != "asc";
        if (CursorCodec.TryDecode(q.Cursor ?? "", out var ctime, out var cid))
        {
            sql.Append(desc ? " AND (e.time, e.id) < (@CTime, @CId)" : " AND (e.time, e.id) > (@CTime, @CId)");
            p["CTime"] = StoreHelpers.Fmt(ctime);
            p["CId"] = cid;
        }

        sql.Append(desc ? " ORDER BY e.time DESC, e.id DESC" : " ORDER BY e.time ASC, e.id ASC");
        sql.Append(" LIMIT @Limit");
        p["Limit"] = q.Limit;

        using var conn = StoreHelpers.Open(db);
        var rows = await conn.QueryAsync(new CommandDefinition(sql.ToString(), p, cancellationToken: ct));
        var items = rows.Select(r => new EventDetail(
            Id: StoreHelpers.ToLong(r.id),
            SourceId: (string)r.source_id,
            SourceName: (string?)r.source_name,
            HostId: (string?)r.host_id,
            HostName: (string?)r.host_name,
            DedupScope: (string)r.dedup_scope,
            RecordId: (string)r.record_id,
            Time: StoreHelpers.Parse((string)r.time),
            Severity: r.severity is null ? null : (int?)StoreHelpers.ToLong(r.severity),
            Facility: (string?)r.facility,
            Message: (string?)r.message,
            FieldsJson: (string?)r.fields_json,
            RawJson: (string?)r.raw_json,
            Channel: (string?)r.channel,
            EventId: r.event_id is null ? null : (long?)StoreHelpers.ToLong(r.event_id),
            Task: r.task is null ? null : (long?)StoreHelpers.ToLong(r.task),
            Opcode: r.opcode is null ? null : (long?)StoreHelpers.ToLong(r.opcode),
            Keywords: (string?)r.keywords)).ToList();
        var hasMore = items.Count >= q.Limit;
        return new EventSearchPage(items, null, hasMore);
    }

    public async Task<EventDetail?> GetAsync(long id, CancellationToken ct)
    {
        using var conn = StoreHelpers.Open(db);
        var r = await conn.QuerySingleOrDefaultAsync(new CommandDefinition("""
            SELECT e.id, e.source_id, e.dedup_scope, e.record_id, e.time, e.severity, e.facility,
                   e.message, e.fields_json, e.raw_json, e.channel, e.event_id, e.task, e.opcode, e.keywords,
                   s.name AS source_name, h.id AS host_id, h.name AS host_name
            FROM events e
            LEFT JOIN sources s ON s.id = e.source_id
            LEFT JOIN hosts h ON h.source_id = e.source_id
            WHERE e.id = @id
            """, new { id }, cancellationToken: ct));
        if (r is null) return null;
        return new EventDetail(
            Id: StoreHelpers.ToLong(r.id), SourceId: (string)r.source_id, SourceName: (string?)r.source_name,
            HostId: (string?)r.host_id, HostName: (string?)r.host_name, DedupScope: (string)r.dedup_scope,
            RecordId: (string)r.record_id, Time: StoreHelpers.Parse((string)r.time),
            Severity: r.severity is null ? null : (int?)StoreHelpers.ToLong(r.severity),
            Facility: (string?)r.facility, Message: (string?)r.message, FieldsJson: (string?)r.fields_json,
            RawJson: (string?)r.raw_json, Channel: (string?)r.channel,
            EventId: r.event_id is null ? null : (long?)StoreHelpers.ToLong(r.event_id),
            Task: r.task is null ? null : (long?)StoreHelpers.ToLong(r.task),
            Opcode: r.opcode is null ? null : (long?)StoreHelpers.ToLong(r.opcode),
            Keywords: (string?)r.keywords);
    }

    public async Task<long> PurgeOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct)
    {
        using var conn = StoreHelpers.Open(db);
        using var tx = conn.BeginTransaction();
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM events_fts WHERE rowid IN (SELECT id FROM events WHERE time < @cutoff)",
            new { cutoff = StoreHelpers.Fmt(cutoff) }, tx, cancellationToken: ct));
        var deleted = await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM events WHERE time < @cutoff", new { cutoff = StoreHelpers.Fmt(cutoff) }, tx, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition(
            "INSERT INTO events_fts(events_fts) VALUES ('optimize')", transaction: tx, cancellationToken: ct));
        tx.Commit();
        return deleted;
    }

    public async Task<long> CountAsync(CancellationToken ct)
    {
        using var conn = StoreHelpers.Open(db);
        return await conn.ExecuteScalarAsync<long>(new CommandDefinition("SELECT COUNT(*) FROM events", cancellationToken: ct));
    }

    public async Task<DateTimeOffset> NewestTimeAsync(CancellationToken ct)
    {
        using var conn = StoreHelpers.Open(db);
        var v = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT MAX(time) FROM events", cancellationToken: ct));
        return v is null ? DateTimeOffset.MinValue : StoreHelpers.Parse(v);
    }
}
