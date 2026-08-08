using System.Net;
using System.Text.Json;
using Hyveman.Server.Auth;
using Hyveman.Server.Common;
using Hyveman.Server.Config;
using Hyveman.Server.Storage;
using Hyveman.Server.Storage.Repos;
using AlertRepo = Hyveman.Server.Storage.Repos.AlertRepository;

namespace Hyveman.Server.Notifications;

public sealed record NotifyResult(bool Success, bool Permanent, string Error);

/// <summary>Outbound notification provider (DESIGN §4.4). No inbound listeners anywhere.</summary>
public interface INotifier
{
    string Kind { get; }
    Task<NotifyResult> SendAsync(Notification n, ChannelConfig c, CancellationToken ct);
}

public sealed record Notification(string AlertId, string RuleName, string RuleType, string Severity,
    string HostName, string Message, string FirstSeen, int Count, string Url);

public sealed record ChannelConfig(string Name, string Kind, string? BotToken, string? ChatId, string? Url, string? Secret);

/// <summary>
/// Durable delivery: EnqueueAsync writes one notification_queue row per channel (§9.5);
/// a background worker drains due rows with backoff across restarts. Permanent 4xx → channel
/// failure surfaced in audit log + in-memory status.
/// </summary>
public sealed class NotificationDispatcher : BackgroundService
{
    private readonly Db _db;
    private readonly ICredentialVault _vault;
    private readonly IEnumerable<INotifier> _notifiers;
    private readonly ServerOptions _opts;
    private readonly Observability.OwnMetrics _metrics;
    private readonly ILogger<NotificationDispatcher> _logger;

    /// <summary>Channel id → last permanent error (surfaced in admin UI).</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _channelErrors = new();

    public NotificationDispatcher(Db db, ICredentialVault vault, IEnumerable<INotifier> notifiers,
        ServerOptions opts, Observability.OwnMetrics metrics, ILogger<NotificationDispatcher> logger)
    {
        _db = db;
        _vault = vault;
        _notifiers = notifiers.ToList();
        _opts = opts;
        _metrics = metrics;
        _logger = logger;
    }

    public IReadOnlyDictionary<string, string> ChannelErrors => _channelErrors;

