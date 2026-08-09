namespace Hyveman.Api;

/// <summary>Server configuration (API.md §11). Loaded from built-in defaults,
/// then {DataDirectory}/config.json, then environment variables / command line.</summary>
public sealed class HyvemanOptions
{
    public string DataDirectory { get; set; } = "data";
    public string PublicOrigin { get; set; } = "";
    public string ApiListenUrls { get; set; } = "http://127.0.0.1:5080";
    public string WebAuthnRpId { get; set; } = "";
    public string WebAuthnExpectedOrigin { get; set; } = "";
    public string ServerVersion { get; set; } = "0.1.0";
    public int AgentProtocolCurrentVersion { get; set; } = 1;
    public int[] AgentProtocolSupportedVersions { get; set; } = [1];
    public int SQLiteBusyTimeoutMs { get; set; } = 5000;
    public int LogRetentionDays { get; set; } = 365;
    public int HardwarePollIntervalS { get; set; } = 60;
    public int HeartbeatSilenceThresholdS { get; set; } = 300;
    public string VaultKeyPath { get; set; } = "";

    /// <summary>Set true only in development/test: allows plain HTTP (the agent
    /// protocol mandates HTTPS in production, PROTOCOL §2).</summary>
    public bool AllowInsecureHttp { get; set; }

    public RateLimitOptions RateLimits { get; set; } = new();

    /// <summary>CIDR list allowed to perform first-run setup (API.md §8.1).</summary>
    public string[] TrustedSetupNetworks { get; set; } = ["127.0.0.1/32", "::1/128"];

    /// <summary>Additional allowed browser origins for CSRF/Origin validation.</summary>
    public string[] AllowedOrigins { get; set; } = [];

    public string DbPath => Path.Combine(DataDirectory, "hyveman.db");
    public string BackupDirectory => Path.Combine(DataDirectory, "backup");
    public string ResolvedVaultKeyPath =>
        string.IsNullOrEmpty(VaultKeyPath) ? Path.Combine(DataDirectory, "vault.key") : VaultKeyPath;

    public TimeSpan HardwarePollInterval => TimeSpan.FromSeconds(Math.Max(5, HardwarePollIntervalS));
    public TimeSpan HeartbeatSilenceThreshold => TimeSpan.FromSeconds(Math.Max(10, HeartbeatSilenceThresholdS));
}

public sealed class RateLimitOptions
{
    public int GlobalPerMinute { get; set; } = 1200;
    public int PerSourcePerMinute { get; set; } = 300;
    public int RegistrationPerMinute { get; set; } = 20;
    public int AuthPerMinute { get; set; } = 30;
}
