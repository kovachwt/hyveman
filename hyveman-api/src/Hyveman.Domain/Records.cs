namespace Hyveman.Domain;

/// <summary>An ingest source (PROTOCOL §4.2): the authoritative identity behind tokens.</summary>
public sealed record Source(string Id, string Kind, string Name, DateTimeOffset CreatedAt);

/// <summary>Token row metadata. The raw token value never leaves the minting
/// response; the store keeps only the hash (DESIGN §5.1).</summary>
public sealed record TokenInfo(
    string Id,
    string SourceId,
    string Prefix,
    string[] Scopes,
    DateTimeOffset Created,
    DateTimeOffset? LastUsed,
    bool Revoked,
    DateTimeOffset? ExpiresAt);

/// <summary>Result of authenticating a raw bearer token.</summary>
public sealed record TokenAuthResult(
    string TokenId,
    string SourceId,
    string SourceKind,
    string[] Scopes);

/// <summary>Admin-issued one-time registration token metadata (PROTOCOL §5).</summary>
public sealed record RegistrationTokenInfo(
    string Id,
    string Kind,
    DateTimeOffset Created,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? ConsumedAt,
    bool Revoked);

/// <summary>Lookup of a raw registration token.</summary>
public sealed record RegistrationTokenLookup(
    string Id,
    string Kind,
    bool Revoked,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? ConsumedAt);

/// <summary>Hardware metadata row; deliberately separate from sources
/// (DESIGN §13 #12: sources and hosts are not implicitly interchangeable).</summary>
public sealed record HostRecord(
    string Id,
    string Name,
    string Kind,
    string? SourceId,
    string? IdracUrl,
    string? IdracCredRef,
    bool Enabled,
    string? Notes,
    DateTimeOffset UpdatedAt,
    DateTimeOffset CreatedAt);

/// <summary>A validated log item ready for persistence (PROTOCOL §6.2).</summary>
public sealed record ValidatedLogItem(
    string DedupScope,
    string RecordId,
    DateTimeOffset Time,
    int? Severity,
    string? Facility,
    string? Message,
    string FieldsJson,
    string? RawJson,
    string? Channel,
    long? EventId,
    long? Task,
    long? Opcode,
    string? Keywords);

/// <summary>Per-item ingest outcome (PROTOCOL §6.3/§6.4).</summary>
public sealed record ItemRejection(string RecordId, string DedupScope, string Reason, bool Permanent = true);

/// <summary>Batch ingest outcome. Invariant: accepted + deduped + rejected == items,
/// and AcceptedItems.Count == Accepted. AcceptedItems is the exact accepted subset in
/// batch order — dedup can hit any position when a partially committed batch is retried
/// (PROTOCOL §6.6), so derived processing must not reconstruct it from the count.</summary>
public sealed record IngestResult(int Accepted, int Deduped, IReadOnlyList<ItemRejection> Rejected,
    IReadOnlyList<ValidatedLogItem> AcceptedItems);

/// <summary>Heartbeat payload accepted from the wire (PROTOCOL §7).</summary>
public sealed record HeartbeatPayload(
    DateTimeOffset SentAt,
    string? AgentVersion,
    int? ProtocolVersion,
    string? OsBuild,
    DateTimeOffset? BootTime,
    long? UptimeS,
    long? MemTotalBytes,
    long? MemAvailableBytes,
    string? Degraded,
    string? ConfigHash,
    string? CountersJson,
    string? FreeDiskJson);

/// <summary>Facts snapshot accepted from the wire (PROTOCOL §7.1).</summary>
public sealed record FactsPayload(
    DateTimeOffset CollectedAt,
    bool Stale,
    IReadOnlyList<VmFact> Vms);

public sealed record VmFact(string Name, string State, bool? HeartbeatOk, double? CpuPct, long? MemMb, DateTimeOffset? LastSeen);

/// <summary>Agent status row (API.md §10 agent_status).</summary>
public sealed record AgentStatusRow(
    string SourceId,
    DateTimeOffset LastReceived,
    DateTimeOffset? LastSentAt,
    string? AgentVersion,
    string? OsBuild,
    DateTimeOffset? BootTime,
    long? UptimeS,
    string? Degraded,
    string? ConfigHash,
    string? CountersJson,
    string? HeartbeatJson,
    string? FactsJson,
    DateTimeOffset? FactsCollectedAt,
    DateTimeOffset UpdatedAt);

/// <summary>Current component health row (DESIGN §5.2).</summary>
public sealed record ComponentRecord(
    string HostId,
    string Type,
    string Name,
    HealthState State,
    string? Detail,
    DateTimeOffset LastSeen);

