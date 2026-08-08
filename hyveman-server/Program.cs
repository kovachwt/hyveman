using Dapper;
using Hyveman.Server.Auth;
using Hyveman.Server.Config;
using Hyveman.Server.Hardware;
using Hyveman.Server.Ingest;
using Hyveman.Server.Ingest.Middleware;
using Hyveman.Server.Maintenance;
using Hyveman.Server.Notifications;
using Hyveman.Server.RateLimit;
using Hyveman.Server.Storage;
using Hyveman.Server.Web.Api;
using Microsoft.Data.Sqlite;
using Serilog;
using Serilog.Events;

namespace Hyveman.Server;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Storage.DapperConfig.Register();
        var cli = ParseArgs(args);
        var dataDir = DataDirectory.Resolve(cli.DataDir);

        // CLI subcommands run without the web host (console-only fallback, §12.2/§12.3).
        if (cli.Command is not null)
        {
            try
            {
                DataDirectory.Bootstrap(dataDir);
                return await RunCliCommandAsync(cli.Command, dataDir);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"error: {ex.Message}");
                return 1;
            }
        }

        // ── bootstrap: data dir → key K → vault → options ──────────────────
        DataDirectory.Bootstrap(dataDir);
        var key = DataDirectory.LoadOrCreateKey(dataDir);

        // Single-instance guard (§3.3, decision S18). A file lock is used instead of a named
        // mutex: .NET's named mutexes are in-process only on Unix (WaitSubsystem), so the
        // Windows-style `Global\` mutex would silently not protect anything on Linux.
        using var instanceLock = AcquireInstanceLock(dataDir);

        // Vault first (options may reference vault secrets, §5.4).
        var factory = new Storage.SqliteFactory(dataDir);
        var writer = new Storage.SqliteWriter(factory);
        var db = new Db(factory, writer);
        var vault = new AesGcmCredentialVault(key, db);
        ServerOptions options;
        try
        {
            options = OptionsResolver.Load(dataDir, vault);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Configuration error: {ex.Message}");
            writer.Dispose();
            return 2;
        }

        // ── logging ─────────────────────────────────────────────────────────
        var level = ParseLogLevel(options.Logging.Level);
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Is(level)
            .Enrich.With<Observability.MaskingEnricher>()
            .WriteTo.Console(restrictedToMinimumLevel: LogEventLevel.Information)
            .WriteTo.File(path: Path.Combine(dataDir, "logs", "server-.json"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: Math.Max(1, options.Logging.FileRetainDays),
                formatter: new Serilog.Formatting.Compact.CompactJsonFormatter())
            .CreateLogger();

        try
        {
            Log.Information("Hyveman server {Version} starting; data dir {DataDir}",
                ServerOptionsAssembly.Version, dataDir);
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                Args = args,
                ApplicationName = typeof(Program).Assembly.FullName,
            });
            builder.Host.UseSerilog();
            builder.Host.UseWindowsService(o => o.ServiceName = "HyvemanServer");
            builder.Logging.ClearProviders();

            builder.WebHost.UseUrls(options.Urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            ConfigureTls(builder, options, dataDir);

            builder.Services.AddSingleton(options);
            builder.Services.AddSingleton(dataDir);
            builder.Services.AddSingleton(key);
            builder.Services.AddSingleton(factory);
            builder.Services.AddSingleton(writer);
            builder.Services.AddSingleton(db);
            builder.Services.AddSingleton<DbMigrator>();
            builder.Services.AddSingleton<ICredentialVault>(vault);
            builder.Services.AddSingleton<ServerReadiness>();
            builder.Services.AddSingleton<Observability.OwnMetrics>();
            // Wire the global rate-limit bucket to ServerOptions.ingest.global_rate (§7.4, PROTOCOL §15).
            builder.Services.AddSingleton<RateLimiter>(_ =>
            {
                var limiter = new RateLimiter();
                limiter.SetGlobalConfig(options.Ingest.GlobalRate);
                return limiter;
            });
            builder.Services.AddSingleton<ITokenService, TokenService>();
            builder.Services.AddSingleton<Auth.PasskeyService>();
            builder.Services.AddSingleton<Auth.SessionAuth>();
            builder.Services.AddSingleton<Ingest.RegistrationService>();
            builder.Services.AddSingleton<Ingest.LogIngestService>();
            builder.Services.AddSingleton<Ingest.TelemetryService>();
            builder.Services.AddSingleton<Hardware.IHardwareProvider, Hardware.DellRedfishProvider>();
            builder.Services.AddSingleton<Hardware.HardwarePollerService>();
            builder.Services.AddSingleton<Alerts.AlertEngineService>();
            builder.Services.AddSingleton<Alerts.AgentSilenceWatchdog>();
            builder.Services.AddSingleton<Alerts.MaintenanceWindowFilter>();
            builder.Services.AddSingleton<Alerts.AlertSignals>();
            builder.Services.AddSingleton<Alerts.IEventSignal>(sp => sp.GetRequiredService<Alerts.AlertSignals>());
            builder.Services.AddSingleton<Alerts.IHeartbeatSignal>(sp => sp.GetRequiredService<Alerts.AlertSignals>());
            builder.Services.AddSingleton<Alerts.IHostUnreachableSignal>(sp => sp.GetRequiredService<Alerts.AlertSignals>());
            builder.Services.AddSingleton<Notifications.NotificationDispatcher>();
            builder.Services.AddSingleton<INotifier, TelegramNotifier>();
            builder.Services.AddSingleton<INotifier, WebhookNotifier>();
            builder.Services.AddHttpClient("redfish");
            builder.Services.AddHttpClient("notify").ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                MaxConnectionsPerServer = 8,
                ConnectTimeout = TimeSpan.FromSeconds(15),
            });

            // Background services (§3.2).
            builder.Services.AddHostedService<StartupService>();
            builder.Services.AddHostedService(sp => sp.GetRequiredService<Hardware.HardwarePollerService>());
            builder.Services.AddHostedService(sp => sp.GetRequiredService<Alerts.AlertEngineService>());
            builder.Services.AddHostedService(sp => sp.GetRequiredService<Alerts.AgentSilenceWatchdog>());
            builder.Services.AddHostedService(sp => sp.GetRequiredService<Notifications.NotificationDispatcher>());
            builder.Services.AddHostedService<RetentionService>();
            builder.Services.AddHostedService<BackupService>();
            builder.Services.AddHostedService<RateLimitReaper>();

            // Blazor Server UI (§11). Pages live under Web/Pages (Razor Pages root must be set).
            builder.Services.AddRazorPages(o => o.RootDirectory = "/Web/Pages");
            builder.Services.AddServerSideBlazor();

            var app = builder.Build();

            // Middleware: ingest API branch carries the full §7.1 chain; web branch carries
            // session/passkey guard. Exception trap applies to both.
            if (!app.Environment.IsDevelopment())
                app.UseHsts();
            app.UseStaticFiles();
            app.UseWhen(IsIngestPath, b => b.UseIngestMiddleware());
            app.UseWhen(IsAuthApiPath, b => b.UseMiddleware<RateLimit.AuthRateLimitMiddleware>());
            app.UseWhen(IsWebPath, b => b.UseMiddleware<Auth.PasskeyAuthMiddleware>());
            app.MapIngestApi();
            app.MapAuthApi();
            app.MapBlazorHub();
            app.MapRazorPages();
            app.MapFallbackToPage("/_Host");

            Log.Information("Listening on {Urls}", options.Urls);
            await app.RunAsync();
            return 0;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Fatal startup failure");
            Console.Error.WriteLine($"Fatal: {ex.Message}");
            return 3;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    /// <summary>Runs migrations at startup, then flips readiness (503 → 200, §3.3).</summary>
    private sealed class StartupService : IHostedService
    {
        private readonly DbMigrator _migrator;
        private readonly ServerReadiness _readiness;
        private readonly ILogger<StartupService> _logger;

        public StartupService(DbMigrator migrator, ServerReadiness readiness, ILogger<StartupService> logger)
        {
            _migrator = migrator;
            _readiness = readiness;
            _logger = logger;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            await _migrator.MigrateAsync(cancellationToken);
            _readiness.IsReady = true;
            _logger.LogInformation("Database ready; server accepting traffic");
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private static bool IsIngestPath(HttpContext ctx)
    {
        var p = ctx.Request.Path.Value ?? "";
        return p == "/register" || p == "/health" || p.StartsWith("/ingest/", StringComparison.Ordinal);
    }

    private static bool IsWebPath(HttpContext ctx)
    {
        var p = ctx.Request.Path.Value ?? "";
        return !IsIngestPath(ctx)
            && !p.StartsWith("/api/", StringComparison.Ordinal)
            && !p.StartsWith("/_blazor", StringComparison.Ordinal)
            && !p.StartsWith("/_framework", StringComparison.Ordinal);
    }

    private static bool IsAuthApiPath(HttpContext ctx)
    {
        var p = ctx.Request.Path.Value ?? "";
        return p.StartsWith("/api/auth/", StringComparison.Ordinal);
    }

    private static void ConfigureTls(WebApplicationBuilder builder, ServerOptions opts, string dataDir)
    {
        var tls = opts.Tls;
        if (string.IsNullOrEmpty(tls.CertPath))
        {
            if (builder.Environment.IsDevelopment())
            {
                // Kestrel falls back to the ASP.NET Core dev cert in Development.
                builder.WebHost.ConfigureKestrel(k => k.ConfigureHttpsDefaults(h =>
                    h.SslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13));
                return;
            }
            throw new InvalidOperationException(
                "tls.cert_path is not configured and the environment is not Development — refusing to start without a server certificate (PROTOCOL §2).");
        }

        var certPath = File.Exists(tls.CertPath)
            ? tls.CertPath
            : Path.GetFullPath(Path.Combine(dataDir, tls.CertPath));
        var cert = string.IsNullOrEmpty(tls.CertPassword)
            ? new System.Security.Cryptography.X509Certificates.X509Certificate2(certPath)
            : new System.Security.Cryptography.X509Certificates.X509Certificate2(certPath, tls.CertPassword);

        builder.WebHost.ConfigureKestrel(k => k.ConfigureHttpsDefaults(h =>
        {
            h.ServerCertificate = cert;
            h.SslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13;
        }));
    }

    /// <summary>
    /// Cross-platform single-instance guard: an exclusive file lock on
    /// <c>&lt;data_dir&gt;/state/server.lock</c>, held for the process lifetime. The kernel
    /// releases it automatically on crash, and it protects the same data dir regardless of
    /// platform (named mutexes are in-process only on Unix in .NET 8).
    /// </summary>
    private static FileStream AcquireInstanceLock(string dataDir)
    {
        var lockDir = Path.Combine(dataDir, "state");
        Directory.CreateDirectory(lockDir);
        var lockPath = Path.Combine(lockDir, "server.lock");
        try
        {
            // FileShare.None is enforced cross-process by .NET on both Windows and Unix.
            return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            Console.Error.WriteLine("Another hyveman-server instance is already running on this data directory (server.lock is held).");
            Environment.Exit(4);
            throw; // unreachable
        }
    }

    private static LogEventLevel ParseLogLevel(string level)
        => Enum.TryParse<LogEventLevel>(level, true, out var l) ? l : LogEventLevel.Information;

    // ── CLI subcommands (§15.2): auth reset|list-passkeys|remove-passkey, vault rotate-key ──
    private static async Task<int> RunCliCommandAsync(string command, string dataDir)
    {
        var factory = new Storage.SqliteFactory(dataDir);
        var writer = new Storage.SqliteWriter(factory);
        var db = new Storage.Db(factory, writer);
        try
        {
            await new DbMigrator(factory, Microsoft.Extensions.Logging.Abstractions.NullLogger<DbMigrator>.Instance)
                .MigrateAsync();
            var key = DataDirectory.LoadOrCreateKey(dataDir);

            switch (command)
            {
                case "auth list-passkeys":
                {
                    var keys = await db.Passkeys.ListAsync();
                    if (keys.Count == 0) { Console.WriteLine("No passkeys registered."); return 0; }
                    foreach (var k in keys)
                        Console.WriteLine($"{k.Id}\t{k.Name}\t{k.CredentialId}\tcreated {k.Created}");
                    return 0;
                }
                case "auth reset":
                {
                    await db.Writer.WithTransactionAsync(conn => Storage.Repos.PasskeyRepository.ClearAllAsync(conn));
                    await db.Audit.WriteAsync("cli", "auth.reset", "passkeys", null, null);
                    Console.WriteLine("Passkeys cleared. The first-run setup wizard will be served again on the next UI visit.");
                    return 0;
                }
                case "auth remove-passkey":
                {
                    Console.Write("Passkey name or id: ");
                    var arg = Console.ReadLine()?.Trim() ?? "";
                    var keys = await db.Passkeys.ListAsync();
                    var match = keys.FirstOrDefault(k => k.Name == arg || k.Id == arg);
                    if (match is null) { Console.WriteLine($"No passkey matches '{arg}'."); return 1; }
                    await db.Writer.WithTransactionAsync(conn => Storage.Repos.PasskeyRepository.DeleteAsync(conn, match.Id));
                    await db.Audit.WriteAsync("cli", "passkey.remove", "passkeys", match.Id, null);
                    Console.WriteLine($"Removed passkey '{match.Name}'.");
                    return 0;
                }
                case "vault rotate-key":
                {
                    var vault = new AesGcmCredentialVault(key, db);
                    var metas = await vault.ListAsync();
                    var newKey = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
                    var newVault = new AesGcmCredentialVault(newKey, db);
                    foreach (var m in metas)
                    {
                        var plain = await vault.GetSecretAsync(m.Label);
                        if (plain is null) { Console.WriteLine($"warning: could not decrypt '{m.Label}'; skipping"); continue; }
                        await newVault.PutSecretAsync(m.Label, m.Kind, plain, "cli");
                    }
                    File.WriteAllBytes(DataDirectory.KeyFilePath(dataDir), newKey);
                    DataDirectory.RestrictAclFile(DataDirectory.KeyFilePath(dataDir));
                    await db.Audit.WriteAsync("cli", "vault.rotate_key", "credentials", null, null);
                    Console.WriteLine($"Key K rotated; {metas.Count} credentials re-wrapped.");
                    return 0;
                }
                default:
                    Console.Error.WriteLine($"Unknown command '{command}'.");
                    return 1;
            }
        }
        finally
        {
            writer.Dispose();
        }
    }

    private sealed record CliArgs(string? DataDir, string? Command);

    private static CliArgs ParseArgs(string[] args)
    {
        string? dataDir = null;
        string? command = null;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--data-dir":
                    if (i + 1 < args.Length) dataDir = args[++i];
                    break;
                case "--console":
                    break;   // console mode is the default when run interactively
                case "auth" when i + 1 < args.Length:
                    command = $"auth {args[i + 1].ToLowerInvariant()}";
                    if (args[i + 1].ToLowerInvariant() == "remove-passkey") i++;   // arg read interactively
                    i++;
                    break;
                case "vault" when i + 1 < args.Length:
                    command = $"vault {args[i + 1].ToLowerInvariant()}";
                    i++;
                    break;
            }
        }
        return new CliArgs(dataDir, command);
    }
}
