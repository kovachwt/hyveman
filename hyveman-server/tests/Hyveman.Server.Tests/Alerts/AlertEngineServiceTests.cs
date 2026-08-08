using System.Security.Cryptography;
using Hyveman.Server.Alerts;
using Hyveman.Server.Common;
using Hyveman.Server.Config;
using Hyveman.Server.Notifications;
using Hyveman.Server.Observability;
using Hyveman.Server.Storage.Repos;
using Hyveman.Server.Tests.TestInfra;
using Microsoft.Extensions.Logging.Abstractions;
using AlertRepo = Hyveman.Server.Storage.Repos.AlertRepository;

namespace Hyveman.Server.Tests.Alerts;

/// <summary>
/// Alert engine semantics (§9.1–§9.3): baseline diffing, dedup/bump, escalation, resolution,
/// heartbeat silence, maintenance windows, iDRAC unreachable, notification cooldown.
/// </summary>
public sealed class AlertEngineServiceTests
{
    private sealed class Harness : IDisposable
    {
        public TestDb Db { get; }
        public ServerOptions Opts { get; } = new();
        public OwnMetrics Metrics { get; } = new();
        public AlertEngineService Engine { get; }

        public Harness()
        {
            Db = TestDb.CreateAsync().GetAwaiter().GetResult();
            var vault = new Hyveman.Server.Auth.AesGcmCredentialVault(RandomNumberGenerator.GetBytes(32), Db.Db);
            var dispatcher = new NotificationDispatcher(Db.Db, vault, Array.Empty<INotifier>(),
                Opts, Metrics, NullLogger<NotificationDispatcher>.Instance);
            Engine = new AlertEngineService(Db.Db, Opts, dispatcher, new MaintenanceWindowFilter(Db.Db),
                Metrics, NullLogger<AlertEngineService>.Instance);
        }

        public async Task AddHostAsync(string id = "host_1", string name = "HOST01")
            => Db.Db.Writer.WithTransactionAsync(conn => Db.Db.Hosts.InsertAsync(conn, id, null, name, "dell-poweredge", null, null));

        public Task AddSourceAsync(string id = "src_1", string name = "HOST01")
            => Db.Db.Writer.WithTransactionAsync(conn => Db.Db.Sources.InsertAsync(conn, id, "windows-agent", name, null));

        public Task SetComponentsAsync(params ComponentState[] states)
            => Db.Db.Writer.WithTransactionAsync(conn => ComponentRepository.MergeComponentsAsync(conn, "host_1", states, WireTime.NowMs()));

        public async Task AddRuleAsync(string id, string type, string matchJson, int cooldown = 300, string severity = "warning")
        {
            await Db.Db.Writer.WithTransactionAsync(conn =>
                Db.Db.Alerts.InsertRuleAsync(conn, id, "rule " + id, type, matchJson, severity, cooldown));
        }

        public async Task<RuleRow> RuleAsync(string id)
            => await Db.Db.Alerts.GetRuleAsync(id) ?? throw new InvalidOperationException($"rule {id} missing");

        public async Task<List<AlertRow>> ActiveAlertsAsync()
            => (await Db.Db.Alerts.ListAsync()).Where(a => a.Status == "active").ToList();

        public void Dispose() => Db.Dispose();
    }

    private static string Iso(DateTimeOffset dt) => WireTime.ToIsoMs(dt);

    [Fact]
    public async Task HealthRule_FiresOnBadStateChange_NotOnBaseline()
    {
        using var h = new Harness();
        await h.AddHostAsync();
        await h.AddRuleAsync("rule_health", "health", "{\"states\":[\"warning\",\"critical\"]}");

        // Baseline sweep with the component already healthy.
        await h.SetComponentsAsync(new ComponentState("disk", "DISK1", "ok"));
        await h.Engine.EvaluateAsync(CancellationToken.None);
        Assert.Empty(await h.ActiveAlertsAsync());

        // State worsens → fire.
        await h.SetComponentsAsync(new ComponentState("disk", "DISK1", "warning"));
        await h.Engine.EvaluateAsync(CancellationToken.None);

        var alert = Assert.Single(await h.ActiveAlertsAsync());
        Assert.Equal("warning", alert.Severity);
        Assert.Contains("\"state\":\"warning\"", alert.DetailJson);
        Assert.Equal(1, h.Metrics.AlertsFired);
    }

