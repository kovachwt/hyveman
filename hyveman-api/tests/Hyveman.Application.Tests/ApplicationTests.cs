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

    private sealed class FakeRegTokens : IRegistrationTokenStore
    {
        public RegistrationTokenLookup? Lookup { get; set; }
        public bool MarkedConsumed { get; private set; }
        public Task<(string Id, string RawToken)> CreateAsync(string kind, TimeSpan? lifetime, string? createdBy, DateTimeOffset now, CancellationToken ct)
            => Task.FromResult(("rt_new", "reg_newtoken"));
        public Task<RegistrationTokenLookup?> LookupAsync(string rawToken, CancellationToken ct) => Task.FromResult(Lookup);
        public Task MarkConsumedAsync(string id, DateTimeOffset at, CancellationToken ct) { MarkedConsumed = true; return Task.CompletedTask; }
        public Task<bool> RevokeAsync(string id, CancellationToken ct) => Task.FromResult(true);
        public Task<IReadOnlyList<RegistrationTokenInfo>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<RegistrationTokenInfo>>([]);
    }

    private sealed class FakeSources : ISourceStore
    {
        public Source? Existing { get; set; }
        public readonly List<Source> Created = [];
        public Task<Source?> GetByIdAsync(string id, CancellationToken ct) => Task.FromResult<Source?>(null);
        public Task<Source?> GetByKindNameAsync(string kind, string name, CancellationToken ct) => Task.FromResult(Existing);
        public Task<Source> CreateAsync(string kind, string name, DateTimeOffset now, CancellationToken ct)
        {
            var s = new Source("src_" + Created.Count, kind, name, now);
            Created.Add(s);
            return Task.FromResult(s);
        }
        public Task<IReadOnlyList<Source>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<Source>>(Created);
        public Task DeleteAsync(string id, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeTokens : ITokenStore
    {
        public string? LastSourceId { get; private set; }
        public Task<TokenAuthResult?> AuthenticateAsync(string rawToken, CancellationToken ct) => Task.FromResult<TokenAuthResult?>(null);
        public Task<bool> IsRevokedAsync(string rawToken, CancellationToken ct) => Task.FromResult(false);
        public Task<bool> SourceMissingAsync(string rawToken, CancellationToken ct) => Task.FromResult(false);
        public Task<string> CreateAgentTokenAsync(string sourceId, string[] scopes, DateTimeOffset now, CancellationToken ct)
        {
            LastSourceId = sourceId;
            return Task.FromResult("agt_fresh");
        }
        public Task<IReadOnlyList<TokenInfo>> ListForSourceAsync(string sourceId, CancellationToken ct) => Task.FromResult<IReadOnlyList<TokenInfo>>([]);
        public Task<bool> RevokeAsync(string tokenId, CancellationToken ct) => Task.FromResult(true);
        public Task TouchAsync(string tokenId, DateTimeOffset at, CancellationToken ct) => Task.CompletedTask;
    }

    private static (RegistrationService Service, FakeRegTokens Tokens, FakeSources Sources, FakeTokens AgentTokens, List<AuditEntry> Audit) Build()
    {
        var regTokens = new FakeRegTokens();
        var sources = new FakeSources();
        var agentTokens = new FakeTokens();
        var audit = new List<AuditEntry>();
        var svc = new RegistrationService(regTokens, sources, agentTokens,
            new FakeAudit(audit), new FakeClock(DateTimeOffset.Parse("2024-08-07T15:00:00Z")),
            NullLogger<RegistrationService>.Instance);
        return (svc, regTokens, sources, agentTokens, audit);
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
        var (svc, regTokens, sources, agentTokens, _) = Build();
        regTokens.Lookup = new RegistrationTokenLookup("rt_1", "windows-agent", false, null, null);

        var outcome = await svc.RegisterAsync("reg_x", "windows-agent", "HOST01", "0.1.0", "17763", CancellationToken.None);

        Assert.Equal("agt_fresh", outcome.RawToken);
        Assert.Equal(["ingest"], outcome.Scopes);
        Assert.True(regTokens.MarkedConsumed);
        Assert.Equal("HOST01", Assert.Single(sources.Created).Name);
        Assert.Equal(outcome.SourceId, agentTokens.LastSourceId);
        Assert.Equal("windows-agent", outcome.Kind);
    }

    [Fact]
    public async Task Register_ExistingSource_ReusesIt()
    {
        var (svc, regTokens, sources, _, _) = Build();
        regTokens.Lookup = new RegistrationTokenLookup("rt_1", "windows-agent", false, null, null);
        sources.Existing = new Source("src_existing", "windows-agent", "HOST01", DateTimeOffset.MinValue);

        var outcome = await svc.RegisterAsync("reg_x", "windows-agent", "HOST01", null, null, CancellationToken.None);

        Assert.Equal("src_existing", outcome.SourceId);   // reinstall path (PROTOCOL §5.2)
        Assert.Empty(sources.Created);                     // no new row
    }

    [Fact]
    public async Task Register_ConsumedToken_Throws410()
    {
        var (svc, regTokens, _, _, _) = Build();
        regTokens.Lookup = new RegistrationTokenLookup("rt_1", "windows-agent", false, null, DateTimeOffset.UtcNow);

        var ex = await Assert.ThrowsAsync<RegistrationException>(
            () => svc.RegisterAsync("reg_x", "windows-agent", "HOST01", null, null, CancellationToken.None));
        Assert.Equal(410, ex.Status);
        Assert.Equal("token_consumed", ex.Code);
    }

    [Fact]
    public async Task Register_RevokedToken_Throws401()
    {
        var (svc, regTokens, _, _, _) = Build();
        regTokens.Lookup = new RegistrationTokenLookup("rt_1", "windows-agent", true, null, null);

        var ex = await Assert.ThrowsAsync<RegistrationException>(
            () => svc.RegisterAsync("reg_x", "windows-agent", "HOST01", null, null, CancellationToken.None));
        Assert.Equal(401, ex.Status);
        Assert.Equal("token_revoked", ex.Code);
    }

    [Fact]
    public async Task Register_KindMismatch_Throws400()
    {
        var (svc, regTokens, _, _, _) = Build();
        regTokens.Lookup = new RegistrationTokenLookup("rt_1", "syslog-feed", false, null, null);

        var ex = await Assert.ThrowsAsync<RegistrationException>(
            () => svc.RegisterAsync("reg_x", "windows-agent", "HOST01", null, null, CancellationToken.None));
        Assert.Equal(400, ex.Status);
    }

    [Fact]
    public async Task Register_UnknownToken_Throws401()
    {
        var (svc, _, _, _, _) = Build();
        var ex = await Assert.ThrowsAsync<RegistrationException>(
            () => svc.RegisterAsync("reg_x", "windows-agent", "HOST01", null, null, CancellationToken.None));
        Assert.Equal("token_invalid", ex.Code);
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
        public Task ReplaceComponentsAsync(string hostId, IReadOnlyList<ComponentRecord> components, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<ComponentRecord>> GetComponentsAsync(string hostId, CancellationToken ct) => Task.FromResult<IReadOnlyList<ComponentRecord>>([]);
        public Task AddSnapshotAsync(string hostId, DateTimeOffset time, string rollupState, string componentsJson, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<HealthSnapshotRecord>> GetSnapshotsAsync(string hostId, DateTimeOffset? from, DateTimeOffset? to, int limit, CancellationToken ct) => Task.FromResult<IReadOnlyList<HealthSnapshotRecord>>([]);
        public Task AddMetricsAsync(string hostId, DateTimeOffset time, IReadOnlyList<MetricRecord> metrics, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<MetricRecord>> GetLatestMetricsAsync(string hostId, int maxPerName, CancellationToken ct) => Task.FromResult<IReadOnlyList<MetricRecord>>([]);
        public Task<IReadOnlyList<MetricRecord>> GetMetricsInRangeAsync(string hostId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct) => Task.FromResult<IReadOnlyList<MetricRecord>>([]);
        public Task UpsertVmsAsync(string hostId, IReadOnlyList<VmRecord> vms, bool stale, DateTimeOffset collectedAt, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<VmRecord>> GetVmsAsync(string hostId, CancellationToken ct) => Task.FromResult<IReadOnlyList<VmRecord>>([]);
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

    private static (AlertEvaluatorService Eval, InMemoryRuleStore Rules, InMemoryAlertStore Alerts, NoopOutbox Outbox) Build(DateTimeOffset now)
    {
        var rules = new InMemoryRuleStore();
        var alerts = new InMemoryAlertStore();
        var outbox = new NoopOutbox();
        var eval = new AlertEvaluatorService(rules, alerts, new NoopHosts(), new NoopSources(), new NoopHealth(),
            new NoopAgentStatus(), new NoopWindows(), outbox, new FixedClock(now),
            NullLogger<AlertEvaluatorService>.Instance);
        return (eval, rules, alerts, outbox);
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }

    private static ValidatedLogItem EventItem(string channel, long eventId, int severity, string message) => new(
        "System", "1", DateTimeOffset.UtcNow, severity, "Microsoft-Windows-Kernel-Power", message,
        "{}", null, channel, eventId, 0, 0, null);

    [Fact]
    public async Task EventRule_Fires_Deduplicates_ThenResolves()
    {
        var now = DateTimeOffset.Parse("2024-08-07T15:00:00Z");
        var (eval, rules, alerts, _) = Build(now);
        rules.Rules.Add(new RuleRecord("r1", "6008", RuleTypes.Event,
            """{"channel":"System","eventIds":[6008],"severityMin":3}""", "warning", 0, true, now, now));

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
    public async Task EventRule_SeverityMin_Filters()
    {
        var now = DateTimeOffset.Parse("2024-08-07T15:00:00Z");
        var (eval, rules, alerts, _) = Build(now);
        rules.Rules.Add(new RuleRecord("r1", "critical only", RuleTypes.Event,
            """{"channel":"System","severityMin":2}""", "critical", 0, true, now, now));

        await eval.OnEventsAcceptedAsync("src_1", [EventItem("System", 1, 4, "info event")], CancellationToken.None);
        Assert.Empty(alerts.Alerts);

        await eval.OnEventsAcceptedAsync("src_1", [EventItem("System", 2, 2, "error event")], CancellationToken.None);
        Assert.Single(alerts.Alerts);
    }

    [Fact]
    public async Task HealthRule_ComponentTransition_FiresAndResolves()
    {
        var now = DateTimeOffset.Parse("2024-08-07T15:00:00Z");
        var (eval, rules, alerts, _) = Build(now);
        rules.Rules.Add(new RuleRecord("r1", "disk health", RuleTypes.Health,
            """{"componentTypes":["disk"],"states":["warning","critical"],"includeRollup":false}""",
            "critical", 0, true, now, now));

        var disk = new ComponentRecord("h1", ComponentTypes.Disk, "Physical Disk 1", HealthState.Ok, null, now);
        await eval.OnHealthStateChangedAsync("h1", "ok", [disk], now, CancellationToken.None);
        Assert.Empty(alerts.Alerts);

        var bad = disk with { State = HealthState.Warning };
        await eval.OnHealthStateChangedAsync("h1", "warning", [bad], now, CancellationToken.None);
        var alert = Assert.Single(alerts.Alerts.Where(a => a.Status != AlertStatuses.Resolved));
        Assert.Equal("h1", alert.HostId);
        Assert.Contains("Physical Disk 1", alert.Title);

        // Returning to OK resolves the occurrence.
        await eval.OnHealthStateChangedAsync("h1", "ok", [disk], now.AddMinutes(1), CancellationToken.None);
        Assert.Equal(AlertStatuses.Resolved, alerts.Alerts.Single(a => a.Id == alert.Id).Status);
    }

    [Fact]
    public async Task Cooldown_SuppressesRapidRefire()
    {
        var now = DateTimeOffset.Parse("2024-08-07T15:00:00Z");
        var (eval, rules, alerts, _) = Build(now);
        rules.Rules.Add(new RuleRecord("r1", "6008", RuleTypes.Event,
            """{"channel":"System","eventIds":[6008]}""", "warning", 3600, true, now, now));

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
        var (eval, rules, alerts, _) = Build(now);
        rules.Rules.Add(new RuleRecord("r1", "silent", RuleTypes.Heartbeat,
            """{"silenceAfterS":300}""", "warning", 0, true, now, now));

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
        var (eval, rules, alerts, _) = Build(now);
        rules.Rules.Add(new RuleRecord("r1", "hot", RuleTypes.Threshold,
            """{"metric":"temperature:System Board Inlet Temp","comparator":"gt","value":45}""",
            "warning", 0, true, now, now));

        await eval.OnThresholdsAsync("h1", [new MetricRecord("h1", "temperature:System Board Inlet Temp", 40, "C", now)], now, CancellationToken.None);
        Assert.Empty(alerts.Alerts);

        await eval.OnThresholdsAsync("h1", [new MetricRecord("h1", "temperature:System Board Inlet Temp", 46, "C", now)], now, CancellationToken.None);
        Assert.Single(alerts.Alerts);

        await eval.OnThresholdsAsync("h1", [new MetricRecord("h1", "temperature:System Board Inlet Temp", 44, "C", now)], now, CancellationToken.None);
        Assert.Equal(AlertStatuses.Resolved, alerts.Alerts.Single().Status);
    }

    [Fact]
    public async Task Outbox_EnqueuedForRuleChannels()
    {
        var now = DateTimeOffset.Parse("2024-08-07T15:00:00Z");
        var (eval, rules, alerts, outbox) = Build(now);
        rules.Rules.Add(new RuleRecord("r1", "6008", RuleTypes.Event,
            """{"channel":"System","eventIds":[6008]}""", "warning", 0, true, now, now));
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
        new(sentAt, "0.1.0", 1, "17763", bootTime, 100, "", "abc", null, null);

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
