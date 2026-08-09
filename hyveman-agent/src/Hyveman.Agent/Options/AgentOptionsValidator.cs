using Microsoft.Extensions.Options;

namespace Hyveman.Agent.Options;

/// <summary>
/// Startup validation — invalid config fails the service start with a clear
/// message (AGENT.md §10, §15). Never start in a half-broken state.
/// </summary>
public sealed class AgentOptionsValidator : IValidateOptions<AgentOptions>
{
    public ValidateOptionsResult Validate(string? name, AgentOptions o)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(o.DataDir))
            errors.Add("data_dir: must not be empty");

        if (string.IsNullOrWhiteSpace(o.Backend.Url))
            errors.Add("backend.url: must not be empty");
        else if (!Uri.TryCreate(o.Backend.Url, UriKind.Absolute, out var url) ||
                 (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp))
            errors.Add("backend.url: must be an absolute http(s) URL");
        else if (url.Scheme == Uri.UriSchemeHttp && !url.IsLoopback)
            errors.Add("backend.url: plain http is only permitted for loopback (lab); https required");
        else if (url.PathAndQuery != "/" && url.PathAndQuery.Length > 1)
            errors.Add("backend.url: must be a base URL without a path (no trailing path)");

        if (!string.IsNullOrEmpty(o.Backend.CaPath) && !File.Exists(o.Backend.CaPath))
            errors.Add($"backend.ca_path: file not found: {o.Backend.CaPath}");

        if (string.IsNullOrWhiteSpace(o.Spool.Dir))
            errors.Add("spool.dir: must not be empty");

        if (o.Spool.MaxBytes <= 0)
            errors.Add("spool.max_bytes: must be > 0");
        if (o.Spool.MinFreeBytes <= 0)
            errors.Add("spool.min_free_bytes: must be > 0");
        if (o.Spool.MaxBytes >= o.Spool.MinFreeBytes)
            errors.Add("spool.max_bytes: must be smaller than spool.min_free_bytes (the caps only make sense layered)");

        if (o.Limits.ProcessMemoryBytes < 16L * 1024 * 1024)
            errors.Add("limits.process_memory_bytes: must be >= 16 MiB");
        if (o.Limits.CpuRatePercent is < 1 or > 100)
            errors.Add("limits.cpu_rate_percent: must be in 1..100");
        if (o.Limits.InMemoryQueueEvents <= 0)
            errors.Add("limits.in_memory_queue_events: must be > 0");
        if (o.Limits.BatchMaxEvents is <= 0 or > 1000)
            errors.Add("limits.batch_max_events: must be in 1..1000");
        if (o.Limits.BatchMaxAgeMs <= 0)
            errors.Add("limits.batch_max_age_ms: must be > 0");
        if (o.Limits.MaxBatchBytes is <= 0 or > 16 * 1024 * 1024)
            errors.Add("limits.max_batch_bytes: must be in 1..16 MiB");
        if (o.Limits.MaxRawBytes <= 0)
            errors.Add("limits.max_raw_bytes: must be > 0");
        if (o.Limits.SendConcurrency is < 1 or > 8)
            errors.Add("limits.send_concurrency: must be in 1..8");
        if (o.Limits.SendTimeoutMs <= 0)
            errors.Add("limits.send_timeout_ms: must be > 0");

        if (o.Wmi.ScanIntervalS <= 0)
            errors.Add("wmi.scan_interval_s: must be > 0");
        if (o.Wmi.QueryTimeoutS is <= 0 or > 120)
            errors.Add("wmi.query_timeout_s: must be in 1..120");
        if (o.Wmi.MaxQueriesPerScan <= 0)
            errors.Add("wmi.max_queries_per_scan: must be > 0");

        if (o.Heartbeat.IntervalS <= 0)
            errors.Add("heartbeat.interval_s: must be > 0");

        if (o.SecurityLog.IncludeIds.Count == 0)
            errors.Add("security_log.include_ids: must not be empty");
        if (o.SecurityLog.LogonTypesFor4624.Count == 0)
            errors.Add("security_log.logon_types_for_4624: must not be empty");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Normalize before comparison: the CLI override may carry forward
        // slashes ("C:/dev/...") while the config file holds backslashes, and
        // the registration rewrite persists the CLI form verbatim — a raw
        // StartsWith would then fail on the next start after a perfectly
        // valid registration. GetFullPath canonicalizes both sides.
        if (TryGetFullPath(o.DataDir, out var dataDir) && TryGetFullPath(o.Spool.Dir, out var spoolDir))
        {
            var under = spoolDir.Equals(dataDir, StringComparison.OrdinalIgnoreCase)
                        || spoolDir.StartsWith(dataDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            if (!under)
                errors.Add("spool.dir: must live under data_dir (single-data-dir rule, DESIGN §9)");
        }
        else if (o.Spool.Dir.Length > 0)
        {
            errors.Add("spool.dir: invalid path");
        }

        foreach (var ch in o.Channels)
        {
            if (string.IsNullOrWhiteSpace(ch.Name))
            {
                errors.Add("channels: entry with empty name");
                continue;
            }

            if (!seen.Add(ch.Name))
                errors.Add($"channels: duplicate channel name '{ch.Name}'");

            var actualChannel = ch.Channel ?? ch.Name;
            if (string.IsNullOrWhiteSpace(actualChannel))
                errors.Add($"channels[{ch.Name}]: channel must not be empty");

            if (ch.Level is LevelName.Verbose)
                errors.Add($"channels[{ch.Name}]: level Verbose is not useful (fires on every event); use Information or stricter");
        }

        if (o.Registration is { } reg)
        {
            if (string.IsNullOrWhiteSpace(reg.Token))
                errors.Add("registration.token: must not be empty when registration section is present");
            else if (!reg.Token.StartsWith("reg_", StringComparison.Ordinal))
                errors.Add("registration.token: must start with 'reg_'");
        }

        if (o.Backend.Token is { } tok && tok.Length > 0 && !tok.StartsWith("agt_", StringComparison.Ordinal))
            errors.Add("backend.token: must start with 'agt_'");

        if (!string.IsNullOrEmpty(o.SourceId) && !o.SourceId.StartsWith("src_", StringComparison.Ordinal))
            errors.Add("source_id: must start with 'src_'");

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }

    private static bool TryGetFullPath(string path, out string full)
    {
        try
        {
            full = Path.GetFullPath(path);
            return true;
        }
        catch (Exception)
        {
            full = "";
            return false;
        }
    }
}
