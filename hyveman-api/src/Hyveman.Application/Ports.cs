using Hyveman.Domain;

namespace Hyveman.Application;

/// <summary>Clock abstraction so ordering rules are testable.</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

/// <summary>Source identity store (sources table; UNIQUE(kind, name)).</summary>
public interface ISourceStore
{
    Task<Source?> GetByIdAsync(string id, CancellationToken ct);
    Task<Source?> GetByKindNameAsync(string kind, string name, CancellationToken ct);
    Task<Source> CreateAsync(string kind, string name, DateTimeOffset now, CancellationToken ct);
    Task<IReadOnlyList<Source>> ListAsync(CancellationToken ct);
    Task DeleteAsync(string id, CancellationToken ct);
}

/// <summary>Agent token store. Only hashes are persisted (DESIGN §5.1);
/// raw tokens exist solely in the minting response.</summary>
public interface ITokenStore
{
    /// <summary>Constant-time hash lookup of a raw bearer token.</summary>
    Task<TokenAuthResult?> AuthenticateAsync(string rawToken, CancellationToken ct);

    /// <summary>True when the token exists but is revoked (401 token_revoked).</summary>
    Task<bool> IsRevokedAsync(string rawToken, CancellationToken ct);

    /// <summary>True when the token exists but its source row is gone
    /// (404 unknown_source).</summary>
    Task<bool> SourceMissingAsync(string rawToken, CancellationToken ct);

    /// <summary>Mints a new agent token (agt_ prefix) for the source; returns the raw token.</summary>
    Task<string> CreateAgentTokenAsync(string sourceId, string[] scopes, DateTimeOffset now, CancellationToken ct);

    Task<IReadOnlyList<TokenInfo>> ListForSourceAsync(string sourceId, CancellationToken ct);
    Task<bool> RevokeAsync(string tokenId, CancellationToken ct);
    Task TouchAsync(string tokenId, DateTimeOffset at, CancellationToken ct);
}

/// <summary>Admin-issued single-use registration tokens (PROTOCOL §5).</summary>
public interface IRegistrationTokenStore
{
    /// <summary>Mints a reg_ token bound to a source kind; returns (id, raw token).</summary>
    Task<(string Id, string RawToken)> CreateAsync(string kind, TimeSpan? lifetime, string? createdBy, DateTimeOffset now, CancellationToken ct);

    Task<RegistrationTokenLookup?> LookupAsync(string rawToken, CancellationToken ct);
    Task MarkConsumedAsync(string id, DateTimeOffset at, CancellationToken ct);
    Task<bool> RevokeAsync(string id, CancellationToken ct);
    Task<IReadOnlyList<RegistrationTokenInfo>> ListAsync(CancellationToken ct);
}

/// <summary>Outcome of an atomic registration attempt (API.md §6.2).</summary>
public enum RegistrationStatus
{
    Ok,
    UnknownToken,
    Revoked,
    Expired,
    Consumed,
    KindMismatch,
}

/// <summary>Result of a registration-unit attempt. On Ok, the identity/token
/// fields are populated; otherwise Status carries the failure classification.</summary>
public sealed record RegistrationUnitResult(
    RegistrationStatus Status,
    string? SourceId = null,
    string? SourceKind = null,
    string? SourceName = null,
    string? RawToken = null,
    string[]? Scopes = null,
    DateTimeOffset? IssuedAt = null,
    bool SourceCreated = false,
    string? BoundKind = null);

/// <summary>Atomic registration unit (API.md §6.2): validates the reg_ token,
/// resolves or creates the (kind, hostname) source, mints the agt_ token and
/// marks the reg_ token consumed in one transaction, so concurrent
/// registrations can neither consume the same registration token twice nor
/// create duplicate source rows.</summary>
public interface IRegistrationUnit
{
    Task<RegistrationUnitResult> ExecuteAsync(string rawRegToken, string kind, string hostname,
        DateTimeOffset now, CancellationToken ct);
}

/// <summary>Idempotent event store with FTS5-backed search (API.md §10).</summary>
public interface IEventStore
{
    /// <summary>Inserts a batch with ON CONFLICT(source_id, dedup_scope, record_id)
    /// DO NOTHING; FTS5 is updated only for newly inserted messages, atomically.</summary>
    Task<IngestResult> InsertBatchAsync(string sourceId, IReadOnlyList<ValidatedLogItem> items, CancellationToken ct);

    Task<EventSearchPage> SearchAsync(EventQuery query, CancellationToken ct);
    Task<EventDetail?> GetAsync(long id, CancellationToken ct);
    Task<long> PurgeOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct);
    Task<long> CountAsync(CancellationToken ct);
    Task<DateTimeOffset> NewestTimeAsync(CancellationToken ct);
}

