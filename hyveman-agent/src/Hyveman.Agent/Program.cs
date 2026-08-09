using System.Diagnostics;
using System.Text;
using Hyveman.Agent.Lifecycle;
using Hyveman.Agent.Net;
using Hyveman.Agent.Options;
using Hyveman.Agent.Pipeline;
using Hyveman.Agent.Telemetry;
using Hyveman.Agent.Wevtapi;
using Hyveman.Agent.Wmi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;

namespace Hyveman.Agent;

public static class Program
{
    private static FileStream? _pidLock;

    public static async Task<int> Main(string[] args)
    {
        var cli = CliOptions.Parse(args);

        // ---- Load + validate config (fail closed; AGENT §15) ----
        var dataDir = cli.DataDir ?? @"C:\ProgramData\hyveman-agent";
        var configPath = cli.Config ?? Path.Combine(dataDir, "agent.json");

        AgentOptions options;
        string configHash;
        try
        {
            if (!File.Exists(configPath))
                throw new FileNotFoundException($"Config file not found: {configPath} (run install.ps1 or create agent.json)");
            var raw = await File.ReadAllBytesAsync(configPath);
            options = ConfigLoader.FromBytes(raw);
            configHash = ConfigLoader.ComputeHash(raw);
            var result = new AgentOptionsValidator().Validate(null, options);
            if (result.Failed)
                throw new InvalidDataException("agent.json validation failed:\n" + string.Join("\n", result.Failures ?? Array.Empty<string>()));
            if (cli.DataDir is not null)
                options.DataDir = cli.DataDir;
        }
        catch (Exception ex)
        {
            var msg = $"hyveman-agent failed to start: {ex.Message}";
            Console.Error.WriteLine(msg);
            EventLogLifecycle.Write(EventLogLifecycle.EventIdPreflightFail, msg, EventLogEntryType.Error);
            return 1;
        }

        if (cli.ValidateConfig)
        {
            Console.WriteLine($"config OK (hash {configHash})");
            return 0;
        }

        // ---- Job Object containment BEFORE large allocations (AGENT §4.2) ----
        var bootstrapLog = CreateBootstrapLogger(options);
        JobObjectHost.Apply(options.Limits.ProcessMemoryBytes, options.Limits.CpuRatePercent, bootstrapLog);

        // ---- Double-instance guard (AGENT §14) ----
        var stateDir = Path.Combine(dataDir, "state");
        if (!TryAcquirePidLock(stateDir, out var pidError))
        {
            var msg = $"Another hyveman-agent instance is running ({pidError}); exiting.";
            Console.Error.WriteLine(msg);
            bootstrapLog.LogWarning(msg);
            return 1;
        }

        // ---- Serilog file logging (AGENT §12) ----
        var serilog = BuildSerilog(options);
        Log.Logger = serilog;
        var loggerFactory = LoggerFactory.Create(b => b.AddSerilog(serilog));

        var snapshot = new OptionsSnapshot(options);
        snapshot.Swap(options, configHash);

        var loader = new ConfigLoader(configPath);

        // ---- Registration exchange on first contact (AGENT §11.2 step 9, PROTOCOL §5) ----
        if (string.IsNullOrEmpty(options.Backend.Token) && options.Registration?.Token is { } regToken)
        {
            try
            {
                using var regClient = new BackendClient(snapshot, loggerFactory.CreateLogger<BackendClient>());
                var regResp = await regClient.RegisterAsync(new RegisterRequest
                {
                    Kind = options.Registration.Kind,
                    Hostname = Environment.MachineName,
                    AgentVersion = AgentInfo.Version,
                    OsBuild = AgentInfo.OsBuild,
                    BootId = $"{Environment.MachineName}-{AgentInfo.BootTimeUtc:yyyyMMddHHmmss}"
                }, regToken, CancellationToken.None);

                if (regResp?.Token is null)
                {
                    var msg = "Registration with backend failed (invalid/expired reg_ token, or backend unreachable). Service will not start without an ingest token. Reissue an install token and restart.";
                    Console.Error.WriteLine(msg);
                    EventLogLifecycle.Write(EventLogLifecycle.EventIdPreflightFail, msg, EventLogEntryType.Error);
                    return 1;
                }

                options.Backend.Token = regResp.Token;
                options.SourceId = regResp.SourceId;
                options.Registration = null; // discard the one-time reg token (§13: tokens never persisted beyond need)
                loader.Rewrite(options);
                var newHash = loader.ComputeHashOfCurrentFile();
                snapshot.Swap(options, newHash);
                loggerFactory.CreateLogger("Registration").LogInformation(
                    "Registered as source {source} (scopes: {scopes}); ingest token stored in agent.json, reg token discarded",
                    regResp.SourceId, string.Join(",", regResp.Scopes ?? new List<string>()));
            }
            catch (Exception ex)
            {
                // A network failure mid-exchange can mean the server consumed the
                // one-time reg_ token but the response was lost (PROTOCOL §5.4
                // response-loss). Fail closed with a clean diagnostic: the next
                // restart would get 410 token_consumed, so the operator must
                // reissue a fresh reg_ token either way.
                var msg = $"Registration with backend failed: {ex.Message}. If the reg_ token was already consumed (response lost), reissue a fresh install token in the admin UI and restart.";
                Console.Error.WriteLine(msg);
                EventLogLifecycle.Write(EventLogLifecycle.EventIdPreflightFail, msg, EventLogEntryType.Error);
                return 1;
            }
        }

        var queueCapacity = options.Limits.InMemoryQueueEvents;

        var bootstrapLogger2 = loggerFactory.CreateLogger("Startup");
        bootstrapLogger2.LogInformation("ConfigureServices: {n} channels: {names}",
            options.Channels.Count, string.Join(",", options.Channels.Select(c => c.Name)));

        var hostBuilder = Host.CreateDefaultBuilder(args)
            .UseWindowsService(o => o.ServiceName = "hyveman-agent")
            .UseSerilog(serilog)
            .ConfigureServices((ctx, services) =>
            {
                services.AddSingleton(snapshot);
                services.AddSingleton(new ConfigLoader(configPath));
                services.AddSingleton(new RuntimeMonitor());
                services.AddSingleton(new BoundedQueue<EvtLogEvent>(queueCapacity));
                services.AddSingleton(new SpoolCaps(options.Spool.MaxBytes, options.Spool.MinFreeBytes));
                services.AddSingleton(sp => new SpoolWriter(
                    sp.GetRequiredService<OptionsSnapshot>().Active.Spool.Dir,
                    sp.GetRequiredService<SpoolCaps>(),
                    sp.GetRequiredService<RuntimeMonitor>(),
                    sp.GetRequiredService<ILoggerFactory>().CreateLogger<SpoolWriter>()));
                services.AddSingleton(sp => new BookmarkManager(
                    Path.Combine(sp.GetRequiredService<OptionsSnapshot>().Active.DataDir, "state"),
                    sp.GetRequiredService<ILoggerFactory>().CreateLogger<BookmarkManager>()));
                services.AddSingleton(sp => new EpochManager(
                    Path.Combine(sp.GetRequiredService<OptionsSnapshot>().Active.DataDir, "state"),
                    sp.GetRequiredService<ILoggerFactory>().CreateLogger<EpochManager>()));
                services.AddSingleton<BackendClient>();
                services.AddSingleton<TelemetrySender>();

                // One push subscriber per configured channel. NOTE: plain
                // AddSingleton<IHostedService>, NOT AddHostedService — the latter
                // uses TryAddEnumerable which dedups factory registrations by
                // return type (all subscribers return ChannelSubscriber!).
                foreach (var channel in options.Channels)
                {
                    var ch = channel;
                    services.AddSingleton<IHostedService>(sp => new ChannelSubscriber(
                        ch,
                        snapshot,
                        sp.GetRequiredService<BoundedQueue<EvtLogEvent>>(),
                        sp.GetRequiredService<RuntimeMonitor>(),
                        sp.GetRequiredService<BookmarkManager>(),
                        sp.GetRequiredService<EpochManager>(),
                        sp.GetRequiredService<ILoggerFactory>().CreateLogger<ChannelSubscriber>()));
                }

                services.AddSingleton<IHostedService>(sp => new BatchBuilder(
                    sp.GetRequiredService<BoundedQueue<EvtLogEvent>>(),
                    snapshot,
                    sp.GetRequiredService<SpoolWriter>(),
                    sp.GetRequiredService<BookmarkManager>(),
                    sp.GetRequiredService<RuntimeMonitor>(),
                    sp.GetRequiredService<ILoggerFactory>().CreateLogger<BatchBuilder>()));
                services.AddSingleton<IHostedService>(sp => new LogSender(
                    sp.GetRequiredService<OptionsSnapshot>().Active.Spool.Dir,
                    Path.Combine(sp.GetRequiredService<OptionsSnapshot>().Active.DataDir, "state"),
                    snapshot,
                    sp.GetRequiredService<BackendClient>(),
                    sp.GetRequiredService<RuntimeMonitor>(),
                    sp.GetRequiredService<ILoggerFactory>().CreateLogger<LogSender>()));
                services.AddSingleton<IHostedService>(sp => new HeartbeatTimer(
                    snapshot,
                    sp.GetRequiredService<RuntimeMonitor>(),
                    sp.GetRequiredService<BoundedQueue<EvtLogEvent>>(),
                    sp.GetRequiredService<TelemetrySender>(),
                    sp.GetRequiredService<OptionsSnapshot>().Active.Spool.Dir,
                    sp.GetRequiredService<ILoggerFactory>().CreateLogger<HeartbeatTimer>()));
                services.AddSingleton<IHostedService>(sp => new WmiFactCollector(
                    snapshot,
                    sp.GetRequiredService<RuntimeMonitor>(),
                    sp.GetRequiredService<TelemetrySender>(),
                    sp.GetRequiredService<ILoggerFactory>().CreateLogger<WmiFactCollector>()));

                // Optional config hot-reload (safe subset only; AGENT §10).
                services.AddSingleton<IHostedService>(sp =>
                {
                    var reload = new ConfigReloadService(snapshot, configPath,
                        sp.GetRequiredService<ILoggerFactory>().CreateLogger<ConfigReloadService>());
                    reload.Start();
                    return new LambdaHostedService(reload);
                });
            });

        var host = hostBuilder.Build();
        host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStarted.Register(() =>
            Log.Information("APPLICATION STARTED — all hosted services up"));

        // Create the dirs the pipeline expects.
        Directory.CreateDirectory(options.Spool.Dir);
        Directory.CreateDirectory(stateDir);
        Directory.CreateDirectory(Path.Combine(dataDir, "logs"));

        // Clean .tmp leftovers from a prior crash (AGENT §17 step 4): never a
        // corrupt final file, never a stale temp that lingers on disk.
        host.Services.GetRequiredService<SpoolWriter>().Initialize();

        // Startup reachability + token introspection (PROTOCOL §8). Never
        // aborts startup — a down backend is handled by spool + retry.
        var healthLog = loggerFactory.CreateLogger("Health");
        using (var healthCts = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
        {
            var healthClient = host.Services.GetRequiredService<BackendClient>();
            try
            {
                var health = await healthClient.HealthAsync(
                    string.IsNullOrEmpty(options.Backend.Token) ? null : options.Backend.Token,
                    healthCts.Token).ConfigureAwait(false);
                if (health?.Ok == true)
                {
                    healthLog.LogInformation(
                        "Backend health: ok=true (server {serverVersion}); source_id={source} scopes=[{scopes}]",
                        health.ServerVersion ?? "-",
                        health.SourceId ?? "(none — token invalid/missing)",
                        health.Scopes is { Count: > 0 } ? string.Join(",", health.Scopes) : "-");
                }
                else
                {
                    healthLog.LogWarning(
                        "Backend health check failed (unreachable/not ready, or token not resolved); agent continuing — senders will spool and retry");
                }
            }
            catch (Exception ex)
            {
                healthLog.LogWarning(ex, "Backend health check failed; agent continuing — senders will spool and retry");
            }
        }

        EventLogLifecycle.Write(EventLogLifecycle.EventIdStarted,
            $"hyveman-agent {AgentInfo.Version} started (os_build {AgentInfo.OsBuild}, config hash {configHash})",
            EventLogEntryType.Information, loggerFactory.CreateLogger("Lifecycle"));
        Log.Information("hyveman-agent {version} starting (source {source})", AgentInfo.Version, options.SourceId ?? "(not registered)");

        try
        {
            await host.RunAsync();
        }
        finally
        {
            EventLogLifecycle.Write(EventLogLifecycle.EventIdStopped,
                $"hyveman-agent {AgentInfo.Version} stopped", EventLogEntryType.Information,
                loggerFactory.CreateLogger("Lifecycle"));
            Log.CloseAndFlush();
            _pidLock?.Dispose();
        }

        return 0;
    }

    private static Microsoft.Extensions.Logging.ILogger CreateBootstrapLogger(AgentOptions options)
    {
        try
        {
            Directory.CreateDirectory(Path.Combine(options.DataDir, "logs"));
            var level = ParseLevel(options.Logging.Level);
            var serilog = new LoggerConfiguration()
                .MinimumLevel.Is(level)
                .WriteTo.File(
                    Path.Combine(options.DataDir, "logs", "hyveman-agent-.log"),
                    rollingInterval: RollingInterval.Day,
                    fileSizeLimitBytes: 10 * 1024 * 1024,
                    retainedFileCountLimit: 5,
                    rollOnFileSizeLimit: true)
                .CreateLogger();
            return LoggerFactory.Create(b => b.AddSerilog(serilog)).CreateLogger("Bootstrap");
        }
        catch (Exception)
        {
            return Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        }
    }

    private static Serilog.ILogger BuildSerilog(AgentOptions options)
    {
        Directory.CreateDirectory(Path.Combine(options.DataDir, "logs"));
        var level = ParseLevel(options.Logging.Level);
        return new LoggerConfiguration()
            .MinimumLevel.Is(level)
            .WriteTo.File(
                Path.Combine(options.DataDir, "logs", "hyveman-agent-.log"),
                rollingInterval: RollingInterval.Day,
                fileSizeLimitBytes: 10 * 1024 * 1024,
                retainedFileCountLimit: 5,
                rollOnFileSizeLimit: true,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
    }

    private static LogEventLevel ParseLevel(string level) => level.ToLowerInvariant() switch
    {
        "critical" => LogEventLevel.Fatal,
        "error" => LogEventLevel.Error,
        "warning" => LogEventLevel.Warning,
        "information" or "" => LogEventLevel.Information,
        "debug" => LogEventLevel.Debug,
        "verbose" => LogEventLevel.Verbose,
        _ => LogEventLevel.Information
    };

    private static bool TryAcquirePidLock(string stateDir, out string error)
    {
        Directory.CreateDirectory(stateDir);
        var path = Path.Combine(stateDir, "pid");
        try
        {
            _pidLock = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            error = "";
            return true;
        }
        catch (IOException)
        {
            error = $"pid lock {path} is held";
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            error = $"cannot open pid lock {path} (access denied)";
            return false;
        }
    }

    /// <summary>Wraps an IDisposable as a hosted service (for the config watcher).</summary>
    private sealed class LambdaHostedService : IHostedService, IDisposable
    {
        private readonly ConfigReloadService _reload;
        public LambdaHostedService(ConfigReloadService reload) => _reload = reload;
        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
        public void Dispose() => _reload.Dispose();
    }
}

/// <summary>Command-line switches: --config, --data-dir, --validate-config.</summary>
public sealed class CliOptions
{
    public string? Config { get; private init; }
    public string? DataDir { get; private init; }
    public bool ValidateConfig { get; private init; }

    public static CliOptions Parse(string[] args)
    {
        string? config = null, dataDir = null;
        bool validate = false;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--config" when i + 1 < args.Length: config = args[++i]; break;
                case "--data-dir" when i + 1 < args.Length: dataDir = args[++i]; break;
                case "--validate-config": validate = true; break;
            }
        }
        return new CliOptions { Config = config, DataDir = dataDir, ValidateConfig = validate };
    }
}
