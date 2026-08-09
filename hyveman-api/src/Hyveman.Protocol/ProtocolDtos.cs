using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hyveman.Protocol;

/// <summary>Protocol version constants (PROTOCOL.md §3).</summary>
public static class ProtocolVersion
{
    /// <summary>The single integer protocol version currently served.</summary>
    public const int Current = 1;

    /// <summary>Versions this server serves (contiguous range).</summary>
    public static readonly int[] Supported = [Current];

    public const string HeaderName = "X-Hyveman-Protocol";

    /// <summary>Format used for every protocol timestamp: UTC RFC 3339 with Z.</summary>
    public static string FormatUtc(DateTimeOffset dt) =>
        dt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
}

/// <summary>Shared JSON options for protocol bodies (snake_case, strict UTC).</summary>
public static class ProtocolJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.General)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };
}

// ─── Request DTOs (PROTOCOL §5.1, §6.1, §7.1) ────────────────────────────────

public sealed class RegisterRequest
{
    [JsonPropertyName("v")] public int V { get; set; }
    [JsonPropertyName("kind")] public string? Kind { get; set; }
    [JsonPropertyName("hostname")] public string? Hostname { get; set; }
    [JsonPropertyName("agent_version")] public string? AgentVersion { get; set; }
    [JsonPropertyName("os_build")] public string? OsBuild { get; set; }
    [JsonPropertyName("boot_id")] public string? BootId { get; set; }
}

// Log batches are parsed structurally from the root JsonElement (items are
// List<JsonElement>) so a malformed item is rejected per-item with "schema"
// instead of failing the whole batch at typed deserialization (PROTOCOL §6.2).
public sealed class LogItemDto
{
    [JsonPropertyName("kind")] public string? Kind { get; set; }
    [JsonPropertyName("record_id")] public string? RecordId { get; set; }
    [JsonPropertyName("dedup_scope")] public string? DedupScope { get; set; }
    [JsonPropertyName("time")] public string? Time { get; set; }
    [JsonPropertyName("severity")] public int? Severity { get; set; }
    [JsonPropertyName("facility")] public string? Facility { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonPropertyName("fields")] public JsonElement? Fields { get; set; }
    [JsonPropertyName("raw")] public string? Raw { get; set; }
}

/// <summary>Telemetry envelope. Items are polymorphic (heartbeat|facts) and are
/// parsed manually so unknown optional members survive (PROTOCOL §3 additive rule).</summary>
public sealed class TelemetryRequest
{
    [JsonPropertyName("v")] public int V { get; set; }
    [JsonPropertyName("source")] public string? Source { get; set; }
    [JsonPropertyName("items")] public List<JsonElement>? Items { get; set; }
}

public sealed class HeartbeatItemDto
{
    [JsonPropertyName("kind")] public const string Kind = "heartbeat";
    [JsonPropertyName("source_id")] public string? SourceId { get; set; }
    [JsonPropertyName("sent_at")] public string? SentAt { get; set; }
    [JsonPropertyName("agent_version")] public string? AgentVersion { get; set; }
    [JsonPropertyName("protocol_version")] public int? ProtocolVersion { get; set; }
    [JsonPropertyName("os_build")] public string? OsBuild { get; set; }
    [JsonPropertyName("boot_time")] public string? BootTime { get; set; }
    [JsonPropertyName("uptime_s")] public long? UptimeS { get; set; }
    [JsonPropertyName("free_disk")] public JsonElement? FreeDisk { get; set; }
    [JsonPropertyName("counters")] public JsonElement? Counters { get; set; }
    [JsonPropertyName("degraded")] public string? Degraded { get; set; }
    [JsonPropertyName("config_hash")] public string? ConfigHash { get; set; }
}

public sealed class FactsItemDto
{
    [JsonPropertyName("kind")] public const string Kind = "facts";
    [JsonPropertyName("collected_at")] public string? CollectedAt { get; set; }
    [JsonPropertyName("stale")] public bool Stale { get; set; }
    [JsonPropertyName("vms")] public List<VmItemDto>? Vms { get; set; }
}

public sealed class VmItemDto
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("state")] public string? State { get; set; }
    [JsonPropertyName("heartbeat_ok")] public bool? HeartbeatOk { get; set; }
    [JsonPropertyName("cpu_pct")] public double? CpuPct { get; set; }
    [JsonPropertyName("mem_mb")] public long? MemMb { get; set; }
    [JsonPropertyName("last_seen")] public string? LastSeen { get; set; }
}