public sealed record EventQuery(
    DateTimeOffset? From,
    DateTimeOffset? To,
    string? HostId,
    string? SourceId,
    string? Channel,
    int? SeverityMin,
    long? EventId,
    string? Q,
    int Limit,
    string? Cursor,
    string Sort);

public sealed record EventSearchPage(
    IReadOnlyList<EventDetail> Items,
    string? NextCursor,
    bool HasMore);

public sealed record EventDetail(
    long Id,
    string SourceId,
    string? SourceName,
    string? HostId,
    string? HostName,
    string DedupScope,
    string RecordId,
    DateTimeOffset Time,
    int? Severity,
    string? Facility,
    string? Message,
    string? FieldsJson,
    string? RawJson,
    string? Channel,
    long? EventId,
    long? Task,
    long? Opcode,
    string? Keywords);

/// <summary>Latest-wins agent telemetry state (API.md §6.4, §10 agent_status).</summary>
public interface IAgentStatusStore
{
    Task<AgentStatusRow?> GetAsync(string sourceId, CancellationToken ct);
    Task<IReadOnlyList<AgentStatusRow>> ListAllAsync(CancellationToken ct);

    /// <summary>Applies the §7.4 heartbeat ordering rule. Returns true when the
    /// state payload was stored (not just the receive time).</summary>
    Task<bool> ApplyHeartbeatAsync(string sourceId, HeartbeatPayload hb, DateTimeOffset receivedAt, CancellationToken ct);

    /// <summary>Applies the §7.4 facts ordering rule; returns true when the
    /// snapshot was stored (vms upsert happens via IHealthStore).</summary>
    Task<bool> ApplyFactsAsync(string sourceId, FactsPayload facts, DateTimeOffset receivedAt, CancellationToken ct);
}

/// <summary>Last hardware poll status per host (API.md §9.1).</summary>
public interface IPollStatusStore
{
    Task<PollStatusRecord?> GetAsync(string hostId, CancellationToken ct);
    Task MarkSuccessAsync(string hostId, DateTimeOffset at, CancellationToken ct);
    Task MarkFailureAsync(string hostId, DateTimeOffset at, string? error, CancellationToken ct);
}

public sealed record PollStatusRecord(
    string HostId,
    DateTimeOffset LastPoll,
    DateTimeOffset? LastSuccess,
    string? LastError,
    int Failures);

/// <summary>Hardware metadata store (hosts table; separate from sources).</summary>
public interface IHostStore
{
    Task<IReadOnlyList<HostRecord>> ListAsync(CancellationToken ct);
    Task<HostRecord?> GetAsync(string id, CancellationToken ct);
    Task<HostRecord?> GetBySourceAsync(string sourceId, CancellationToken ct);
    Task<HostRecord> CreateAsync(HostRecord host, CancellationToken ct);

    /// <summary>Optimistic update; false when updatedAt is stale (409 conflict).</summary>
    Task<bool> UpdateAsync(HostRecord host, DateTimeOffset expectedUpdatedAt, CancellationToken ct);
    Task DeleteAsync(string id, CancellationToken ct);
}

/// <summary>Vendor-neutral health store: components, snapshots, metrics, VMs.</summary>
public interface IHealthStore
{
    Task ReplaceComponentsAsync(string hostId, IReadOnlyList<ComponentRecord> components, CancellationToken ct);
    Task<IReadOnlyList<ComponentRecord>> GetComponentsAsync(string hostId, CancellationToken ct);
    Task AddSnapshotAsync(string hostId, DateTimeOffset time, string rollupState, string componentsJson, CancellationToken ct);
    Task<IReadOnlyList<HealthSnapshotRecord>> GetSnapshotsAsync(string hostId, DateTimeOffset? from, DateTimeOffset? to, int limit, CancellationToken ct);
    Task AddMetricsAsync(string hostId, DateTimeOffset time, IReadOnlyList<MetricRecord> metrics, CancellationToken ct);
    Task<IReadOnlyList<MetricRecord>> GetLatestMetricsAsync(string hostId, int maxPerName, CancellationToken ct);
    Task<IReadOnlyList<MetricRecord>> GetMetricsInRangeAsync(string hostId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct);
    Task UpsertVmsAsync(string hostId, IReadOnlyList<VmRecord> vms, bool stale, DateTimeOffset collectedAt, CancellationToken ct);
    Task<IReadOnlyList<VmRecord>> GetVmsAsync(string hostId, CancellationToken ct);
    Task<long> PurgeMetricsOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct);
    Task<long> PurgeSnapshotsOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct);
    Task<long> PurgeVmsAsync(DateTimeOffset cutoff, CancellationToken ct);
}

