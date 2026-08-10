using Microsoft.Management.Infrastructure;

namespace Hyveman.Agent.Wmi;

/// <summary>
/// The Hyper-V WMI query table (AGENT.md §7; companion to the Hyper-V WMI
/// reference sub-doc). All queries hit root\virtualization\v2 via a CimSession
/// with a per-query timeout. GetSummaryInformation request IDs are pinned to
/// the documented values for Server 2019 (Msvm_SummaryInformationBase).
/// </summary>
public static class HyperVQueries
{
    public const string Namespace = @"root\virtualization\v2";

    // Msvm_SummaryInformation / GetSummaryInformation request IDs
    // (MS-WIN32 docs, GetSummaryInformation method).
    public const uint ReqName = 0;                 // Name = VM GUID (always returned regardless)
    public const uint ReqElementName = 1;          // ElementName = friendly display name
    public const uint ReqEnabledState = 100;       // EnabledState
    public const uint ReqProcessorLoad = 101;      // ProcessorLoad (uint16, %)
    public const uint ReqMemoryUsage = 103;        // MemoryUsage (uint64, MB)
    public const uint ReqHeartbeat = 104;          // Heartbeat (uint16, VMHeartbeat enum: 0 unknown,1 OK,2 OK+app healthy,3 OK+app not healthy,4 no contact,5 lost comm,6 error)
    public const uint ReqUptime = 105;             // UpTime (uint64, s)
    public const uint ReqHealthState = 109;        // HealthState (uint16)
    public const uint ReqMemoryAvailable = 112;    // MemoryAvailable (uint64, MB)

    public static readonly uint[] SummaryRequested =
        { ReqName, ReqElementName, ReqEnabledState, ReqProcessorLoad, ReqMemoryUsage, ReqHeartbeat, ReqUptime, ReqHealthState, ReqMemoryAvailable };

    /// <summary>WQL for the VM list (Msvm_ComputerSystem, filtered to VMs).</summary>
    public const string VmListWql = "SELECT * FROM Msvm_ComputerSystem WHERE Caption = 'Virtual Machine'";

    /// <summary>
    /// WQL for Hyper-V Replica relationships (AGENT.md §7). One instance per
    /// VM replication relationship (primary side on the replicating host,
    /// inbound on the replica host); the WMI behind Get-VMReplication.
    /// Joined to the VM list via SystemName = VM GUID.
    /// </summary>
    public const string ReplicationRelationshipWql = "SELECT * FROM Msvm_ReplicationRelationship";

    // Msvm_VirtualSystemManagementService is a singleton class — enumerate by class name.
    public const string ServiceClass = "Msvm_VirtualSystemManagementService";

    /// <summary>EnabledState → wire state (PROTOCOL §7.1).</summary>
    public static string MapState(ushort enabledState) => enabledState switch
    {
        2 => "on",      // Running
        3 => "off",     // Off
        6 => "saved",   // Saved (Offline)
        9 => "paused",  // Paused
        0 => "unknown",
        _ => "other"
    };

    /// <summary>
    /// SummaryInformation Heartbeat (104) → heartbeat_ok (PROTOCOL §7.1).
    /// Values are the Hyper-V VMHeartbeat enum (the same enum Get-VM surfaces;
    /// Microsoft.HyperV.PowerShell.VMHeartbeat):
    ///   0 Unknown · 1 Ok · 2 OkApplicationsHealthy · 3 OkApplicationsNotHealthy
    ///   4 OkApplicationsUnknown · 5 NoContact · 6 LostCommunication · 7 Error
    /// States 1–4 all mean the guest IS heartbeating (2–4 add application-health
    /// detail Windows guests report and Linux guests don't) → true. 5–7 are
    /// real failure states → false. The old table (2=error, 3=no contact,
    /// 4=lost comm) was the pre-application-health 5-value enum: modern
    /// Hyper-V inserted the app-health states at 2–4, so healthy Windows
    /// guests (2 = OkApplicationsHealthy) and healthy Linux guests (4 =
    /// OkApplicationsUnknown) were both shown as Lost. Note: 3
    /// (OkApplicationsNotHealthy) is guest-reported application health —
    /// heartbeat_ok in protocol v1 cannot carry it, so it is treated as
    /// heartbeat OK (the guest is responsive).
    /// </summary>
    public static bool? MapHeartbeat(ushort heartbeat) => heartbeat switch
    {
        1 => true,      // Ok
        2 => true,      // OkApplicationsHealthy
        3 => true,      // OkApplicationsNotHealthy (heartbeat OK; app health not representable in v1)
        4 => true,      // OkApplicationsUnknown (Linux guests: no app-health reporting)
        5 => false,     // No contact
        6 => false,     // Lost communication
        7 => false,     // Error
        _ => null       // 0 = unknown / not available
    };