// ─── Response DTOs (PROTOCOL §5.3, §6.3, §7.3, §8.2, §13.2) ──────────────────

public sealed class RegisterResponse
{
    [JsonPropertyName("v")] public int V { get; set; }
    [JsonPropertyName("source_id")] public string? SourceId { get; set; }
    [JsonPropertyName("token")] public string? Token { get; set; }
    [JsonPropertyName("scopes")] public string[] Scopes { get; set; } = [];
    [JsonPropertyName("issued_at")] public string? IssuedAt { get; set; }
    [JsonPropertyName("commands")] public object[] Commands { get; set; } = [];
}

public sealed class LogsResponse
{
    [JsonPropertyName("v")] public int V { get; set; }
    [JsonPropertyName("accepted")] public int Accepted { get; set; }
    [JsonPropertyName("deduped")] public int Deduped { get; set; }
    [JsonPropertyName("rejected")] public List<RejectedItem> Rejected { get; set; } = [];
    [JsonPropertyName("commands")] public object[] Commands { get; set; } = [];
}

public sealed class RejectedItem
{
    [JsonPropertyName("record_id")] public string? RecordId { get; set; }
    [JsonPropertyName("dedup_scope")] public string? DedupScope { get; set; }
    [JsonPropertyName("reason")] public string? Reason { get; set; }
    [JsonPropertyName("permanent")] public bool Permanent { get; set; } = true;
}

public sealed class TelemetryResponse
{
    [JsonPropertyName("v")] public int V { get; set; }
    [JsonPropertyName("accepted")] public bool Accepted { get; set; } = true;
    [JsonPropertyName("commands")] public object[] Commands { get; set; } = [];
}

public sealed class HealthResponse
{
    [JsonPropertyName("v")] public int V { get; set; }
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("server_time")] public string? ServerTime { get; set; }
    [JsonPropertyName("server_version")] public string? ServerVersion { get; set; }
    [JsonPropertyName("source_id")] public string? SourceId { get; set; }
    [JsonPropertyName("scopes")] public string[]? Scopes { get; set; }
    [JsonPropertyName("commands")] public object[] Commands { get; set; } = [];
}

public sealed class ProtocolError
{
    [JsonPropertyName("code")] public string? Code { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonPropertyName("supported")] public int[]? Supported { get; set; }
}

public sealed class ErrorEnvelope
{
    [JsonPropertyName("v")] public int V { get; set; }
    [JsonPropertyName("error")] public ProtocolError? Error { get; set; }
    [JsonPropertyName("commands")] public object[] Commands { get; set; } = [];
}

/// <summary>Stable protocol error codes (PROTOCOL.md §13.3). Codes are additive;
/// clients fall back to HTTP status for unknown codes.</summary>
public static class ErrorCodes
{
    public const string UnsupportedVersion = "unsupported_version";
    public const string MissingVersion = "missing_version";
    public const string InvalidRequest = "invalid_request";
    public const string TooManyItems = "too_many_items";
    public const string PayloadTooLarge = "payload_too_large";
    public const string TokenInvalid = "token_invalid";
    public const string TokenRevoked = "token_revoked";
    public const string TokenMissing = "token_missing";
    public const string TokenConsumed = "token_consumed";
    public const string WrongScope = "wrong_scope";
    public const string UnknownSource = "unknown_source";
    public const string NameCollision = "name_collision";
    public const string TooManyRequests = "too_many_requests";
    public const string Unavailable = "unavailable";
    public const string Internal = "internal";
    public const string UnsupportedMediaType = "unsupported_media_type";
}

/// <summary>Loads the machine-readable schema (docs/schemas/protocol-v1.json,
/// embedded) for structural validation. The schema runs in forward-compatible
/// mode: unknown members are allowed (PROTOCOL §6.7); endpoint/token/source-kind
/// semantics are validated explicitly by the endpoint validators.</summary>
public static class ProtocolSchema
{
    public static readonly SchemaValidator Validator = Load();

    private static SchemaValidator Load()
    {
        var asm = typeof(ProtocolSchema).Assembly;
        using var stream = asm.GetManifestResourceStream("Hyveman.Protocol.schemas.protocol-v1.json")
            ?? throw new InvalidOperationException("embedded protocol schema missing");
        using var reader = new StreamReader(stream);
        return SchemaValidator.FromJson(reader.ReadToEnd());
    }
}