/// <summary>Health snapshot for history/sparklines.</summary>
public sealed record HealthSnapshotRecord(
    long Id,
    string HostId,
    DateTimeOffset Time,
    string RollupState,
    string? ComponentsJson);

/// <summary>Metric sample (temps, watts, disk free, ...).</summary>
public sealed record MetricRecord(string HostId, string Name, double Value, string? Unit, DateTimeOffset Time);

/// <summary>Hyper-V VM fact row.</summary>
public sealed record VmRecord(
    string HostId,
    string Name,
    string State,
    bool? HeartbeatOk,
    double? CpuPct,
    long? MemMb,
    DateTimeOffset? LastSeen,
    bool Stale,
    DateTimeOffset CollectedAt);

/// <summary>Alert record (DESIGN §5.2, API.md §9.3).</summary>
public sealed record AlertRecord(
    string Id,
    string? RuleId,
    string? HostId,
    string? SourceId,
    string Key,
    string Fingerprint,
    string Severity,
    string Status,
    string Title,
    string? Detail,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen,
    long Count,
    DateTimeOffset? AckAt,
    string? AckReason,
    DateTimeOffset? SilenceUntil,
    DateTimeOffset? ResolvedAt,
    DateTimeOffset UpdatedAt);

/// <summary>Alert rule (DESIGN §4.4).</summary>
public sealed record RuleRecord(
    string Id,
    string Name,
    string Type,
    string MatchJson,
    string Severity,
    long CooldownS,
    bool Enabled,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>Notification channel metadata. Secrets live in the vault and are
/// never returned by read paths (API.md §7.4).</summary>
public sealed record ChannelRecord(
    string Id,
    string Name,
    string Kind,
    string? ConfigRef,
    bool Enabled,
    DateTimeOffset Created,
    DateTimeOffset? Rotated,
    DateTimeOffset? LastTestAt,
    bool? LastTestOk,
    DateTimeOffset UpdatedAt);

/// <summary>Durable notification outbox row (API.md §9.4).</summary>
public sealed record OutboxItem(
    string Id,
    string? AlertId,
    string ChannelId,
    string Status,
    int AttemptCount,
    DateTimeOffset NextAttemptAt,
    string? LastError,
    DateTimeOffset CreatedAt,
    DateTimeOffset? SentAt);

/// <summary>Maintenance window (API.md §7.3).</summary>
public sealed record MaintenanceWindowRecord(
    string Id,
    string? HostId,
    DateTimeOffset Start,
    DateTimeOffset End,
    string? Reason,
    string? CreatedBy,
    DateTimeOffset CreatedAt);

/// <summary>Web session row (API.md §8.2).</summary>
public sealed record WebSession(
    string IdHash,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset LastSeen,
    DateTimeOffset? RevokedAt);

/// <summary>Passkey row (API.md §8.1).</summary>
public sealed record PasskeyRecord(
    string Id,
    string Name,
    string CredentialId,
    string PublicKey,
    uint SignCount,
    DateTimeOffset Created,
    DateTimeOffset? LastUsed);

/// <summary>Vault credential metadata (DESIGN §7). The blob is AES-GCM ciphertext.</summary>
public sealed record CredentialMeta(string Id, string Kind, string Label, DateTimeOffset Created, DateTimeOffset? Rotated);

/// <summary>Audit entry (API.md §7, DESIGN §5.2).</summary>
public sealed record AuditEntry(
    long Id,
    DateTimeOffset Time,
    string? Actor,
    string Action,
    string? TargetKind,
    string? TargetId,
    string? DetailJson);

/// <summary>Saved event search (API.md §7.2, FRONTEND §8.3).</summary>
public sealed record SavedSearchRecord(
    string Id,
    string Name,
    string FilterJson,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>One aggregated security-logon delta (DESIGN §4.1/§13 #5).
/// Day is the UTC event day (yyyy-MM-dd). LogonType is null for account
/// lockouts (4740), which carry no logon type.</summary>
public sealed record LogonStatEntry(
    string Day,
    string User,
    int? LogonType,
    long SuccessDelta,
    long FailureDelta);

/// <summary>Aggregated per-user/per-day logon counts (logon_stats row).</summary>
public sealed record LogonStatRow(
    string Day,
    string SourceId,
    string? SourceName,
    string User,
    int? LogonType,
    long SuccessCount,
    long FailureCount);

public sealed record LogonStatsQuery(
    DateTimeOffset? From,
    DateTimeOffset? To,
    string? SourceId,
    string? User,
    int Limit);

/// <summary>Outcome of a VACUUM INTO backup (API.md §9.5).</summary>
public sealed record BackupResult(bool Ok, string Path, long SizeBytes, string? Error);

public sealed record BackupInfo(string Path, DateTimeOffset Time, long SizeBytes, string Kind);
