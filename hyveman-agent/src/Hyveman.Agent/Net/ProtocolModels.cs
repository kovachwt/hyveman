using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hyveman.Agent.Net;

// Wire models per docs/PROTOCOL.md §5-§8 (v1). Field names are the contract.

public sealed class LogBatchEnvelope
{
    [JsonPropertyName("v")] public int V { get; set; } = 1;
    [JsonPropertyName("source")] public string? Source { get; set; }
    [JsonPropertyName("items")] public List<LogItem> Items { get; set; } = new();
}

public sealed class LogItem
{
    [JsonPropertyName("kind")] public string Kind { get; set; } = "log";
    [JsonPropertyName("record_id")] public string RecordId { get; set; } = "";
    [JsonPropertyName("dedup_scope")] public string DedupScope { get; set; } = "";
    [JsonPropertyName("time")] public string Time { get; set; } = "";
    [JsonPropertyName("severity")] public int? Severity { get; set; } // omitted when Windows Level is unspecified (0)
    [JsonPropertyName("facility")] public string? Facility { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonPropertyName("fields")] public LogFields? Fields { get; set; }
    [JsonPropertyName("raw")] public string? Raw { get; set; }
}

public sealed class LogFields
{
    [JsonPropertyName("channel")] public string? Channel { get; set; }
    [JsonPropertyName("event_id")] public uint EventId { get; set; }
    [JsonPropertyName("task")] public ushort Task { get; set; }
    [JsonPropertyName("opcode")] public ushort Opcode { get; set; }
    [JsonPropertyName("keywords")] public string? Keywords { get; set; }
    [JsonPropertyName("provider_guid")] public string? ProviderGuid { get; set; }
    [JsonPropertyName("computer")] public string? Computer { get; set; }
    [JsonPropertyName("activity_id")] public string? ActivityId { get; set; }
    [JsonPropertyName("process_id")] public uint? ProcessId { get; set; }
    [JsonPropertyName("thread_id")] public uint? ThreadId { get; set; }
    [JsonPropertyName("event_data")] public Dictionary<string, string?>? EventData { get; set; }
}

public sealed class LogsResponse
{
    [JsonPropertyName("v")] public int V { get; set; }
    [JsonPropertyName("accepted")] public int Accepted { get; set; }
    [JsonPropertyName("deduped")] public int Deduped { get; set; }
    [JsonPropertyName("rejected")] public List<RejectedItem> Rejected { get; set; } = new();
    [JsonPropertyName("commands")] public List<JsonElement> Commands { get; set; } = new();
}

public sealed class RejectedItem
{
    [JsonPropertyName("record_id")] public string? RecordId { get; set; }
    [JsonPropertyName("dedup_scope")] public string? DedupScope { get; set; }
    [JsonPropertyName("reason")] public string? Reason { get; set; }
    [JsonPropertyName("permanent")] public bool Permanent { get; set; }
}

public sealed class TelemetryEnvelope
{
    [JsonPropertyName("v")] public int V { get; set; } = 1;
    [JsonPropertyName("source")] public string? Source { get; set; }
    [JsonPropertyName("items")] public List<JsonElement> Items { get; set; } = new();
}

public sealed class HeartbeatItem
{
    [JsonPropertyName("kind")] public string Kind { get; set; } = "heartbeat";
    [JsonPropertyName("sent_at")] public string SentAt { get; set; } = "";
    [JsonPropertyName("agent_version")] public string AgentVersion { get; set; } = "";
    [JsonPropertyName("protocol_version")] public int ProtocolVersion { get; set; } = 1;
    [JsonPropertyName("os_build")] public string? OsBuild { get; set; }
    [JsonPropertyName("boot_time")] public string? BootTime { get; set; }
    [JsonPropertyName("uptime_s")] public long UptimeS { get; set; }
    [JsonPropertyName("mem_total_bytes")] public long? MemTotalBytes { get; set; }
    [JsonPropertyName("mem_available_bytes")] public long? MemAvailableBytes { get; set; }
    [JsonPropertyName("free_disk")] public List<FreeDisk> FreeDisk { get; set; } = new();
    [JsonPropertyName("source_id")] public string? SourceId { get; set; }
    [JsonPropertyName("counters")] public HeartbeatCountersWire Counters { get; set; } = new();
    [JsonPropertyName("degraded")] public string Degraded { get; set; } = "";
    [JsonPropertyName("config_hash")] public string? ConfigHash { get; set; }
}

