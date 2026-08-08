using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hyveman.Agent.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hyveman.Agent.Options;

/// <summary>
/// Loads agent.json, computes the config hash, validates, and caches the raw
/// bytes for atomic rewrites (registration exchange rewrites the file).
/// </summary>
public sealed class ConfigLoader
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = SnakeCaseNamingPolicy.Instance, // agent.json keys are snake_case (AGENT §10)
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true
    };

    public string ConfigPath { get; }

    public ConfigLoader(string configPath) => ConfigPath = configPath;

    public bool Exists => File.Exists(ConfigPath);

    public static AgentOptions FromBytes(byte[] raw)
    {
        // PowerShell/notepad may leave a UTF-8 BOM; JsonSerializer rejects it.
        if (raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF)
            raw = raw[3..];
        var opts = JsonSerializer.Deserialize<AgentOptions>(raw, JsonOpts)
                   ?? throw new InvalidDataException("agent.json: empty document");
        return opts;
    }

    public static AgentOptions FromJson(string json) => FromBytes(Encoding.UTF8.GetBytes(json));

    /// <summary>Load + validate. Throws with a clear message on failure.</summary>
    public AgentOptions Load(ILogger? log = null)
    {
        var raw = File.ReadAllBytes(ConfigPath);
        var opts = FromBytes(raw);
        var result = new AgentOptionsValidator().Validate(null, opts);
        if (result.Failed)
            throw new InvalidDataException("agent.json validation failed:\n" + string.Join("\n", result.Failures ?? Array.Empty<string>()));
        return opts;
    }

    public static string ComputeHash(byte[] raw)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(raw, hash);
        return Convert.ToHexString(hash[..3]).ToLowerInvariant(); // 6 hex chars (AGENT §8 config_hash)
    }

    public string ComputeHashOfCurrentFile()
    {
        var raw = File.ReadAllBytes(ConfigPath);
        return ComputeHash(raw);
    }

    /// <summary>
    /// Atomic rewrite of agent.json (temp + rename). Used by the registration
    /// exchange to store the long-lived ingest token and drop the reg token.
    /// </summary>
    public void Rewrite(AgentOptions opts)
    {
        var dir = Path.GetDirectoryName(ConfigPath)!;
        Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(opts, JsonOpts);
        var tmp = ConfigPath + ".tmp";
        File.WriteAllText(tmp, json, new UTF8Encoding(false));
        File.Move(tmp, ConfigPath, overwrite: true);
    }
}

/// <summary>
/// A cheap structural fingerprint. URL/token/channels/caps changes are "cold"
/// (restart required); level/filter/interval changes are "hot" (AGENT §10).
/// </summary>
public static class ConfigChangeKind
{
    public static bool IsStructural(AgentOptions a, AgentOptions b)
    {
        if (a.Backend.Url != b.Backend.Url) return true;
        if (a.Backend.Token != b.Backend.Token) return true;
        if (a.Backend.CaPath != b.Backend.CaPath) return true;
        if (a.Backend.ValidateCert != b.Backend.ValidateCert) return true;
        if (a.Spool.Dir != b.Spool.Dir) return true;
        if (a.Spool.MaxBytes != b.Spool.MaxBytes) return true;
        if (a.Spool.MinFreeBytes != b.Spool.MinFreeBytes) return true;
        if (a.Limits.ProcessMemoryBytes != b.Limits.ProcessMemoryBytes) return true;
        if (a.Limits.CpuRatePercent != b.Limits.CpuRatePercent) return true;
        if (a.Limits.InMemoryQueueEvents != b.Limits.InMemoryQueueEvents) return true;
        if (a.Limits.MaxBatchBytes != b.Limits.MaxBatchBytes) return true;
        if (a.Limits.MaxRawBytes != b.Limits.MaxRawBytes) return true;
        if (a.Limits.SendConcurrency != b.Limits.SendConcurrency) return true;
        if (a.Limits.SendTimeoutMs != b.Limits.SendTimeoutMs) return true;
        if (a.Limits.Gzip != b.Limits.Gzip) return true;
        if (a.DataDir != b.DataDir) return true;
        if (a.Registration?.Token != b.Registration?.Token) return true;
        if (a.SourceId != b.SourceId) return true;

        var an = a.Channels.Select(c => c.Name).OrderBy(x => x, StringComparer.OrdinalIgnoreCase);
        var bn = b.Channels.Select(c => c.Name).OrderBy(x => x, StringComparer.OrdinalIgnoreCase);
        if (!an.SequenceEqual(bn)) return true;

        return false;
    }
}

/// <summary>
/// Mutable snapshot of the active (possibly hot-reloaded) options. Components
/// read the volatile reference each tick/event so the safe subset applies
/// without restart.
/// </summary>
public sealed class OptionsSnapshot
{
    private volatile AgentOptions _active;

    public OptionsSnapshot(AgentOptions initial) => _active = initial;

    public AgentOptions Active => _active;

    public string ConfigHash { get; private set; } = "";

    public void Swap(AgentOptions opts, string configHash)
    {
        _active = opts;
        ConfigHash = configHash;
    }

    /// <summary>Per-channel lookup used by hot-applied filters.</summary>
    public ChannelOptions? Channel(string name) =>
        _active.Channels.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Optional file watcher on agent.json: validates and hot-applies the safe
/// subset (levels, include/exclude IDs, intervals). Structural changes
/// (URL, token, channels, caps) are logged and require a restart (AGENT §10).
/// </summary>
public sealed class ConfigReloadService : IDisposable
{
    private readonly OptionsSnapshot _snapshot;
    private readonly ILogger<ConfigReloadService> _log;
    private readonly string _configPath;
    private FileSystemWatcher? _watcher;

    public ConfigReloadService(OptionsSnapshot snapshot, string configPath, ILogger<ConfigReloadService> log)
    {
        _snapshot = snapshot;
        _configPath = configPath;
        _log = log;
    }

    public void Start()
    {
        var dir = Path.GetDirectoryName(_configPath);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            return;
        _watcher = new FileSystemWatcher(dir, Path.GetFileName(_configPath))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            EnableRaisingEvents = true
        };
        _watcher.Changed += OnChanged;
        _watcher.Renamed += OnChanged;
    }

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        try
        {
            Thread.Sleep(200); // let the writer finish
            var raw = File.ReadAllBytes(_configPath);
            var opts = ConfigLoader.FromBytes(raw);
            var result = new AgentOptionsValidator().Validate(null, opts);
            if (result.Failed)
            {
                _log.LogWarning("Config reload skipped: invalid ({failures}); keeping previous config",
                    string.Join("; ", result.Failures ?? Array.Empty<string>()));
                return;
            }

            if (ConfigChangeKind.IsStructural(_snapshot.Active, opts))
            {
                _log.LogWarning("Config reload skipped: structural change detected (backend/spool/limits/channels); restart required to apply");
                return;
            }

            _snapshot.Swap(opts, ConfigLoader.ComputeHash(raw));
            _log.LogInformation("Config hot-reloaded (hash {hash})", _snapshot.ConfigHash);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Config reload failed; keeping previous config");
        }
    }

    public void Dispose()
    {
        if (_watcher != null)
        {
            _watcher.Changed -= OnChanged;
            _watcher.Renamed -= OnChanged;
            _watcher.Dispose();
        }
    }
}
