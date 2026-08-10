using Hyveman.Agent.Wmi;
using Xunit;

namespace Hyveman.Agent.Tests;

/// <summary>
/// Regression tests for the SummaryInformation → wire mappings (AGENT.md §7,
/// PROTOCOL §7.1). The heartbeat table was wrong: Msvm_SummaryInformation.
/// Heartbeat (request ID 104) carries the Hyper-V VMHeartbeat enum
/// (Microsoft.HyperV.PowerShell.VMHeartbeat, the same values Get-VM surfaces):
/// 0 Unknown · 1 Ok · 2 OkApplicationsHealthy · 3 OkApplicationsNotHealthy ·
/// 4 OkApplicationsUnknown · 5 NoContact · 6 LostCommunication · 7 Error.
/// The old table was the pre-application-health 5-value enum, so healthy
/// Windows guests (2) rendered as "Lost". Healthy Linux guests report 4
/// (OkApplicationsUnknown — heartbeat OK, no application-health component),
/// which must also map to true.
/// </summary>
public class HyperVQueriesTests
{
    [Theory]
    [InlineData(1, true)]  // Ok
    [InlineData(2, true)]  // OkApplicationsHealthy — the Windows regression case
    [InlineData(3, true)]  // OkApplicationsNotHealthy — heartbeat OK; app health not representable in v1
    [InlineData(4, true)]  // OkApplicationsUnknown — the Linux regression case
    [InlineData(5, false)] // No contact
    [InlineData(6, false)] // Lost communication
    [InlineData(7, false)] // Error
    public void MapHeartbeat_Maps_VmHeartbeat_Enum(ushort wmiValue, bool expected)
    {
        Assert.Equal(expected, HyperVQueries.MapHeartbeat(wmiValue));
    }

    [Theory]
    [InlineData(0)]   // Unknown / not available
    [InlineData(255)] // out-of-range garbage
    public void MapHeartbeat_Unknown_Is_Null(ushort wmiValue)
    {
        Assert.Null(HyperVQueries.MapHeartbeat(wmiValue));
    }

    [Fact]
    public void MapHeartbeat_Healthy_States_Are_Not_Lost()
    {
        // The exact values Get-VM reports for healthy guests: Windows reports
        // "Ok"/"OkApplicationsHealthy", Linux reports "OkApplicationsUnknown".
        // None of these may render as Lost.
        Assert.True(HyperVQueries.MapHeartbeat(1));
        Assert.True(HyperVQueries.MapHeartbeat(2));
        Assert.True(HyperVQueries.MapHeartbeat(3));
        Assert.True(HyperVQueries.MapHeartbeat(4));
    }

    [Theory]
    [InlineData(2, "on")]     // Running
    [InlineData(3, "off")]    // Off
    [InlineData(6, "saved")]  // Saved (Offline)
    [InlineData(9, "paused")] // Paused
    [InlineData(0, "unknown")]
    [InlineData(42, "other")]
    public void MapState_Covers_EnabledState(ushort enabledState, string expected)
    {
        Assert.Equal(expected, HyperVQueries.MapState(enabledState));
    }

    [Theory]
    [InlineData(0, "disabled")]
    [InlineData(1, "error")]
    [InlineData(2, "enabled")]
    [InlineData(3, "replication_in_progress")]
    [InlineData(4, "planned_failover_in_progress")]
    [InlineData(5, "snapshot_in_progress")]
    [InlineData(6, "initial_replication_in_progress")]
    [InlineData(7, "initial_replication_pending")]
    [InlineData(8, "recovery_in_progress")]
    [InlineData(9, "failback_in_progress")]
    [InlineData(10, "failback_complete")]
    [InlineData(11, "discarded")]
    public void MapReplicationState_Covers_ReplicationState_Enum(ushort wmiValue, string expected)
    {
        Assert.Equal(expected, HyperVQueries.MapReplicationState(wmiValue));
    }

    [Theory]
    [InlineData(255)] // out-of-range garbage
    public void MapReplicationState_Unknown_Is_Null(ushort wmiValue)
    {
        Assert.Null(HyperVQueries.MapReplicationState(wmiValue));
    }

    [Theory]
    [InlineData(0, "not_applicable")]
    [InlineData(1, "ok")]
    [InlineData(2, "warning")]
    [InlineData(3, "critical")]
    public void MapReplicationHealth_Covers_ReplicationHealth_Enum(ushort wmiValue, string expected)
    {
        Assert.Equal(expected, HyperVQueries.MapReplicationHealth(wmiValue));
    }

    [Theory]
    [InlineData(255)]
    public void MapReplicationHealth_Unknown_Is_Null(ushort wmiValue)
    {
        Assert.Null(HyperVQueries.MapReplicationHealth(wmiValue));
    }

    [Theory]
    // UTC form (what the Hyper-V provider yields via MMI as DateTime, and the
    // raw CIM_DATETIME string as a defensive fallback).
    [InlineData("20240807150211.000000+000", "2024-08-07T15:02:11Z")]
    [InlineData("20240807150211.123456+000", "2024-08-07T15:02:11Z")]
    // Local-offset form: normalized to UTC.
    [InlineData("20240807150211.000000-300", "2024-08-07T20:02:11Z")]
    [InlineData("20240807150211.000000+045", "2024-08-07T14:17:11Z")]
    public void TryParseWmiDateTime_Parses_CimDateTimeString(string raw, string expectedUtc)
    {
        var parsed = HyperVQueries.TryParseWmiDateTime(raw);
        Assert.NotNull(parsed);
        Assert.Equal(DateTimeOffset.Parse(expectedUtc).UtcDateTime, parsed!.Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-date")]
    [InlineData("20240807150211.000000")]      // missing offset sign
    [InlineData("20240807150211.000000Z")]     // ISO form, not CIM form
    [InlineData("************************")]   // wildcard (unused timestamp)
    [InlineData("20240807150211.000000+00")]   // truncated offset
    public void TryParseWmiDateTime_Unparseable_Is_Null(string raw)
    {
        Assert.Null(HyperVQueries.TryParseWmiDateTime(raw));
    }
}
