using Hyveman.Agent.Options;
using Xunit;

namespace Hyveman.Agent.Tests;

/// <summary>Config validation (AGENT.md §19.A): every invalid field rejected with a clear message.</summary>
public class OptionsValidatorTests
{
    private static AgentOptions Valid() => new()
    {
        Backend = new BackendOptions { Url = "https://hyveman.example.lan:8443", Token = "agt_test123" },
        Spool = new SpoolOptions { Dir = @"C:\ProgramData\hyveman-agent\spool", MaxBytes = 100_000, MinFreeBytes = 1_000_000 },
        DataDir = @"C:\ProgramData\hyveman-agent",
        Channels = new List<ChannelOptions> { new() { Name = "System", Level = LevelName.Warning } }
    };

    private static List<string> Errors(AgentOptions o)
    {
        var r = new AgentOptionsValidator().Validate(null, o);
        return r.Failed ? (r.Failures ?? new List<string>()).ToList() : new List<string>();
    }

    [Fact]
    public void Valid_Config_Passes() => Assert.Empty(Errors(Valid()));

    [Theory]
    [InlineData("", "backend.url: must not be empty")]
    [InlineData("not a url", "backend.url: must be an absolute http(s) URL")]
    [InlineData("ftp://x", "backend.url: must be an absolute http(s) URL")]
    [InlineData("http://remote.example.lan:8443", "backend.url: plain http is only permitted for loopback")]
    [InlineData("https://x.example.lan/base/path", "backend.url: must be a base URL without a path")]
    public void BackendUrl_Rejected(string url, string expected)
    {
        var o = Valid();
        o.Backend.Url = url;
        Assert.Contains(Errors(o), e => e.Contains(expected));
    }

    [Fact]
    public void Http_Loopback_Accepted()
    {
        var o = Valid();
        o.Backend.Url = "http://127.0.0.1:8443"; // lab-only plain http on loopback
        Assert.Empty(Errors(o));
    }

    [Fact]
    public void Missing_CaPath_Rejected()
    {
        var o = Valid();
        o.Backend.CaPath = @"C:\nope\ca.pem";
        Assert.Contains(Errors(o), e => e.Contains("backend.ca_path"));
    }

    [Fact]
    public void Spool_Must_Live_Under_DataDir()
    {
        var o = Valid();
        o.Spool.Dir = @"D:\elsewhere\spool";
        Assert.Contains(Errors(o), e => e.Contains("spool.dir"));
    }

    [Fact]
    public void Spool_Under_DataDir_With_Mixed_Separators_Passes()
    {
        // Regression: CLI override "C:/dev/..." (forward slashes) persisted by
        // the registration rewrite must not invalidate a backslash spool.dir
        // on the next start (previously failed the raw StartsWith check).
        var o = Valid();
        o.DataDir = "C:/Dev/hyveman/devdata/agent";
        o.Spool.Dir = @"C:\Dev\hyveman\devdata\agent\spool";
        Assert.Empty(Errors(o));
    }

    [Fact]
    public void Spool_With_Similar_Prefix_Dir_Rejected()
    {
        // "hyveman-agentX" is not "under" "hyveman-agent" — path-boundary check.
        var o = Valid();
        o.Spool.Dir = @"C:\ProgramData\hyveman-agentX\spool";
        Assert.Contains(Errors(o), e => e.Contains("spool.dir"));
    }

    [Fact]
    public void Spool_Caps_Rejected_When_Inverted()
    {
        var o = Valid();
        o.Spool.MaxBytes = 5_000_000;
        o.Spool.MinFreeBytes = 1_000_000;
        Assert.Contains(Errors(o), e => e.Contains("spool.max_bytes"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void BatchMaxEvents_Rejected(int v)
    {
        var o = Valid();
        o.Limits.BatchMaxEvents = v;
        Assert.Contains(Errors(o), e => e.Contains("batch_max_events"));
    }

    [Fact]
    public void CpuRate_Out_Of_Range_Rejected()
    {
        var o = Valid();
        o.Limits.CpuRatePercent = 0;
        Assert.Contains(Errors(o), e => e.Contains("cpu_rate_percent"));
        o.Limits.CpuRatePercent = 101;
        Assert.Contains(Errors(o), e => e.Contains("cpu_rate_percent"));
    }

    [Fact]
    public void MemoryCap_Too_Small_Rejected()
    {
        var o = Valid();
        o.Limits.ProcessMemoryBytes = 1024;
        Assert.Contains(Errors(o), e => e.Contains("process_memory_bytes"));
    }

    [Fact]
    public void Duplicate_Channels_Rejected()
    {
        var o = Valid();
        o.Channels.Add(new ChannelOptions { Name = "System", Level = LevelName.Error });
        Assert.Contains(Errors(o), e => e.Contains("duplicate channel name 'System'"));
    }

    [Fact]
    public void Verbose_Level_Rejected_As_Unhelpful()
    {
        var o = Valid();
        o.Channels.Add(new ChannelOptions { Name = "Application", Level = LevelName.Verbose });
        Assert.Contains(Errors(o), e => e.Contains("level Verbose is not useful"));
    }

    [Fact]
    public void Empty_Security_IncludeIds_Rejected()
    {
        var o = Valid();
        o.SecurityLog.IncludeIds.Clear();
        Assert.Contains(Errors(o), e => e.Contains("security_log.include_ids"));
    }

    [Fact]
    public void Reg_Token_Shape_Validated()
    {
        var o = Valid();
        o.Backend.Token = null;
        o.Registration = new RegistrationOptions { Token = "not-a-reg-token" };
        Assert.Contains(Errors(o), e => e.Contains("'reg_'"));
    }

    [Fact]
    public void Agent_Token_Shape_Validated()
    {
        var o = Valid();
        o.Backend.Token = "reg_123"; // wrong kind for an ingest token
        Assert.Contains(Errors(o), e => e.Contains("'agt_'"));
    }
}
