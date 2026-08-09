namespace Hyveman.Contracts;

// ─── Auth & session (API.md §8, FRONTEND §7) ────────────────────────────────

public sealed class SessionResponse
{
    public bool Authenticated { get; set; }
    public bool SetupRequired { get; set; }
    public string? AdminName { get; set; }
}

public sealed class PasskeyDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTimeOffset Created { get; set; }
    public DateTimeOffset? LastUsed { get; set; }
}

public sealed class PasskeyRegisterRequest
{
    public string? Name { get; set; }
}

// ─── Overview (API.md §7.1) ─────────────────────────────────────────────────

public sealed class OverviewResponse
{
    public DateTimeOffset GeneratedAt { get; set; }
    public List<HostTileDto> Hosts { get; set; } = [];
    public OverviewSummaryDto Summary { get; set; } = new();
    public List<AlertDto> RecentAlerts { get; set; } = [];
}

public sealed class OverviewSummaryDto
{
    public int Total { get; set; }
    public int Ok { get; set; }
    public int Warning { get; set; }
    public int Critical { get; set; }
    public int Unknown { get; set; }
    public int SilentAgents { get; set; }
    public int ActiveAlerts { get; set; }
    public int UnacknowledgedAlerts { get; set; }
}

public sealed class HostTileDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "";
    public string? SourceId { get; set; }
    public string RollupState { get; set; } = "unknown";
    public DateTimeOffset? RollupAt { get; set; }
    public string? HardwareState { get; set; }
    public string? OsState { get; set; }
    public string? HyperVState { get; set; }
    public AgentStatusDto? Agent { get; set; }
    public IdracStatusDto? Idrac { get; set; }
    public int ActiveAlertCount { get; set; }
}

public sealed class AgentStatusDto
{
    public string? SourceId { get; set; }
    public string Status { get; set; } = "unknown"; // online | silent | unknown
    public DateTimeOffset? LastReceived { get; set; }
    public DateTimeOffset? LastSentAt { get; set; }
    public string? AgentVersion { get; set; }
    public string? OsBuild { get; set; }
    public DateTimeOffset? BootTime { get; set; }
    public long? UptimeS { get; set; }
    public string? Degraded { get; set; }
    public string? ConfigHash { get; set; }
    public Dictionary<string, long>? Counters { get; set; }
    public bool FactsStale { get; set; }
    public DateTimeOffset? FactsCollectedAt { get; set; }
    public int VmCount { get; set; }
}

public sealed class IdracStatusDto
{
    public bool Configured { get; set; }
    public DateTimeOffset? LastPoll { get; set; }
    public bool LastPollOk { get; set; }
    public string? LastError { get; set; }
}

// ─── Hosts (API.md §7.1) ────────────────────────────────────────────────────

