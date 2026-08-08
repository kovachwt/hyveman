using Dapper;
using Microsoft.Data.Sqlite;

namespace Hyveman.Server.Storage.Repos;

public sealed record EventRow(long Id, string SourceId, string DedupScope, string RecordId, string Time,
    int? Severity, string? Facility, string Message, string? FieldsJson, string? RawJson,
    string? Channel, long? EventId, long? Task, long? Opcode, string? Keywords, string IngestedAt);

public sealed class EventRepository
{
    private readonly SqliteFactory _factory;

    public EventRepository(SqliteFactory factory) => _factory = factory;

    /// <summary>Insert a batch of events with idempotent semantics. Returns (accepted, deduped).</summary>
    public static async Task<(int accepted, int deduped)> InsertBatchAsync(SqliteConnection conn, IReadOnlyList<EventInsert> items)
    {
        int accepted = 0, deduped = 0;
        foreach (var e in items)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO events
                  (source_id, dedup_scope, record_id, time, severity, facility, message,
                   fields_json, raw_json, channel, event_id, task, opcode, keywords)
                VALUES
                  (@source_id,@dedup_scope,@record_id,@time,@severity,@facility,@message,
                   @fields_json,@raw_json,@channel,@event_id,@task,@opcode,@keywords)
                ON CONFLICT(source_id, dedup_scope, record_id) DO NOTHING;
                SELECT changes();
                """;
            cmd.Parameters.AddWithValue("@source_id", e.SourceId);
            cmd.Parameters.AddWithValue("@dedup_scope", e.DedupScope);
            cmd.Parameters.AddWithValue("@record_id", e.RecordId);
            cmd.Parameters.AddWithValue("@time", e.Time);
            cmd.Parameters.AddWithValue("@severity", (object?)e.Severity ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@facility", (object?)e.Facility ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@message", e.Message);
            cmd.Parameters.AddWithValue("@fields_json", (object?)e.FieldsJson ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@raw_json", (object?)e.RawJson ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@channel", (object?)e.Channel ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@event_id", (object?)e.EventId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@task", (object?)e.Task ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@opcode", (object?)e.Opcode ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@keywords", (object?)e.Keywords ?? DBNull.Value);
            var changes = (long)(await cmd.ExecuteScalarAsync())!;
            if (changes > 0) accepted++; else deduped++;
        }
        return (accepted, deduped);
    }

    public async Task<List<EventRow>> SearchAsync(EventSearchQuery q)
    {
        var where = new List<string> { "e.source_id = @sourceId" };
        var parms = new Dapper.DynamicParameters();
        parms.Add("sourceId", q.SourceId);
        if (!string.IsNullOrEmpty(q.Channel)) { where.Add("e.channel = @channel"); parms.Add("channel", q.Channel); }
        if (q.EventId is not null) { where.Add("e.event_id = @eventId"); parms.Add("eventId", q.EventId); }
        if (q.SeverityMin is not null) { where.Add("e.severity >= @sevMin"); parms.Add("sevMin", q.SeverityMin); }
        if (q.Since is not null) { where.Add("e.time >= @since"); parms.Add("since", q.Since); }
        if (q.Until is not null) { where.Add("e.time <= @until"); parms.Add("until", q.Until); }
        if (!string.IsNullOrEmpty(q.Text))
        {
            // FTS5 external-content: join on rowid. Sanitize the query for FTS syntax safety.
            var fts = FtsQuery(q.Text);
            where.Add("events_fts MATCH @fts AND e.id = events_fts.rowid");
            parms.Add("fts", fts);
        }
        var sql = $"""
            SELECT e.id, e.source_id, e.dedup_scope, e.record_id, e.time, e.severity, e.facility,
                   e.message, e.fields_json, e.raw_json, e.channel, e.event_id, e.task, e.opcode,
                   e.keywords, e.ingested_at
            FROM events e
            {(!string.IsNullOrEmpty(q.Text) ? "JOIN events_fts ON events_fts.rowid = e.id" : "")}
            WHERE {string.Join(" AND ", where)}
            ORDER BY e.time DESC, e.id DESC
            LIMIT @limit
            """;
        parms.Add("limit", q.Limit > 0 ? q.Limit : 200);
        await using var conn = _factory.OpenReadOnly();
        var rows = await conn.QueryAsync<EventRow>(sql, parms);
        return rows.ToList();
    }

    public async Task<List<EventRow>> RecentAsync(int limit = 100)
    {
        await using var conn = _factory.OpenReadOnly();
        var rows = await conn.QueryAsync<EventRow>("""
            SELECT id, source_id, dedup_scope, record_id, time, severity, facility,
                   message, fields_json, raw_json, channel, event_id, task, opcode, keywords, ingested_at
            FROM events ORDER BY time DESC, id DESC LIMIT @limit
            """, new { limit });
        return rows.ToList();
    }

    public async Task<List<EventRow>> RecentForSourceAsync(string sourceId, int limit = 100)
    {
        await using var conn = _factory.OpenReadOnly();
        var rows = await conn.QueryAsync<EventRow>("""
            SELECT id, source_id, dedup_scope, record_id, time, severity, facility,
                   message, fields_json, raw_json, channel, event_id, task, opcode, keywords, ingested_at
            FROM events WHERE source_id=@sourceId ORDER BY time DESC, id DESC LIMIT @limit
            """, new { sourceId, limit });
        return rows.ToList();
    }

    public async Task<long> CountAsync()
    {
        await using var conn = _factory.OpenReadOnly();
        return await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM events");
    }

    /// <summary>Turn a user free-text query into a safe FTS5 MATCH expression (AND of quoted terms).</summary>
    internal static string FtsQuery(string text)
    {
        var tokens = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length > 0)
            .Select(t => t.Replace("\"", "\"\""));
        var terms = tokens.Select(t => $"\"{t}\"*");
        return string.Join(" AND ", terms);
    }
}

public sealed record EventInsert(
    string SourceId, string DedupScope, string RecordId, string Time, int? Severity,
    string? Facility, string Message, string? FieldsJson, string? RawJson,
    string? Channel, long? EventId, long? Task, long? Opcode, string? Keywords);

public sealed record EventSearchQuery(string SourceId, string? Text, string? Channel, long? EventId,
    int? SeverityMin, string? Since, string? Until, int Limit = 200);
