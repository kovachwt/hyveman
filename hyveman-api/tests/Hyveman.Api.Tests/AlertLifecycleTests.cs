using System.Net;
using System.Text;
using System.Text.Json;
using Hyveman.Application;
using Hyveman.Domain;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Hyveman.Tests.Api;

/// <summary>DEFECTS.md D2/D3 regression tests through the real DI container and
/// SQLite: second fire→resolve cycles must not throw, derived-alerting failure
/// must not fail an accepted telemetry request, and evaluator transition state
/// must be durable across instances (fresh scope per tick, exactly like the
/// poller and the request paths).</summary>
[Collection("api")]
public class AlertLifecycleTests
{
    private readonly ApiFixture _fx;

    public AlertLifecycleTests(ApiFixture fx) => _fx = fx;

    [Fact]
    public async Task Telemetry_Returns200_WhenAlertEvaluatorThrows()
    {
        // D2: the heartbeat-clear path runs on every telemetry POST. A failure
        // in that derived work must never convert an accepted request into a
        // retry-looping 500.
        var token = await _fx.RegisterAgentAsync("TELE-THROW-1");
        var client = _fx.NewClientWithEvaluator<ThrowingEvaluator>();

        using var req = new HttpRequestMessage(HttpMethod.Post, "/ingest/telemetry");
        req.Headers.Add("X-Hyveman-Protocol", "1");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        req.Content = new StringContent("""
            {"v":1,"items":[
              {"kind":"heartbeat","sent_at":"2024-08-07T10:30:00Z","boot_time":"2024-08-01T00:00:00Z","uptime_s":100,"degraded":""}]}
            """, Encoding.UTF8, "application/json");

        var resp = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.GetProperty("accepted").GetBoolean());
    }

    [Fact]
    public async Task HealthAlert_TwoWarningToOkCycles_ResolveAndKeepHistory()
    {
        // D3: transition state is durable. Each tick runs in a fresh scope —
        // a fresh evaluator instance, exactly like HardwarePollingService's
        // per-tick scope — and the previous state is read from the store, so
        // warning→ok resolves. The second cycle additionally exercises D2:
        // resolving a key that already has resolved history must not throw.
        var now = DateTimeOffset.UtcNow;
        const string hostId = "hst_d3";
        const string ruleId = "rul_d3";

        using (var seed = _fx.Factory.Services.CreateScope())
        {
            var hosts = seed.ServiceProvider.GetRequiredService<IHostStore>();
            await hosts.CreateAsync(new HostRecord(hostId, "HOST-D3", "windows-server", null, null, null,
                true, null, now, now), CancellationToken.None);
            var rules = seed.ServiceProvider.GetRequiredService<IRuleStore>();
            await rules.CreateAsync(new RuleRecord(ruleId, "disk health", RuleTypes.Health,
                """{"componentTypes":["disk"],"states":["warning","critical"],"includeRollup":false}""",
                "warning", 0, true, now, now), CancellationToken.None);
        }

        var diskOk = new ComponentRecord(hostId, ComponentTypes.Disk, "Disk 0", HealthState.Ok, null, now);
        var diskWarn = diskOk with { State = HealthState.Warning };

        async Task Tick(string rollup, ComponentRecord comp, DateTimeOffset at)
        {
            using var scope = _fx.Factory.Services.CreateScope();
            var eval = scope.ServiceProvider.GetRequiredService<IAlertEvaluator>();
            await eval.OnHealthStateChangedAsync(hostId, rollup, [comp], at, CancellationToken.None);
            // The poller replaces stored components after evaluation (D3).
            var health = scope.ServiceProvider.GetRequiredService<IHealthStore>();
            await health.ReplaceComponentsAsync(hostId, [comp], CancellationToken.None);
        }

        // Cycle 1: warning → ok.
        await Tick("warning", diskWarn, now);
        await Tick("ok", diskOk, now.AddMinutes(1));
        // Cycle 2: warning → ok (second resolve of the key; D2).
        await Tick("warning", diskWarn, now.AddMinutes(2));
        await Tick("ok", diskOk, now.AddMinutes(3));

        using var check = _fx.Factory.Services.CreateScope();
        var alerts = check.ServiceProvider.GetRequiredService<IAlertStore>();
        // Filter to this host: the shared fixture DB may hold alerts from other
        // tests in the collection (e.g. the D1 mixed-batch contract test).
        var all = await alerts.ListAsync(new AlertQuery(null, hostId, null, null, null, 50, null), CancellationToken.None);
        Assert.Equal(2, all.Count); // history retains both occurrences
        Assert.All(all, a =>
        {
            Assert.Equal(hostId, a.HostId);
            Assert.Equal(AlertStatuses.Resolved, a.Status);
            Assert.NotNull(a.ResolvedAt);
        });
        Assert.Equal(0, await alerts.CountLiveAsync(CancellationToken.None));
    }

    [Fact]
    public async Task EventRule_RefireInsideCooldown_Suppressed()
    {
        // D3: cooldown keys off the most recent occurrence's last_seen (the
        // resolved alert's last_seen), so it holds across evaluator instances.
        var token = await _fx.RegisterAgentAsync("CD-01");
        string sourceId;
        using (var seed = _fx.Factory.Services.CreateScope())
        {
            var sources = seed.ServiceProvider.GetRequiredService<ISourceStore>();
            var src = await sources.GetByKindNameAsync(SourceKinds.WindowsAgent, "CD-01", CancellationToken.None);
            sourceId = src!.Id;
            var rules = seed.ServiceProvider.GetRequiredService<IRuleStore>();
            await rules.CreateAsync(new RuleRecord("rul_cd", "6008", RuleTypes.Event,
                """{"channel":"System","eventIds":[6008]}""", "warning", 3600, true,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow), CancellationToken.None);
        }

        var now = DateTimeOffset.UtcNow;
        var item = new ValidatedLogItem("System", "e1", now, 2, "Microsoft-Windows-Kernel-Power",
            "unexpected shutdown", "{}", null, "System", 6008, 0, 0, null);
        var key = $"rul_cd|-|{sourceId}|event:System:6008";

        // Fire once.
        using (var s1 = _fx.Factory.Services.CreateScope())
        {
            var eval = s1.ServiceProvider.GetRequiredService<IAlertEvaluator>();
            await eval.OnEventsAcceptedAsync(sourceId, [item], CancellationToken.None);
        }

        // Resolve the occurrence (e.g. acknowledgement or a clear path).
        using (var s2 = _fx.Factory.Services.CreateScope())
        {
            var alerts = s2.ServiceProvider.GetRequiredService<IAlertStore>();
            var live = await alerts.FindLiveAsync(key, CancellationToken.None);
            Assert.NotNull(live);
            await alerts.UpdateAsync(live! with { Status = AlertStatuses.Resolved, ResolvedAt = now, UpdatedAt = now }, CancellationToken.None);
        }

        // Refire inside the cooldown window from a fresh evaluator: suppressed.
        using (var s3 = _fx.Factory.Services.CreateScope())
        {
            var eval = s3.ServiceProvider.GetRequiredService<IAlertEvaluator>();
            await eval.OnEventsAcceptedAsync(sourceId, [item with { RecordId = "e2", Message = "again" }], CancellationToken.None);

            var alerts = s3.ServiceProvider.GetRequiredService<IAlertStore>();
            Assert.Null(await alerts.FindLiveAsync(key, CancellationToken.None));
            var all = await alerts.ListAsync(new AlertQuery(null, null, "rul_cd", null, null, 50, null), CancellationToken.None);
            Assert.Single(all); // only the resolved occurrence
        }
    }
}

/// <summary>IAlertEvaluator whose derived-alerting path throws; every other
/// path is a no-op.</summary>
public sealed class ThrowingEvaluator : IAlertEvaluator
{
    public Task OnEventsAcceptedAsync(string sourceId, IReadOnlyList<ValidatedLogItem> items, CancellationToken ct)
        => Task.CompletedTask;

    public Task OnHealthStateChangedAsync(string hostId, string rollupState,
        IReadOnlyList<ComponentRecord> components, DateTimeOffset at, CancellationToken ct)
        => Task.CompletedTask;

    public Task OnHeartbeatSilenceChangedAsync(string? ruleId, string sourceId, bool silent,
        DateTimeOffset at, CancellationToken ct)
        => throw new InvalidOperationException("simulated derived-alerting failure");

    public Task OnThresholdsAsync(string hostId, IReadOnlyList<MetricRecord> metrics,
        DateTimeOffset at, CancellationToken ct)
        => Task.CompletedTask;

    public Task ReconcileAsync(CancellationToken ct) => Task.CompletedTask;
}
