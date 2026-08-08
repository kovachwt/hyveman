using Hyveman.Server.Storage.Repos;
using Hyveman.Server.Tests.TestInfra;

namespace Hyveman.Server.Tests.Storage;

/// <summary>
/// Event storage semantics (§13 #11/#15): idempotency on (source_id, dedup_scope, record_id),
/// epoch-prefixed record IDs after channel clears, FTS5 search, field promotion to columns.
/// </summary>
public sealed class EventRepositoryTests
{
    private static EventInsert Evt(string scope, string recordId, string message, string? channel = null,
        long? eventId = null, int? severity = 2, string time = "2025-01-01T00:00:00Z")
        => new("src_1", scope, recordId, time, severity, "Microsoft-Windows-Kernel-Power", message,
            null, null, channel, eventId, null, null, null);

    private static EventSearchQuery Q(string? text = null, string? channel = null, long? eventId = null, int? severityMin = null)
        => new("src_1", text, channel, eventId, severityMin, null, null);

    private async Task<(TestDb db, (int accepted, int deduped))> InsertAsync(params EventInsert[] items)
    {
        var db = await TestDb.CreateAsync();
        // events.source_id → sources FK; the source always exists in production (registration).
        await db.Db.Writer.WithTransactionAsync(async conn =>
            await db.Db.Sources.InsertAsync(conn, "src_1", "windows-agent", "HOST01", null));
        var result = await db.Db.Writer.WithTransactionAsync(conn => EventRepository.InsertBatchAsync(conn, items));
        return (db, result);
    }

    [Fact]
    public async Task InsertBatch_AcceptsNewAndDedupesRepeats()
    {
        var (db, (accepted, deduped)) = await InsertAsync(
            Evt("System", "100", "first"),
            Evt("System", "100", "first again")); // same dedup key → idempotent

        using (db)
        {
            Assert.Equal(1, accepted);
            Assert.Equal(1, deduped);
            Assert.Equal(1, await db.Db.Events.CountAsync());
        }
    }

    [Fact]
    public async Task SameRecordId_DifferentScopes_BothAccepted()
    {
        var (db, (accepted, deduped)) = await InsertAsync(
            Evt("System", "42", "system event"),
            Evt("Application", "42", "application event")); // scope is part of the key

        using (db)
        {
            Assert.Equal(2, accepted);
            Assert.Equal(0, deduped);
        }
    }

    [Fact]
    public async Task EpochPrefixedRecordId_IsDistinctFromBareRecordId()
    {
        // §13 #15: after a channel clear the agent sends "e1:42"; pre-clear "42" must not collide.
        var (db, (accepted, deduped)) = await InsertAsync(
            Evt("Security", "42", "pre-clear"),
            Evt("Security", "e1:42", "post-clear"));

        using (db)
        {
            Assert.Equal(2, accepted);
            Assert.Equal(0, deduped);
        }
    }

    [Fact]
    public async Task Search_ByFreeText_UsesFts()
    {
        var (db, _) = await InsertAsync(
            Evt("System", "1", "disk SMART predictive failure", channel: "System", eventId: 7),
            Evt("Security", "2", "logon success for user", channel: "Security", eventId: 4624));

        using (db)
        {
            var hits = await db.Db.Events.SearchAsync(Q("SMART"));
            var hit = Assert.Single(hits);
            Assert.Contains("disk SMART", hit.Message);
        }
    }

    [Fact]
    public async Task Search_SupportsChannelEventIdAndSeverityFilters()
    {
        var (db, _) = await InsertAsync(
            Evt("System", "1", "disk error", channel: "System", eventId: 7, severity: 2),
            Evt("Application", "2", "disk error", channel: "Application", eventId: 1000, severity: 2),
            Evt("System", "3", "disk warning", channel: "System", eventId: 7, severity: 3));

        using (db)
        {
            var byChannel = await db.Db.Events.SearchAsync(Q(channel: "System"));
            Assert.Equal(2, byChannel.Count);

            var byEventId = await db.Db.Events.SearchAsync(Q(eventId: 7));
            Assert.Equal(2, byEventId.Count);

            var bySeverity = await db.Db.Events.SearchAsync(Q(severityMin: 3));
            var sev = Assert.Single(bySeverity);
            Assert.Equal("disk warning", sev.Message);
        }
    }

    [Fact]
    public async Task Search_OrdersByTimeDescending()
    {
        var (db, _) = await InsertAsync(
            Evt("System", "1", "older", time: "2025-01-01T00:00:00Z"),
            Evt("System", "2", "newer", time: "2025-01-02T00:00:00Z"));

        using (db)
        {
            var hits = await db.Db.Events.SearchAsync(Q());
            Assert.Equal("newer", hits[0].Message);
            Assert.Equal("older", hits[1].Message);
        }
    }

    [Fact]
    public async Task Insert_PromotesWindowsFieldsToColumns()
    {
        var (db, _) = await InsertAsync(
            Evt("Security", "100", "logon", channel: "Security", eventId: 4624));

        using (db)
        {
            var row = Assert.Single(await db.Db.Events.RecentAsync());
            Assert.Equal("Security", row.Channel);
            Assert.Equal(4624, row.EventId);
            Assert.Equal("Microsoft-Windows-Kernel-Power", row.Facility);
        }
    }

    [Theory]
    [InlineData("disk", "\"disk\"*")]
    [InlineData("disk fail", "\"disk\"* AND \"fail\"*")]
    [InlineData("say \"hello\"", "\"say\"* AND \"\"\"hello\"\"\"*")]
    public void FtsQuery_SanitizesAndAndsTerms(string input, string expected)
    {
        Assert.Equal(expected, EventRepository.FtsQuery(input));
    }

    [Fact]
    public void FtsQuery_EmptyText_YieldsEmptyMatch()
    {
        Assert.Equal("", EventRepository.FtsQuery("   "));
    }
}
