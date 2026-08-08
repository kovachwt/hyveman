using System.Text.Json.Serialization;

namespace Hyveman.Agent.Options;

/// <summary>
/// Strongly typed mirror of agent.json (AGENT.md §10).
/// </summary>
public sealed class AgentOptions
{
    public BackendOptions Backend { get; set; } = new();
    public SpoolOptions Spool { get; set; } = new();
    public LimitsOptions Limits { get; set; } = new();
    public WmiOptions Wmi { get; set; } = new();
    public HeartbeatOptions Heartbeat { get; set; } = new();
    public SecurityLogOptions SecurityLog { get; set; } = new();
    public List<ChannelOptions> Channels { get; set; } = new();

    /// <summary>Optional registration bootstrap (install token). Removed after exchange (PROTOCOL §5).</summary>
    public RegistrationOptions? Registration { get; set; }

    /// <summary>Corroborating source id returned by POST /register; authoritative identity is the token.</summary>
    public string? SourceId { get; set; }

    public LoggingOptions Logging { get; set; } = new();

    /// <summary>Everything lives under this directory (DESIGN §9 single-data-dir rule).</summary>
    public string DataDir { get; set; } = @"C:\ProgramData\hyveman-agent";
}

public sealed class BackendOptions
{
    /// <summary>Base URL, no trailing slash. https only.</summary>
    public string Url { get; set; } = "https://hyveman.example.lan:8443";

    /// <summary>Ingest-scope bearer token (agt_...). Empty until registration exchange completes.</summary>
    public string? Token { get; set; }

    /// <summary>Optional pinned CA (PEM/DER file) for lab networks; null = system store.</summary>
    public string? CaPath { get; set; }

    /// <summary>false = skip certificate validation (DISCOURAGED; lab only, logged loudly).</summary>
    public bool ValidateCert { get; set; } = true;
}

public sealed class SpoolOptions
{
    public string Dir { get; set; } = @"C:\ProgramData\hyveman-agent\spool";

    /// <summary>Absolute spool cap (bytes). Default 100 MiB (H1).</summary>
    public long MaxBytes { get; set; } = 100L * 1024 * 1024;

    /// <summary>Never push volume free space below this (bytes). Default 5 GiB (H1).</summary>
    public long MinFreeBytes { get; set; } = 5L * 1024 * 1024 * 1024;
}

public sealed class LimitsOptions
{
    /// <summary>Job Object process memory cap; OS kills the agent on exceed (H4). Default 256 MiB.</summary>
    public long ProcessMemoryBytes { get; set; } = 256L * 1024 * 1024;

    /// <summary>Job Object CPU rate cap, % of a single logical processor (H5). Default 25.</summary>
    public int CpuRatePercent { get; set; } = 25;

    /// <summary>Bounded in-memory channel size (H3). Default 10 000.</summary>
    public int InMemoryQueueEvents { get; set; } = 10000;

    public int BatchMaxEvents { get; set; } = 500;
    public int BatchMaxAgeMs { get; set; } = 1000;
    public int MaxBatchBytes { get; set; } = 4 * 1024 * 1024;
    public int MaxRawBytes { get; set; } = 8192;
    public int SendConcurrency { get; set; } = 2;
    public int SendTimeoutMs { get; set; } = 30000;

    /// <summary>gzip request bodies for /ingest/logs (PROTOCOL §9). Default on.</summary>
    public bool Gzip { get; set; } = true;

    /// <summary>Bounded shutdown grace for in-flight uploads (AGENT §17).</summary>
    public int ShutdownGraceS { get; set; } = 10;
}

public sealed class WmiOptions
{
    public int ScanIntervalS { get; set; } = 60;
    public int QueryTimeoutS { get; set; } = 20;
    public int MaxQueriesPerScan { get; set; } = 8;
}

public sealed class HeartbeatOptions
{
    public int IntervalS { get; set; } = 30;
}

public sealed class SecurityLogOptions
{
    public bool Enabled { get; set; } = true;
    public List<uint> IncludeIds { get; set; } = new() { 4624, 4625, 4740 };
    public List<int> LogonTypesFor4624 { get; set; } = new() { 2, 10 };
}

public sealed class RegistrationOptions
{
    /// <summary>One-time install token (reg_...), exchanged for an ingest token on first contact.</summary>
    public string? Token { get; set; }

    public string Kind { get; set; } = "windows-agent";

    public string? AgentVersion { get; set; }

    public string? OsBuild { get; set; }

    /// <summary>Opaque host fingerprint, aids reinstall de-dup.</summary>
    public string? BootId { get; set; }
}

public sealed class ChannelOptions
{
    /// <summary>Config identity; also the default dedup scope (PROTOCOL §11.1).</summary>
    public string Name { get; set; } = "";

    /// <summary>Actual event-log channel to subscribe to. Defaults to <see cref="Name"/>.
    /// The self-collect "HyvemanAgent" entry maps to channel "Application" + provider filter.</summary>
    public string? Channel { get; set; }

    /// <summary>Optional provider-name XPath constraint, e.g. "HyvemanAgent" for self-collect.</summary>
    public string? Provider { get; set; }

    /// <summary>Min level: "Critical"|"Error"|"Warning"|"Information"|"Verbose" (1..5).</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<LevelName>))]
    public LevelName? Level { get; set; }

    public List<uint>? IncludeIds { get; set; }
    public List<uint>? ExcludeIds { get; set; }
}

public enum LevelName
{
    Critical = 1,
    Error = 2,
    Warning = 3,
    Information = 4,
    Verbose = 5
}

public sealed class LoggingOptions
{
    public string Level { get; set; } = "Information";
    public string Dir { get; set; } = @"C:\ProgramData\hyveman-agent\logs";
    public string Rolling { get; set; } = "10MBx5";
}