    [Fact]
    public async Task HealthRule_DoesNotRefireOnUnchangedState()
    {
        using var h = new Harness();
        await h.AddHostAsync();
        await h.AddRuleAsync("rule_health", "health", "{\"states\":[\"warning\",\"critical\"]}");
        await h.SetComponentsAsync(new ComponentState("disk", "DISK1", "warning"));

        await h.Engine.EvaluateAsync(CancellationToken.None); // baseline = warning
        await h.Engine.EvaluateAsync(CancellationToken.None); // unchanged

        Assert.Empty(await h.ActiveAlertsAsync());
        Assert.Equal(0, h.Metrics.AlertsFired);
    }

    [Fact]
    public async Task HealthAlert_ResolvesOnRecovery()
    {
        using var h = new Harness();
        await h.AddHostAsync();
        await h.AddRuleAsync("rule_health", "health", "{\"states\":[\"warning\",\"critical\"]}");
        await h.SetComponentsAsync(new ComponentState("disk", "DISK1", "ok"));
        await h.Engine.EvaluateAsync(CancellationToken.None);
        await h.SetComponentsAsync(new ComponentState("disk", "DISK1", "critical"));
        await h.Engine.EvaluateAsync(CancellationToken.None);
        Assert.Single(await h.ActiveAlertsAsync());

        await h.SetComponentsAsync(new ComponentState("disk", "DISK1", "ok"));
        await h.Engine.EvaluateAsync(CancellationToken.None);

        Assert.Empty(await h.ActiveAlertsAsync());
        Assert.Equal(1, h.Metrics.AlertsResolved);
        var history = await h.Db.Db.Alerts.ListAsync();
        Assert.Equal("resolved", Assert.Single(history).Status);
    }

    [Fact]
    public async Task WarningToCritical_EscalatesSeverity()
    {
        using var h = new Harness();
        await h.AddHostAsync();
        await h.AddRuleAsync("rule_health", "health", "{\"states\":[\"warning\",\"critical\"]}", severity: "warning");

        await h.SetComponentsAsync(new ComponentState("disk", "DISK1", "ok"));
        await h.Engine.EvaluateAsync(CancellationToken.None);
        await h.SetComponentsAsync(new ComponentState("disk", "DISK1", "warning"));
        await h.Engine.EvaluateAsync(CancellationToken.None);
        await h.SetComponentsAsync(new ComponentState("disk", "DISK1", "critical"));
        await h.Engine.EvaluateAsync(CancellationToken.None);

        var alert = Assert.Single(await h.ActiveAlertsAsync());
        Assert.Equal("critical", alert.Severity);
        Assert.Equal(2, alert.Count); // one alert, dedup-bumped: created on warning, bumped on escalation
    }

    [Fact]
    public async Task RepeatedFires_BumpCount()
    {
        using var h = new Harness();
        await h.AddHostAsync();
        await h.AddRuleAsync("rule_health", "health", "{\"states\":[\"warning\",\"critical\"]}");
        var rule = await h.RuleAsync("rule_health");

        await h.Engine.FireAsync(rule, "host_1", null, "sig-1", "warning", "{}", CancellationToken.None);
        await h.Engine.FireAsync(rule, "host_1", null, "sig-1", "warning", "{}", CancellationToken.None);

        var alert = Assert.Single(await h.ActiveAlertsAsync());
        Assert.Equal(2, alert.Count);
    }

    [Fact]
    public async Task HeartbeatSilence_FiresAndRecoveryResolves()
    {
        using var h = new Harness();
        await h.AddHostAsync();
        await h.AddSourceAsync();
        await h.AddRuleAsync("rule_hb", "heartbeat", "{\"miss_s\":30}");

        // Stale heartbeat → silence alert.
        await h.Db.Db.Writer.WithTransactionAsync(conn => HeartbeatRepository.UpsertAsync(conn, new HeartbeatRow(
            "src_1", Iso(DateTimeOffset.UtcNow.AddSeconds(-200)), Iso(DateTimeOffset.UtcNow.AddSeconds(-200)),
            "1.0", 1, null, null, 300, "false", null, null, null)));
        await h.Engine.EvaluateAsync(CancellationToken.None);

        var alert = Assert.Single(await h.ActiveAlertsAsync());
        Assert.Equal("warning", alert.Severity); // rule severity, not hard-coded critical
        Assert.Contains("missed_for_s", alert.DetailJson);

        // Fresh heartbeat → resolved.
        await h.Db.Db.Writer.WithTransactionAsync(conn => HeartbeatRepository.UpsertAsync(conn, new HeartbeatRow(
            "src_1", Iso(DateTimeOffset.UtcNow), Iso(DateTimeOffset.UtcNow),
            "1.0", 1, null, null, 300, "false", null, null, null)));
        await h.Engine.EvaluateAsync(CancellationToken.None);

        Assert.Empty(await h.ActiveAlertsAsync());
    }