/// <summary>Alerts (DESIGN §5.2, API.md §9.3).</summary>
public interface IAlertStore
{
    /// <summary>Finds the live occurrence (active/acknowledged/silenced) for the
    /// stable key, if any.</summary>
    Task<AlertRecord?> FindLiveAsync(string key, CancellationToken ct);
    Task<AlertRecord?> GetAsync(string id, CancellationToken ct);
    Task CreateAsync(AlertRecord alert, CancellationToken ct);
    Task UpdateAsync(AlertRecord alert, CancellationToken ct);
    Task<IReadOnlyList<AlertRecord>> ListAsync(AlertQuery query, CancellationToken ct);
    Task<IReadOnlyList<AlertRecord>> ListLiveAsync(CancellationToken ct);
    Task<long> CountLiveAsync(CancellationToken ct);
    Task<long> CountUnacknowledgedAsync(CancellationToken ct);
    Task ResolveForHostAsync(string hostId, DateTimeOffset at, CancellationToken ct);
}

public sealed record AlertQuery(
    string? Status,
    string? HostId,
    string? RuleId,
    DateTimeOffset? From,
    DateTimeOffset? To,
    int Limit,
    string? Cursor);

/// <summary>Alert rules (DESIGN §4.4, API.md §7.3).</summary>
public interface IRuleStore
{
    Task<IReadOnlyList<RuleRecord>> ListAsync(CancellationToken ct);
    Task<RuleRecord?> GetAsync(string id, CancellationToken ct);
    Task<RuleRecord> CreateAsync(RuleRecord rule, CancellationToken ct);
    Task<bool> UpdateAsync(RuleRecord rule, DateTimeOffset expectedUpdatedAt, CancellationToken ct);
    Task DeleteAsync(string id, CancellationToken ct);
    Task SetChannelsAsync(string ruleId, IReadOnlyList<string> channelIds, CancellationToken ct);
    Task<IReadOnlyList<string>> GetChannelIdsAsync(string ruleId, CancellationToken ct);
}

/// <summary>Notification channels. Secrets live in the vault; this store keeps
/// metadata only.</summary>
public interface INotificationChannelStore
{
    Task<IReadOnlyList<ChannelRecord>> ListAsync(CancellationToken ct);
    Task<ChannelRecord?> GetAsync(string id, CancellationToken ct);
    Task<ChannelRecord> CreateAsync(ChannelRecord channel, CancellationToken ct);
    Task<bool> UpdateAsync(ChannelRecord channel, DateTimeOffset expectedUpdatedAt, CancellationToken ct);
    Task DeleteAsync(string id, CancellationToken ct);
    Task MarkTestResultAsync(string id, bool ok, DateTimeOffset at, CancellationToken ct);
}

/// <summary>Durable notification outbox (API.md §9.4).</summary>
public interface IOutboxStore
{
    Task EnqueueAsync(string alertId, string channelId, DateTimeOffset now, CancellationToken ct);
    Task<IReadOnlyList<OutboxItem>> DequeueDueAsync(int max, DateTimeOffset now, CancellationToken ct);
    Task MarkResultAsync(string id, bool success, string? error, DateTimeOffset now, CancellationToken ct);
    Task<long> CountPendingAsync(CancellationToken ct);
}

/// <summary>Configuration audit trail; every config mutation records a row in
/// the same logical operation (API.md §7).</summary>
public interface IAuditStore
{
    Task RecordAsync(string actor, string action, string? targetKind, string? targetId, string? detailJson, DateTimeOffset now, CancellationToken ct);
    Task<IReadOnlyList<AuditEntry>> ListAsync(AuditQuery query, CancellationToken ct);
}

public sealed record AuditQuery(string? Action, string? TargetKind, DateTimeOffset? From, DateTimeOffset? To, int Limit, string? Cursor);

/// <summary>Encrypted-at-rest credential vault (DESIGN §7, API.md §10.1).</summary>
public interface ICredentialVault
{
    /// <summary>Encrypts plaintext and stores a credentials row; returns its id.</summary>
    Task<string> StoreAsync(string kind, string label, string plaintextJson, CancellationToken ct);

    /// <summary>Decrypts and returns the plaintext, or null when missing.</summary>
    Task<string?> LoadAsync(string id, CancellationToken ct);

    /// <summary>Re-encrypts an existing credential (rotation).</summary>
    Task UpdateAsync(string id, string plaintextJson, CancellationToken ct);

