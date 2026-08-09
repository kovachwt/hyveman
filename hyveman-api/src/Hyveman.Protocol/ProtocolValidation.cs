using System.Globalization;
using System.Text.Json;
using Hyveman.Domain;

namespace Hyveman.Protocol;

/// <summary>Per-item rejection reasons (PROTOCOL.md §6.4).</summary>
public static class RejectionReasons
{
    public const string RawOversize = "raw_oversize";
    public const string MessageOversize = "message_oversize";
    public const string FieldOversize = "field_oversize";
    public const string BadTime = "bad_time";
    public const string BadRecordId = "bad_record_id";
    public const string BadDedupScope = "bad_dedup_scope";
    public const string Schema = "schema";
}

/// <summary>Endpoint-level protocol validation. The JSON Schema check
/// (forward-compatible, PROTOCOL §6.7) runs first; these validators implement
/// the semantics the schema cannot: token/source-kind-derived severity ranges,
/// byte caps, and the heartbeat/facts ordering inputs.</summary>
public static class ProtocolValidation
{
    // PROTOCOL §12
    public const int MaxItemsPerBatch = 1000;
    public const int MaxBodyBytes = 4 * 1024 * 1024;
    public const int MaxRawBytes = 16 * 1024;
    public const int MaxMessageBytes = 64 * 1024;
    public const int MaxFieldStringBytes = 64 * 1024;
    public const int MaxRecordIdLength = 128;

