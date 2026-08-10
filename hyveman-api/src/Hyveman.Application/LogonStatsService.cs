using System.Globalization;
using System.Text.Json;
using Hyveman.Contracts;
using Hyveman.Domain;

namespace Hyveman.Application;

/// <summary>
/// Security-logon aggregation (DESIGN §4.1/§13 #5, Phase 2): accepted Security
/// events are aggregated into per-user/per-day counts in `logon_stats`. The
/// curation mirrors the agent's policy — 4624 success restricted to LogonType
/// 2 (interactive/console) and 10 (RDP), 4625 failed logon (all types), 4740
/// account lockout — so the server does not blindly trust the agent's filter.
/// Only newly accepted items count; deduped replays must not double-count.
/// </summary>
public sealed class LogonStatsService(ILogonStatsStore store)
{
    public const int MaxPageSize = 200;
    public const int DefaultPageSize = 50;

    /// <summary>Aggregates a batch of accepted items; a no-op when nothing
    /// matches. Failures are the caller's concern — stats are derived data
    /// and must never reject an already-committed event batch.</summary>
    public async Task RecordAcceptedAsync(string sourceId, IReadOnlyList<ValidatedLogItem> acceptedItems, CancellationToken ct)
    {
        var entries = ExtractEntries(acceptedItems);
        if (entries.Count == 0) return;
        await store.IncrementAsync(sourceId, entries, ct);
    }

    /// <summary>Maps accepted Security logon events to aggregated deltas,
    /// merging repeats within the batch. Pure function — unit-testable.</summary>
    public static IReadOnlyList<LogonStatEntry> ExtractEntries(IReadOnlyList<ValidatedLogItem> items)
    {
        var merged = new Dictionary<(string Day, string User, int? Type), (long S, long F)>();
        foreach (var item in items)
        {
            var info = TryClassify(item);
            if (info is null) continue;

            var success = info.Outcome == LogonOutcomes.Success ? 1L : 0L;
            var failure = info.Outcome == LogonOutcomes.Failure || info.Outcome == LogonOutcomes.Lockout ? 1L : 0L;

            var day = item.Time.ToUniversalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var key = (day, info.User, info.LogonType);
            merged[key] = merged.TryGetValue(key, out var cur)
                ? (cur.S + success, cur.F + failure)
                : (success, failure);
        }
        return merged
            .Select(kv => new LogonStatEntry(kv.Key.Day, kv.Key.User, kv.Key.Type, kv.Value.S, kv.Value.F))
            .ToList();
    }

    /// <summary>Classifies one item as a security-logon event, or null when it
    /// is not one. Single curation site for the logon_stats aggregate and the
    /// logon alert rules (DESIGN §4.4 type 6): the server does not blindly
    /// trust the agent's filter — 4624 counts only when LogonType is 2
    /// (interactive) or 10 (RDP), 4625 fails for any type, 4740 is a lockout
    /// and carries no logon type, and TargetUserName must be present.</summary>
    public static LogonEventInfo? TryClassify(ValidatedLogItem item)
    {
        // The curated channel is "Security"; everything else is not logon
        // tracking (channel names are case-insensitive on Windows).
        if (!string.Equals(item.Channel, "Security", StringComparison.OrdinalIgnoreCase)) return null;

        string? outcome = null;
        int? logonType = null;
        switch (item.EventId)
        {
            case 4624:
                // Curated policy (DESIGN §4.1): only interactive/RDP logons.
                var lt = ReadLogonType(item.FieldsJson);
                if (lt is not (2 or 10)) return null;
                logonType = lt;
                outcome = LogonOutcomes.Success;
                break;
            case 4625:
                logonType = ReadLogonType(item.FieldsJson);
                outcome = LogonOutcomes.Failure;
                break;
            case 4740:
                outcome = LogonOutcomes.Lockout; // lockout carries no logon type (NULL column)
                break;
            default:
                return null;
        }

        var user = ReadTargetUser(item.FieldsJson);
        if (string.IsNullOrEmpty(user)) return null;

        return new LogonEventInfo(user, outcome, logonType);
    }

    public async Task<LogonStatsResponse> QueryAsync(DateTimeOffset? from, DateTimeOffset? to,
        string? sourceId, string? user, int? limit, CancellationToken ct)
    {
        var pageSize = Math.Clamp(limit ?? DefaultPageSize, 1, MaxPageSize);
        var rows = await store.QueryAsync(new LogonStatsQuery(from, to, sourceId, user, pageSize + 1), ct);
        var items = rows.Take(pageSize).Select(r => new LogonStatDto
        {
            Day = r.Day,
            SourceId = r.SourceId,
            SourceName = r.SourceName,
            User = r.User,
            LogonType = r.LogonType,
            SuccessCount = r.SuccessCount,
            FailureCount = r.FailureCount,
        }).ToList();
        return new LogonStatsResponse { Items = items, HasMore = rows.Count > pageSize };
    }

    private static string? ReadTargetUser(string fieldsJson)
    {
        if (!TryGetEventData(fieldsJson, out var ed)) return null;
        return ed.TryGetProperty("TargetUserName", out var u) && u.ValueKind == JsonValueKind.String
            ? u.GetString() : null;
    }

    /// <summary>LogonType arrives as a JSON string from the agent
    /// (Dictionary&lt;string,string?&gt; event_data) and as a number in the
    /// PROTOCOL example; accept both.</summary>
    private static int? ReadLogonType(string fieldsJson)
    {
        if (!TryGetEventData(fieldsJson, out var ed)) return null;
        if (!ed.TryGetProperty("LogonType", out var lt)) return null;
        return lt.ValueKind switch
        {
            JsonValueKind.String => int.TryParse(lt.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : null,
            JsonValueKind.Number => lt.TryGetInt32(out var n) ? n : null,
            _ => null,
        };
    }

    private static bool TryGetEventData(string fieldsJson, out JsonElement eventData)
    {
        eventData = default;
        if (string.IsNullOrEmpty(fieldsJson)) return false;
        try
        {
            using var doc = JsonDocument.Parse(fieldsJson);
            // Clone: the element must outlive the document (callers read it
            // after this method returns).
            if (!doc.RootElement.TryGetProperty("event_data", out var ed)
                || ed.ValueKind != JsonValueKind.Object)
                return false;
            eventData = ed.Clone();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