    [Fact]
    public async Task MaintenanceWindow_SilencesNewAlerts()
    {
        using var h = new Harness();
        await h.AddHostAsync();
        await h.AddRuleAsync("rule_health", "health", "{\"states\":[\"warning\",\"critical\"]}");
        var rule = await h.RuleAsync("rule_health");
        await h.Db.Db.Writer.WithTransactionAsync(conn => AlertRepo.InsertWindowAsync(conn, "win_1", "host_1",
            Iso(DateTimeOffset.UtcNow.AddMinutes(-5)), Iso(DateTimeOffset.UtcNow.AddMinutes(5)), "firmware update", "admin"));

        await h.Engine.FireAsync(rule, "host_1", null, "sig-1", "warning", "{}", CancellationToken.None);

        var alert = Assert.Single(await h.Db.Db.Alerts.ListAsync());
        Assert.Equal("silenced", alert.Status);
        Assert.Equal(0, await h.Db.Db.Alerts.QueueDepthAsync()); // no notifications while silenced
    }

    [Fact]
    public async Task UnreachableSignal_FiresAndPollSuccessResolves()
    {
        using var h = new Harness();
        await h.AddHostAsync();
        await h.AddRuleAsync("rule_unreachable", "heartbeat", "{\"unreachable\":true}");

        h.Engine.Signal(AlertEngineService.SignalKind.HostUnreachable, null, "host_1", 3, "connection timeout");
        await h.Engine.EvaluateAsync(CancellationToken.None);

        var alert = Assert.Single(await h.ActiveAlertsAsync());
        Assert.Contains("consecutive_failures", alert.DetailJson);

        h.Engine.Signal(AlertEngineService.SignalKind.PollSuccess, null, "host_1", null, null);
        await h.Engine.EvaluateAsync(CancellationToken.None);

        Assert.Empty(await h.ActiveAlertsAsync());
    }

    [Fact]
    public async Task NotificationCooldown_SuppressesRapidRefires_AndIsPersisted()
    {
        using var h = new Harness();
        await h.AddHostAsync();
        await h.AddRuleAsync("rule_health", "health", "{\"states\":[\"warning\",\"critical\"]}", cooldown: 300);
        var rule = await h.RuleAsync("rule_health");
        await h.Db.Db.Writer.WithTransactionAsync(async conn =>
        {
            await ChannelRepository.InsertAsync(conn, "chan_1", "chan", "telegram", "cfg-label", true);
            await AlertRepo.SetRuleChannelsAsync(conn, "rule_health", new[] { "chan_1" });
        });
        await h.Db.Db.Writer.WithTransactionAsync(conn =>
            AlertRepo.UpsertAsync(conn, "alert_1", "rule_health", "host_1", null, "warning", "sig-1", WireTime.NowMs(), "{}"));

        var alert = await h.Db.Db.Alerts.GetAsync("alert_1");
        await h.Engine.FireAsync(rule, "host_1", null, "sig-1", "warning", "{}", CancellationToken.None);
        Assert.Equal(1, await h.Db.Db.Alerts.QueueDepthAsync()); // first fire notifies

        await h.Engine.FireAsync(rule, "host_1", null, "sig-1", "warning", "{}", CancellationToken.None);
        Assert.Equal(1, await h.Db.Db.Alerts.QueueDepthAsync()); // cooldown: no second notification

        // Restart-safety: cooldown is persisted; an old last_notified_at re-enables notification.
        await h.Db.Db.Writer.WithTransactionAsync(conn =>
            AlertRepo.MarkNotifiedAsync(conn, alert!.Id, Iso(DateTimeOffset.UtcNow.AddMinutes(-10))));
        await h.Engine.FireAsync(rule, "host_1", null, "sig-1", "warning", "{}", CancellationToken.None);
        Assert.Equal(2, await h.Db.Db.Alerts.QueueDepthAsync());
    }
}