    /// <summary>UTC ISO-8601 with a trailing Z (schema definition utcTime).</summary>
    public static readonly System.Text.RegularExpressions.Regex UtcTimePattern =
        new(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d{1,9})?Z$",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    public static bool TryParseUtcTime(string? s, out DateTimeOffset utc)
    {
        utc = default;
        if (string.IsNullOrEmpty(s) || !UtcTimePattern.IsMatch(s)) return false;
        // Format: yyyy-MM-ddTHH:mm:ss(.fffffffff)?Z — force UTC.
        // The regex already pinned the shape (Z, 1-9 fractional digits);
        // TryParse handles arbitrary precision and always yields UTC here.
        return DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out utc);
    }

    /// <summary>Validates one log item against the schema + semantic rules and
    /// returns the mapped row, or a per-item rejection reason (PROTOCOL §6.4).</summary>
    public static (ValidatedLogItem? Item, ItemRejection? Rejection) ValidateLogItem(
        LogItemDto item, string sourceKind)
    {
        if (item.Kind != "log")
            return (null, new ItemRejection(item.RecordId ?? "", item.DedupScope ?? "", RejectionReasons.Schema));

        if (string.IsNullOrWhiteSpace(item.RecordId) || item.RecordId.Length > MaxRecordIdLength)
            return (null, new ItemRejection(item.RecordId ?? "", item.DedupScope ?? "", RejectionReasons.BadRecordId));
        if (item.DedupScope is null)
            return (null, new ItemRejection(item.RecordId, "", RejectionReasons.BadDedupScope));
        if (!TryParseUtcTime(item.Time, out var time))
            return (null, new ItemRejection(item.RecordId, item.DedupScope, RejectionReasons.BadTime));

        // Severity is per source kind (PROTOCOL §10). Absent is allowed and
        // defaulted at ingest (Windows Level 0 is omitted, not sent).
        int? severity = null;
        if (item.Severity is { } sev)
        {
            var (min, max) = SourceKinds.SeverityRange(sourceKind);
            if (sev < min || sev > max)
                return (null, new ItemRejection(item.RecordId, item.DedupScope, RejectionReasons.Schema));
            severity = sev;
        }

        if (item.Message is { } msg && System.Text.Encoding.UTF8.GetByteCount(msg) > MaxMessageBytes)
            return (null, new ItemRejection(item.RecordId, item.DedupScope, RejectionReasons.MessageOversize));

        if (item.Raw is { } raw && System.Text.Encoding.UTF8.GetByteCount(raw) > MaxRawBytes)
            return (null, new ItemRejection(item.RecordId, item.DedupScope, RejectionReasons.RawOversize));

        // fields.* string values are capped at 64 KiB (PROTOCOL §12).
        string? channel = null;
        long? eventId = null, task = null, opcode = null;
        string? keywords = null;
        string fieldsJson = "{}";
        if (item.Fields is { } fields && fields.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in fields.EnumerateObject())
            {
                var key = prop.Name;
                var value = prop.Value;
                if (value.ValueKind == JsonValueKind.String &&
                    System.Text.Encoding.UTF8.GetByteCount(value.GetString() ?? "") > MaxFieldStringBytes)
                    return (null, new ItemRejection(item.RecordId, item.DedupScope, RejectionReasons.FieldOversize));
                if (value.ValueKind is JsonValueKind.Object or JsonValueKind.Array &&
                    value.GetRawText().Length > MaxFieldStringBytes)
                    return (null, new ItemRejection(item.RecordId, item.DedupScope, RejectionReasons.FieldOversize));
            }
            if (fields.TryGetProperty("channel", out var ch) && ch.ValueKind == JsonValueKind.String)
                channel = ch.GetString();
            if (fields.TryGetProperty("event_id", out var eid) && eid.ValueKind is JsonValueKind.Number)
                eventId = eid.GetInt64();
            if (fields.TryGetProperty("task", out var t) && t.ValueKind is JsonValueKind.Number)
                task = t.GetInt64();
            if (fields.TryGetProperty("opcode", out var op) && op.ValueKind is JsonValueKind.Number)
                opcode = op.GetInt64();
            if (fields.TryGetProperty("keywords", out var kw) && kw.ValueKind == JsonValueKind.String)
                keywords = kw.GetString();
            fieldsJson = fields.GetRawText();
        }
        else if (item.Fields is { } bad)
        {
            return (null, new ItemRejection(item.RecordId, item.DedupScope, RejectionReasons.Schema));
        }

        var row = new ValidatedLogItem(
            DedupScope: item.DedupScope,
            RecordId: item.RecordId,
            Time: time,
            Severity: severity,
            Facility: item.Facility,
            Message: item.Message,
            FieldsJson: fieldsJson,
            RawJson: item.Raw,
            Channel: channel,
            EventId: eventId,
            Task: task,
            Opcode: opcode,
            Keywords: keywords);
        return (row, null);
    }

    /// <summary>Parses a telemetry item. Returns null and a whole-batch
    /// rejection when the item is malformed (telemetry has no per-item results,
    /// PROTOCOL §7.3).</summary>
    public static object? ParseTelemetryItem(JsonElement item, out string? error)
    {
        error = null;
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("kind", out var kindProp)
            || kindProp.ValueKind != JsonValueKind.String)
        {
            error = "telemetry item missing string kind";
            return null;
        }
        return kindProp.GetString() switch
        {
            "heartbeat" => ParseHeartbeat(item, out error),
            "facts" => ParseFacts(item, out error),
            _ => Fail<object?>($"unknown telemetry item kind '{kindProp.GetString()}'", out error),
        };
    }

    private static T? Fail<T>(string msg, out string error)
    {
        error = msg;
        return default;
    }

    private static HeartbeatPayload? ParseHeartbeat(JsonElement item, out string error)
    {
        if (!item.TryGetProperty("sent_at", out var sentProp) || sentProp.ValueKind != JsonValueKind.String
            || !TryParseUtcTime(sentProp.GetString(), out var sentAt))
            return Fail<HeartbeatPayload>("heartbeat missing or invalid sent_at", out error);

        DateTimeOffset? bootTime = null;
        if (item.TryGetProperty("boot_time", out var bootProp) && bootProp.ValueKind == JsonValueKind.String)
        {
            if (!TryParseUtcTime(bootProp.GetString(), out var bt)) return Fail<HeartbeatPayload>("invalid boot_time", out error);
            bootTime = bt;
        }

        var degraded = item.TryGetProperty("degraded", out var deg) && deg.ValueKind == JsonValueKind.String
            ? deg.GetString() : null;
        if (degraded is not null && !DegradedStates.Known.Contains(degraded))
            return Fail<HeartbeatPayload>($"invalid degraded state '{degraded}'", out error);

        error = null;
        return new HeartbeatPayload(
            SentAt: sentAt,
            AgentVersion: ReadString(item, "agent_version"),
            ProtocolVersion: ReadInt(item, "protocol_version"),
            OsBuild: ReadString(item, "os_build"),
            BootTime: bootTime,
            UptimeS: ReadLong(item, "uptime_s"),
            Degraded: degraded,
            ConfigHash: ReadString(item, "config_hash"),
            CountersJson: ReadRaw(item, "counters"),
            FreeDiskJson: ReadRaw(item, "free_disk"));
    }

    private static FactsPayload? ParseFacts(JsonElement item, out string error)
    {
        if (!item.TryGetProperty("collected_at", out var caProp) || caProp.ValueKind != JsonValueKind.String
            || !TryParseUtcTime(caProp.GetString(), out var collectedAt))
            return Fail<FactsPayload>("facts missing or invalid collected_at", out error);

        var stale = item.TryGetProperty("stale", out var staleProp) && staleProp.ValueKind == JsonValueKind.True
            && staleProp.GetBoolean();

        var vms = new List<VmFact>();
        if (item.TryGetProperty("vms", out var vmsProp))
        {
            if (vmsProp.ValueKind != JsonValueKind.Array) return Fail<FactsPayload>("facts vms must be an array", out error);
            foreach (var vm in vmsProp.EnumerateArray())
            {
                if (vm.ValueKind != JsonValueKind.Object || !vm.TryGetProperty("name", out var nameProp)
                    || nameProp.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(nameProp.GetString()))
                    return Fail<FactsPayload>("facts vm missing string name", out error);
                var state = vm.TryGetProperty("state", out var stateProp) && stateProp.ValueKind == JsonValueKind.String
                    ? stateProp.GetString() : null;
                if (state is null || !VmStates.Known.Contains(state))
                    return Fail<FactsPayload>($"facts vm '{nameProp.GetString()}' has invalid state", out error);
                bool? hb = null;
                if (vm.TryGetProperty("heartbeat_ok", out var hbProp) && hbProp.ValueKind == JsonValueKind.True)
                    hb = hbProp.GetBoolean();
                double? cpu = null;
                if (vm.TryGetProperty("cpu_pct", out var cpuProp) && cpuProp.ValueKind is JsonValueKind.Number)
                    cpu = cpuProp.GetDouble();
                long? mem = null;
                if (vm.TryGetProperty("mem_mb", out var memProp) && memProp.ValueKind is JsonValueKind.Number)
                    mem = memProp.GetInt64();
                DateTimeOffset? lastSeen = null;
                if (vm.TryGetProperty("last_seen", out var lsProp) && lsProp.ValueKind == JsonValueKind.String)
                {
                    if (!TryParseUtcTime(lsProp.GetString(), out var ls)) return Fail<FactsPayload>("facts vm invalid last_seen", out error);
                    lastSeen = ls;
                }
                vms.Add(new VmFact(nameProp.GetString()!, state, hb, cpu, mem, lastSeen));
            }
        }

        error = null;
        return new FactsPayload(collectedAt, stale, vms);
    }

    private static string? ReadString(JsonElement item, string name) =>
        item.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static int? ReadInt(JsonElement item, string name) =>
        item.TryGetProperty(name, out var p) && p.ValueKind is JsonValueKind.Number ? p.GetInt32() : null;

    private static long? ReadLong(JsonElement item, string name) =>
        item.TryGetProperty(name, out var p) && p.ValueKind is JsonValueKind.Number ? p.GetInt64() : null;

    private static string? ReadRaw(JsonElement item, string name) =>
        item.TryGetProperty(name, out var p) ? p.GetRawText() : null;
}