    Task DeleteAsync(string id, CancellationToken ct);
    Task<IReadOnlyList<CredentialMeta>> ListAsync(CancellationToken ct);
}

/// <summary>Revocable single-admin web sessions (API.md §8.2).</summary>
public interface ISessionStore
{
    /// <summary>Creates a session; returns the opaque id (server keeps the hash).</summary>
    Task<string> CreateAsync(DateTimeOffset now, TimeSpan lifetime, CancellationToken ct);

    /// <summary>Validates a session id; slides the expiry and last_seen.</summary>
    Task<WebSession?> ValidateAsync(string sessionId, DateTimeOffset now, CancellationToken ct);

    Task RevokeAsync(string sessionId, CancellationToken ct);
    Task CleanupExpiredAsync(DateTimeOffset now, CancellationToken ct);
}

/// <summary>Passkey credentials (API.md §8.1).</summary>
public interface IPasskeyStore
{
    Task<IReadOnlyList<PasskeyRecord>> ListAsync(CancellationToken ct);
    Task<PasskeyRecord?> GetAsync(string id, CancellationToken ct);
    Task<PasskeyRecord?> GetByCredentialIdAsync(string credentialId, CancellationToken ct);
    Task AddAsync(PasskeyRecord passkey, CancellationToken ct);
    Task RemoveAsync(string id, CancellationToken ct);
    Task UpdateSignCountAsync(string id, uint signCount, DateTimeOffset at, CancellationToken ct);
    Task<int> CountAsync(CancellationToken ct);
}

/// <summary>WebAuthn ceremony challenge state (API.md §8.1): short-lived,
/// single-use, bound to the intended operation.</summary>
public interface ICeremonyStore
{
    Task SaveAsync(string challengeHash, string operation, string optionsJson, DateTimeOffset now, TimeSpan lifetime, CancellationToken ct);
    /// <summary>Single-use take: returns options JSON and removes the row.</summary>
    Task<string?> TakeAsync(string challengeHash, string operation, DateTimeOffset now, CancellationToken ct);
    Task CleanupExpiredAsync(DateTimeOffset now, CancellationToken ct);
}

public interface ISettingsStore
{
    Task<string?> GetAsync(string key, CancellationToken ct);
    Task SetAsync(string key, string value, DateTimeOffset now, CancellationToken ct);
}

public interface ISavedSearchStore
{
    Task<IReadOnlyList<SavedSearchRecord>> ListAsync(CancellationToken ct);
    Task<SavedSearchRecord?> GetAsync(string id, CancellationToken ct);
    Task<SavedSearchRecord> CreateAsync(SavedSearchRecord search, CancellationToken ct);
    Task<bool> UpdateAsync(SavedSearchRecord search, DateTimeOffset expectedUpdatedAt, CancellationToken ct);
    Task DeleteAsync(string id, CancellationToken ct);
}

/// <summary>Maintenance windows; host-scoped or fleet-wide (API.md §7.3).</summary>
public interface IMaintenanceWindowStore
{
    Task<IReadOnlyList<MaintenanceWindowRecord>> ListAsync(CancellationToken ct);
    Task<MaintenanceWindowRecord?> GetAsync(string id, CancellationToken ct);
    Task<MaintenanceWindowRecord> CreateAsync(MaintenanceWindowRecord window, CancellationToken ct);
    Task<bool> UpdateAsync(MaintenanceWindowRecord window, DateTimeOffset expectedUpdatedAt, CancellationToken ct);
    Task DeleteAsync(string id, CancellationToken ct);
    Task<bool> IsInWindowAsync(string? hostId, DateTimeOffset at, CancellationToken ct);
    Task<IReadOnlyList<MaintenanceWindowRecord>> ActiveWindowsAsync(DateTimeOffset at, CancellationToken ct);
    Task DeleteExpiredAsync(DateTimeOffset now, CancellationToken ct);
}

/// <summary>VACUUM INTO snapshot backups + retention ladder (API.md §9.5).</summary>
public interface IBackupStore
{
    Task<BackupResult> CreateSnapshotAsync(DateTimeOffset now, CancellationToken ct);
    Task<IReadOnlyList<BackupInfo>> ListAsync(CancellationToken ct);
    Task PruneAsync(DateTimeOffset now, CancellationToken ct);
}

/// <summary>Vendor-neutral hardware provider boundary (API.md §9.1).</summary>
public interface IHardwareProvider
{
    Task<HardwarePollResult> PollAsync(HardwarePollTarget target, CancellationToken ct);
}

