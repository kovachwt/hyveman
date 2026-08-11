using System.Text.Json;
using Hyveman.Application;
using Hyveman.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Hyveman.Tests.Application;

public class RegistrationServiceTests
{
    private sealed class FakeClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }

    private sealed class FakeRegistrationUnit : IRegistrationUnit
    {
        public RegistrationUnitResult Result { get; set; } = new(
            RegistrationStatus.Ok, "src_1", "windows-agent", "HOST01", "agt_fresh",
            [TokenKinds.ScopeIngest], DateTimeOffset.Parse("2024-08-07T15:00:00Z"),
            SourceCreated: true, BoundKind: "windows-agent");

        public string? LastRawToken { get; private set; }
        public string? LastKind { get; private set; }
        public string? LastHostname { get; private set; }

        public Task<RegistrationUnitResult> ExecuteAsync(string rawRegToken, string kind, string hostname,
            DateTimeOffset now, CancellationToken ct)
        {
            LastRawToken = rawRegToken;
            LastKind = kind;
            LastHostname = hostname;
            return Task.FromResult(Result);
        }
    }

    private static (RegistrationService Service, FakeRegistrationUnit Unit, List<AuditEntry> Audit) Build()
    {
        var unit = new FakeRegistrationUnit();
        var audit = new List<AuditEntry>();
        var svc = new RegistrationService(unit, new FakeAudit(audit),
            new FakeClock(DateTimeOffset.Parse("2024-08-07T15:00:00Z")),
            NullLogger<RegistrationService>.Instance);
        return (svc, unit, audit);
    }

    private sealed class FakeAudit(List<AuditEntry> sink) : IAuditStore
    {
        public Task RecordAsync(string? actor, string action, string? targetKind, string? targetId, string? detailJson, DateTimeOffset now, CancellationToken ct)
        {
            sink.Add(new AuditEntry(sink.Count + 1, now, actor, action, targetKind, targetId, detailJson));
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<AuditEntry>> ListAsync(AuditQuery query, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<AuditEntry>>(sink);
    }

    [Fact]
    public async Task Register_NewSource_MintsTokenAndConsumesReg()
    {
        var (svc, unit, audit) = Build();

        var outcome = await svc.RegisterAsync("reg_x", "windows-agent", "HOST01", "0.1.0", "17763", CancellationToken.None);

        Assert.Equal("agt_fresh", outcome.RawToken);
        Assert.Equal(["ingest"], outcome.Scopes);
        Assert.Equal("windows-agent", outcome.Kind);
        Assert.Equal("reg_x", unit.LastRawToken);
        Assert.Equal("windows-agent", unit.LastKind);
        Assert.Equal("HOST01", unit.LastHostname);
        Assert.Contains(audit, a => a.Action == "source.created");
        Assert.Contains(audit, a => a.Action == "token.minted");
    }

    [Fact]
    public async Task Register_ExistingSource_ReusesIt_AndSkipsCreateAudit()
    {
        var (svc, unit, audit) = Build();
        unit.Result = unit.Result with { SourceId = "src_existing", SourceCreated = false };

        var outcome = await svc.RegisterAsync("reg_x", "windows-agent", "HOST01", null, null, CancellationToken.None);

        Assert.Equal("src_existing", outcome.SourceId);   // reinstall path (PROTOCOL §5.2)
        Assert.DoesNotContain(audit, a => a.Action == "source.created");
        Assert.Contains(audit, a => a.Action == "token.minted");
    }

    [Theory]
    [InlineData(RegistrationStatus.Consumed, 410, "token_consumed")]
    [InlineData(RegistrationStatus.Revoked, 401, "token_revoked")]
    [InlineData(RegistrationStatus.Expired, 401, "token_revoked")]
    [InlineData(RegistrationStatus.UnknownToken, 401, "token_invalid")]
    public async Task Register_UnitFailures_MapToProtocolErrors(RegistrationStatus status, int httpStatus, string code)
    {
        var (svc, unit, _) = Build();
        unit.Result = unit.Result with { Status = status };

        var ex = await Assert.ThrowsAsync<RegistrationException>(
            () => svc.RegisterAsync("reg_x", "windows-agent", "HOST01", null, null, CancellationToken.None));
        Assert.Equal(httpStatus, ex.Status);
        Assert.Equal(code, ex.Code);
    }

    [Fact]
    public async Task Register_KindMismatch_Throws400()
    {
        var (svc, unit, _) = Build();
        unit.Result = unit.Result with { Status = RegistrationStatus.KindMismatch, BoundKind = "syslog-feed" };

        var ex = await Assert.ThrowsAsync<RegistrationException>(
            () => svc.RegisterAsync("reg_x", "windows-agent", "HOST01", null, null, CancellationToken.None));
        Assert.Equal(400, ex.Status);
        Assert.Equal("invalid_request", ex.Code);
        Assert.Contains("syslog-feed", ex.Message);
    }

    [Fact]
    public async Task Register_UnknownKind_Throws400()
    {
        var (svc, _, _) = Build();
        var ex = await Assert.ThrowsAsync<RegistrationException>(
            () => svc.RegisterAsync("reg_x", "not-a-kind", "HOST01", null, null, CancellationToken.None));
        Assert.Equal(400, ex.Status);
    }
}

public class AlertEvaluatorTests
{
    private sealed class InMemoryRuleStore : IRuleStore
    {
        public List<RuleRecord> Rules { get; } = [];
        public Dictionary<string, List<string>> Channels { get; } = [];
        public Task<IReadOnlyList<RuleRecord>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<RuleRecord>>(Rules);
        public Task<RuleRecord?> GetAsync(string id, CancellationToken ct) => Task.FromResult(Rules.FirstOrDefault(r => r.Id == id));
        public Task<RuleRecord> CreateAsync(RuleRecord rule, CancellationToken ct) { Rules.Add(rule); return Task.FromResult(rule); }
        public Task<bool> UpdateAsync(RuleRecord rule, DateTimeOffset expectedUpdatedAt, CancellationToken ct) => Task.FromResult(true);
        public Task DeleteAsync(string id, CancellationToken ct) { Rules.RemoveAll(r => r.Id == id); return Task.CompletedTask; }
        public Task SetChannelsAsync(string ruleId, IReadOnlyList<string> channelIds, CancellationToken ct)
        { Channels[ruleId] = channelIds.ToList(); return Task.CompletedTask; }
        public Task<IReadOnlyList<string>> GetChannelIdsAsync(string ruleId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<string>>(Channels.GetValueOrDefault(ruleId) ?? []);
    }

    private sealed class InMemoryAlertStore : IAlertStore
    {
        public List<AlertRecord> Alerts { get; } = [];
        public Task<AlertRecord?> FindLiveAsync(string key, CancellationToken ct)
            => Task.FromResult(Alerts.FirstOrDefault(a => a.Key == key && AlertStatuses.Live.Contains(a.Status)));
        public Task<AlertRecord?> GetLatestAsync(string key, CancellationToken ct)
            => Task.FromResult(Alerts.Where(a => a.Key == key).OrderByDescending(a => a.LastSeen).FirstOrDefault());
        public Task<AlertRecord?> GetAsync(string id, CancellationToken ct) => Task.FromResult(Alerts.FirstOrDefault(a => a.Id == id));
        public Task CreateAsync(AlertRecord alert, CancellationToken ct) { Alerts.Add(alert); return Task.CompletedTask; }
        public Task UpdateAsync(AlertRecord alert, CancellationToken ct)
        { var i = Alerts.FindIndex(a => a.Id == alert.Id); if (i >= 0) Alerts[i] = alert; return Task.CompletedTask; }
        public Task<IReadOnlyList<AlertRecord>> ListAsync(AlertQuery query, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<AlertRecord>>(Alerts);
        public Task<IReadOnlyList<AlertRecord>> ListLiveAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<AlertRecord>>(Alerts.Where(a => AlertStatuses.Live.Contains(a.Status)).ToList());
        public Task<long> CountLiveAsync(CancellationToken ct) => Task.FromResult((long)Alerts.Count(a => AlertStatuses.Live.Contains(a.Status)));
        public Task<long> CountUnacknowledgedAsync(CancellationToken ct) => Task.FromResult((long)Alerts.Count(a => a.Status == AlertStatuses.Active));
        public Task ResolveForHostAsync(string hostId, DateTimeOffset at, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class NoopHosts : IHostStore
    {
        public Task<IReadOnlyList<HostRecord>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<HostRecord>>([]);
        public Task<HostRecord?> GetAsync(string id, CancellationToken ct) => Task.FromResult<HostRecord?>(null);
        public Task<HostRecord?> GetBySourceAsync(string sourceId, CancellationToken ct) => Task.FromResult<HostRecord?>(null);
        public Task<HostRecord> CreateAsync(HostRecord host, CancellationToken ct) => Task.FromResult(host);
        public Task<bool> UpdateAsync(HostRecord host, DateTimeOffset expectedUpdatedAt, CancellationToken ct) => Task.FromResult(true);
        public Task DeleteAsync(string id, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class NoopSources : ISourceStore
    {
        public Task<Source?> GetByIdAsync(string id, CancellationToken ct) => Task.FromResult<Source?>(new Source(id, "windows-agent", "HOST01", DateTimeOffset.MinValue));
        public Task<Source?> GetByKindNameAsync(string kind, string name, CancellationToken ct) => Task.FromResult<Source?>(null);
        public Task<Source> CreateAsync(string kind, string name, DateTimeOffset now, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<Source>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<Source>>([]);
        public Task DeleteAsync(string id, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class NoopHealth : IHealthStore
    {
        public List<ComponentRecord> Components { get; } = [];
        public List<VmRecord> Vms { get; } = [];
        public Task ReplaceComponentsAsync(string hostId, IReadOnlyList<ComponentRecord> components, CancellationToken ct)
        {
            Components.Clear();
            Components.AddRange(components);
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<ComponentRecord>> GetComponentsAsync(string hostId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ComponentRecord>>(Components);
        public Task AddSnapshotAsync(string hostId, DateTimeOffset time, string rollupState, string componentsJson, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<HealthSnapshotRecord>> GetSnapshotsAsync(string hostId, DateTimeOffset? from, DateTimeOffset? to, int limit, CancellationToken ct) => Task.FromResult<IReadOnlyList<HealthSnapshotRecord>>([]);
        public Task AddMetricsAsync(string hostId, DateTimeOffset time, IReadOnlyList<MetricRecord> metrics, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<MetricRecord>> GetLatestMetricsAsync(string hostId, int maxPerName, CancellationToken ct) => Task.FromResult<IReadOnlyList<MetricRecord>>([]);
        public Task<IReadOnlyList<MetricRecord>> GetMetricsInRangeAsync(string hostId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct) => Task.FromResult<IReadOnlyList<MetricRecord>>([]);
        public Task UpsertVmsAsync(string hostId, IReadOnlyList<VmRecord> vms, bool stale, DateTimeOffset collectedAt, CancellationToken ct)
        {
            Vms.Clear();
            Vms.AddRange(vms);
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<VmRecord>> GetVmsAsync(string hostId, CancellationToken ct) => Task.FromResult<IReadOnlyList<VmRecord>>(Vms);
        public Task<long> PurgeMetricsOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct) => Task.FromResult(0L);
        public Task<long> PurgeSnapshotsOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct) => Task.FromResult(0L);
        public Task<long> PurgeVmsAsync(DateTimeOffset cutoff, CancellationToken ct) => Task.FromResult(0L);
    }

    private sealed class NoopAgentStatus : IAgentStatusStore
    {
        public Task<AgentStatusRow?> GetAsync(string sourceId, CancellationToken ct) => Task.FromResult<AgentStatusRow?>(null);
        public Task<IReadOnlyList<AgentStatusRow>> ListAllAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<AgentStatusRow>>([]);
        public Task<bool> ApplyHeartbeatAsync(string sourceId, HeartbeatPayload hb, DateTimeOffset receivedAt, CancellationToken ct) => Task.FromResult(true);
        public Task<bool> ApplyFactsAsync(string sourceId, FactsPayload facts, DateTimeOffset receivedAt, CancellationToken ct) => Task.FromResult(true);
    }

    private sealed class NoopWindows : IMaintenanceWindowStore
    {
        public Task<IReadOnlyList<MaintenanceWindowRecord>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<MaintenanceWindowRecord>>([]);
        public Task<MaintenanceWindowRecord?> GetAsync(string id, CancellationToken ct) => Task.FromResult<MaintenanceWindowRecord?>(null);
        public Task<MaintenanceWindowRecord> CreateAsync(MaintenanceWindowRecord window, CancellationToken ct) => Task.FromResult(window);
        public Task<bool> UpdateAsync(MaintenanceWindowRecord window, DateTimeOffset expectedUpdatedAt, CancellationToken ct) => Task.FromResult(true);
        public Task DeleteAsync(string id, CancellationToken ct) => Task.CompletedTask;
        public Task<bool> IsInWindowAsync(string? hostId, DateTimeOffset at, CancellationToken ct) => Task.FromResult(false);
        public Task<IReadOnlyList<MaintenanceWindowRecord>> ActiveWindowsAsync(DateTimeOffset at, CancellationToken ct) => Task.FromResult<IReadOnlyList<MaintenanceWindowRecord>>([]);
        public Task DeleteExpiredAsync(DateTimeOffset now, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class NoopOutbox : IOutboxStore
    {
        public List<string> Enqueued { get; } = [];
        public Task EnqueueAsync(string alertId, string channelId, DateTimeOffset now, CancellationToken ct) { Enqueued.Add($"{alertId}|{channelId}"); return Task.CompletedTask; }
        public Task<IReadOnlyList<OutboxItem>> DequeueDueAsync(int max, DateTimeOffset now, CancellationToken ct) => Task.FromResult<IReadOnlyList<OutboxItem>>([]);
        public Task MarkResultAsync(string id, bool success, string? error, DateTimeOffset now, CancellationToken ct) => Task.CompletedTask;
        public Task<long> CountPendingAsync(CancellationToken ct) => Task.FromResult(0L);
    }

    private sealed class NoopAudit : IAuditStore
    {
        public Task RecordAsync(string? actor, string action, string? targetKind, string? targetId, string? detailJson, DateTimeOffset now, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<AuditEntry>> ListAsync(AuditQuery query, CancellationToken ct) => Task.FromResult<IReadOnlyList<AuditEntry>>([]);
    }

    private static (AlertEvaluatorService Eval, InMemoryRuleStore Rules, InMemoryAlertStore Alerts, NoopOutbox Outbox, NoopHealth Health) Build(DateTimeOffset now)
    {
        var rules = new InMemoryRuleStore();
        var alerts = new InMemoryAlertStore();
        var outbox = new NoopOutbox();
        var health = new NoopHealth();
        var eval = new AlertEvaluatorService(rules, alerts, new NoopHosts(), new NoopSources(), health,
            new NoopAgentStatus(), new NoopWindows(), outbox, new FixedClock(now),
            NullLogger<AlertEvaluatorService>.Instance);
        return (eval, rules, alerts, outbox, health);
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }

    private static ValidatedLogItem EventItem(string channel, long eventId, int severity, string message) => new(
        "System", "1", DateTimeOffset.UtcNow, severity, "Microsoft-Windows-Kernel-Power", message,
        "{}", null, channel, eventId, 0, 0, null);

    private static ValidatedLogItem LogonItem(string user, long eventId, string? logonType = null, string channel = "Security") => new(
        "Security", $"e{eventId}:{user}", DateTimeOffset.Parse("2024-08-07T15:00:00Z"), 4,
        "Microsoft-Windows-Security-Auditing", "logon",
        logonType is null
            ? $"{{\"channel\":\"Security\",\"event_data\":{{\"TargetUserName\":\"{user}\"}}}}"
            : $"{{\"channel\":\"Security\",\"event_data\":{{\"TargetUserName\":\"{user}\",\"LogonType\":\"{logonType}\"}}}}",
        null, channel, eventId, 0, 0, null);

    [Fact]
    public async Task EventRule_Fires_Deduplicates_ThenResolves()
    {
        var now = DateTimeOffset.Parse("2024-08-07T15:00:00Z");
        var (eval, rules, alerts, _, _) = Build(now);
        rules.Rules.Add(new RuleRecord("r1", "6008", RuleTypes.Event,
            """{"channel":"System","eventIds":[6008],"severityMin":3}""", "warning", 0, null, true, now, now));

        await eval.OnEventsAcceptedAsync("src_1", [EventItem("System", 6008, 2, "boom")], CancellationToken.None);
        var live = Assert.Single(alerts.Alerts.Where(a => a.Status != AlertStatuses.Resolved));
        Assert.Equal("r1", live.RuleId);
        Assert.Equal("src_1", live.SourceId);
        Assert.Equal("warning", live.Severity);

        // Second matching event bumps count, does not create a new alert.
        await eval.OnEventsAcceptedAsync("src_1", [EventItem("System", 6008, 2, "boom again")], CancellationToken.None);
        var live2 = Assert.Single(alerts.Alerts.Where(a => a.Status != AlertStatuses.Resolved));
        Assert.Equal(2, live2.Count);
        Assert.Equal(1, alerts.Alerts.Count);

        // Non-matching event does nothing.
        await eval.OnEventsAcceptedAsync("src_1", [EventItem("Application", 1000, 2, "irrelevant")], CancellationToken.None);
        Assert.Equal(1, alerts.Alerts.Count);
    }

    [Fact]
    public async Task EventRule_AutoResolveAfterWindow_Resolves_AndBumpRestartsTimer()
    {
        var now = DateTimeOffset.Parse("2024-08-07T15:00:00Z");
        var (eval, rules, alerts, _, _) = Build(now);
        // AutoResolveAfterS = 300s: a fire-and-forget event rule that opts
        // into the timeout instead of a manual ack.
        rules.Rules.Add(new RuleRecord("r1", "6008", RuleTypes.Event,
            """{"channel":"System","eventIds":[6008]}""", "warning", 0, 300, true, now, now));

        await eval.OnEventsAcceptedAsync("src_1", [EventItem("System", 6008, 2, "boom")], CancellationToken.None);
        var live = Assert.Single(alerts.Alerts);
        Assert.Equal(AlertStatuses.Active, live.Status);

        // Not yet due: 299s < 300s window.
        await eval.AutoResolveDueAsync(now.AddSeconds(299), CancellationToken.None);
        Assert.Equal(AlertStatuses.Active, (await alerts.GetAsync(live.Id, CancellationToken.None))!.Status);

        // A new occurrence bumps last_seen (the evaluator's bump path sets
        // LastSeen to the occurrence time) and restarts the timer: 300s after
        // the first event but only 1s after the latest one → still live.
        await eval.OnEventsAcceptedAsync("src_1", [EventItem("System", 6008, 2, "boom again")], CancellationToken.None);
        var bumped = (await alerts.GetAsync(live.Id, CancellationToken.None))!;
        Assert.Equal(2, bumped.Count);
        await alerts.UpdateAsync(bumped with { LastSeen = now.AddSeconds(300) }, CancellationToken.None);
        await eval.AutoResolveDueAsync(now.AddSeconds(301), CancellationToken.None);
        Assert.Equal(AlertStatuses.Active, (await alerts.GetAsync(live.Id, CancellationToken.None))!.Status);

        // Quiet for the full window after the last occurrence → resolved.
        await eval.AutoResolveDueAsync(now.AddSeconds(601), CancellationToken.None);
        var resolved = (await alerts.GetAsync(live.Id, CancellationToken.None))!;
        Assert.Equal(AlertStatuses.Resolved, resolved.Status);
        Assert.NotNull(resolved.ResolvedAt);
    }

    [Fact]
    public async Task AutoResolve_SkipsRulesWithoutTimeout_AndResolvesAcknowledged()
    {
        var now = DateTimeOffset.Parse("2024-08-07T15:00:00Z");
        var (eval, rules, alerts, _, _) = Build(now);
        // No timeout on r1 → never auto-resolved, even when long quiet.
        rules.Rules.Add(new RuleRecord("r1", "6008", RuleTypes.Event,
            """{"channel":"System","eventIds":[6008]}""", "warning", 0, null, true, now, now));
        // r2 has a timeout; its alert is acknowledged — the timeout still
        // resolves it (that is the point: no manual ack needed).
        rules.Rules.Add(new RuleRecord("r2", "6008 acked", RuleTypes.Event,
            """{"channel":"System","eventIds":[6008]}""", "warning", 0, 60, true, now, now));

        await eval.OnEventsAcceptedAsync("src_1", [EventItem("System", 6008, 2, "boom")], CancellationToken.None);
        var r1 = Assert.Single(alerts.Alerts.Where(a => a.RuleId == "r1"));
        var r2 = Assert.Single(alerts.Alerts.Where(a => a.RuleId == "r2"));
        await alerts.UpdateAsync(r2 with { AckAt = now, AckReason = "seen", Status = AlertStatuses.Acknowledged }, CancellationToken.None);

        await eval.AutoResolveDueAsync(now.AddHours(1), CancellationToken.None);
        Assert.Equal(AlertStatuses.Active, (await alerts.GetAsync(r1.Id, CancellationToken.None))!.Status);
        var resolved2 = (await alerts.GetAsync(r2.Id, CancellationToken.None))!;
        Assert.Equal(AlertStatuses.Resolved, resolved2.Status);
        Assert.NotNull(resolved2.ResolvedAt);
    }

    [Fact]
    public async Task EventRule_SeverityMin_Filters()
    {
        var now = DateTimeOffset.Parse("2024-08-07T15:00:00Z");
        var (eval, rules, alerts, _, _) = Build(now);
        rules.Rules.Add(new RuleRecord("r1", "critical only", RuleTypes.Event,
            """{"channel":"System","severityMin":2}""", "critical", 0, null, true, now, now));

        await eval.OnEventsAcceptedAsync("src_1", [EventItem("System", 1, 4, "info event")], CancellationToken.None);
        Assert.Empty(alerts.Alerts);

        await eval.OnEventsAcceptedAsync("src_1", [EventItem("System", 2, 2, "error event")], CancellationToken.None);
        Assert.Single(alerts.Alerts);
    }

    [Fact]
    public async Task HealthRule_ComponentTransition_FiresAndResolves()
    {
        var now = DateTimeOffset.Parse("2024-08-07T15:00:00Z");
        var (eval, rules, alerts, _, health) = Build(now);
        rules.Rules.Add(new RuleRecord("r1", "disk health", RuleTypes.Health,
            """{"componentTypes":["disk"],"states":["warning","critical"],"includeRollup":false}""",
            "critical", 0, null, true, now, now));

        var disk = new ComponentRecord("h1", ComponentTypes.Disk, "Physical Disk 1", HealthState.Ok, null, now);
        await eval.OnHealthStateChangedAsync("h1", "ok", [disk], now, CancellationToken.None);
        Assert.Empty(alerts.Alerts);

        var bad = disk with { State = HealthState.Warning };
        await eval.OnHealthStateChangedAsync("h1", "warning", [bad], now, CancellationToken.None);
        var alert = Assert.Single(alerts.Alerts.Where(a => a.Status != AlertStatuses.Resolved));
        Assert.Equal("h1", alert.HostId);
        Assert.Contains("Physical Disk 1", alert.Title);

        // The evaluator reads the previous poll from the store (D3); the poller
        // replaces components after evaluation, so the test mirrors that order.
        await health.ReplaceComponentsAsync("h1", [bad], CancellationToken.None);

        // Returning to OK resolves the occurrence.
        await eval.OnHealthStateChangedAsync("h1", "ok", [disk], now.AddMinutes(1), CancellationToken.None);
        await health.ReplaceComponentsAsync("h1", [disk], CancellationToken.None);
        Assert.Equal(AlertStatuses.Resolved, alerts.Alerts.Single(a => a.Id == alert.Id).Status);
    }

    [Fact]
    public async Task Cooldown_SuppressesRapidRefire()
    {
        var now = DateTimeOffset.Parse("2024-08-07T15:00:00Z");
        var (eval, rules, alerts, _, _) = Build(now);
        rules.Rules.Add(new RuleRecord("r1", "6008", RuleTypes.Event,
            """{"channel":"System","eventIds":[6008]}""", "warning", 3600, null, true, now, now));

        await eval.OnEventsAcceptedAsync("src_1", [EventItem("System", 6008, 2, "a")], CancellationToken.None);
        Assert.Single(alerts.Alerts);

        // Resolve, then re-fire within the cooldown window: suppressed entirely.
        var live = alerts.Alerts.Single();
        await alerts.UpdateAsync(live with { Status = AlertStatuses.Resolved, ResolvedAt = now }, CancellationToken.None);
        await eval.OnEventsAcceptedAsync("src_1", [EventItem("System", 6008, 2, "b")], CancellationToken.None);
        Assert.Equal(0, alerts.Alerts.Count(a => a.Status == AlertStatuses.Active));
        Assert.Equal(1, alerts.Alerts.Count); // only the resolved occurrence
    }

    [Fact]
    public async Task HeartbeatSilence_FiresAndClears()
    {
        var now = DateTimeOffset.Parse("2024-08-07T15:00:00Z");
        var (eval, rules, alerts, _, _) = Build(now);
        rules.Rules.Add(new RuleRecord("r1", "silent", RuleTypes.Heartbeat,
            """{"silenceAfterS":300}""", "warning", 0, null, true, now, now));

        await eval.OnHeartbeatSilenceChangedAsync("r1", "src_1", silent: true, now, CancellationToken.None);
        var alert = Assert.Single(alerts.Alerts.Where(a => a.Status != AlertStatuses.Resolved));
        Assert.Contains("silent", alert.Title, StringComparison.OrdinalIgnoreCase);

        await eval.OnHeartbeatSilenceChangedAsync(null, "src_1", silent: false, now, CancellationToken.None);
        Assert.Equal(AlertStatuses.Resolved, alerts.Alerts.Single().Status);
    }

    [Fact]
    public async Task ThresholdRule_FiresOnCrossing()
    {
        var now = DateTimeOffset.Parse("2024-08-07T15:00:00Z");
        var (eval, rules, alerts, _, _) = Build(now);
        rules.Rules.Add(new RuleRecord("r1", "hot", RuleTypes.Threshold,
            """{"metric":"temperature:System Board Inlet Temp","comparator":"gt","value":45}""",
            "warning", 0, null, true, now, now));

        await eval.OnThresholdsAsync("h1", [new MetricRecord("h1", "temperature:System Board Inlet Temp", 40, "C", now)], now, CancellationToken.None);
        Assert.Empty(alerts.Alerts);

        await eval.OnThresholdsAsync("h1", [new MetricRecord("h1", "temperature:System Board Inlet Temp", 46, "C", now)], now, CancellationToken.None);
        Assert.Single(alerts.Alerts);

        await eval.OnThresholdsAsync("h1", [new MetricRecord("h1", "temperature:System Board Inlet Temp", 44, "C", now)], now, CancellationToken.None);
        Assert.Equal(AlertStatuses.Resolved, alerts.Alerts.Single().Status);
    }

    private static VmRecord Vm(string name, string state, bool? heartbeatOk) =>
        new("h1", name, state, heartbeatOk, null, null, LastSeen: DateTimeOffset.Parse("2024-08-07T15:00:00Z"), Stale: false, CollectedAt: DateTimeOffset.Parse("2024-08-07T15:00:00Z"));

    private static VmRecord ReplVm(string name, string? health, string? state) =>
        new("h1", name, "on", true, null, null, LastSeen: DateTimeOffset.Parse("2024-08-07T15:00:00Z"), Stale: false,
            CollectedAt: DateTimeOffset.Parse("2024-08-07T15:00:00Z"),
            ReplicationState: state, ReplicationHealth: health,
            ReplicationLastApplyTime: DateTimeOffset.Parse("2024-08-07T15:00:00Z"));

    [Fact]
    public async Task VmHeartbeatRule_OkToLost_FiresAndResolves()
    {
        var now = DateTimeOffset.Parse("2024-08-07T15:00:00Z");
        var (eval, rules, alerts, _, health) = Build(now);
        rules.Rules.Add(new RuleRecord("r1", "vm down", RuleTypes.VmHeartbeat, "{}", "critical", 0, null, true, now, now));

        // Store the previous facts (the telemetry path evaluates BEFORE the
        // latest-wins upsert, so the test mirrors that order).
        await health.UpsertVmsAsync("h1", [Vm("web01", "on", true)], stale: false, now, CancellationToken.None);

        // OK → lost while running: fires.
        await eval.OnVmsChangedAsync("h1", [Vm("web01", "on", false)], now, CancellationToken.None);
        var alert = Assert.Single(alerts.Alerts.Where(a => a.Status != AlertStatuses.Resolved));
        Assert.Equal("h1", alert.HostId);
        Assert.Contains("web01", alert.Title);
        await health.UpsertVmsAsync("h1", [Vm("web01", "on", false)], stale: false, now, CancellationToken.None);

        // Still lost on the next scan: no new alert, count stays one per episode.
        await eval.OnVmsChangedAsync("h1", [Vm("web01", "on", false)], now.AddSeconds(60), CancellationToken.None);
        Assert.Equal(1, alerts.Alerts.Count(a => a.Status != AlertStatuses.Resolved));
        Assert.Equal(1, alerts.Alerts.Single().Count);

        // Heartbeat returns: resolved.
        await eval.OnVmsChangedAsync("h1", [Vm("web01", "on", true)], now.AddMinutes(2), CancellationToken.None);
        Assert.Equal(AlertStatuses.Resolved, alerts.Alerts.Single().Status);
    }

    [Fact]
    public async Task VmHeartbeatRule_NoFire_ForVmsWithoutPriorOkHeartbeat()
    {
        var now = DateTimeOffset.Parse("2024-08-07T15:00:00Z");
        var (eval, rules, alerts, _, health) = Build(now);
        rules.Rules.Add(new RuleRecord("r1", "vm down", RuleTypes.VmHeartbeat, "{}", "critical", 0, null, true, now, now));

        // VM that never had an OK heartbeat (prev unknown/off): no fire.
        await health.UpsertVmsAsync("h1", [Vm("web01", "off", null)], stale: false, now, CancellationToken.None);
        await eval.OnVmsChangedAsync("h1", [Vm("web01", "on", false)], now, CancellationToken.None);
        Assert.Empty(alerts.Alerts);

        // Power-off of a heartbeating VM is not a loss either.
        await health.UpsertVmsAsync("h1", [Vm("web01", "on", true)], stale: false, now, CancellationToken.None);
        await eval.OnVmsChangedAsync("h1", [Vm("web01", "off", null)], now, CancellationToken.None);
        Assert.Empty(alerts.Alerts);
    }

    [Fact]
    public async Task VmHeartbeatRule_PowerOff_ResolvesOpenAlert()
    {
        var now = DateTimeOffset.Parse("2024-08-07T15:00:00Z");
        var (eval, rules, alerts, _, health) = Build(now);
        rules.Rules.Add(new RuleRecord("r1", "vm down", RuleTypes.VmHeartbeat, "{}", "critical", 0, null, true, now, now));

        await health.UpsertVmsAsync("h1", [Vm("web01", "on", true)], stale: false, now, CancellationToken.None);
        await eval.OnVmsChangedAsync("h1", [Vm("web01", "on", false)], now, CancellationToken.None);
        Assert.Single(alerts.Alerts.Where(a => a.Status != AlertStatuses.Resolved));

        // Graceful shutdown: heartbeat loss is moot, alert resolves.
        await eval.OnVmsChangedAsync("h1", [Vm("web01", "off", null)], now.AddMinutes(1), CancellationToken.None);
        Assert.Equal(AlertStatuses.Resolved, alerts.Alerts.Single().Status);
    }

    [Fact]
    public async Task VmReplicationRule_Default_HealthWarningFiresAndResolves()
    {
        var now = DateTimeOffset.Parse("2024-08-07T15:00:00Z");
        var (eval, rules, alerts, _, _) = Build(now);
        // Default match ({}): healths defaults to warning+critical.
        rules.Rules.Add(new RuleRecord("r1", "repl bad", RuleTypes.VmReplication, "{}", "critical", 0, null, true, now, now));

        // Healthy replication: no fire.
        await eval.OnVmReplicationChangedAsync("h1", [ReplVm("web01", "ok", "enabled")], now, CancellationToken.None);
        Assert.Empty(alerts.Alerts);

        // Health drops to warning: fires once.
        await eval.OnVmReplicationChangedAsync("h1", [ReplVm("web01", "warning", "replication_in_progress")], now.AddSeconds(30), CancellationToken.None);
        var alert = Assert.Single(alerts.Alerts.Where(a => a.Status != AlertStatuses.Resolved));
        Assert.Equal("h1", alert.HostId);
        Assert.Contains("web01", alert.Title);
        Assert.Contains("warning", alert.Detail);

        // Still warning on the next scan: count stays one per episode.
        await eval.OnVmReplicationChangedAsync("h1", [ReplVm("web01", "warning", "replication_in_progress")], now.AddSeconds(60), CancellationToken.None);
        Assert.Equal(1, alerts.Alerts.Count(a => a.Status != AlertStatuses.Resolved));
        Assert.Equal(1, alerts.Alerts.Single().Count);

        // Back to ok: resolved.
        await eval.OnVmReplicationChangedAsync("h1", [ReplVm("web01", "ok", "enabled")], now.AddMinutes(2), CancellationToken.None);
        Assert.Equal(AlertStatuses.Resolved, alerts.Alerts.Single().Status);
    }

    [Fact]
    public async Task VmReplicationRule_CriticalHealth_FiresAndResolves_WhenRecovered()
    {
        var now = DateTimeOffset.Parse("2024-08-07T15:00:00Z");
        var (eval, rules, alerts, _, _) = Build(now);
        rules.Rules.Add(new RuleRecord("r1", "repl down", RuleTypes.VmReplication,
            """{"healths":["critical"]}""", "critical", 0, null, true, now, now));

        await eval.OnVmReplicationChangedAsync("h1", [ReplVm("web01", "critical", "error")], now, CancellationToken.None);
        Assert.Single(alerts.Alerts.Where(a => a.Status != AlertStatuses.Resolved));

        // Warning is not in the rule's set: resolves.
        await eval.OnVmReplicationChangedAsync("h1", [ReplVm("web01", "warning", "enabled")], now.AddMinutes(1), CancellationToken.None);
        Assert.Equal(AlertStatuses.Resolved, alerts.Alerts.Single().Status);
    }

    [Fact]
    public async Task VmReplicationRule_StateOnlyRule_FiresOnConfiguredState()
    {
        var now = DateTimeOffset.Parse("2024-08-07T15:00:00Z");
        var (eval, rules, alerts, _, _) = Build(now);
        // healths empty, states=[error]: fires only when the state matches.
        rules.Rules.Add(new RuleRecord("r1", "repl error", RuleTypes.VmReplication,
            """{"healths":[],"states":["error"]}""", "warning", 0, null, true, now, now));

        // State error with healthy-looking health: fires (healths empty = don't care).
        await eval.OnVmReplicationChangedAsync("h1", [ReplVm("web01", "ok", "error")], now, CancellationToken.None);
        Assert.Single(alerts.Alerts.Where(a => a.Status != AlertStatuses.Resolved));

        // Different VM, enabled state: no fire.
        await eval.OnVmReplicationChangedAsync("h1", [ReplVm("web01", "ok", "enabled")], now.AddSeconds(30), CancellationToken.None);
        Assert.Equal(AlertStatuses.Resolved, alerts.Alerts.Single().Status);
    }

    [Fact]
    public async Task VmReplicationRule_NonReplicatedVms_NeverFire()
    {
        var now = DateTimeOffset.Parse("2024-08-07T15:00:00Z");
        var (eval, rules, alerts, _, _) = Build(now);
        rules.Rules.Add(new RuleRecord("r1", "repl bad", RuleTypes.VmReplication, "{}", "critical", 0, null, true, now, now));

        // Null replication fields (not replicated / no relationship): never a match.
        await eval.OnVmReplicationChangedAsync("h1", [ReplVm("web01", null, null)], now, CancellationToken.None);
        await eval.OnVmReplicationChangedAsync("h1", [ReplVm("web02", "not_applicable", "disabled")], now, CancellationToken.None);
        Assert.Empty(alerts.Alerts);
    }

    [Fact]
    public async Task VmReplicationRule_HealthAndState_MustBothMatch()
    {
        var now = DateTimeOffset.Parse("2024-08-07T15:00:00Z");
        var (eval, rules, alerts, _, _) = Build(now);
        rules.Rules.Add(new RuleRecord("r1", "repl fail", RuleTypes.VmReplication,
            """{"healths":["critical"],"states":["error","discarded"]}""", "critical", 0, null, true, now, now));

        // Health critical but state not in the set: no fire.
        await eval.OnVmReplicationChangedAsync("h1", [ReplVm("web01", "critical", "enabled")], now, CancellationToken.None);
        Assert.Empty(alerts.Alerts);

        // Both match: fires.
        await eval.OnVmReplicationChangedAsync("h1", [ReplVm("web01", "critical", "discarded")], now.AddSeconds(30), CancellationToken.None);
        Assert.Single(alerts.Alerts.Where(a => a.Status != AlertStatuses.Resolved));
    }

    [Fact]
    public async Task LogonRule_AnyUserSuccess_FiresPerUserAndBumps()
    {
        var now = DateTimeOffset.Parse("2024-08-07T15:00:00Z");
        var (eval, rules, alerts, _, _) = Build(now);
        rules.Rules.Add(new RuleRecord("r1", "human logon", RuleTypes.Logon,
            """{"outcome":"success"}""", "info", 0, null, true, now, now));

        await eval.OnLogonEventsAsync("src_1", [LogonItem("admin", 4624, "2")], CancellationToken.None);
        var admin = Assert.Single(alerts.Alerts.Where(a => a.Status != AlertStatuses.Resolved));
        Assert.Equal("r1", admin.RuleId);
        Assert.Equal("src_1", admin.SourceId);
        Assert.Contains("admin", admin.Title);
        Assert.Contains("logon type 2", admin.Detail);

        // A second user is a separate alert (per-user fingerprint)…
        await eval.OnLogonEventsAsync("src_1", [LogonItem("bob", 4624, "10")], CancellationToken.None);
        Assert.Equal(2, alerts.Alerts.Count(a => a.Status != AlertStatuses.Resolved));

        // …and the same user again bumps the existing occurrence.
        await eval.OnLogonEventsAsync("src_1", [LogonItem("admin", 4624, "2")], CancellationToken.None);
        Assert.Equal(2, alerts.Alerts.Count(a => a.Status != AlertStatuses.Resolved));
        Assert.Equal(2, alerts.Alerts.Single(a => a.Title.Contains("admin")).Count);
    }

    [Fact]
    public async Task LogonRule_UserSpecific_CaseInsensitive_AndOutcomeScoped()
    {
        var now = DateTimeOffset.Parse("2024-08-07T15:00:00Z");
        var (eval, rules, alerts, _, _) = Build(now);
        rules.Rules.Add(new RuleRecord("r1", "admin logon", RuleTypes.Logon,
            """{"outcome":"success","users":["ADMIN"]}""", "critical", 0, null, true, now, now));

        // Case-insensitive account match (Windows names are case-insensitive).
        await eval.OnLogonEventsAsync("src_1", [LogonItem("admin", 4624, "2")], CancellationToken.None);
        Assert.Single(alerts.Alerts);

        // Same user failing is a different outcome: no fire.
        await eval.OnLogonEventsAsync("src_1", [LogonItem("admin", 4625, "3")], CancellationToken.None);
        // Unlisted user: no fire.
        await eval.OnLogonEventsAsync("src_1", [LogonItem("bob", 4624, "2")], CancellationToken.None);
        Assert.Single(alerts.Alerts);
    }

    [Fact]
    public async Task LogonRule_IgnoresDwmUmfdForAnyUser_ButMatchesWhenExplicit()
    {
        var now = DateTimeOffset.Parse("2024-08-07T15:00:00Z");
        var (eval, rules, alerts, _, _) = Build(now);
        rules.Rules.Add(new RuleRecord("r1", "any logon", RuleTypes.Logon,
            """{"outcome":"success"}""", "info", 0, null, true, now, now));

        // Internal console-session noise accounts never fire any-user rules.
        await eval.OnLogonEventsAsync("src_1", [LogonItem("DWM-1", 4624, "2"), LogonItem("umfd-0", 4624, "2")], CancellationToken.None);
        Assert.Empty(alerts.Alerts);

        // A real user still fires.
        await eval.OnLogonEventsAsync("src_1", [LogonItem("admin", 4624, "2")], CancellationToken.None);
        Assert.Single(alerts.Alerts);

        // An explicitly listed noise account matches (deliberate opt-in).
        var explicitRule = new RuleRecord("r2", "dwm watch", RuleTypes.Logon,
            """{"outcome":"success","users":["DWM-1"]}""", "info", 0, null, true, now, now);
        rules.Rules.Add(explicitRule);
        await eval.OnLogonEventsAsync("src_1", [LogonItem("DWM-1", 4624, "2")], CancellationToken.None);
        Assert.Equal(2, alerts.Alerts.Count(a => a.Status != AlertStatuses.Resolved));
    }

    [Fact]
    public async Task LogonRule_LogonTypesAndLockout_Scope()
    {
        var now = DateTimeOffset.Parse("2024-08-07T15:00:00Z");
        var (eval, rules, alerts, _, _) = Build(now);
        rules.Rules.Add(new RuleRecord("r1", "network failures", RuleTypes.Logon,
            """{"outcome":"failure","logonTypes":[3]}""", "warning", 0, null, true, now, now));
        rules.Rules.Add(new RuleRecord("r2", "lockouts", RuleTypes.Logon,
            """{"outcome":"lockout"}""", "critical", 0, null, true, now, now));

        await eval.OnLogonEventsAsync("src_1",
            [LogonItem("bob", 4625, "3"), LogonItem("carol", 4625, "10"), LogonItem("dave", 4740)],
            CancellationToken.None);

        // r1 fires only for logon type 3; r2 fires only for the lockout.
        var live = alerts.Alerts.Where(a => a.Status != AlertStatuses.Resolved).ToList();
        Assert.Equal(2, live.Count);
        Assert.Contains(live, a => a.RuleId == "r1" && a.Title.Contains("bob"));
        Assert.Contains(live, a => a.RuleId == "r2" && a.Title.Contains("dave"));
        Assert.DoesNotContain(live, a => a.Title.Contains("carol"));
    }

    [Fact]
    public async Task LogonRule_NonLogonItems_Ignored()
    {
        var now = DateTimeOffset.Parse("2024-08-07T15:00:00Z");
        var (eval, rules, alerts, _, _) = Build(now);
        rules.Rules.Add(new RuleRecord("r1", "any failure", RuleTypes.Logon,
            """{"outcome":"failure"}""", "warning", 0, null, true, now, now));

        // Service logon type 3 on 4624 is not a curated success; a 6008 on the
        // Security channel is not a logon event at all; only the 4625 fires.
        await eval.OnLogonEventsAsync("src_1",
            [LogonItem("svc", 4624, "3"), LogonItem("x", 6008, null), LogonItem("x", 4625, null)],
            CancellationToken.None);
        Assert.Single(alerts.Alerts);
        Assert.Contains("x", alerts.Alerts.Single().Title);
    }

    [Fact]
    public async Task Outbox_EnqueuedForRuleChannels()
    {
        var now = DateTimeOffset.Parse("2024-08-07T15:00:00Z");
        var (eval, rules, alerts, outbox, _) = Build(now);
        rules.Rules.Add(new RuleRecord("r1", "6008", RuleTypes.Event,
            """{"channel":"System","eventIds":[6008]}""", "warning", 0, null, true, now, now));
        rules.Channels["r1"] = ["ch_telegram", "ch_webhook"];

        await eval.OnEventsAcceptedAsync("src_1", [EventItem("System", 6008, 2, "x")], CancellationToken.None);

        var alert = Assert.Single(alerts.Alerts);
        Assert.Equal(2, outbox.Enqueued.Count);
        Assert.Contains($"{alert.Id}|ch_telegram", outbox.Enqueued);
        Assert.Contains($"{alert.Id}|ch_webhook", outbox.Enqueued);
    }
}

public class TelemetryOrderingTests
{
    private sealed class FakeStatusStore : IAgentStatusStore
    {
        public AgentStatusRow? Current { get; set; }
        public bool LastStored { get; private set; }
        public Task<AgentStatusRow?> GetAsync(string sourceId, CancellationToken ct) => Task.FromResult(Current);
        public Task<IReadOnlyList<AgentStatusRow>> ListAllAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<AgentStatusRow>>(Current is null ? [] : [Current]);
        public Task<bool> ApplyHeartbeatAsync(string sourceId, HeartbeatPayload hb, DateTimeOffset receivedAt, CancellationToken ct)
        {
            LastStored = Current is null
                || (hb.BootTime is { } bt && Current.BootTime is { } ebt && bt != ebt)
                || hb.SentAt > (Current.LastSentAt ?? DateTimeOffset.MinValue);
            if (LastStored)
            {
                Current = new AgentStatusRow(sourceId, receivedAt, hb.SentAt, hb.AgentVersion, hb.OsBuild,
                    hb.BootTime, hb.UptimeS, hb.Degraded, hb.ConfigHash, hb.CountersJson, null, null, null, receivedAt);
            }
            return Task.FromResult(LastStored);
        }
        public Task<bool> ApplyFactsAsync(string sourceId, FactsPayload facts, DateTimeOffset receivedAt, CancellationToken ct)
        {
            LastStored = Current?.FactsCollectedAt is null || facts.CollectedAt > Current.FactsCollectedAt;
            if (LastStored)
            {
                Current = (Current ?? new AgentStatusRow(sourceId, receivedAt, null, null, null, null, null, null, null, null, null, null, null, receivedAt))
                    with { FactsJson = "{}", FactsCollectedAt = facts.CollectedAt };
            }
            return Task.FromResult(LastStored);
        }
    }

    private static HeartbeatPayload Hb(DateTimeOffset sentAt, DateTimeOffset? bootTime) =>
        new(sentAt, "0.1.0", 1, "17763", bootTime, 100, null, null, "", "abc", null, null);

    [Fact]
    public async Task Heartbeat_NewerSentAt_Stored()
    {
        var store = new FakeStatusStore();
        var boot = DateTimeOffset.Parse("2024-08-01T00:00:00Z");
        await store.ApplyHeartbeatAsync("src_1", Hb(DateTimeOffset.Parse("2024-08-07T10:00:00Z"), boot), DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.True(store.LastStored);

        await store.ApplyHeartbeatAsync("src_1", Hb(DateTimeOffset.Parse("2024-08-07T11:00:00Z"), boot), DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.True(store.LastStored);
        Assert.Equal(DateTimeOffset.Parse("2024-08-07T11:00:00Z"), store.Current!.LastSentAt);
    }

    [Fact]
    public async Task Heartbeat_OlderSentAt_SameBoot_Ignored()
    {
        var store = new FakeStatusStore();
        var boot = DateTimeOffset.Parse("2024-08-01T00:00:00Z");
        await store.ApplyHeartbeatAsync("src_1", Hb(DateTimeOffset.Parse("2024-08-07T11:00:00Z"), boot), DateTimeOffset.UtcNow, CancellationToken.None);

        // PROTOCOL §7.4: an older sent_at in the same boot session is ignored.
        await store.ApplyHeartbeatAsync("src_1", Hb(DateTimeOffset.Parse("2024-08-07T10:00:00Z"), boot), DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.False(store.LastStored);
        Assert.Equal(DateTimeOffset.Parse("2024-08-07T11:00:00Z"), store.Current!.LastSentAt);
    }

    [Fact]
    public async Task Heartbeat_NewBootTime_AlwaysStored_EvenIfOlderSentAt()
    {
        var store = new FakeStatusStore();
        await store.ApplyHeartbeatAsync("src_1", Hb(DateTimeOffset.Parse("2024-08-07T11:00:00Z"), DateTimeOffset.Parse("2024-08-01T00:00:00Z")), DateTimeOffset.UtcNow, CancellationToken.None);

        await store.ApplyHeartbeatAsync("src_1", Hb(DateTimeOffset.Parse("2024-08-07T09:00:00Z"), DateTimeOffset.Parse("2024-08-05T00:00:00Z")), DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.True(store.LastStored);
        Assert.Equal(DateTimeOffset.Parse("2024-08-05T00:00:00Z"), store.Current!.BootTime);
    }

    [Fact]
    public async Task Facts_NewerCollectedAt_Stored()
    {
        var store = new FakeStatusStore();
        await store.ApplyFactsAsync("src_1", new FactsPayload(DateTimeOffset.Parse("2024-08-07T10:00:00Z"), false, []), DateTimeOffset.UtcNow, CancellationToken.None);
        await store.ApplyFactsAsync("src_1", new FactsPayload(DateTimeOffset.Parse("2024-08-07T11:00:00Z"), true, []), DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.True(store.LastStored);
        Assert.Equal(DateTimeOffset.Parse("2024-08-07T11:00:00Z"), store.Current!.FactsCollectedAt);
    }

    [Fact]
    public async Task Facts_OlderCollectedAt_Ignored()
    {
        var store = new FakeStatusStore();
        await store.ApplyFactsAsync("src_1", new FactsPayload(DateTimeOffset.Parse("2024-08-07T11:00:00Z"), false, []), DateTimeOffset.UtcNow, CancellationToken.None);
        await store.ApplyFactsAsync("src_1", new FactsPayload(DateTimeOffset.Parse("2024-08-07T10:00:00Z"), false, []), DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.False(store.LastStored);
    }

    [Fact]
    public void CursorCodec_RoundTrips()
    {
        var t = DateTimeOffset.Parse("2024-08-07T15:02:11.123Z");
        var cursor = CursorCodec.Encode(t, 42);
        Assert.True(CursorCodec.TryDecode(cursor, out var decoded, out var id));
        Assert.Equal(t, decoded);
        Assert.Equal(42, id);
        Assert.False(CursorCodec.TryDecode("not-base64!!", out _, out _));
        Assert.False(CursorCodec.TryDecode("", out _, out _));
    }
}

public class HeartbeatMetricsTests
{
    private static HeartbeatPayload Hb(string? freeDiskJson, long? memTotal = null, long? memAvail = null) => new(
        DateTimeOffset.Parse("2024-08-07T15:00:00Z"), "0.1.0", 1, "17763",
        DateTimeOffset.Parse("2024-08-01T00:00:00Z"), 100, memTotal, memAvail, "", "abc", null, freeDiskJson);

    [Fact]
    public void FromHeartbeat_MapsEveryVolumeToBytesAndPctSeries()
    {
        var metrics = HeartbeatMetrics.FromHeartbeat("h1", Hb(
            """[{"path":"C:\\","bytes":12345678,"pct":0.23},{"path":"D:\\","bytes":999999999,"pct":0.05}]"""),
            DateTimeOffset.Parse("2024-08-07T15:00:30Z"));

        Assert.Equal(4, metrics.Count);
        Assert.Contains(metrics, m => m.Name == "disk_free:C:\\" && m.Value == 12345678 && m.Unit == "B");
        Assert.Contains(metrics, m => m.Name == "disk_free_pct:C:\\" && m.Value == 23.0 && m.Unit == "%");
        Assert.Contains(metrics, m => m.Name == "disk_free:D:\\" && m.Value == 999999999 && m.Unit == "B");
        Assert.Contains(metrics, m => m.Name == "disk_free_pct:D:\\" && m.Value == 5.0 && m.Unit == "%");
        Assert.All(metrics, m => Assert.Equal("h1", m.HostId));
    }

    [Fact]
    public void FromHeartbeat_MapsAvailableRamToBytesAndPctSeries()
    {
        var metrics = HeartbeatMetrics.FromHeartbeat("h1", Hb(null, memTotal: 34_359_738_368, memAvail: 8_589_934_592),
            DateTimeOffset.Parse("2024-08-07T15:00:30Z"));

        Assert.Equal(2, metrics.Count);
        Assert.Contains(metrics, m => m.Name == "mem_available" && m.Value == 8_589_934_592 && m.Unit == "B");
        Assert.Contains(metrics, m => m.Name == "mem_available_pct" && m.Value == 25.0 && m.Unit == "%");
    }

    [Fact]
    public void FromHeartbeat_MemWithoutTotal_OnlyAbsoluteSeries()
    {
        // No total ⇒ the pct series cannot be derived; the absolute one still ships.
        var metrics = HeartbeatMetrics.FromHeartbeat("h1", Hb(null, memTotal: null, memAvail: 4_000_000_000),
            DateTimeOffset.UtcNow);

        var single = Assert.Single(metrics);
        Assert.Equal("mem_available", single.Name);
        Assert.Equal(4_000_000_000, single.Value);
    }

    [Fact]
    public void FromHeartbeat_NoFreeDiskOrMem_ReturnsEmpty()
    {
        Assert.Empty(HeartbeatMetrics.FromHeartbeat("h1", Hb(null), DateTimeOffset.UtcNow));
        Assert.Empty(HeartbeatMetrics.FromHeartbeat("h1", Hb("null"), DateTimeOffset.UtcNow));
        Assert.Empty(HeartbeatMetrics.FromHeartbeat("h1", Hb("{}"), DateTimeOffset.UtcNow));
    }

    [Fact]
    public void FromHeartbeat_SkipsMalformedEntries()
    {
        var metrics = HeartbeatMetrics.FromHeartbeat("h1", Hb(
            """[{"bytes":1,"pct":0.5},{"path":"","bytes":2,"pct":0.5},{"path":"E:\\"},{"path":"F:\\","bytes":3,"pct":"oops"},42]"""),
            DateTimeOffset.UtcNow);

        var single = Assert.Single(metrics);
        Assert.Equal("disk_free:F:\\", single.Name);
        Assert.Equal(3, single.Value);
        Assert.Equal("B", single.Unit);
    }
}

public class TelemetryServiceTests
{
    private const string HbWithDisk = """
        {"kind":"heartbeat","sent_at":"2024-08-07T15:00:00Z","boot_time":"2024-08-01T00:00:00Z",
         "mem_total_bytes":34359738368,"mem_available_bytes":8589934592,
         "free_disk":[{"path":"C:\\","bytes":12345678,"pct":0.23},{"path":"D:\\","bytes":999999999,"pct":0.05}]}
        """;

    private static readonly DateTimeOffset ReceivedAt = DateTimeOffset.Parse("2024-08-07T15:00:30Z");

    private static readonly HostRecord Host = new("h1", "HOST01", "windows-agent", "src_1", null, null, true, null,
        DateTimeOffset.Parse("2024-08-01T00:00:00Z"), DateTimeOffset.Parse("2024-08-01T00:00:00Z"));

    private static JsonElement Item(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }

    private sealed class FakeStatusStore(bool stored) : IAgentStatusStore
    {
        public Task<AgentStatusRow?> GetAsync(string sourceId, CancellationToken ct) => Task.FromResult<AgentStatusRow?>(null);
        public Task<IReadOnlyList<AgentStatusRow>> ListAllAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<AgentStatusRow>>([]);
        public Task<bool> ApplyHeartbeatAsync(string sourceId, HeartbeatPayload hb, DateTimeOffset receivedAt, CancellationToken ct)
            => Task.FromResult(stored);
        public Task<bool> ApplyFactsAsync(string sourceId, FactsPayload facts, DateTimeOffset receivedAt, CancellationToken ct)
            => Task.FromResult(true);
    }

    private sealed class FakeHosts(HostRecord? host) : IHostStore
    {
        public Task<IReadOnlyList<HostRecord>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<HostRecord>>(host is null ? [] : [host]);
        public Task<HostRecord?> GetAsync(string id, CancellationToken ct) => Task.FromResult(host);
        public Task<HostRecord?> GetBySourceAsync(string sourceId, CancellationToken ct) => Task.FromResult(host);
        public Task<HostRecord> CreateAsync(HostRecord h, CancellationToken ct) => Task.FromResult(h);
        public Task<bool> UpdateAsync(HostRecord h, DateTimeOffset expectedUpdatedAt, CancellationToken ct) => Task.FromResult(true);
        public Task DeleteAsync(string id, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class RecordingHealth : IHealthStore
    {
        public List<MetricRecord> StoredMetrics { get; } = [];
        public Task ReplaceComponentsAsync(string hostId, IReadOnlyList<ComponentRecord> components, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<ComponentRecord>> GetComponentsAsync(string hostId, CancellationToken ct) => Task.FromResult<IReadOnlyList<ComponentRecord>>([]);
        public Task AddSnapshotAsync(string hostId, DateTimeOffset time, string rollupState, string componentsJson, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<HealthSnapshotRecord>> GetSnapshotsAsync(string hostId, DateTimeOffset? from, DateTimeOffset? to, int limit, CancellationToken ct) => Task.FromResult<IReadOnlyList<HealthSnapshotRecord>>([]);
        public Task AddMetricsAsync(string hostId, DateTimeOffset time, IReadOnlyList<MetricRecord> metrics, CancellationToken ct)
        { StoredMetrics.AddRange(metrics); return Task.CompletedTask; }
        public Task<IReadOnlyList<MetricRecord>> GetLatestMetricsAsync(string hostId, int maxPerName, CancellationToken ct) => Task.FromResult<IReadOnlyList<MetricRecord>>(StoredMetrics);
        public Task<IReadOnlyList<MetricRecord>> GetMetricsInRangeAsync(string hostId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct) => Task.FromResult<IReadOnlyList<MetricRecord>>(StoredMetrics);
        public Task UpsertVmsAsync(string hostId, IReadOnlyList<VmRecord> vms, bool stale, DateTimeOffset collectedAt, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<VmRecord>> GetVmsAsync(string hostId, CancellationToken ct) => Task.FromResult<IReadOnlyList<VmRecord>>([]);
        public Task<long> PurgeMetricsOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct) => Task.FromResult(0L);
        public Task<long> PurgeSnapshotsOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct) => Task.FromResult(0L);
        public Task<long> PurgeVmsAsync(DateTimeOffset cutoff, CancellationToken ct) => Task.FromResult(0L);
    }

    private sealed class RecordingEvaluator : IAlertEvaluator
    {
        public Exception? ThrowOnThresholds { get; set; }
        public List<(string HostId, IReadOnlyList<MetricRecord> Metrics)> ThresholdCalls { get; } = [];
        public List<(string HostId, IReadOnlyList<VmRecord> Vms)> VmCalls { get; } = [];
        public List<(string HostId, IReadOnlyList<VmRecord> Vms)> ReplicationCalls { get; } = [];
        public Task OnEventsAcceptedAsync(string sourceId, IReadOnlyList<ValidatedLogItem> items, CancellationToken ct) => Task.CompletedTask;
        public Task OnLogonEventsAsync(string sourceId, IReadOnlyList<ValidatedLogItem> items, CancellationToken ct) => Task.CompletedTask;
        public Task OnHealthStateChangedAsync(string hostId, string rollupState, IReadOnlyList<ComponentRecord> components, DateTimeOffset at, CancellationToken ct) => Task.CompletedTask;
        public Task OnHeartbeatSilenceChangedAsync(string? ruleId, string sourceId, bool silent, DateTimeOffset at, CancellationToken ct) => Task.CompletedTask;
        public Task OnThresholdsAsync(string hostId, IReadOnlyList<MetricRecord> metrics, DateTimeOffset at, CancellationToken ct)
        {
            ThresholdCalls.Add((hostId, metrics));
            return ThrowOnThresholds is null ? Task.CompletedTask : Task.FromException(ThrowOnThresholds);
        }
        public Task OnVmsChangedAsync(string hostId, IReadOnlyList<VmRecord> vms, DateTimeOffset at, CancellationToken ct)
        {
            VmCalls.Add((hostId, vms));
            return Task.CompletedTask;
        }
        public Task OnVmReplicationChangedAsync(string hostId, IReadOnlyList<VmRecord> vms, DateTimeOffset at, CancellationToken ct)
        {
            ReplicationCalls.Add((hostId, vms));
            return Task.CompletedTask;
        }
        public Task AutoResolveDueAsync(DateTimeOffset at, CancellationToken ct) => Task.CompletedTask;
        public Task ReconcileAsync(CancellationToken ct) => Task.CompletedTask;
    }

    [Fact]
    public async Task Heartbeat_WithFreeDisk_StoresMetricsAndEvaluatesThresholds()
    {
        var health = new RecordingHealth();
        var evaluator = new RecordingEvaluator();
        var svc = new TelemetryService(new FakeStatusStore(stored: true), new FakeHosts(Host), health, evaluator,
            new FixedClock(ReceivedAt), NullLogger<TelemetryService>.Instance);

        await svc.ProcessAsync("src_1", [Item(HbWithDisk)], CancellationToken.None);

        // Thresholds are evaluated before the metrics are stored (poll pattern).
        var call = Assert.Single(evaluator.ThresholdCalls);
        Assert.Equal("h1", call.HostId);
        Assert.Equal(6, call.Metrics.Count); // 4 disk series + 2 RAM series

        Assert.Equal(6, health.StoredMetrics.Count);
        Assert.Contains(health.StoredMetrics, m => m.Name == "disk_free:C:\\" && m.Value == 12345678 && m.Unit == "B");
        Assert.Contains(health.StoredMetrics, m => m.Name == "disk_free_pct:C:\\" && m.Value == 23.0 && m.Unit == "%");
        Assert.Contains(health.StoredMetrics, m => m.Name == "disk_free:D:\\" && m.Value == 999999999 && m.Unit == "B");
        Assert.Contains(health.StoredMetrics, m => m.Name == "disk_free_pct:D:\\" && m.Value == 5.0 && m.Unit == "%");
        Assert.Contains(health.StoredMetrics, m => m.Name == "mem_available" && m.Value == 8_589_934_592 && m.Unit == "B");
        Assert.Contains(health.StoredMetrics, m => m.Name == "mem_available_pct" && m.Value == 25.0 && m.Unit == "%");
        Assert.All(health.StoredMetrics, m => Assert.Equal(ReceivedAt, m.Time));
    }

    [Fact]
    public async Task Heartbeat_ThresholdFailure_DoesNotFailIngest()
    {
        // DEFECTS.md D2: derived alerting must never fail an accepted telemetry request.
        var health = new RecordingHealth();
        var evaluator = new RecordingEvaluator { ThrowOnThresholds = new InvalidOperationException("boom") };
        var svc = new TelemetryService(new FakeStatusStore(stored: true), new FakeHosts(Host), health, evaluator,
            new FixedClock(ReceivedAt), NullLogger<TelemetryService>.Instance);

        await svc.ProcessAsync("src_1", [Item(HbWithDisk)], CancellationToken.None);

        Assert.Empty(health.StoredMetrics); // evaluation failed → storage not reached
    }

    [Fact]
    public async Task Facts_StaleBatch_SkipsVmHeartbeatEvaluation()
    {
        // PROTOCOL §7.4: stale facts are re-emitted old data after a WMI
        // timeout — a VM heartbeat transition must not be evaluated from them
        // (they are still stored so the UI can mark them stale).
        var health = new RecordingHealth();
        var evaluator = new RecordingEvaluator();
        var svc = new TelemetryService(new FakeStatusStore(stored: true), new FakeHosts(Host), health, evaluator,
            new FixedClock(ReceivedAt), NullLogger<TelemetryService>.Instance);

        var staleFacts = Item("""{"kind":"facts","collected_at":"2024-08-07T15:00:00Z","stale":true,"vms":[{"name":"web01","state":"on","heartbeat_ok":false}]}""");
        await svc.ProcessAsync("src_1", [staleFacts], CancellationToken.None);
        Assert.Empty(evaluator.VmCalls);
        Assert.Empty(evaluator.ReplicationCalls);

        var freshFacts = Item("""{"kind":"facts","collected_at":"2024-08-07T15:01:00Z","stale":false,"vms":[{"name":"web01","state":"on","heartbeat_ok":false}]}""");
        await svc.ProcessAsync("src_1", [freshFacts], CancellationToken.None);
        var call = Assert.Single(evaluator.VmCalls);
        Assert.Equal("h1", call.HostId);
        Assert.Equal("web01", Assert.Single(call.Vms).Name);
        Assert.Single(evaluator.ReplicationCalls);
    }

    [Fact]
    public async Task Heartbeat_NoHostRow_NoDiskMetrics()
    {
        // Metrics are host-scoped (DESIGN §5.2); no host row (not yet created in
        // the web UI) ⇒ the disk samples are dropped, like facts.
        var health = new RecordingHealth();
        var evaluator = new RecordingEvaluator();
        var svc = new TelemetryService(new FakeStatusStore(stored: true), new FakeHosts(null), health, evaluator,
            new FixedClock(ReceivedAt), NullLogger<TelemetryService>.Instance);

        await svc.ProcessAsync("src_1", [Item(HbWithDisk)], CancellationToken.None);

        Assert.Empty(evaluator.ThresholdCalls);
        Assert.Empty(health.StoredMetrics);
    }

    [Fact]
    public async Task Heartbeat_NotStored_OlderPayload_NoDiskMetrics()
    {
        // PROTOCOL §7.4: an older payload in the same boot session is ignored,
        // so its disk samples must not pollute the time series.
        var health = new RecordingHealth();
        var evaluator = new RecordingEvaluator();
        var svc = new TelemetryService(new FakeStatusStore(stored: false), new FakeHosts(Host), health, evaluator,
            new FixedClock(ReceivedAt), NullLogger<TelemetryService>.Instance);

        await svc.ProcessAsync("src_1", [Item(HbWithDisk)], CancellationToken.None);

        Assert.Empty(evaluator.ThresholdCalls);
        Assert.Empty(health.StoredMetrics);
    }
}
