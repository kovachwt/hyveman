using Hyveman.Server.Alerts;

namespace Hyveman.Server.Tests.Alerts;

/// <summary>Rule match_json parsing (health/heartbeat rule predicates).</summary>
public sealed class RuleMatchTests
{
    [Fact]
    public void ParseMatch_ReadsStatesAndTypes()
    {
        var m = AlertEngineService.ParseMatch("{\"states\":[\"warning\",\"critical\"],\"types\":[\"disk\",\"psu\"]}");

        Assert.Equal(new[] { "warning", "critical" }, m.States);
        Assert.Equal(new[] { "disk", "psu" }, m.Types);
    }

    [Fact]
    public void ParseMatch_ReadsMissSecondsAndUnreachable()
    {
        var m = AlertEngineService.ParseMatch("{\"miss_s\":180,\"unreachable\":true}");

        Assert.Equal(180, m.MissS);
        Assert.True(m.Unreachable);
    }

    [Fact]
    public void ParseMatch_InvalidJson_YieldsDefaults()
    {
        var m = AlertEngineService.ParseMatch("not json {{{");

        Assert.Equal(0, m.MissS);
        Assert.False(m.Unreachable);
        Assert.Empty(m.States);
        Assert.Empty(m.Types);
    }

    [Fact]
    public void ParseMatch_MissingProperties_YieldDefaults()
    {
        var m = AlertEngineService.ParseMatch("{}");

        Assert.Equal(0, m.MissS);
        Assert.False(m.Unreachable);
        Assert.Empty(m.States);
        Assert.Empty(m.Types);
    }
}
