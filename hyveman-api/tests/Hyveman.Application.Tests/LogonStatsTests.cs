using Hyveman.Application;
using Hyveman.Domain;
using Xunit;

namespace Hyveman.Tests.Application;

public class LogonStatsTests
{
    private static ValidatedLogItem Item(long eventId, string user, string? logonType = null, string? channel = "Security") => new(
        DedupScope: "Security",
        RecordId: $"r{eventId}",
        Time: DateTimeOffset.Parse("2024-08-07T10:00:00Z"),
        Severity: 4,
        Facility: "Microsoft-Windows-Security-Auditing",
        Message: "logon",
        FieldsJson: BuildFields(user, logonType),
        RawJson: null,
        Channel: channel,
        EventId: eventId,
        Task: 0,
        Opcode: 0,
        Keywords: null);

    private static string BuildFields(string? user, string? logonType) =>
        logonType is null
            ? $"{{\"channel\":\"Security\",\"event_id\":6008,\"event_data\":{{\"TargetUserName\":\"{user}\"}}}}"
            : $"{{\"channel\":\"Security\",\"event_id\":6008,\"event_data\":{{\"TargetUserName\":\"{user}\",\"LogonType\":\"{logonType}\"}}}}";

    [Fact]
    public void Extract_4624InteractiveAndRdp_AreSuccesses()
    {
        var entries = LogonStatsService.ExtractEntries(
            [Item(4624, "admin", "2"), Item(4624, "admin", "10")]);

        // LogonType is part of the (day, user, logon_type) key, so the two
        // types aggregate into separate rows.
        Assert.Equal(2, entries.Count);
        Assert.All(entries, e =>
        {
            Assert.Equal("admin", e.User);
            Assert.Equal("2024-08-07", e.Day);
            Assert.Equal(1, e.SuccessDelta);
            Assert.Equal(0, e.FailureDelta);
        });
        Assert.Equal([2, 10], entries.Select(e => e.LogonType).Order().ToArray());
    }

    [Fact]
    public void Extract_4624NonInteractiveType_IsDropped()
    {
        // Curated policy (DESIGN §4.1): only LogonType 2 and 10 count as successes.
        var entries = LogonStatsService.ExtractEntries([Item(4624, "svc", "3")]);
        Assert.Empty(entries);
    }

    [Fact]
    public void Extract_4625And4740_AreFailures()
    {
        var entries = LogonStatsService.ExtractEntries(
            [Item(4625, "bob", "3"), Item(4740, "bob")]);

        Assert.Equal(2, entries.Count);
        var failed = Assert.Single(entries, e => e.LogonType == 3);
        Assert.Equal("bob", failed.User);
        Assert.Equal(1, failed.FailureDelta);
        var locked = Assert.Single(entries, e => e.LogonType is null);
        Assert.Equal("bob", locked.User);
        Assert.Equal(1, locked.FailureDelta);
    }

    [Fact]
    public void Extract_StringOrNumberLogonType_BothAccepted()
    {
        // The agent serializes event_data as strings; PROTOCOL examples use numbers.
        var asString = Item(4624, "admin", "10");
        var asNumber = asString with { FieldsJson = """{"event_data":{"TargetUserName":"admin","LogonType":10}}""" };

        var entries = LogonStatsService.ExtractEntries([asString, asNumber]);
        var admin = Assert.Single(entries);
        Assert.Equal(2, admin.SuccessDelta);
    }

    [Fact]
    public void Extract_NonSecurityChannel_IsIgnored()
    {
        var entries = LogonStatsService.ExtractEntries([Item(4624, "admin", "2", channel: "Application")]);
        Assert.Empty(entries);
    }

    [Fact]
    public void Extract_OtherEventIds_AreIgnored()
    {
        var entries = LogonStatsService.ExtractEntries([Item(6008, "admin", "2")]);
        Assert.Empty(entries);
    }

    [Fact]
    public void Extract_MissingTargetUser_IsIgnored()
    {
        var noUser = Item(4625, "x", "3") with { FieldsJson = """{"event_data":{"LogonType":"3"}}""" };
        Assert.Empty(LogonStatsService.ExtractEntries([noUser]));
    }

    [Fact]
    public void Extract_Day_UsesUtcEventDay()
    {
        var lateUtc = Item(4625, "bob", "3") with
        {
            Time = new DateTimeOffset(2024, 8, 7, 23, 59, 59, TimeSpan.Zero),
        };
        var entries = LogonStatsService.ExtractEntries([lateUtc]);
        Assert.Equal("2024-08-07", Assert.Single(entries).Day);
    }

    private sealed class FakeLogonStatsStore(List<LogonStatEntry> sink) : ILogonStatsStore
    {
        public Task IncrementAsync(string sourceId, IReadOnlyList<LogonStatEntry> entries, CancellationToken ct)
        {
            Assert.Equal("src_1", sourceId);
            sink.AddRange(entries);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<LogonStatRow>> QueryAsync(LogonStatsQuery query, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<LogonStatRow>>([]);
    }

    [Fact]
    public async Task RecordAccepted_EmptyBatch_DoesNotTouchStore()
    {
        var sink = new List<LogonStatEntry>();
        var svc = new LogonStatsService(new FakeLogonStatsStore(sink));

        await svc.RecordAcceptedAsync("src_1", [], CancellationToken.None);

        Assert.Empty(sink);
    }

    [Fact]
    public async Task RecordAccepted_PassesMergedEntries()
    {
        var sink = new List<LogonStatEntry>();
        var svc = new LogonStatsService(new FakeLogonStatsStore(sink));

        await svc.RecordAcceptedAsync("src_1",
            [Item(4624, "admin", "2"), Item(4624, "admin", "2"), Item(4625, "bob", "3")],
            CancellationToken.None);

        Assert.Equal(2, sink.Count);
        Assert.Equal(2, Assert.Single(sink, e => e.User == "admin").SuccessDelta);
        Assert.Equal(1, Assert.Single(sink, e => e.User == "bob").FailureDelta);
    }
}
