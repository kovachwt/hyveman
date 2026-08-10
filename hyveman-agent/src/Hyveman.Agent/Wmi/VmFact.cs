namespace Hyveman.Agent.Wmi;

/// <summary>One Msvm_ReplicationRelationship row, joined to a VM.</summary>
public sealed class ReplicationFact
{
    /// <summary>VM display name (relationship ElementName) — the join key
    /// verified populated on real hosts (AGENT.md §7).</summary>
    public string? VmElementName { get; init; }

    /// <summary>VM GUID parsed from InstanceID ("Microsoft:&lt;guid&gt;\HVR\&lt;n&gt;");
    /// null when unparseable. Precision fallback join key.</summary>
    public string? VmGuid { get; init; }

    public string? State { get; init; }                       // PROTOCOL §7.1 replication_state
    public string? Health { get; init; }                      // PROTOCOL §7.1 replication_health
    public DateTime? LastApplyTimeUtc { get; init; }          // LastApplyTime, UTC
}

/// <summary>One VM fact row (PROTOCOL §7.1 facts item).</summary>
public sealed class VmFact
{
    public required string Name { get; init; }
    public string State { get; init; } = "unknown";   // on|off|paused|saved|other|unknown
    public bool? HeartbeatOk { get; init; }           // true|false|null
    public double? CpuPct { get; init; }
    public long? MemMb { get; init; }
    public string? ReplicationState { get; init; }    // null when not replicated (PROTOCOL §7.1)
    public string? ReplicationHealth { get; init; }   // null when not replicated (PROTOCOL §7.1)
    public DateTime? LastApplyTimeUtc { get; init; }  // replication LastApplyTime, UTC
    public DateTime LastSeenUtc { get; init; }
}
