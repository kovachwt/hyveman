using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hyveman.Server.Ingest;

// ── Wire contract DTOs (PROTOCOL.md v1). Unknown fields are ignored (forward compat §3). ──

public sealed class RegisterRequest
{
    [JsonPropertyName("v")] public int V { get; set; }
    [JsonPropertyName("kind")] public string? Kind { get; set; }
    [JsonPropertyName("hostname")] public string? Hostname { get; set; }
    [JsonPropertyName("agent_version")] public string? AgentVersion { get; set; }
    [JsonPropertyName("os_build")] public string? OsBuild { get; set; }
    [JsonPropertyName("boot_id")] public string? BootId { get; set; }
}

public sealed class RegisterResponse
{
    [JsonPropertyName("v")] public int V { get; set; } = 1;
    [JsonPropertyName("source_id")] public string SourceId { get; set; } = "";
    [JsonPropertyName("token")] public string Token { get; set; } = "";
    [JsonPropertyName("scopes")] public List<string> Scopes { get; set; } = new();
    [JsonPropertyName("issued_at")] public string IssuedAt { get; set; } = "";
    [JsonPropertyName("commands")] public List<object> Commands { get; set; } = new();
}

public sealed class LogsRequest
{
    [JsonPropertyName("v")] public int V { get; set; }
    [JsonPropertyName("source")] public string? Source { get; set; }
    [JsonPropertyName("items")] public List<LogItem>? Items { get; set; }
}

public sealed class LogItem
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

public sealed class LogsResponse
{
    [JsonPropertyName("v")] public int V { get; set; } = 1;
    [JsonPropertyName("accepted")] public int Accepted { get; set; }
    [JsonPropertyName("deduped")] public int Deduped { get; set; }
    [JsonPropertyName("rejected")] public List<RejectedItem> Rejected { get; set; } = new();
    [JsonPropertyName("commands")] public List<object> Commands { get; set; } = new();
}

public sealed class RejectedItem
{
    [JsonPropertyName("record_id")] public string RecordId { get; set; } = "";
    [JsonPropertyName("dedup_scope")] public string DedupScope { get; set; } = "";
    [JsonPropertyName("reason")] public string Reason { get; set; } = "";
    [JsonPropertyName("permanent")] public bool Permanent { get; set; } = true;
}

public sealed class TelemetryRequest
{
    [JsonPropertyName("v")] public int V { get; set; }
    [JsonPropertyName("source")] public string? Source { get; set; }
    [JsonPropertyName("items")] public List<TelemetryItem>? Items { get; set; }
}

public sealed class TelemetryItem
{
    [JsonPropertyName("kind")] public string? Kind { get; set; }
    // heartbeat
    [JsonPropertyName("sent_at")] public string? SentAt { get; set; }
    [JsonPropertyName("agent_version")] public string? AgentVersion { get; set; }
    [JsonPropertyName("protocol_version")] public int? ProtocolVersion { get; set; }
    [JsonPropertyName("os_build")] public string? OsBuild { get; set; }
    [JsonPropertyName("boot_time")] public string? BootTime { get; set; }
    [JsonPropertyName("uptime_s")] public long? UptimeS { get; set; }
    [JsonPropertyName("free_disk")] public List<FreeDiskEntry>? FreeDisk { get; set; }
    [JsonPropertyName("counters")] public Dictionary<string, long>? Counters { get; set; }
    [JsonPropertyName("degraded")] public string? Degraded { get; set; }
    [JsonPropertyName("config_hash")] public string? ConfigHash { get; set; }
    // facts
    [JsonPropertyName("collected_at")] public string? CollectedAt { get; set; }
    [JsonPropertyName("stale")] public bool? Stale { get; set; }
    [JsonPropertyName("vms")] public List<VmItem>? Vms { get; set; }
}

public sealed class FreeDiskEntry
{
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("bytes")] public long Bytes { get; set; }
    [JsonPropertyName("pct")] public double Pct { get; set; }
}

public sealed class VmItem
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("state")] public string? State { get; set; }
    [JsonPropertyName("heartbeat_ok")] public bool? HeartbeatOk { get; set; }
    [JsonPropertyName("cpu_pct")] public double? CpuPct { get; set; }
    [JsonPropertyName("mem_mb")] public int? MemMb { get; set; }
    [JsonPropertyName("last_seen")] public string? LastSeen { get; set; }
}

public sealed class TelemetryResponse
{
    [JsonPropertyName("v")] public int V { get; set; } = 1;
    [JsonPropertyName("accepted")] public bool Accepted { get; set; } = true;
    [JsonPropertyName("commands")] public List<object> Commands { get; set; } = new();
}

public sealed class HealthResponse
{
    [JsonPropertyName("v")] public int V { get; set; } = 1;
    [JsonPropertyName("ok")] public bool Ok { get; set; } = true;
    [JsonPropertyName("server_time")] public string ServerTime { get; set; } = "";
    [JsonPropertyName("server_version")] public string ServerVersion { get; set; } = "";
    [JsonPropertyName("source_id")] public string? SourceId { get; set; }
    [JsonPropertyName("scopes")] public List<string>? Scopes { get; set; }
    [JsonPropertyName("commands")] public List<object> Commands { get; set; } = new();
}

public sealed class ErrorResponse
{
    [JsonPropertyName("v")] public int V { get; set; } = 1;
    [JsonPropertyName("error")] public ErrorBody Error { get; set; } = new();
    [JsonPropertyName("commands")] public List<object> Commands { get; set; } = new();
}

public sealed class ErrorBody
{
    [JsonPropertyName("code")] public string Code { get; set; } = "";
    [JsonPropertyName("message")] public string Message { get; set; } = "";
    [JsonPropertyName("supported")] public List<int>? Supported { get; set; }
}

[JsonSerializable(typeof(RegisterRequest))]
[JsonSerializable(typeof(RegisterResponse))]
[JsonSerializable(typeof(LogsRequest))]
[JsonSerializable(typeof(LogsResponse))]
[JsonSerializable(typeof(TelemetryRequest))]
[JsonSerializable(typeof(TelemetryResponse))]
[JsonSerializable(typeof(HealthResponse))]
[JsonSerializable(typeof(ErrorResponse))]
public partial class WireJsonContext : JsonSerializerContext;
