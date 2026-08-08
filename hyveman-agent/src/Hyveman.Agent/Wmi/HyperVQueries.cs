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
    public const uint ReqName = 0;                 // Name (always returned regardless)
    public const uint ReqElementName = 1;          // ElementName
    public const uint ReqEnabledState = 100;       // EnabledState
    public const uint ReqProcessorLoad = 101;      // ProcessorLoad (uint16, %)
    public const uint ReqMemoryUsage = 103;        // MemoryUsage (uint64, MB)
    public const uint ReqHeartbeat = 104;          // Heartbeat (uint16: 0 unknown,1 OK,2 error,3 no contact,4 lost comm)
    public const uint ReqUptime = 105;             // UpTime (uint64, s)
    public const uint ReqHealthState = 109;        // HealthState (uint16)
    public const uint ReqMemoryAvailable = 112;    // MemoryAvailable (uint64, MB)

    public static readonly uint[] SummaryRequested =
        { ReqName, ReqElementName, ReqEnabledState, ReqProcessorLoad, ReqMemoryUsage, ReqHeartbeat, ReqUptime, ReqHealthState, ReqMemoryAvailable };

    /// <summary>WQL for the VM list (Msvm_ComputerSystem, filtered to VMs).</summary>
    public const string VmListWql = "SELECT * FROM Msvm_ComputerSystem WHERE Caption = 'Virtual Machine'";

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

    /// <summary>SummaryInformation Heartbeat (104) → heartbeat_ok (PROTOCOL §7.1).</summary>
    public static bool? MapHeartbeat(ushort heartbeat) => heartbeat switch
    {
        1 => true,      // OK
        2 => false,     // Error
        3 => false,     // No contact
        4 => false,     // Lost communication
        _ => null       // 0 = unknown / not available
    };

    /// <summary>
    /// Reads the summary-info CimInstances returned by GetSummaryInformation.
    /// Property values are null when not requested / unavailable.
    /// </summary>
    public static VmFact? ToVmFact(CimInstance summary)
    {
        string? name = GetString(summary, "Name");
        if (string.IsNullOrEmpty(name))
            return null; // entry for a VM that could not be found

        var state = MapState(GetUInt16(summary, "EnabledState") ?? 0);
        var heartbeat = GetUInt16(summary, "Heartbeat");
        var cpu = GetUInt16(summary, "ProcessorLoad");
        var mem = GetUInt64(summary, "MemoryUsage");

        bool? heartbeatOk = state == "on" ? MapHeartbeat(heartbeat ?? 0) : null;

        return new VmFact
        {
            Name = name,
            State = state,
            HeartbeatOk = heartbeatOk,
            CpuPct = cpu is null ? null : Math.Round(cpu.Value / 100.0, 2),
            MemMb = mem is null ? null : (long?)mem.Value,
            LastSeenUtc = DateTime.UtcNow
        };
    }

    private static string? GetString(CimInstance instance, string prop)
        => instance.CimInstanceProperties[prop]?.Value as string;

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
