using System.Text.Json.Serialization;
using Fido2NetLib;
using Hyveman.Application;
using Hyveman.Infrastructure.Notify;
using Hyveman.Infrastructure.Redfish;
using Hyveman.Infrastructure.Security;
using Hyveman.Infrastructure.Sqlite;
using Hyveman.Protocol;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Serilog;

namespace Hyveman.Api;

/// <summary>
/// hyveman-api host (API.md §2/§11): modular monolith entry point. Startup
/// sequence: data directory + configuration → vault key → SQLite migrations →
/// protocol/WebAuthn verification → HTTP listener → readiness → background
/// services. CLI: `hyveman-api auth reset|list-passkeys|remove-passkey`.
/// </summary>
public partial class Program
{
    public static async Task<int> Main(string[] args)
    {
        // Local administrative commands run without the web host (API.md §8.3).
        if (args.Length > 0 && args[0] == "auth")
            return await AdminCommands.RunAsync(args);

        var dataDir = ResolveDataDir(args);
        Directory.CreateDirectory(dataDir);

        var builder = WebApplication.CreateBuilder(args);
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddJsonFile(Path.Combine(dataDir, "config.json"), optional: true);
        builder.Configuration.AddEnvironmentVariables("HYVEMAN_");
        builder.Configuration.AddCommandLine(args);

        var opts = new HyvemanOptions();
        builder.Configuration.Bind(opts);
        opts.DataDirectory = Path.GetFullPath(dataDir);
        // WebAuthn needs an RP id and origins; dev defaults keep localhost
        // usable, production must set them explicitly (API.md §8.1/§11).
        if (string.IsNullOrEmpty(opts.WebAuthnRpId)) opts.WebAuthnRpId = "localhost";
        if (string.IsNullOrEmpty(opts.WebAuthnExpectedOrigin)) opts.WebAuthnExpectedOrigin = "http://localhost:5080";

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft.AspNetCore", Serilog.Events.LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore", Serilog.Events.LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.Console()
            .WriteTo.File(Path.Combine(dataDir, "logs", "hyveman-api-.log"),
                rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14)
            .CreateLogger();
        builder.Host.UseSerilog();

        builder.WebHost.UseUrls(opts.ApiListenUrls.Split(';', StringSplitOptions.RemoveEmptyEntries));

        // Windows service hosting (API.md §2): a no-op when not running under
        // the Service Control Manager, so the same binary still runs as a
        // console process on Linux/Docker or in a terminal.
        builder.Host.UseWindowsService(o => o.ServiceName = "hyveman-api");

        // ── Configuration & core singletons ──────────────────────────────
        builder.Services.AddSingleton(opts);
        builder.Services.AddSingleton<Hyveman.Application.IClock, Hyveman.Application.SystemClock>();
        builder.Services.AddSingleton(new SqliteDb(opts.DbPath, opts.SQLiteBusyTimeoutMs));
        builder.Services.AddSingleton(new Fido2Configuration
        {
            ServerDomain = opts.WebAuthnRpId,
            ServerName = "Hyveman",
            Origins = new HashSet<string> { opts.WebAuthnExpectedOrigin },
            Timeout = 120_000,
        });
        builder.Services.AddSingleton<RateLimiterRegistry>(sp => new RateLimiterRegistry(sp.GetRequiredService<HyvemanOptions>().RateLimits));
        builder.Services.AddSingleton(sp => TrustedNetwork.Create(
            sp.GetRequiredService<HyvemanOptions>().TrustedSetupNetworks));

        // ── Stores (scoped; stateless over the singleton SqliteDb) ───────
        builder.Services.AddScoped<ISourceStore, SourceStore>();
        builder.Services.AddScoped<ITokenStore, TokenStore>();
        builder.Services.AddScoped<IRegistrationTokenStore, RegistrationTokenStore>();
        builder.Services.AddScoped<IRegistrationUnit, RegistrationUnit>();
        builder.Services.AddScoped<IEventStore, EventStore>();
        builder.Services.AddScoped<ILogonStatsStore, LogonStatsStore>();
        builder.Services.AddScoped<IAgentStatusStore, AgentStatusStore>();
        builder.Services.AddScoped<IHostStore, HostStore>();
        builder.Services.AddScoped<IHealthStore, HealthStore>();
        builder.Services.AddScoped<IPollStatusStore, PollStatusStore>();
        builder.Services.AddScoped<IIdracCertStore, IdracCertStore>();
        builder.Services.AddScoped<IAlertStore, AlertStore>();
        builder.Services.AddScoped<IRuleStore, RuleStore>();
        builder.Services.AddScoped<INotificationChannelStore, ChannelStore>();
        builder.Services.AddScoped<IOutboxStore, OutboxStore>();
        builder.Services.AddScoped<IAuditStore, AuditStore>();
        builder.Services.AddScoped<ICredentialBlobStore, CredentialBlobStore>();
        builder.Services.AddScoped<ISessionStore, SessionStore>();
        builder.Services.AddScoped<IPasskeyStore, PasskeyStore>();
        builder.Services.AddScoped<ICeremonyStore, CeremonyStore>();
        builder.Services.AddScoped<IUserStore, UserStore>();
        builder.Services.AddScoped<IInvitationStore, InvitationStore>();
        builder.Services.AddScoped<ISettingsStore, SettingsStore>();
        builder.Services.AddScoped<ISavedSearchStore, SavedSearchStore>();
        builder.Services.AddScoped<IMaintenanceWindowStore, MaintenanceWindowStore>();
        builder.Services.AddScoped<IBackupStore>(sp => new BackupStore(
            sp.GetRequiredService<SqliteDb>(), sp.GetRequiredService<HyvemanOptions>().BackupDirectory));

        // ── Vault, services, providers ────────────────────────────────────
        builder.Services.AddScoped<ICredentialVault>(sp => new CredentialVault(
            sp.GetRequiredService<ICredentialBlobStore>(),
            sp.GetRequiredService<HyvemanOptions>().ResolvedVaultKeyPath,
            sp.GetRequiredService<IClock>()));
        builder.Services.AddScoped<CredentialVault>(sp => (CredentialVault)sp.GetRequiredService<ICredentialVault>());

        builder.Services.AddScoped<RegistrationService>();
        builder.Services.AddScoped<LogIngestService>();
        builder.Services.AddScoped<LogonStatsService>();
        builder.Services.AddScoped<TelemetryService>();
        builder.Services.AddScoped<IAlertEvaluator, AlertEvaluatorService>();
        builder.Services.AddScoped<HeartbeatMonitor>();
        builder.Services.AddScoped<OverviewService>();
        builder.Services.AddScoped<HostsService>();
        builder.Services.AddScoped<EventsService>();
        builder.Services.AddScoped<SavedSearchesService>();
        builder.Services.AddScoped<SourcesService>();
        builder.Services.AddScoped<UsersService>();
        builder.Services.AddScoped<AlertsService>();
        builder.Services.AddScoped<RulesService>();
        builder.Services.AddScoped<ChannelsService>();
        builder.Services.AddScoped<MaintenanceWindowsService>();
        builder.Services.AddScoped<SettingsService>();
        builder.Services.AddScoped<AuditService>();
        builder.Services.AddScoped<IWebAuthnService>(sp => new WebAuthnService(
            sp.GetRequiredService<Fido2Configuration>(),
            sp.GetRequiredService<IPasskeyStore>(),
            sp.GetRequiredService<ICeremonyStore>(),
            sp.GetRequiredService<ISessionStore>(),
            sp.GetRequiredService<IUserStore>(),
            sp.GetRequiredService<IInvitationStore>(),
            sp.GetRequiredService<IAuditStore>(),
            sp.GetRequiredService<IClock>(),
            sp.GetRequiredService<Func<string?, bool>>(),
            sp.GetRequiredService<ILogger<WebAuthnService>>(),
            sp.GetRequiredService<HyvemanOptions>().SessionLifetime));
        builder.Services.AddScoped<IMaintenanceJob, MaintenanceJob>();
        builder.Services.AddSingleton<IReadinessCheck, ReadinessCheck>();

        builder.Services.AddScoped<IHardwareProvider>(sp => new DellRedfishProvider(
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetRequiredService<HyvemanOptions>().IdracCertPolicy,
            sp.GetRequiredService<IIdracCertStore>(),
            sp.GetRequiredService<ILogger<DellRedfishProvider>>()));
        builder.Services.AddTransient<INotifier, TelegramNotifier>();
        builder.Services.AddTransient<INotifier, WebhookNotifier>();
        builder.Services.AddTransient<INotifier, SmtpNotifier>();
        builder.Services.AddScoped<INotificationSender, NotificationSender>();

        builder.Services.AddHttpClient("notify", c => c.Timeout = TimeSpan.FromSeconds(15));
        builder.Services.AddHttpClient("redfish", c => c.Timeout = TimeSpan.FromSeconds(15))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AllowAutoRedirect = false, // the Redfish client must not follow arbitrary redirects (API.md §12)
            });

        // ── Web: controllers, session auth, authorization, OpenAPI ───────
        builder.Services.AddControllers()
            .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        builder.Services.AddAuthentication(SessionAuthOptions.SchemeName)
            .AddScheme<SessionAuthOptions, SessionAuthHandler>(SessionAuthOptions.SchemeName,
                o => o.Lifetime = opts.SessionLifetime);
        builder.Services.AddAuthorization(o =>
        {
            o.FallbackPolicy = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .Build();
        });
        builder.Services.AddOpenApi();

        // ── Background services (API.md §9) ───────────────────────────────
        builder.Services.AddSingleton<HardwarePollingService>();
        builder.Services.AddSingleton(sp => new HeartbeatMonitorService(
            sp.GetRequiredService<IServiceScopeFactory>(),
            TimeSpan.FromSeconds(30),
            sp.GetRequiredService<ILogger<HeartbeatMonitorService>>()));
        builder.Services.AddSingleton<AlertReconciliationService>();
        builder.Services.AddSingleton<AlertAutoResolveService>();
        builder.Services.AddSingleton<NotificationDispatchService>();
        builder.Services.AddSingleton<MaintenanceService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<HardwarePollingService>());
        builder.Services.AddHostedService(sp => sp.GetRequiredService<HeartbeatMonitorService>());
        builder.Services.AddHostedService(sp => sp.GetRequiredService<AlertReconciliationService>());
        builder.Services.AddHostedService(sp => sp.GetRequiredService<AlertAutoResolveService>());
        builder.Services.AddHostedService(sp => sp.GetRequiredService<NotificationDispatchService>());
        builder.Services.AddHostedService(sp => sp.GetRequiredService<MaintenanceService>());

        // ── Startup sequence: vault → migrations → verify config ─────────
        var app = builder.Build();
        var startupLog = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Hyveman.Api");
        try
        {
            using (var scope = app.Services.CreateScope())
            {
                var vault = scope.ServiceProvider.GetRequiredService<CredentialVault>();
                vault.CheckKey();
                var db = scope.ServiceProvider.GetRequiredService<SqliteDb>();
                db.Migrate();
                var ready = scope.ServiceProvider.GetRequiredService<IReadinessCheck>();
                if (!await ready.IsReadyAsync(CancellationToken.None))
                    throw new InvalidOperationException("readiness check failed at startup");
            }
            var checkOpts = app.Services.GetRequiredService<HyvemanOptions>();
            if (string.IsNullOrEmpty(checkOpts.WebAuthnRpId) || string.IsNullOrEmpty(checkOpts.WebAuthnExpectedOrigin))
                throw new InvalidOperationException("WebAuthnRpId and WebAuthnExpectedOrigin must be configured");
            if (checkOpts.AgentProtocolCurrentVersion != ProtocolVersion.Current ||
                !checkOpts.AgentProtocolSupportedVersions.SequenceEqual(ProtocolVersion.Supported))
                throw new InvalidOperationException("agent protocol configuration does not match the compiled protocol version");
            if (!IdracCertPolicies.Known.Contains(checkOpts.IdracCertPolicy))
                throw new InvalidOperationException($"IdracCertPolicy must be one of: {string.Join(", ", IdracCertPolicies.Known)}");
            startupLog.LogInformation("hyveman-api {version} starting; data directory {dir}",
                checkOpts.ServerVersion, checkOpts.DataDirectory);
        }
        catch (Exception ex)
        {
            startupLog.LogCritical(ex, "Startup failed");
            return 1;
        }

        // ── Pipeline ──────────────────────────────────────────────────────
        app.UseSerilogRequestLogging();
        app.UseForwardedHeaders(new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
            // trust only loopback proxies by default; deployers with a remote
            // proxy must add their proxy addresses to KnownProxies
        });
        app.UseMiddleware<AgentProtocolMiddleware>();
        app.UseAuthentication();
        app.UseMiddleware<CsrfMiddleware>();
        app.UseMiddleware<ProblemDetailsMiddleware>();
        app.UseAuthorization();
        app.MapControllers();

        // Operational endpoints (API.md §6.5): separate from the /health wire
        // contract, which the agent protocol middleware owns.
        app.MapGet("/health/live", () => Results.Ok(new { ok = true, time = DateTimeOffset.UtcNow }))
            .AllowAnonymous();
        app.MapGet("/health/ready", async (IReadinessCheck readiness, CancellationToken ct) =>
            await readiness.IsReadyAsync(ct)
                ? Results.Ok(new { ok = true })
                : Results.StatusCode(503))
            .AllowAnonymous();

        if (app.Environment.IsDevelopment())
            app.MapOpenApi().AllowAnonymous();
        else
            app.MapOpenApi(); // protected by the authenticated fallback policy

        try
        {
            await app.RunAsync();
            return 0;
        }
        catch (Exception ex)
        {
            startupLog.LogCritical(ex, "Host terminated unexpectedly");
            return 1;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    private static string ResolveDataDir(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--data-dir" && i + 1 < args.Length)
                return args[i + 1];
            if (args[i].StartsWith("--data-dir=", StringComparison.Ordinal))
                return args[i]["--data-dir=".Length..];
        }
        return Environment.GetEnvironmentVariable("HYVEMAN_DATA_DIR") ?? "data";
    }
}