    /// <summary>
    /// Msvm_ReplicationRelationship.ReplicationState → wire value (PROTOCOL
    /// §7.1). Same enum Get-VMReplication surfaces: 0 Disabled · 1 Error ·
    /// 2 Enabled · 3 ReplicationInProgress · 4 PlannedFailoverInProgress ·
    /// 5 SnapshotInProgress · 6 InitialReplicationInProgress ·
    /// 7 InitialReplicationPendingForCompletion · 8 RecoveryInProgress ·
    /// 9 FailbackInProgress · 10 FailbackComplete · 11 Discarded. Out-of-range
    /// → null (never a literal "unknown").
    /// </summary>
    public static string? MapReplicationState(ushort state) => state switch
    {
        0 => "disabled",
        1 => "error",
        2 => "enabled",
        3 => "replication_in_progress",
        4 => "planned_failover_in_progress",
        5 => "snapshot_in_progress",
        6 => "initial_replication_in_progress",
        7 => "initial_replication_pending",
        8 => "recovery_in_progress",
        9 => "failback_in_progress",
        10 => "failback_complete",
        11 => "discarded",
        _ => null
    };

    /// <summary>
    /// Msvm_ReplicationRelationship.ReplicationHealth → wire value (PROTOCOL
    /// §7.1): 0 NotApplicable · 1 Ok · 2 Warning · 3 Critical. Out-of-range →
    /// null.
    /// </summary>
    public static string? MapReplicationHealth(ushort health) => health switch
    {
        0 => "not_applicable",
        1 => "ok",
        2 => "warning",
        3 => "critical",
        _ => null
    };

    /// <summary>
    /// Reads the replication-facts CimInstances returned by the
    /// Msvm_ReplicationRelationship enumeration.
    ///
    /// Empirical note (Server 2019 host, 2026): the class's key properties
    /// SystemName/Name come back EMPTY from the provider — they cannot be the
    /// join key. What is populated: ElementName (the VM's display name) and
    /// InstanceID ("Microsoft:&lt;VM-GUID&gt;\HVR\&lt;n&gt;"). The agent joins on
    /// ElementName first (both sides verified populated), then falls back to
    /// the InstanceID-embedded GUID against Msvm_SummaryInformation.Name.
    /// Extended-replica hosts expose two relationships per VM; no reliable
    /// primary discriminator exists in WMI, so the last enumerated wins
    /// (documented simplification, AGENT.md §7).
    /// </summary>
    public static ReplicationFact? ToReplicationFact(CimInstance rel)
    {
        string? elementName = GetString(rel, "ElementName");
        string? guid = TryParseInstanceIdGuid(GetString(rel, "InstanceID"));
        if (string.IsNullOrEmpty(elementName) && guid is null)
            return null;

        return new ReplicationFact
        {
            VmElementName = string.IsNullOrEmpty(elementName) ? null : elementName,
            VmGuid = guid,
            State = MapReplicationState(GetUInt16(rel, "ReplicationState") ?? 0),
            Health = MapReplicationHealth(GetUInt16(rel, "ReplicationHealth") ?? 0),
            LastApplyTimeUtc = GetDateTime(rel, "LastApplyTime")
        };
    }

    /// <summary>
    /// Extracts the VM GUID from an InstanceID like
    /// "Microsoft:4708A0F4-C902-429B-A1E0-D4AB0893E452\HVR\0". Returns null
    /// when the shape does not match (unknown prefix/separator — the caller
    /// falls back to the ElementName join, so a format change degrades
    /// gracefully instead of failing the scan).
    /// </summary>
    public static string? TryParseInstanceIdGuid(string? instanceId)
    {
        if (string.IsNullOrEmpty(instanceId))
            return null;
        var segment = instanceId.Split('\\')[0];
        var colon = segment.LastIndexOf(':');
        var guid = colon >= 0 ? segment[(colon + 1)..] : segment;
        return guid.Length == 36 && guid[8] == '-' && guid[13] == '-' && guid[18] == '-' && guid[23] == '-'
            ? guid
            : null;
    }

