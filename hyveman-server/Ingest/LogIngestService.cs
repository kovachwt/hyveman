using System.Text.Json;
using Hyveman.Server.Auth;
using Hyveman.Server.Common;
using Hyveman.Server.Config;
using Hyveman.Server.Storage;
using Hyveman.Server.Storage.Repos;

namespace Hyveman.Server.Ingest;

/// <summary>
/// POST /ingest/logs (§7.6, PROTOCOL §6) — the hot path. Whole-batch size checks, per-item
/// validation → partition into toStore/rejected, one transactional idempotent insert.
/// </summary>
public sealed class LogIngestService
{
    private readonly Db _db;
    private readonly ServerOptions _opts;
    private readonly Observability.OwnMetrics _metrics;
    private readonly Alerts.IEventSignal _eventSignal;
    private readonly ILogger<LogIngestService> _logger;

    public LogIngestService(Db db, ServerOptions opts, Observability.OwnMetrics metrics,
        Alerts.IEventSignal eventSignal, ILogger<LogIngestService> logger)
    {
        _db = db;
        _opts = opts;
        _metrics = metrics;
        _eventSignal = eventSignal;
        _logger = logger;
    }

    public enum BatchError { None, PayloadTooLarge, TooManyItems, WrongItemKind, BadJson }

    public sealed record BatchResult(BatchError Error, string? ErrorCode, string? ErrorMessage,
        int Accepted, int Deduped, List<RejectedItem> Rejected);

    /// <summary>Item-by-item: one bad item never rejects the batch (§6.3).</summary>
    public async Task<BatchResult> IngestAsync(string sourceId, LogsRequest req, int bodyBytes)
    {
        // Corroborating identity (§4.2): body source is a hint only; the token is authoritative.
        // Log a warning when it differs (possible misconfig) but proceed with the token's identity.
        if (!string.IsNullOrEmpty(req.Source) && req.Source != sourceId)
            _logger.LogWarning("body source {Claimed} differs from token source {Actual} (possible misconfig)",
                req.Source, sourceId);

        if (bodyBytes > _opts.Ingest.MaxBatchBytes)
            return new(BatchError.PayloadTooLarge, "payload_too_large",
                $"batch exceeds max_batch_bytes ({_opts.Ingest.MaxBatchBytes})", 0, 0, new());

        if (req.Items is null)
            return new(BatchError.BadJson, "invalid_request", "items is required", 0, 0, new());

        if (req.Items.Count > _opts.Ingest.MaxItems)
            return new(BatchError.TooManyItems, "too_many_items",
                $"batch has {req.Items.Count} items; max is {_opts.Ingest.MaxItems} (split the batch)", 0, 0, new());

        if (req.Items.Count > 0 && req.Items.Any(i => !string.Equals(i.Kind, "log", StringComparison.Ordinal)))
            return new(BatchError.WrongItemKind, "invalid_request",
                "wrong_item_kind: /ingest/logs accepts only kind:\"log\" items", 0, 0, new());

        var toStore = new List<EventInsert>(req.Items.Count);
        var rejected = new List<RejectedItem>();

        foreach (var item in req.Items)
        {
            var reason = Validate(item, _opts);
            if (reason is not null)
            {
                rejected.Add(new RejectedItem
                {
                    RecordId = item.RecordId ?? "",
                    DedupScope = item.DedupScope ?? "",
                    Reason = reason,
                    Permanent = true,
                });
                continue;
            }

            // Promote Windows fields from fields.* to indexed columns (PROTOCOL §6.2, §17).
            string? channel = null; long? eventId = null, task = null, opcode = null; string? keywords = null;
            string? fieldsJson = null;
            if (item.Fields is { } f && f.ValueKind == JsonValueKind.Object)
            {
                fieldsJson = f.GetRawText();
                if (TryGetString(f, "channel", out var ch)) channel = ch;
                if (TryGetInt64(f, "event_id", out var ev)) eventId = ev;
                if (TryGetInt64(f, "task", out var ta)) task = ta;
                if (TryGetInt64(f, "opcode", out var op)) opcode = op;
                if (TryGetString(f, "keywords", out var kw)) keywords = kw;
            }
            var (timeOk, time) = NormalizeTime(item.Time!);
            if (!timeOk)
            {
                rejected.Add(new RejectedItem { RecordId = item.RecordId!, DedupScope = item.DedupScope!, Reason = "bad_time", Permanent = true });
                continue;
            }

            // severity omitted (Windows Level 0) → Information (4) (PROTOCOL §10).
            var severity = item.Severity ?? 4;
            if (severity is < 0 or > 255) severity = 4;

            toStore.Add(new EventInsert(
                sourceId, item.DedupScope!, item.RecordId!, time, severity, item.Facility,
                item.Message!, fieldsJson, item.Raw is null ? null : JsonSerializer.Serialize(item.Raw),
                channel, eventId, task, opcode, keywords));
        }

        var (accepted, deduped) = (0, 0);
        if (toStore.Count > 0)
        {
            // One transaction for the batch (§7.6 step 4): a 5xx before commit → agent retries
            // the whole batch safely (idempotency collapses duplicates).
            (accepted, deduped) = await _db.Writer.WithTransactionAsync(conn =>
                EventRepository.InsertBatchAsync(conn, toStore));
        }

        _metrics.LogIngest(accepted, deduped, rejected.Count);

        // Phase 1 stub: event-rule evaluation hook (§7.6 step 5) — Phase 2 wires this signal.
        if (accepted > 0) _eventSignal.OnNewEvents(sourceId, accepted);

        return new(BatchError.None, null, null, accepted, deduped, rejected);
    }

    private static string? Validate(LogItem item, ServerOptions o)
    {
        if (string.IsNullOrEmpty(item.RecordId) || item.RecordId.Length > o.Ingest.MaxRecordIdLen) return "bad_record_id";
        if (item.DedupScope is null) return "bad_dedup_scope";   // must be present; "" if empty (§11)
        if (string.IsNullOrEmpty(item.Time) || !WireTime.TryParseUtc(item.Time, out _)) return "bad_time";
        if (string.IsNullOrEmpty(item.Message) || item.Message.Length > o.Ingest.MaxMessageBytes) return "message_oversize";
        if (item.Raw is not null && item.Raw.Length > o.Ingest.MaxRawBytes) return "raw_oversize";
        if (item.Fields is { ValueKind: JsonValueKind.Object } f)
        {
            foreach (var prop in f.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.String && prop.Value.GetString()!.Length > o.Ingest.MaxFieldBytes)
                    return "field_oversize";
            }
        }
        else if (item.Fields is { } nf && nf.ValueKind != JsonValueKind.Null && nf.ValueKind != JsonValueKind.Undefined)
        {
            return "schema";   // fields must be an object when present
        }
        if (item.Facility is { Length: > 512 }) return "schema";
        return null;
    }

    private static bool TryGetString(JsonElement obj, string name, out string value)
    {
        value = "";
        if (obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String)
        {
            value = el.GetString()!;
            return true;
        }
        return false;
    }

    private static bool TryGetInt64(JsonElement obj, string name, out long value)
    {
        value = 0;
        if (obj.TryGetProperty(name, out var el)
            && (el.ValueKind == JsonValueKind.Number || el.ValueKind == JsonValueKind.String)
            && el.TryGetInt64(out value))
            return true;
        return false;
    }

    private static (bool ok, string iso) NormalizeTime(string raw)
    {
        if (!WireTime.TryParseUtc(raw, out var dt)) return (false, "");
        return (true, WireTime.ToIsoMs(dt));
    }
}
