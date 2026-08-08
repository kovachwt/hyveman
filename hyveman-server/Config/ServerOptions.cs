namespace Hyveman.Server.Config;

/// <summary>
/// Mirrors <c>config/server.json</c> (SERVER.md §5.3). All options validated at startup;
/// invalid → fail fast with a clear message.
/// </summary>
public sealed class ServerOptions
{
    public const int CurrentProtocolVersion = 1;

    /// <summary>Persisted data directory (used for resolution precedence, §5.1).</summary>
    public string? DataDir { get; set; }
    public string Urls { get; set; } = "https://0.0.0.0:443";
    public TlsOptions Tls { get; set; } = new();
    public IngestOptions Ingest { get; set; } = new();
    public PollerOptions Poller { get; set; } = new();
    public AlertsOptions Alerts { get; set; } = new();
    public NotificationsOptions Notifications { get; set; } = new();
    public RetentionOptions Retention { get; set; } = new();
    public BackupOptions Backup { get; set; } = new();
    public WebOptions Web { get; set; } = new();
    public LoggingOptions Logging { get; set; } = new();

    public sealed class TlsOptions
    {
        public string? CertPath { get; set; }
        public string? CertPassword { get; set; }
        public string? CertPasswordRef { get; set; }   // "label:tls-cert-password" vault ref (§5.4)
        public string MinTls { get; set; } = "1.2";
        public string PreferredTls { get; set; } = "1.3";
    }

    public sealed class IngestOptions
    {
        public int MaxBatchBytes { get; set; } = 4 * 1024 * 1024;       // PROTOCOL §12
        public int MaxItems { get; set; } = 1000;
        public int MaxRawBytes { get; set; } = 16 * 1024;
        public int MaxMessageBytes { get; set; } = 64 * 1024;
        public int MaxFieldBytes { get; set; } = 64 * 1024;
        public int MaxRecordIdLen { get; set; } = 128;
        public RateLimitConfig PerSourceRate { get; set; } = new() { RequestsPerMin = 120, BytesPerMin = 32 * 1024 * 1024 };
        public RateLimitConfig GlobalRate { get; set; } = new() { RequestsPerMin = 1200, BytesPerMin = 256 * 1024 * 1024 };
        // Registration tokens (no source yet) get a small budget (§7.4).
        public RateLimitConfig RegisterRate { get; set; } = new() { RequestsPerMin = 10, BytesPerMin = 2 * 1024 * 1024 };
    }

    public sealed class RateLimitConfig
    {
        public int RequestsPerMin { get; set; } = 120;
        public int BytesPerMin { get; set; } = 32 * 1024 * 1024;
    }

    public sealed class PollerOptions
    {
        public int IntervalS { get; set; } = 60;
        public int TimeoutS { get; set; } = 15;
        public int Concurrency { get; set; } = 4;
    }

    public sealed class AlertsOptions
    {
        public int SweepS { get; set; } = 10;
        public int DefaultHeartbeatMissS { get; set; } = 180;
        public int IdracUnreachablePolls { get; set; } = 3;   // N consecutive poll failures → alert
    }

    public sealed class NotificationsOptions
    {
        public WebhookOptions Webhook { get; set; } = new();
    }

    public sealed class WebhookOptions
    {
        public bool AllowPrivate { get; set; } = false;
        public List<string> AllowedHosts { get; set; } = new();   // e.g. ["10.0.0.5", "intranet.local"]
    }

    public sealed class RetentionOptions
    {
        public int EventsDays { get; set; } = 365;
        public int MetricsDays { get; set; } = 365;
        public int HealthSnapshotsDays { get; set; } = 365;
        public int AuditDays { get; set; } = 730;
        public int ResolvedAlertsDays { get; set; } = 730;
        public bool VacuumAfterPurge { get; set; } = true;
    }

    public sealed class BackupOptions
    {
        public string TimeLocal { get; set; } = "03:00";
        public int KeepDaily { get; set; } = 7;
        public int KeepWeekly { get; set; } = 4;
        public int KeepMonthly { get; set; } = 12;
    }

    public sealed class WebOptions
    {
        public int SessionDays { get; set; } = 14;
        public List<string> TrustedNetworks { get; set; } = new();  // CIDR list for /auth/setup access
        public int AuthRequestsPerMin { get; set; } = 30;           // per-IP cap on /api/auth/* (§12.2)
    }

    public sealed class LoggingOptions
    {
        public string Level { get; set; } = "Information";
        public int FileRetainDays { get; set; } = 14;
    }
}
