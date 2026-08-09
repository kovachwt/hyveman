using Hyveman.Application;
using Hyveman.Contracts;
using Hyveman.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Hyveman.Tests.Application;

/// <summary>Overview iDRAC poll-status mapping (API.md §7.1/§9.1): the tile
/// must reflect real poll attempts and failures, not component freshness —
/// a host whose polls keep failing shows "Failed · time" with the error
/// instead of "never polled" forever.</summary>
public class OverviewIdracStatusTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-09T20:48:16Z");

    private static HostRecord Host(string id, string? idracUrl = "https://idrac.example") => new(
        id, "HOST01", "windows-server", null, idracUrl, null, true, null, Now, Now);

    private sealed class FakeHosts(List<HostRecord> hosts) : IHostStore
    {
        public Task<IReadOnlyList<HostRecord>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<HostRecord>>(hosts);
        public Task<HostRecord?> GetAsync(string id, CancellationToken ct) => Task.FromResult(hosts.FirstOrDefault(h => h.Id == id));
        public Task<HostRecord?> GetBySourceAsync(string sourceId, CancellationToken ct) => Task.FromResult<HostRecord?>(null);
        public Task<HostRecord> CreateAsync(HostRecord host, CancellationToken ct) => Task.FromResult(host);
        public Task<bool> UpdateAsync(HostRecord host, DateTimeOffset expectedUpdatedAt, CancellationToken ct) => Task.FromResult(true);
        public Task DeleteAsync(string id, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakePollStatus(Dictionary<string, PollStatusRecord> rows) : IPollStatusStore
    {
        public Task<PollStatusRecord?> GetAsync(string hostId, CancellationToken ct)
            => Task.FromResult(rows.GetValueOrDefault(hostId));
        public Task MarkSuccessAsync(string hostId, DateTimeOffset at, CancellationToken ct) => Task.CompletedTask;
        public Task MarkFailureAsync(string hostId, DateTimeOffset at, string? error, CancellationToken ct) => Task.CompletedTask;
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

    private sealed class NoopAlerts : IAlertStore
    {
        public Task<AlertRecord?> FindLiveAsync(string key, CancellationToken ct) => Task.FromResult<AlertRecord?>(null);
        public Task<AlertRecord?> GetAsync(string id, CancellationToken ct) => Task.FromResult<AlertRecord?>(null);
        public Task CreateAsync(AlertRecord alert, CancellationToken ct) => Task.CompletedTask;
        public Task UpdateAsync(AlertRecord alert, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<AlertRecord>> ListAsync(AlertQuery query, CancellationToken ct) => Task.FromResult<IReadOnlyList<AlertRecord>>([]);
        public Task<IReadOnlyList<AlertRecord>> ListLiveAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<AlertRecord>>([]);
        public Task<long> CountLiveAsync(CancellationToken ct) => Task.FromResult(0L);
        public Task<long> CountUnacknowledgedAsync(CancellationToken ct) => Task.FromResult(0L);
        public Task ResolveForHostAsync(string hostId, DateTimeOffset at, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class NoopSources : ISourceStore
    {
        public Task<Source?> GetByIdAsync(string id, CancellationToken ct) => Task.FromResult<Source?>(null);
        public Task<Source?> GetByKindNameAsync(string kind, string name, CancellationToken ct) => Task.FromResult<Source?>(null);
        public Task<Source> CreateAsync(string kind, string name, DateTimeOffset now, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<Source>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<Source>>([]);
        public Task DeleteAsync(string id, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }

    private static async Task<HostTileDto?> TileAsync(
        List<HostRecord> hosts, Dictionary<string, PollStatusRecord> pollRows, string hostId)
    {
        var svc = new OverviewService(
            new FakeHosts(hosts), new NoopHealth(), new NoopAgentStatus(), new NoopAlerts(),
            new NoopSources(), new FakePollStatus(pollRows), new FixedClock(Now),
            NullLogger<OverviewService>.Instance);
        var response = await svc.BuildAsync(CancellationToken.None);
        return response.Hosts.FirstOrDefault(t => t.Id == hostId);
    }

    [Fact]
    public async Task FailingPolls_ShowAttemptTimeAndError_NotNeverPolled()
    {
        var tile = await TileAsync(
            [Host("h1")],
            new Dictionary<string, PollStatusRecord>
            {
                ["h1"] = new("h1", Now.AddMinutes(-1), null, "System resource not reachable", 5),
            },
            "h1");
        Assert.NotNull(tile);
        Assert.True(tile!.Idrac!.Configured);
        Assert.Equal(Now.AddMinutes(-1), tile.Idrac.LastPoll);
        Assert.False(tile.Idrac.LastPollOk);
        Assert.Equal("System resource not reachable", tile.Idrac.LastError);
    }

    [Fact]
    public async Task SuccessfulPoll_ShowsOk_WithNoError()
    {
        var tile = await TileAsync(
            [Host("h1")],
            new Dictionary<string, PollStatusRecord>
            {
                ["h1"] = new("h1", Now.AddMinutes(-2), Now.AddMinutes(-2), null, 0),
            },
            "h1");
        Assert.NotNull(tile);
        Assert.True(tile!.Idrac!.LastPollOk);
        Assert.Equal(Now.AddMinutes(-2), tile.Idrac.LastPoll);
        Assert.Null(tile.Idrac.LastError);
    }

    [Fact]
    public async Task ConfiguredButNeverAttempted_ShowsNeverPolled()
    {
        var tile = await TileAsync([Host("h1")], [], "h1");
        Assert.NotNull(tile);
        Assert.True(tile!.Idrac!.Configured);
        Assert.Null(tile.Idrac.LastPoll);
        Assert.False(tile.Idrac.LastPollOk); // no attempt has ever succeeded
        Assert.Null(tile.Idrac.LastError);
    }

    [Fact]
    public async Task NoIdracUrl_HasNoIdracTile()
    {
        var tile = await TileAsync([Host("h1", idracUrl: null)], [], "h1");
        Assert.NotNull(tile);
        Assert.Null(tile!.Idrac);
    }
}
