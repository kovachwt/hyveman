using System.Text.Json;
using Hyveman.Contracts;
using Hyveman.Domain;

namespace Hyveman.Application;

/// <summary>Event search (API.md §7.2): cursor-paginated FTS5-backed search
/// with structured filters. The cursor encodes the last (time, id) position;
/// page size capped at 200, default 50.</summary>
public sealed class EventsService(IEventStore store)
{
    public const int MaxPageSize = 200;
    public const int DefaultPageSize = 50;

    public async Task<EventSearchResponse> SearchAsync(EventSearchParams p, CancellationToken ct)
    {
        var limit = Math.Clamp(p.Limit ?? DefaultPageSize, 1, MaxPageSize);
        var page = await store.SearchAsync(new EventQuery(
            From: p.From, To: p.To, HostId: p.HostId, SourceId: p.SourceId, Channel: p.Channel,
            SeverityMin: p.SeverityMin, EventId: p.EventId, Q: p.Q, Limit: limit + 1,
            Cursor: p.Cursor, Sort: p.Sort ?? "desc"), ct);
        var items = page.Items.Take(limit).Select(EventMapper.ToDto).ToList();
        return new EventSearchResponse
        {
            Items = items,
            // DEFECTS.md D5: the cursor must encode the last *returned* row.
            // page.Items[^1] is the +1 probe row deliberately withheld from the
            // client; cursor-encoding it makes the next page start after a row
            // neither page delivers.
            NextCursor = page.HasMore ? CursorCodec.Encode(items[^1].Time, items[^1].Id) : null,
            HasMore = page.HasMore,
        };
    }

    public async Task<EventDto?> GetAsync(long id, CancellationToken ct)
    {
        var ev = await store.GetAsync(id, ct);
        return ev is null ? null : EventMapper.ToDto(ev);
    }
}

public sealed record EventSearchParams(
    DateTimeOffset? From, DateTimeOffset? To, string? HostId, string? SourceId,
    string? Channel, int? SeverityMin, long? EventId, string? Q, int? Limit,
    string? Cursor, string? Sort);

/// <summary>Opaque cursor: base64 of "time|id" of the last row (API.md §7.2:
/// the cursor encodes the last (time, id) position and is opaque to the client).</summary>
public static class CursorCodec
{
    public static string Encode(DateTimeOffset time, long id) =>
        Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{time.ToUniversalTime():O}|{id}"));

    public static bool TryDecode(string cursor, out DateTimeOffset time, out long id)
    {
        time = default;
        id = 0;
        try
        {
            var parts = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(cursor)).Split('|');
            if (parts.Length != 2 || !DateTimeOffset.TryParse(parts[0], null, System.Globalization.DateTimeStyles.RoundtripKind, out time)
                || !long.TryParse(parts[1], out id))
                return false;
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

public static class EventMapper
{
    public static EventDto ToDto(EventDetail e) => new()
    {
        Id = e.Id, SourceId = e.SourceId, SourceName = e.SourceName, HostId = e.HostId,
        HostName = e.HostName, DedupScope = e.DedupScope, RecordId = e.RecordId, Time = e.Time,
        Severity = e.Severity, Facility = e.Facility, Message = e.Message, Channel = e.Channel,
        EventId = e.EventId, Task = e.Task, Opcode = e.Opcode, Keywords = e.Keywords,
        FieldsJson = e.FieldsJson, RawJson = e.RawJson,
    };
}

/// <summary>Saved searches CRUD (API.md §7, FRONTEND §8.3).</summary>
public sealed class SavedSearchesService(ISavedSearchStore store, IClock clock, IAuditStore audit)
{
    public Task<IReadOnlyList<SavedSearchRecord>> ListAsync(CancellationToken ct) => store.ListAsync(ct);

    public async Task<SavedSearchRecord?> GetAsync(string id, CancellationToken ct) => await store.GetAsync(id, ct);

    public async Task<SavedSearchRecord> CreateAsync(string name, Dictionary<string, object?> filter, string actor, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ValidationProblemException(new Dictionary<string, List<string>>
        {
            ["name"] = ["Name is required."],
        });
        var now = clock.UtcNow;
        var rec = new SavedSearchRecord("ss_" + HostsService.RandomId(18), name.Trim(),
            JsonSerializer.Serialize(filter ?? []), now, now);
        await store.CreateAsync(rec, ct);
        await audit.RecordAsync(actor, "saved_search.created", "saved_search", rec.Id, null, now, ct);
        return rec;
    }

    public async Task<SavedSearchRecord> PatchAsync(string id, string? name, Dictionary<string, object?>? filter,
        DateTimeOffset? expectedUpdatedAt, string actor, CancellationToken ct)
    {
        var existing = await store.GetAsync(id, ct) ?? throw new NotFoundException($"saved search '{id}' not found");
        if (expectedUpdatedAt is { } expected && existing.UpdatedAt != expected)
            throw new ConflictException($"saved search '{id}' was modified concurrently; reload and retry");
        var now = clock.UtcNow;
        var updated = existing with
        {
            Name = name?.Trim() ?? existing.Name,
            FilterJson = filter is null ? existing.FilterJson : JsonSerializer.Serialize(filter),
            UpdatedAt = now,
        };
        var ok = await store.UpdateAsync(updated, existing.UpdatedAt, ct);
        if (!ok) throw new ConflictException($"saved search '{id}' was modified concurrently; reload and retry");
        await audit.RecordAsync(actor, "saved_search.updated", "saved_search", id, null, now, ct);
        return updated;
    }

    public async Task DeleteAsync(string id, string actor, CancellationToken ct)
    {
        if (await store.GetAsync(id, ct) is null) throw new NotFoundException($"saved search '{id}' not found");
        await store.DeleteAsync(id, ct);
        await audit.RecordAsync(actor, "saved_search.deleted", "saved_search", id, null, clock.UtcNow, ct);
    }
}
