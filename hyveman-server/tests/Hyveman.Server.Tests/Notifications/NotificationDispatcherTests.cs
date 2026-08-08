using System.Security.Cryptography;
using Hyveman.Server.Auth;
using Hyveman.Server.Common;
using Hyveman.Server.Config;
using Hyveman.Server.Notifications;
using Hyveman.Server.Observability;
using Hyveman.Server.Storage.Repos;
using Hyveman.Server.Tests.TestInfra;
using Microsoft.Extensions.Logging.Abstractions;
using AlertRepo = Hyveman.Server.Storage.Repos.AlertRepository;

namespace Hyveman.Server.Tests.Notifications;

/// <summary>
/// Durable notification queue (§9.5): enqueue per rule channel, backoff on transient failure,
/// drop + audit on permanent failure, restart-safe.
/// </summary>
public sealed class NotificationDispatcherTests
{
    private sealed class Harness : IDisposable
    {
        public TestDb Db { get; }
        public AesGcmCredentialVault Vault { get; }
        public CountingNotifier Notifier { get; } = new() { Kind = "counting" };
        public OwnMetrics Metrics { get; } = new();
        public NotificationDispatcher Dispatcher { get; }

        public Harness()
        {
            Db = TestDb.CreateAsync().GetAwaiter().GetResult();
            Vault = new AesGcmCredentialVault(RandomNumberGenerator.GetBytes(32), Db.Db);
            Dispatcher = new NotificationDispatcher(Db.Db, Vault, new INotifier[] { Notifier },
                new ServerOptions(), Metrics, NullLogger<NotificationDispatcher>.Instance);
        }

        public async Task SeedAsync(bool channelEnabled = true, bool withConfig = true)
        {
            await Db.Db.Writer.WithTransactionAsync(async conn =>
            {
                // alerts.source_id → sources FK; the source always exists in production.
                await Db.Db.Sources.InsertAsync(conn, "src_1", "windows-agent", "HOST01", null);
                await Db.Db.Alerts.InsertRuleAsync(conn, "rule_1", "test rule", "health", "{}", "warning", 300);
                await ChannelRepository.InsertAsync(conn, "chan_1", "chan", Notifier.Kind, "cfg-label", channelEnabled);
                await AlertRepo.SetRuleChannelsAsync(conn, "rule_1", new[] { "chan_1" });
                await AlertRepo.UpsertAsync(conn, "alert_1", "rule_1", null, "src_1", "warning",
                    "sig-1", WireTime.NowMs(), "{\"component\":\"disk:DISK1\"}");
            });
            if (withConfig)
                await Vault.PutSecretAsync("cfg-label", Notifier.Kind, "{}", "test");
        }

        public async Task<AlertRow> AlertAsync()
            => await Db.Db.Alerts.GetAsync("alert_1") ?? throw new InvalidOperationException("alert_1 missing");

        public void Dispose() => Db.Dispose();
    }

    [Fact]
    public async Task Enqueue_WithNoChannels_QueuesNothing()
    {
        using var h = new Harness();
        await h.Db.Db.Writer.WithTransactionAsync(async conn =>
        {
            await h.Db.Db.Sources.InsertAsync(conn, "src_1", "windows-agent", "HOST01", null);
            await h.Db.Db.Alerts.InsertRuleAsync(conn, "rule_1", "test rule", "health", "{}", "warning", 300);
            await AlertRepo.UpsertAsync(conn, "alert_1", "rule_1", null, "src_1", "warning", "sig-1", WireTime.NowMs(), null);
        });

        await h.Dispatcher.EnqueueAsync(await h.AlertAsync(), CancellationToken.None);

        Assert.Equal(0, await h.Db.Db.Alerts.QueueDepthAsync());
        Assert.Equal(0, h.Metrics.NotificationsQueued);
    }