public class HostDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "";
    public string? SourceId { get; set; }
    public string? IdracUrl { get; set; }
    public bool IdracCredentialSet { get; set; }
    public bool Enabled { get; set; }
    public string? Notes { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class HostInput
{
    public string? Name { get; set; }
    public string? Kind { get; set; }
    public string? SourceId { get; set; }
    public string? IdracUrl { get; set; }
    public string? IdracUsername { get; set; }
    public string? IdracPassword { get; set; }
    public bool? Enabled { get; set; }
    public string? Notes { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public bool? ConfirmDelete { get; set; }
}

public sealed class HostDetailDto : HostDto
{
    public string RollupState { get; set; } = "unknown";
    public DateTimeOffset? RollupAt { get; set; }
    public List<ComponentDto> Components { get; set; } = [];
    public List<MetricDto> LatestMetrics { get; set; } = [];
    public List<AlertDto> RecentAlerts { get; set; } = [];
    public List<EventDto> RecentEvents { get; set; } = [];
    public AgentStatusDto? Agent { get; set; }
}

public sealed class ComponentDto
{
    public string Type { get; set; } = "";
    public string Name { get; set; } = "";
    public string State { get; set; } = "unknown";
    public string? Detail { get; set; }
    public DateTimeOffset LastSeen { get; set; }
}

public sealed class MetricDto
{
    public string Name { get; set; } = "";
    public double Value { get; set; }
    public string? Unit { get; set; }
    public DateTimeOffset Time { get; set; }
}

public sealed class HealthHistoryResponse
{
    public string HostId { get; set; } = "";
    public DateTimeOffset From { get; set; }
    public DateTimeOffset To { get; set; }
    public string Resolution { get; set; } = "";
    public List<HealthHistoryPoint> Points { get; set; } = [];
}

public sealed class HealthHistoryPoint
{
    public DateTimeOffset Time { get; set; }
    public string RollupState { get; set; } = "unknown";
    public double? TemperatureMaxC { get; set; }
    public double? PowerWatts { get; set; }
}

/// <summary>GET /api/v1/hosts/{id}/health (API.md §7.1): current components,
/// rollup, latest metrics and a bounded recent-snapshot preview.</summary>
public sealed class HostHealthResponse
{
    public string HostId { get; set; } = "";
    public string RollupState { get; set; } = "unknown";
    public DateTimeOffset? RollupAt { get; set; }
    public List<ComponentDto> Components { get; set; } = [];
    public List<MetricDto> LatestMetrics { get; set; } = [];
    public List<HealthSnapshotDto> RecentSnapshots { get; set; } = [];
}

public sealed class HealthSnapshotDto
{
    public DateTimeOffset Time { get; set; }
    public string RollupState { get; set; } = "";
}

public sealed class VmDto
{
    public string Name { get; set; } = "";
    public string State { get; set; } = "unknown";
    public bool? HeartbeatOk { get; set; }
    public double? CpuPct { get; set; }
    public long? MemMb { get; set; }
    public DateTimeOffset? LastSeen { get; set; }
    public bool Stale { get; set; }
}

// ─── Events (API.md §7.2) ───────────────────────────────────────────────────

public sealed class EventDto
{
    public long Id { get; set; }
    public string SourceId { get; set; } = "";
    public string? SourceName { get; set; }
    public string? HostId { get; set; }
    public string? HostName { get; set; }
    public string DedupScope { get; set; } = "";
    public string RecordId { get; set; } = "";
    public DateTimeOffset Time { get; set; }
    public int? Severity { get; set; }
    public string? Facility { get; set; }
    public string? Message { get; set; }
    public string? Channel { get; set; }
    public long? EventId { get; set; }
    public long? Task { get; set; }
    public long? Opcode { get; set; }
    public string? Keywords { get; set; }
    public string? FieldsJson { get; set; }
    public string? RawJson { get; set; }
}

public sealed class EventSearchResponse
{
    public List<EventDto> Items { get; set; } = [];
    public string? NextCursor { get; set; }
    public bool HasMore { get; set; }
}

public sealed class SavedSearchDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public Dictionary<string, object?> Filter { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class SavedSearchInput
{
    public string? Name { get; set; }
    public Dictionary<string, object?>? Filter { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
}

// ─── Sources & tokens (API.md §7) ───────────────────────────────────────────

public sealed class SourceDto
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public string? HostId { get; set; }
    public AgentStatusDto? Agent { get; set; }
    public List<TokenDto> Tokens { get; set; } = [];
}

public sealed class TokenDto
{
    public string Id { get; set; } = "";
    public string Prefix { get; set; } = "";
    public string[] Scopes { get; set; } = [];
    public DateTimeOffset Created { get; set; }
    public DateTimeOffset? LastUsed { get; set; }
    public bool Revoked { get; set; }
}

public sealed class RegistrationTokenCreateRequest
{
    public string Kind { get; set; } = "";
    public int? LifetimeMinutes { get; set; }
}

public sealed class RegistrationTokenCreatedDto
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Token { get; set; } = "";
    public DateTimeOffset Created { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    /// <summary>Raw token is returned exactly once; never again.</summary>
    public bool ShowOnce { get; set; } = true;
}

public sealed class RegistrationTokenDto
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "";
    public DateTimeOffset Created { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }
    public bool Revoked { get; set; }
}

// ─── Alerts (API.md §7.3) ───────────────────────────────────────────────────

public sealed class AlertDto
{
    public string Id { get; set; } = "";
    public string? RuleId { get; set; }
    public string? RuleName { get; set; }
    public string? HostId { get; set; }
    public string? HostName { get; set; }
    public string? SourceId { get; set; }
    public string Severity { get; set; } = "";
    public string Status { get; set; } = "active";
    public string Title { get; set; } = "";
    public string? Detail { get; set; }
    public DateTimeOffset FirstSeen { get; set; }
    public DateTimeOffset LastSeen { get; set; }
    public long Count { get; set; }
    public DateTimeOffset? AckAt { get; set; }
    public string? AckReason { get; set; }
    public DateTimeOffset? SilenceUntil { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
}

public sealed class AlertListResponse
{
    public List<AlertDto> Items { get; set; } = [];
    public string? NextCursor { get; set; }
    public bool HasMore { get; set; }
}

public sealed class AlertActionRequest
{
    public string? Reason { get; set; }
    public DateTimeOffset? Until { get; set; }
}

// ─── Rules (API.md §7.3) ────────────────────────────────────────────────────

public sealed class RuleDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public Dictionary<string, object?> Match { get; set; } = [];
    public string Severity { get; set; } = "";
    public long CooldownS { get; set; }
    public bool Enabled { get; set; }
    public List<string> ChannelIds { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class RuleInput
{
    public string? Name { get; set; }
    public string? Type { get; set; }
    public Dictionary<string, object?>? Match { get; set; }
    public string? Severity { get; set; }
    public long? CooldownS { get; set; }
    public bool? Enabled { get; set; }
    public List<string>? ChannelIds { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
}

// ─── Notification channels (API.md §7.4) ────────────────────────────────────

public sealed class ChannelDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "";
    public bool Enabled { get; set; }
    public DateTimeOffset Created { get; set; }
    public DateTimeOffset? Rotated { get; set; }
    public DateTimeOffset? LastTestAt { get; set; }
    public bool? LastTestOk { get; set; }
    /// <summary>Redacted configuration summary; never contains secret values.</summary>
    public Dictionary<string, string> ConfigSummary { get; set; } = [];
}

public sealed class ChannelInput
{
    public string? Name { get; set; }
    public string? Kind { get; set; }
    public bool? Enabled { get; set; }
    public ChannelSecretInput? Config { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public bool? ConfirmDelete { get; set; }
}

public sealed class ChannelSecretInput
{
    public string? TelegramBotToken { get; set; }
    public string? TelegramChatId { get; set; }
    public string? WebhookUrl { get; set; }
    public string? SmtpHost { get; set; }
    public int? SmtpPort { get; set; }
    public string? SmtpUsername { get; set; }
    public string? SmtpPassword { get; set; }
    public string? SmtpFrom { get; set; }
    public string? SmtpTo { get; set; }
    public bool? SmtpUseTls { get; set; }
}

public sealed class ChannelTestResult
{
    public string ChannelId { get; set; } = "";
    public bool Ok { get; set; }
    public DateTimeOffset TestedAt { get; set; }
    public string? Error { get; set; }
}

// ─── Maintenance windows (API.md §7.3) ──────────────────────────────────────

public sealed class MaintenanceWindowDto
{
    public string Id { get; set; } = "";
    public string? HostId { get; set; }
    public DateTimeOffset Start { get; set; }
    public DateTimeOffset End { get; set; }
    public string? Reason { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class MaintenanceWindowInput
{
    public string? HostId { get; set; }
    public DateTimeOffset? Start { get; set; }
    public DateTimeOffset? End { get; set; }
    public string? Reason { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
}

// ─── Settings (API.md §7) ───────────────────────────────────────────────────

public sealed class RetentionSettingsDto
{
    public int EventDays { get; set; } = 365;
    public int MetricDays { get; set; } = 180;
    public int SnapshotDays { get; set; } = 180;
}

public sealed class RetentionSettingsInput
{
    public int? EventDays { get; set; }
    public int? MetricDays { get; set; }
    public int? SnapshotDays { get; set; }
}

// ─── Audit (API.md §7) ──────────────────────────────────────────────────────

public sealed class AuditEntryDto
{
    public long Id { get; set; }
    public DateTimeOffset Time { get; set; }
    public string? Actor { get; set; }
    public string Action { get; set; } = "";
    public string? TargetKind { get; set; }
    public string? TargetId { get; set; }
    public string? DetailJson { get; set; }
}

public sealed class AuditListResponse
{
    public List<AuditEntryDto> Items { get; set; } = [];
    public string? NextCursor { get; set; }
    public bool HasMore { get; set; }
}

// ─── Logon stats (DESIGN §4.1, Phase 2) ─────────────────────────────────────

public sealed class LogonStatsResponse
{
    public List<LogonStatDto> Items { get; set; } = [];
    public bool HasMore { get; set; }
}

public sealed class LogonStatDto
{
    /// <summary>UTC event day (yyyy-MM-dd).</summary>
    public string Day { get; set; } = "";
    public string SourceId { get; set; } = "";
    public string? SourceName { get; set; }
    public string User { get; set; } = "";
    /// <summary>Null for account lockouts (4740), which carry no logon type.</summary>
    public int? LogonType { get; set; }
    public long SuccessCount { get; set; }
    public long FailureCount { get; set; }
}

// ─── Errors (API.md §5.2) ───────────────────────────────────────────────────

public sealed class ApiProblem
{
    public string Type { get; set; } = "https://hyveman.example/errors/internal";
    public string Title { get; set; } = "Internal error";
    public int Status { get; set; } = 500;
    public string Code { get; set; } = "internal";
    public string? Detail { get; set; }
    public string? TraceId { get; set; }
    public Dictionary<string, List<string>>? Errors { get; set; }
}