    /// <summary>
    /// Reads the summary-info CimInstances returned by GetSummaryInformation.
    /// Property values are null when not requested / unavailable.
    /// Replication facts (from Msvm_ReplicationRelationship) are attached by
    /// VM GUID (InstanceID-embedded) first, then by ElementName — both
    /// case-insensitive; a VM without a relationship reports null replication
    /// fields (not replicated — PROTOCOL §7.1).
    /// </summary>
    public static VmFact? ToVmFact(CimInstance summary,
        IReadOnlyDictionary<string, ReplicationFact>? replicationByGuid = null,
        IReadOnlyDictionary<string, ReplicationFact>? replicationByName = null)
    {
        // Name (request ID 0) is the VM GUID — the precision join key for
        // replication relationships. ElementName is the friendly display name
        // (and the relationship-side join key, verified populated).
        string? guid = GetString(summary, "Name");
        string? name = GetString(summary, "ElementName") ?? guid;
        if (string.IsNullOrEmpty(name))
            return null; // entry for a VM that could not be found

        var state = MapState(GetUInt16(summary, "EnabledState") ?? 0);
        var heartbeat = GetUInt16(summary, "Heartbeat");
        var cpu = GetUInt16(summary, "ProcessorLoad");
        var mem = GetUInt64(summary, "MemoryUsage");

        bool? heartbeatOk = state == "on" ? MapHeartbeat(heartbeat ?? 0) : null;

        ReplicationFact? repl = null;
        if (guid is not null && replicationByGuid is not null)
            repl = replicationByGuid.GetValueOrDefault(guid);
        repl ??= replicationByName?.GetValueOrDefault(name);

        return new VmFact
        {
            Name = name,
            State = state,
            HeartbeatOk = heartbeatOk,
            CpuPct = cpu is null ? null : Math.Round(cpu.Value / 100.0, 2),
            MemMb = mem is null ? null : (long?)mem.Value,
            ReplicationState = repl?.State,
            ReplicationHealth = repl?.Health,
            LastApplyTimeUtc = repl?.LastApplyTimeUtc,
            LastSeenUtc = DateTime.UtcNow
        };
    }

    private static string? GetString(CimInstance instance, string prop)
        => instance.CimInstanceProperties[prop]?.Value as string;

    private static DateTime? GetDateTime(CimInstance instance, string prop)
    {
        var v = instance.CimInstanceProperties[prop]?.Value;
        switch (v)
        {
            case DateTime dt:
                // CIM's "never" sentinel (1600-12-31, i.e. DateTime.MinValue)
                // surfaces as a real date on disabled relationships — null it.
                return dt.Year < 1900 ? null : dt.Kind == DateTimeKind.Utc ? dt : dt.ToUniversalTime();
            case string s:
                // Defensive: providers may yield the raw CIM_DATETIME string
                // ("20240807150211.000000+000") instead of a typed DateTime.
                return TryParseWmiDateTime(s);
            default:
                return null;
        }
    }

    /// <summary>
    /// Parses the CIM_DATETIME string form ("yyyyMMddHHmmss.ffffff±ooo", with
    /// an optional "*" wildcard segment) into UTC. Returns null on anything
    /// unparseable — a replication last-apply time is nice-to-have, never
    /// worth failing a scan over.
    /// </summary>
    public static DateTime? TryParseWmiDateTime(string raw)
    {
        var s = raw.Trim();
        // CIM_DATETIME: yyyymmddHHMMSS.ffffff±ooo (25 chars, 6 fraction digits).
        if (s.Length != 25 || s[14] != '.')
            return null;

        // Wildcard fields ("********...") appear on unused/unknown timestamps.
        if (s.Contains('*'))
            return null;

        if (!DateTime.TryParseExact(s.AsSpan(0, 14), "yyyyMMddHHmmss",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var dt))
            return null;

        // CIM's "never" sentinel (1600-01-01) — null, not a real timestamp.
        if (dt.Year < 1900)
            return null;

        // Offset: ±hhh (minutes, 3 digits). "+000" / "-000" are UTC.
        var sign = s[21];
        if (sign is not ('+' or '-'))
            return null;
        if (!int.TryParse(s.AsSpan(22, 3), out var offsetMin))
            return null;

        var offset = TimeSpan.FromMinutes(sign == '-' ? -offsetMin : offsetMin);
        return new DateTimeOffset(dt, offset).UtcDateTime;
    }

    private static ushort? GetUInt16(CimInstance instance, string prop)
    {
        var v = instance.CimInstanceProperties[prop]?.Value;
        return v switch
        {
            ushort u => u,
            byte b => b,
            int i when i >= 0 => (ushort)i,
            _ => null
        };
    }

    private static ulong? GetUInt64(CimInstance instance, string prop)
    {
        var v = instance.CimInstanceProperties[prop]?.Value;
        return v switch
        {
            ulong u => u,
            uint i => i,
            int i when i >= 0 => (ulong)i,
            _ => null
        };
    }
}