public sealed record HardwarePollTarget(
    string HostId,
    string Name,
    string BaseUrl,
    string Username,
    string Password);

public sealed record HardwarePollResult(
    bool Success,
    DateTimeOffset PolledAt,
    string RollupState,
    IReadOnlyList<ComponentRecord> Components,
    IReadOnlyList<MetricRecord> Metrics,
    string? Error);

/// <summary>Notification provider boundary (API.md §9.4). The provider
/// receives the decrypted channel configuration JSON.</summary>
public interface INotifier
{
    string Kind { get; }
    Task<NotificationResult> SendAsync(NotificationMessage message, string configJson, CancellationToken ct);
}

public sealed record NotificationMessage(string Title, string Text, string Severity, string? ChannelName);

public sealed record NotificationResult(bool Ok, string? Error, string ProviderClass);

/// <summary>Alert evaluation entry point shared by ingest, pollers and the
/// heartbeat monitor (API.md §9.3).</summary>
public interface IAlertEvaluator
{
    Task OnEventsAcceptedAsync(string sourceId, IReadOnlyList<ValidatedLogItem> items, CancellationToken ct);
    Task OnHealthStateChangedAsync(string hostId, string rollupState, IReadOnlyList<ComponentRecord> components, DateTimeOffset at, CancellationToken ct);

    /// <summary>silent=true carries the evaluated rule (per-rule threshold from
    /// the monitor); silent=false with ruleId null clears heartbeat silence
    /// for every rule of that source (telemetry arrival path).</summary>
    Task OnHeartbeatSilenceChangedAsync(string? ruleId, string sourceId, bool silent, DateTimeOffset at, CancellationToken ct);
    Task OnThresholdsAsync(string hostId, IReadOnlyList<MetricRecord> metrics, DateTimeOffset at, CancellationToken ct);
    Task ReconcileAsync(CancellationToken ct);
}

/// <summary>Credentials table access for the vault (the AES-GCM wrapping lives
/// in Infrastructure.Security; the blob store is a storage seam).</summary>
public interface ICredentialBlobStore
{
    Task<(string Id, byte[] Blob, int KeyVersion)?> GetAsync(string id, CancellationToken ct);
    Task<string> InsertAsync(string kind, string label, byte[] blob, int keyVersion, DateTimeOffset now, CancellationToken ct);
    Task UpdateAsync(string id, byte[] blob, int keyVersion, DateTimeOffset now, CancellationToken ct);
    Task DeleteAsync(string id, CancellationToken ct);
    Task<IReadOnlyList<CredentialMeta>> ListAsync(CancellationToken ct);
}

/// <summary>WebAuthn ceremony orchestration (API.md §8.1). Implemented in the
/// security infrastructure over the Fido2 library.</summary>
public interface IWebAuthnService
{
    /// <summary>First-run registration: allowed without a session only when no
    /// passkey exists and the request comes from the trusted network.
    /// sessionAuthenticated permits additional keys from an authenticated session.</summary>
    Task<object> BeginRegistrationAsync(string? name, bool sessionAuthenticated, string? remoteIp, CancellationToken ct);

    /// <summary>Verifies a registration ceremony; stores the passkey.</summary>
    Task<string> CompleteRegistrationAsync(string responseJson, string origin, CancellationToken ct);

    Task<object> BeginLoginAsync(CancellationToken ct);

    /// <summary>Verifies a login ceremony; creates a web session and returns the
    /// opaque session id (cookie is set by the caller).</summary>
    Task<string> CompleteLoginAsync(string responseJson, string origin, CancellationToken ct);

    Task<IReadOnlyList<PasskeyRecord>> ListPasskeysAsync(CancellationToken ct);

    /// <summary>Removes a passkey; refuses to remove the final usable key.</summary>
    Task RemovePasskeyAsync(string id, CancellationToken ct);

    /// <summary>True when the passkeys table is empty (first-run setup gate).</summary>
    Task<bool> IsSetupRequiredAsync(CancellationToken ct);
}

/// <summary>Outcome of delivering a notification for a channel (used by the
/// channel test endpoint and the dispatcher).</summary>
public interface INotificationSender
{
    Task<NotificationResult> SendToChannelAsync(string channelId, NotificationMessage message, CancellationToken ct);
}

/// <summary>Retention/backup maintenance job (API.md §9.5).</summary>
public interface IMaintenanceJob
{
    Task RunRetentionAsync(DateTimeOffset now, CancellationToken ct);
    Task RunBackupAsync(DateTimeOffset now, CancellationToken ct);
    Task RunCleanupAsync(DateTimeOffset now, CancellationToken ct);
}
