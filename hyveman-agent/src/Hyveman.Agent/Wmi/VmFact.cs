namespace Hyveman.Agent.Wmi;

/// <summary>One VM fact row (PROTOCOL §7.1 facts item).</summary>
public sealed class VmFact
{
    public required string Name { get; init; }
    public string State { get; init; } = "unknown";   // on|off|paused|saved|other|unknown
    public bool? HeartbeatOk { get; init; }           // true|false|null
    public double? CpuPct { get; init; }
    public long? MemMb { get; init; }
    public DateTime LastSeenUtc { get; init; }
}
