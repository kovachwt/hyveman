using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hyveman.Server.Auth;

namespace Hyveman.Server.Config;

/// <summary>
/// Loads/validates <c>config/server.json</c>, resolves vault secret references (e.g. the TLS
/// cert password, §5.4), and produces the effective <see cref="ServerOptions"/>.
/// </summary>
public static class OptionsResolver
{
    public static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,   // server.json is snake_case (§5.3)
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static ServerOptions Load(string dataDir, ICredentialVault? vault)
    {
        var path = DataDirectory.ConfigFilePath(dataDir);
        ServerOptions opts;
        if (File.Exists(path))
        {
            try
            {
                opts = JsonSerializer.Deserialize<ServerOptions>(File.ReadAllText(path), JsonOpts)
                       ?? throw new InvalidOperationException("server.json parsed to null.");
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to parse {path}: {ex.Message}", ex);
            }
        }
        else
        {
            opts = new ServerOptions();
            opts.DataDir = dataDir;
            var defaultJson = JsonSerializer.Serialize(opts, JsonOpts);
            // Drop the DataDir field from the on-disk defaults to keep the sample faithful to §5.3.
            File.WriteAllText(path, SanitizeForDisk(defaultJson));
            DataDirectory.RestrictAclFile(path);
        }

        if (opts.DataDir == null) opts.DataDir = dataDir;

        // Substitute vault-referenced secrets (§5.4).
        if (!string.IsNullOrEmpty(opts.Tls.CertPasswordRef) && vault != null)
        {
            opts.Tls.CertPassword = vault.GetSecret(opts.Tls.CertPasswordRef)
                ?? throw new InvalidOperationException(
                    $"tls.cert_password_ref references '{opts.Tls.CertPasswordRef}' which is not in the credentials vault.");
        }

        Validate(opts, dataDir);
        return opts;
    }

    private static string SanitizeForDisk(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = new Dictionary<string, object?>();
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            if (prop.Name == "dataDir") continue;
            root[prop.Name] = JsonSerializer.Deserialize<object?>(prop.Value.GetRawText());
        }
        return JsonSerializer.Serialize(root, IndentedOpts());
    }

    public static void Save(ServerOptions opts, string dataDir)
    {
        var json = JsonSerializer.Serialize(opts, IndentedOpts());
        var path = DataDirectory.ConfigFilePath(dataDir);
        File.WriteAllText(path, json);
        DataDirectory.RestrictAclFile(path);
    }

    private static JsonSerializerOptions IndentedOpts() => new(JsonOpts) { WriteIndented = true };

    private static void Validate(ServerOptions o, string dataDir)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(o.Urls)) errors.Add("urls must not be empty");
        if (o.Ingest.MaxBatchBytes < 64 * 1024) errors.Add("ingest.max_batch_bytes must be ≥ 64 KiB");
        if (o.Ingest.MaxItems < 1 || o.Ingest.MaxItems > 10000) errors.Add("ingest.max_items must be 1..10000");
        if (o.Ingest.MaxRawBytes < 1024) errors.Add("ingest.max_raw_bytes must be ≥ 1 KiB");
        if (o.Ingest.PerSourceRate.RequestsPerMin < 1) errors.Add("ingest.per_source_rate.requests_per_min must be ≥ 1");
        if (o.Ingest.GlobalRate.RequestsPerMin < 1) errors.Add("ingest.global_rate.requests_per_min must be ≥ 1");
        if (o.Poller.IntervalS < 10) errors.Add("poller.interval_s must be ≥ 10");
        if (o.Poller.TimeoutS < 5) errors.Add("poller.timeout_s must be ≥ 5");
        if (o.Alerts.SweepS < 1) errors.Add("alerts.sweep_s must be ≥ 1");
        if (o.Alerts.DefaultHeartbeatMissS < 30) errors.Add("alerts.default_heartbeat_miss_s must be ≥ 30");
        if (o.Retention.EventsDays < 1) errors.Add("retention.events_days must be ≥ 1");
        if (o.Backup.KeepDaily < 1 || o.Backup.KeepWeekly < 1 || o.Backup.KeepMonthly < 1)
            errors.Add("backup keep counts must be ≥ 1");
        if (!TimeOnly.TryParse(o.Backup.TimeLocal, out _)) errors.Add($"backup.time_local '{o.Backup.TimeLocal}' is not HH:mm");
        if (o.Web.SessionDays < 1) errors.Add("web.session_days must be ≥ 1");
        if (o.Web.AuthRequestsPerMin < 1) errors.Add("web.auth_requests_per_min must be ≥ 1");

        var tls = o.Tls;
        var minOk = tls.MinTls is "1.2" or "1.3";
        var prefOk = tls.PreferredTls is "1.2" or "1.3";
        if (!minOk || !prefOk) errors.Add($"tls.min_tls/preferred_tls must be 1.2 or 1.3 (got {tls.MinTls}/{tls.PreferredTls})");
        if (tls.PreferredTls == "1.2" && tls.MinTls == "1.3") errors.Add("tls.preferred_tls cannot be lower than min_tls");

        if (o.Urls.Contains("http://", StringComparison.OrdinalIgnoreCase) && !o.Urls.Contains("https://", StringComparison.OrdinalIgnoreCase))
            errors.Add("urls must use https (plain HTTP is rejected by the protocol; PROTOCOL §2)");

        var hasCert = !string.IsNullOrWhiteSpace(tls.CertPath) || !string.IsNullOrWhiteSpace(tls.CertPasswordRef);
        if (!hasCert && !OperatingSystem.IsWindows())
            errors.Add("tls.cert_path is required on non-Windows (no dev-cert fallback)");
        // Empty string = not configured (Development may fall back to the dev cert); only a
        // non-empty cert_path must exist on disk.
        if (!string.IsNullOrWhiteSpace(tls.CertPath))
        {
            var raw = tls.CertPath!;
            var combined = Path.IsPathRooted(raw) ? raw : Path.GetFullPath(Path.Combine(dataDir, raw));
            if (!File.Exists(raw) && !File.Exists(combined))
                errors.Add($"tls.cert_path '{raw}' not found (checked '{raw}' and '{combined}')");
        }

        foreach (var host in o.Notifications.Webhook.AllowedHosts)
        {
            if (!IPAddress.TryParse(host, out _) && !host.Contains('.'))
                errors.Add($"notifications.webhook.allowed_hosts entry '{host}' is not an IP or hostname");
        }

        if (errors.Count > 0)
            throw new InvalidOperationException("Invalid config/server.json:\n  - " + string.Join("\n  - ", errors));
    }
}
