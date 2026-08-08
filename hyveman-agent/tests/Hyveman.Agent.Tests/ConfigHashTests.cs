using Hyveman.Agent.Options;
using Xunit;

namespace Hyveman.Agent.Tests;

/// <summary>Config hashing + hot/cold change classification (AGENT.md §8, §10).</summary>
public class ConfigHashTests
{
    [Fact]
    public void Hash_Is_Stable_And_Short()
    {
        var raw = System.Text.Encoding.UTF8.GetBytes("{\"backend\":{\"url\":\"https://x\"}}");
        var h1 = ConfigLoader.ComputeHash(raw);
        var h2 = ConfigLoader.ComputeHash(raw);
        Assert.Equal(h1, h2);
        Assert.Equal(6, h1.Length);
        Assert.All(h1, c => Assert.True(Uri.IsHexDigit(c)));
    }

    [Fact]
    public void Hash_Changes_With_Content()
    {
        var a = ConfigLoader.ComputeHash(System.Text.Encoding.UTF8.GetBytes("{\"a\":1}"));
        var b = ConfigLoader.ComputeHash(System.Text.Encoding.UTF8.GetBytes("{\"a\":2}"));
        Assert.NotEqual(a, b);
    }

    private static AgentOptions Base() => new()
    {
        Backend = new BackendOptions { Url = "https://x", Token = "agt_1" },
        Spool = new SpoolOptions { Dir = @"C:\ProgramData\hyveman-agent\spool" },
        Channels = { new ChannelOptions { Name = "System", Level = LevelName.Warning } }
    };

    [Theory]
    [InlineData("url")]      // backend.url
    [InlineData("token")]    // backend.token
    [InlineData("spool")]    // spool.dir
    [InlineData("cap")]      // spool.max_bytes
    [InlineData("channel")]  // channel set
    [InlineData("raw")]      // limits.max_raw_bytes
    [InlineData("concurrency")] // limits.send_concurrency
    public void Structural_Changes_Require_Restart(string which)
    {
        var a = Base();
        var b = Base();
        switch (which)
        {
            case "url": b.Backend.Url = "https://y"; break;
            case "token": b.Backend.Token = "agt_2"; break;
            case "spool": b.Spool.Dir = @"C:\ProgramData\hyveman-agent\spool2"; break;
            case "cap": b.Spool.MaxBytes = 99; break;
            case "channel": b.Channels.Add(new ChannelOptions { Name = "Application" }); break;
            case "raw": b.Limits.MaxRawBytes = 4096; break;
            case "concurrency": b.Limits.SendConcurrency = 4; break;
        }
        Assert.True(ConfigChangeKind.IsStructural(a, b), $"{which} must be structural (restart required)");
    }

    [Theory]
    [InlineData("level")]
    [InlineData("include")]
    [InlineData("exclude")]
    [InlineData("heartbeat")]
    [InlineData("wmi")]
    public void Safe_Subset_Is_Hot_Reloadable(string which)
    {
        var a = Base();
        var b = Base();
        switch (which)
        {
            case "level": b.Channels[0].Level = LevelName.Error; break;
            case "include": b.Channels[0].IncludeIds = new List<uint> { 1, 2 }; break;
            case "exclude": b.Channels[0].ExcludeIds = new List<uint> { 3 }; break;
            case "heartbeat": b.Heartbeat.IntervalS = 15; break;
            case "wmi": b.Wmi.ScanIntervalS = 120; break;
        }
        Assert.False(ConfigChangeKind.IsStructural(a, b), $"{which} must hot-apply without restart");
    }
}
