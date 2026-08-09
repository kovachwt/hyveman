namespace Hyveman.Domain;

/// <summary>Source kinds per PROTOCOL.md §10. The kind disambiguates the native
/// severity/facility scale; there is intentionally no severity_scale field.</summary>
public static class SourceKinds
{
    public const string WindowsAgent = "windows-agent";
    public const string LinuxAgent = "linux-agent";
    public const string SyslogFeed = "syslog-feed";

    public static readonly string[] Known = [WindowsAgent, LinuxAgent, SyslogFeed];

    public static bool IsKnown(string kind) => Known.Contains(kind);

    /// <summary>Valid native severity range for the source kind (PROTOCOL §10).</summary>
    public static (int Min, int Max) SeverityRange(string kind) => kind switch
    {
        SyslogFeed => (0, 7),        // RFC 5424
        _ => (1, 5),                 // Windows Level 1..5 (omitted when Level is 0)
    };

    /// <summary>Default severity applied at ingest when the item omits it.</summary>
    public static int DefaultSeverity(string kind) => kind switch
    {
        SyslogFeed => 6,             // informational
        _ => 4,                      // Windows Information
    };
}

/// <summary>Vendor-neutral component/rollup health state (DESIGN §5.2).</summary>
public enum HealthState
{
    Unknown = 0,
    Ok = 1,
    Warning = 2,
    Critical = 3,
}

public static class HealthStates
{
    public static string ToWire(HealthState s) => s switch
    {
        HealthState.Ok => "ok",
        HealthState.Warning => "warning",
        HealthState.Critical => "critical",
        _ => "unknown",
    };

    public static HealthState FromWire(string s) => s switch
    {
        "ok" => HealthState.Ok,
        "warning" => HealthState.Warning,
        "critical" => HealthState.Critical,
        _ => HealthState.Unknown,
    };

    public static HealthState Max(HealthState a, HealthState b) =>
        (int)a >= (int)b ? a : b;
}

/// <summary>Alert lifecycle status (API.md §7.3 / §9.3).</summary>
public static class AlertStatuses
{
    public const string Active = "active";
    public const string Acknowledged = "acknowledged";
    public const string Silenced = "silenced";
    public const string Resolved = "resolved";

    /// <summary>Statuses that represent a live (non-resolved) occurrence.</summary>
    public static readonly string[] Live = [Active, Acknowledged, Silenced];
}

/// <summary>Alert rule types (API.md §9.3, DESIGN §4.4).</summary>
public static class RuleTypes
{
    public const string Health = "health";
    public const string Event = "event";
    public const string Heartbeat = "heartbeat";
    public const string Threshold = "threshold";

    public static readonly string[] Known = [Health, Event, Heartbeat, Threshold];
}

/// <summary>Notification channel kinds (DESIGN §4.4).</summary>
public static class ChannelKinds
{
    public const string Telegram = "telegram";
    public const string Webhook = "webhook";
    public const string Smtp = "smtp";

    public static readonly string[] Known = [Telegram, Webhook, Smtp];
}

/// <summary>Credential vault entry kinds.</summary>
public static class CredentialKinds
{
    public const string Idrac = "idrac";
    public const string Telegram = "telegram";
    public const string Webhook = "webhook";
    public const string Smtp = "smtp";
}

/// <summary>Component types in the vendor-neutral model (DESIGN §5.2).</summary>
public static class ComponentTypes
{
    public const string Cpu = "cpu";
    public const string Memory = "memory";
    public const string Disk = "disk";
    public const string Controller = "controller";
    public const string Psu = "psu";
    public const string Fan = "fan";
    public const string Temp = "temp";
    public const string Chassis = "chassis";
    public const string System = "system";
    public const string Other = "other";

    public static readonly string[] Known =
        [Cpu, Memory, Disk, Controller, Psu, Fan, Temp, Chassis, System, Other];
}

/// <summary>VM power states per PROTOCOL.md §7.1.</summary>
public static class VmStates
{
    public const string On = "on";
    public const string Off = "off";
    public const string Paused = "paused";
    public const string Saved = "saved";
    public const string Other = "other";
    public const string Unknown = "unknown";

    public static readonly string[] Known = [On, Off, Paused, Saved, Other, Unknown];
}

/// <summary>Agent degraded states per PROTOCOL.md §7.1.</summary>
public static class DegradedStates
{
    public const string None = "";
    public const string SpoolFull = "spool_full";
    public const string Overrun = "overrun";
    public const string AuthRejected = "auth_rejected";
    public const string Quarantined = "quarantined";
    public const string WmiDegraded = "wmi_degraded";
    public const string ChannelReset = "channel_reset";

    public static readonly string[] Known = [None, SpoolFull, Overrun, AuthRejected, Quarantined, WmiDegraded, ChannelReset];
}

/// <summary>Token kinds/prefixes per PROTOCOL.md §4.1.</summary>
public static class TokenKinds
{
    public const string Registration = "reg_";
    public const string Agent = "agt_";

    public const string ScopeRegister = "register";
    public const string ScopeIngest = "ingest";
}