public sealed class FreeDisk
{
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("bytes")] public long Bytes { get; set; }
    [JsonPropertyName("pct")] public double Pct { get; set; }
}

public sealed class HeartbeatCountersWire
{
    [JsonPropertyName("events_sent")] public long EventsSent { get; set; }
    [JsonPropertyName("events_dropped")] public long EventsDropped { get; set; }
    [JsonPropertyName("batches_sent")] public long BatchesSent { get; set; }
    [JsonPropertyName("batches_failed")] public long BatchesFailed { get; set; }
    [JsonPropertyName("spool_bytes")] public long SpoolBytes { get; set; }
    [JsonPropertyName("spool_files")] public int SpoolFiles { get; set; }
    [JsonPropertyName("queue_depth")] public int QueueDepth { get; set; }
    [JsonPropertyName("wmi_timeouts")] public long WmiTimeouts { get; set; }
    [JsonPropertyName("send_errors_last_min")] public long SendErrorsLastMin { get; set; }
}

public sealed class FactsItem
{
    [JsonPropertyName("kind")] public string Kind { get; set; } = "facts";
    [JsonPropertyName("collected_at")] public string CollectedAt { get; set; } = "";
    [JsonPropertyName("stale")] public bool Stale { get; set; }
    [JsonPropertyName("vms")] public List<VmFactWire> Vms { get; set; } = new();
}

public sealed class VmFactWire
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("state")] public string State { get; set; } = "unknown";
    [JsonPropertyName("heartbeat_ok")] public bool? HeartbeatOk { get; set; }
    [JsonPropertyName("cpu_pct")] public double? CpuPct { get; set; }
    [JsonPropertyName("mem_mb")] public long? MemMb { get; set; }
    [JsonPropertyName("replication_state")] public string? ReplicationState { get; set; } // PROTOCOL §7.1; null = not replicated
    [JsonPropertyName("replication_health")] public string? ReplicationHealth { get; set; } // PROTOCOL §7.1; null = not replicated
    [JsonPropertyName("replication_last_apply_time")] public string? ReplicationLastApplyTime { get; set; } // UTC; null = not replicated
    [JsonPropertyName("last_seen")] public string LastSeen { get; set; } = "";
}

public sealed class RegisterRequest
{
    [JsonPropertyName("v")] public int V { get; set; } = 1;
    [JsonPropertyName("kind")] public string Kind { get; set; } = "windows-agent";
    [JsonPropertyName("hostname")] public string Hostname { get; set; } = "";
    [JsonPropertyName("agent_version")] public string? AgentVersion { get; set; }
    [JsonPropertyName("os_build")] public string? OsBuild { get; set; }
    [JsonPropertyName("boot_id")] public string? BootId { get; set; }
}

public sealed class RegisterResponse
{
    [JsonPropertyName("v")] public int V { get; set; }
    [JsonPropertyName("source_id")] public string? SourceId { get; set; }
    [JsonPropertyName("token")] public string? Token { get; set; }
    [JsonPropertyName("scopes")] public List<string>? Scopes { get; set; }
    [JsonPropertyName("issued_at")] public string? IssuedAt { get; set; }
}

public sealed class HealthResponse
{
    [JsonPropertyName("v")] public int V { get; set; }
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("server_time")] public string? ServerTime { get; set; }
    [JsonPropertyName("server_version")] public string? ServerVersion { get; set; }
    [JsonPropertyName("source_id")] public string? SourceId { get; set; }
    [JsonPropertyName("scopes")] public List<string>? Scopes { get; set; }
}

public sealed class ErrorEnvelope
{
    [JsonPropertyName("v")] public int V { get; set; }
    [JsonPropertyName("error")] public ErrorDetail? Error { get; set; }
    [JsonPropertyName("commands")] public List<JsonElement> Commands { get; set; } = new();
}

public sealed class ErrorDetail
{
    [JsonPropertyName("code")] public string? Code { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonPropertyName("supported")] public List<int>? Supported { get; set; }
}

/// <summary>Result classification for a single POST (PROTOCOL §13.3/§14).</summary>
public enum SendOutcome
{
    Accepted,
    Quarantine,          // non-retryable 4xx, or permanent per-item rejects
    Retry,               // 408/429/5xx/network
    Split,               // 400 too_many_items / 413 payload_too_large — split & resend
    CredentialsInvalid   // token/scope/source-class 4xx — keep batch, surface auth_rejected
}

public sealed record SendResult(SendOutcome Outcome, int? RetryAfterSeconds = null, LogsResponse? Logs = null, string? ErrorCode = null);
