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
        /// <summary>Let's Encrypt (ACME) auto-provisioning; mutually exclusive with cert_path.</summary>
        public LetsEncryptOptions LetsEncrypt { get; set; } = new();
    }

    /// <summary>
    /// Let's Encrypt automatic certificate provisioning (ACME v2, http-01 challenge).
    /// When enabled, the server owns its certificate lifecycle: the account key and the
    /// issued PFX live in <c>&lt;data_dir&gt;/certs/</c>, renewal happens automatically in
    /// the background, and the http-01 challenge is served on <see cref="HttpPort"/>.
    /// </summary>
    public sealed class LetsEncryptOptions
    {
        public bool Enabled { get; set; }
        /// <summary>Public DNS names the certificate must cover (SANs). No wildcards — http-01 cannot validate them.</summary>
        public List<string> Domains { get; set; } = new();
        /// <summary>ACME account contact; used for expiry/revocation notices.</summary>
        public string? Email { get; set; }
        /// <summary>Use the Let's Encrypt staging endpoint (rate-limit-safe testing).</summary>
        public bool Staging { get; set; }
        /// <summary>Renew when the certificate expires within this many days (1..89; LE certs are 90-day).</summary>
        public int RenewDays { get; set; } = 30;
        /// <summary>Port for the plain-HTTP http-01 challenge listener (and HTTP→HTTPS redirect).</summary>
        public int HttpPort { get; set; } = 80;
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