    public async Task EnqueueAsync(AlertRow alert, CancellationToken ct)
    {
        var channels = await _db.Alerts.ChannelsForRuleAsync(alert.RuleId);
        if (channels.Count == 0)
        {
            _logger.LogDebug("Alert {AlertId} has no channels configured; not enqueuing", alert.Id);
            return;
        }
        var now = WireTime.NowMs();
        await _db.Writer.WithTransactionAsync(async conn =>
        {
            foreach (var ch in channels)
                await AlertRepo.EnqueueAsync(conn, alert.Id, ch, now);
        });
        _metrics.NotificationQueued(channels.Count);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DrainAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Notification drain failed");
            }
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }
    }

    public async Task DrainAsync(CancellationToken ct)
    {
        var due = await _db.Alerts.DueAsync(WireTime.NowMs());
        foreach (var row in due)
        {
            ct.ThrowIfCancellationRequested();
            await SendOneAsync(row, ct);
        }
    }

    private async Task SendOneAsync(QueueRow row, CancellationToken ct)
    {
        try
        {
            var alert = await _db.Alerts.GetAsync(row.AlertId);
            var channel = await _db.Channels.GetAsync(row.ChannelId);
            if (alert is null || channel is null || !channel.Enabled)
            {
                await _db.Writer.WithTransactionAsync(conn => AlertRepo.MarkSentAsync(conn, row.Id));
                return;
            }

            var config = await DecryptConfigAsync(channel);
            var notifier = _notifiers.FirstOrDefault(n => n.Kind == channel.Kind);
            if (notifier is null || config is null)
            {
                await FailAsync(row, "no notifier for channel kind " + channel.Kind);
                return;
            }

            var rule = await _db.Alerts.GetRuleAsync(alert.RuleId);
            var hostName = alert.HostId is null ? null : (await _db.Hosts.GetAsync(alert.HostId))?.Name;
            var notification = new Notification(alert.Id, rule?.Name ?? alert.RuleId, rule?.Type ?? "",
                alert.Severity, hostName ?? alert.SourceId ?? "", alert.DetailJson ?? "", alert.FirstSeen,
                alert.Count, "");

            var result = await notifier.SendAsync(notification, config, ct);
            if (result.Success)
            {
                await _db.Writer.WithTransactionAsync(conn => AlertRepo.MarkSentAsync(conn, row.Id));
                _metrics.NotificationSent();
                return;
            }

            if (result.Permanent)
            {
                _channelErrors[channel.Id] = result.Error;
                await _db.Audit.WriteAsync("system", "channel.permanent_failure", "notification_channels",
                    channel.Id, JsonSerializer.Serialize(new { error = result.Error, alert = alert.Id }));
                _logger.LogWarning("Channel {Channel} permanently failed: {Error}", channel.Name, result.Error);
                await _db.Writer.WithTransactionAsync(conn => AlertRepo.MarkSentAsync(conn, row.Id)); // drop; don't loop forever
                return;
            }

            await FailAsync(row, result.Error);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Notification dispatch failed for queue row {RowId}", row.Id);
            await FailAsync(row, ex.Message);
        }
    }

    private async Task FailAsync(QueueRow row, string error)
    {
        var attempts = row.Attempts + 1;
        var backoffS = Math.Min(3600, 5 * (int)Math.Pow(2, Math.Min(attempts, 10)));
        var nextAt = DateTimeOffset.UtcNow.AddSeconds(backoffS).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
        await _db.Writer.WithTransactionAsync(conn => AlertRepo.MarkFailedAsync(conn, row.Id, attempts, nextAt, error[..Math.Min(error.Length, 500)]));
    }

    private static readonly System.Text.Json.JsonSerializerOptions _configJsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private async Task<ChannelConfig?> DecryptConfigAsync(ChannelRow channel)
    {
        var secret = await _vault.GetSecretAsync(channel.ConfigRef);
        if (secret is null) return null;
        try
        {
            var cfg = JsonSerializer.Deserialize<ChannelConfig>(secret, _configJsonOpts);
            return cfg is null ? null : cfg with { Kind = channel.Kind, Name = channel.Name };
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>Telegram Bot API sendMessage — outbound-only (DESIGN §13 #4).</summary>
public sealed class TelegramNotifier : INotifier
{
    private readonly HttpClient _http;
    public string Kind => "telegram";

    public TelegramNotifier(IHttpClientFactory f) => _http = f.CreateClient("notify");

    public async Task<NotifyResult> SendAsync(Notification n, ChannelConfig c, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(c.BotToken) || string.IsNullOrEmpty(c.ChatId))
            return new(false, true, "telegram channel missing bot_token/chat_id in vault config");

        var text = Format(n);
        // Telegram: 4096 char limit per message → split.
        foreach (var chunk in Chunks(text, 4096))
        {
            var body = new Dictionary<string, object>
            {
                ["chat_id"] = c.ChatId,
                ["text"] = chunk,
                ["parse_mode"] = "HTML",
                ["disable_web_page_preview"] = true,
            };
            using var resp = await _http.PostAsJsonAsync($"https://api.telegram.org/bot{c.BotToken}/sendMessage", body, ct);
            if ((int)resp.StatusCode == 401 || (int)resp.StatusCode == 403)
                return new(false, true, $"telegram auth failed: {(int)resp.StatusCode}");
            if (!resp.IsSuccessStatusCode)
                return new(false, false, $"telegram HTTP {(int)resp.StatusCode}: {await resp.Content.ReadAsStringAsync(ct)}");
        }
        return new(true, false, "");
    }

    public static string Format(Notification n)
    {
        var emoji = n.Severity switch { "critical" => "🔴", "warning" => "🟠", _ => "🔵" };
        var host = System.Net.WebUtility.HtmlEncode(n.HostName ?? "?");
        var msg = System.Net.WebUtility.HtmlEncode((n.Message ?? "").Trim());
        if (msg.Length > 500) msg = msg[..500] + "…";
        var rule = System.Net.WebUtility.HtmlEncode(n.RuleName);
        var first = System.Net.WebUtility.HtmlEncode(n.FirstSeen);
        var body = $"<b>{emoji} {n.Severity.ToUpperInvariant()}</b> — {rule}\n" +
                   $"<b>Host:</b> {host}\n" +
                   $"<b>First seen:</b> {first} (x{n.Count})\n" +
                   $"<code>{msg}</code>";
        if (!string.IsNullOrEmpty(n.Url)) body += $"\n<a href=\"{n.Url}\">Open</a>";
        return body;
    }

    private static IEnumerable<string> Chunks(string s, int size)
    {
        for (var i = 0; i < s.Length; i += size)
            yield return s.Substring(i, Math.Min(size, s.Length - i));
    }
}

/// <summary>Generic JSON webhook POST (§10.1). SSRF guard: private/loopback/link-local/metadata
/// rejected unless explicitly allowlisted.</summary>
public sealed class WebhookNotifier : INotifier
{
    private readonly HttpClient _http;
    private readonly ServerOptions _opts;
    private readonly ILogger<WebhookNotifier> _logger;
    public string Kind => "webhook";

    public WebhookNotifier(IHttpClientFactory f, ServerOptions opts, ILogger<WebhookNotifier> logger)
    {
        _http = f.CreateClient("notify");
        _opts = opts;
        _logger = logger;
    }

    public async Task<NotifyResult> SendAsync(Notification n, ChannelConfig c, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(c.Url))
            return new(false, true, "webhook channel missing url in vault config");

        var allow = SsrfGuard.IsAllowed(c.Url, _opts.Notifications.Webhook.AllowPrivate, _opts.Notifications.Webhook.AllowedHosts);
        if (!allow.ok)
        {
            _logger.LogWarning("Webhook target blocked by SSRF guard: {Url} ({Reason})", c.Url, allow.reason);
            return new(false, true, $"webhook target blocked: {allow.reason}");
        }

        var payload = new
        {
            alert_id = n.AlertId,
            rule = n.RuleName,
            type = n.RuleType,
            host = n.HostName,
            severity = n.Severity,
            message = n.Message,
            first_seen = n.FirstSeen,
            count = n.Count,
            url = n.Url,
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, c.Url);
        req.Content = JsonContent.Create(payload);
        if (!string.IsNullOrEmpty(c.Secret))
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {c.Secret}");
        try
        {
            using var resp = await _http.SendAsync(req, ct);
            var code = (int)resp.StatusCode;
            if (code is 401 or 403)
                return new(false, true, $"webhook auth failed: {code}");
            if (code is >= 400 and < 500)
                return new(false, false, $"webhook HTTP {code}: {await resp.Content.ReadAsStringAsync(ct)}");
            if (!resp.IsSuccessStatusCode)
                return new(false, false, $"webhook HTTP {code}");
            return new(true, false, "");
        }
        catch (HttpRequestException ex)
        {
            return new(false, false, ex.Message);
        }
    }
}

public static class SsrfGuard
{
    /// <summary>
    /// Reject loopback, link-local, private-network, and cloud-metadata destinations by default
    /// (§10.1, decision S17). Explicit allowlist permits intentional internal targets.
    /// </summary>
    public static (bool ok, string reason) IsAllowed(string url, bool allowPrivate, IReadOnlyList<string> allowedHosts)
    {
        // PROTOCOL §2's https-only rule governs agent↔server transport; outbound webhook
        // targets may be internal http services. The SSRF guard still applies to both schemes.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http"))
            return (false, "not an absolute http(s) URL");

        var host = uri.Host;
        if (allowedHosts.Any(h => string.Equals(h, host, StringComparison.OrdinalIgnoreCase)))
            return (true, "");

        if (IPAddress.TryParse(host, out var ip))
            return CheckIp(ip, allowPrivate);

        try
        {
            var ips = Dns.GetHostAddresses(host);
            foreach (var addr in ips)
            {
                var r = CheckIp(addr, allowPrivate);
                if (!r.ok) return r;
            }
        }
        catch (Exception)
        {
            return (false, "DNS resolution failed");
        }
        return (true, "");
    }

    private static (bool ok, string reason) CheckIp(IPAddress ip, bool allowPrivate)
    {
        if (IPAddress.IsLoopback(ip)) return (false, "loopback destination");
        if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal) return (false, "link-local/site-local destination");
        var bytes = ip.GetAddressBytes();
        bool privateNet = bytes[0] == 10
            || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
            || (bytes[0] == 192 && bytes[1] == 168)
            || (bytes[0] == 169 && bytes[1] == 254)                      // link-local 169.254.x.x
            || (bytes[0] == 100 && bytes[1] is >= 64 and <= 127)         // CGNAT
            || (bytes[0] == 127);
        if (privateNet && !allowPrivate) return (false, "private-network destination (allowlist to permit)");
        // Cloud metadata (169.254.169.254) is covered by 169.254.0.0/16 above.
        return (true, "");
    }
}
