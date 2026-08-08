using Hyveman.Agent.Options;
using Hyveman.Agent.Pipeline;
using Hyveman.Agent.Wevtapi;
using Xunit;

namespace Hyveman.Agent.Tests;

/// <summary>
/// XPath query + curated-Security filter tests (AGENT.md §6.2/§6.4, §19.A)
/// for representative events: 6008, 4624 LT2, 4624 LT3-dropped, 4625, 4740.
/// </summary>
public class ChannelQueryBuilderTests
{
    private static SecurityLogOptions Security() => new()
    {
        Enabled = true,
        IncludeIds = new() { 4624, 4625, 4740 },
        LogonTypesFor4624 = new() { 2, 10 }
    };

    [Fact]
    public void Warning_Level_System_Channel()
    {
        var ch = new ChannelOptions { Name = "System", Level = LevelName.Warning };
        Assert.Equal("*[System[Level<=3]]", ChannelQueryBuilder.Build(ch, Security(), "System"));
    }

    [Fact]
    public void Include_Ids_Become_Or_Predicate()
    {
        var ch = new ChannelOptions { Name = "App", Level = LevelName.Error, IncludeIds = new() { 1000, 1001 } };
        Assert.Equal("*[System[Level<=2 and (EventID=1000 or EventID=1001)]]",
            ChannelQueryBuilder.Build(ch, Security(), "App"));
    }

    [Fact]
    public void Exclude_Ids_Become_And_Predicate()
    {
        var ch = new ChannelOptions { Name = "App", Level = LevelName.Warning, ExcludeIds = new() { 999 } };
        Assert.Equal("*[System[Level<=3 and (EventID!=999)]]",
            ChannelQueryBuilder.Build(ch, Security(), "App"));
    }

    [Fact]
    public void Security_Uses_Curated_Ids_Not_Level()
    {
        var ch = new ChannelOptions { Name = "Security", Level = LevelName.Warning };
        Assert.Equal("*[System[(EventID=4624 or EventID=4625 or EventID=4740)]]",
            ChannelQueryBuilder.Build(ch, Security(), "Security"));
    }

    [Fact]
    public void SelfCollect_Entry_Provider_And_Id_Allowlist()
    {
        var ch = new ChannelOptions
        {
            Name = "HyvemanAgent",
            Channel = "Application",
            Provider = "HyvemanAgent",
            IncludeIds = new() { 1, 2, 3, 4, 5 }
        };
        Assert.Equal("*[System[Provider[@Name='HyvemanAgent'] and (EventID=1 or EventID=2 or EventID=3 or EventID=4 or EventID=5)]]",
            ChannelQueryBuilder.Build(ch, Security(), "Application"));
    }

    [Fact]
    public void No_Filters_Yields_Wildcard()
    {
        var ch = new ChannelOptions { Name = "Any" };
        Assert.Equal("*", ChannelQueryBuilder.Build(ch, Security(), "Any"));
    }

    // ---- 4624 LogonType post-filter ----

    private static EvtLogEvent SecurityEvent(uint id, string? logonType)
    {
        var ev = new EvtLogEvent { Channel = "Security", EventId = id };
        if (logonType is not null)
            ev.EventData = new Dictionary<string, string?> { ["LogonType"] = logonType, ["TargetUserName"] = "admin" };
        return ev;
    }

    [Theory]
    [InlineData("2")]   // interactive/console — kept
    [InlineData("10")]  // RDP — kept
    public void LogonType_2_And_10_Kept(string lt)
    {
        Assert.True(SecurityFilter.ShouldKeep(SecurityEvent(4624, lt), Security()));
    }

    [Theory]
    [InlineData("3")]   // network
    [InlineData("4")]   // batch
    [InlineData("5")]   // service
    [InlineData("7")]   // unlock
    [InlineData("11")]  // cached interactive
    public void Other_LogonTypes_Dropped_Silently(string lt)
    {
        Assert.False(SecurityFilter.ShouldKeep(SecurityEvent(4624, lt), Security()));
    }

    [Fact]
    public void Missing_LogonType_4624_Dropped()
    {
        Assert.False(SecurityFilter.ShouldKeep(SecurityEvent(4624, logonType: null), Security()));
    }

    [Fact]
    public void NonNumeric_LogonType_Dropped()
    {
        Assert.False(SecurityFilter.ShouldKeep(SecurityEvent(4624, "network"), Security()));
    }

    [Fact]
    public void FailedLogon_4625_Passes_Through()
    {
        Assert.True(SecurityFilter.ShouldKeep(SecurityEvent(4625, "3"), Security()));
    }

    [Fact]
    public void Lockout_4740_Passes_Through()
    {
        Assert.True(SecurityFilter.ShouldKeep(SecurityEvent(4740, null), Security()));
    }

    [Fact]
    public void Non_Security_Events_Unaffected()
    {
        var ev = new EvtLogEvent { Channel = "System", EventId = 6008 };
        Assert.True(SecurityFilter.ShouldKeep(ev, Security()));
    }

    [Fact]
    public void Disabled_Security_Log_Passes_Everything()
    {
        var sec = Security();
        sec.Enabled = false;
        Assert.True(SecurityFilter.ShouldKeep(SecurityEvent(4624, "5"), sec));
    }
}