    [Fact]
    public async Task SuccessfulDelivery_RemovesRowAndCountsMetrics()
    {
        using var h = new Harness();
        await h.SeedAsync();

        await h.Dispatcher.EnqueueAsync(await h.AlertAsync(), CancellationToken.None);
        Assert.Equal(1, await h.Db.Db.Alerts.QueueDepthAsync());

        await h.Dispatcher.DrainAsync(CancellationToken.None);

        Assert.Equal(0, await h.Db.Db.Alerts.QueueDepthAsync());
        Assert.Equal(1, h.Notifier.Calls);
        Assert.Equal(1, h.Metrics.NotificationsSent);
        Assert.Equal(1, h.Metrics.NotificationsQueued);
        Assert.Equal("alert_1", h.Notifier.Received[0].AlertId);
    }

    [Fact]
    public async Task PermanentFailure_SurfacesChannelErrorDropsRowAndAudits()
    {
        using var h = new Harness();
        h.Notifier.Result = new NotifyResult(false, true, "invalid token");
        await h.SeedAsync();

        await h.Dispatcher.EnqueueAsync(await h.AlertAsync(), CancellationToken.None);
        await h.Dispatcher.DrainAsync(CancellationToken.None);

        Assert.Equal(0, await h.Db.Db.Alerts.QueueDepthAsync()); // dropped, not retried forever
        Assert.Equal("invalid token", h.Dispatcher.ChannelErrors["chan_1"]);
        var audit = await h.Db.Db.Audit.ListAsync();
        Assert.Contains(audit, a => a.Action == "channel.permanent_failure" && a.TargetId == "chan_1");
    }

    [Fact]
    public async Task TransientFailure_KeepsRowWithBackoffAndError()
    {
        using var h = new Harness();
        h.Notifier.Result = new NotifyResult(false, false, "telegram HTTP 500");
        await h.SeedAsync();

        await h.Dispatcher.EnqueueAsync(await h.AlertAsync(), CancellationToken.None);
        await h.Dispatcher.DrainAsync(CancellationToken.None);

        Assert.Equal(1, await h.Db.Db.Alerts.QueueDepthAsync()); // kept for retry
        var due = await h.Db.Db.Alerts.DueAsync(WireTime.NowMs());
        Assert.Empty(due); // next_at is in the future (backoff)

        // Inspect the row directly to verify attempts + error were persisted.
        var row = await h.Db.Db.Writer.ReadAsync(async conn =>
            await Dapper.SqlMapper.QueryFirstAsync<QueueRow>(conn, "SELECT id, alert_id, channel_id, attempts, next_at, last_error FROM notification_queue"));
        Assert.Equal(1, row.Attempts);
        Assert.Equal("telegram HTTP 500", row.LastError);
    }

    [Fact]
    public async Task DisabledChannel_IsDroppedWithoutNotifying()
    {
        using var h = new Harness();
        await h.SeedAsync(channelEnabled: false);

        await h.Dispatcher.EnqueueAsync(await h.AlertAsync(), CancellationToken.None);
        await h.Dispatcher.DrainAsync(CancellationToken.None);

        Assert.Equal(0, await h.Db.Db.Alerts.QueueDepthAsync());
        Assert.Equal(0, h.Notifier.Calls);
    }

    [Fact]
    public async Task MissingVaultConfig_BacksOffInsteadOfCrashing()
    {
        using var h = new Harness();
        await h.SeedAsync(withConfig: false);

        await h.Dispatcher.EnqueueAsync(await h.AlertAsync(), CancellationToken.None);
        await h.Dispatcher.DrainAsync(CancellationToken.None);

        Assert.Equal(1, await h.Db.Db.Alerts.QueueDepthAsync()); // failed, kept for retry
        var row = await h.Db.Db.Writer.ReadAsync(async conn =>
            await Dapper.SqlMapper.QueryFirstAsync<QueueRow>(conn, "SELECT id, alert_id, channel_id, attempts, next_at, last_error FROM notification_queue"));
        Assert.Equal(1, row.Attempts);
        Assert.NotEmpty(row.LastError!);
    }
}
