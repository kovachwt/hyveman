using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Hyveman.Agent.Options;
using Microsoft.Extensions.Logging;

namespace Hyveman.Agent.Net;

/// <summary>
/// HTTPS client for the two ingest endpoints + register + health (PROTOCOL §2-§8).
/// One request per batch, closed after the response; no inbound listener ever.
/// </summary>
public sealed class BackendClient : IDisposable
{
    public const string ProtocolHeader = "X-Hyveman-Protocol";
    public const string SourceHeader = "X-Hyveman-Source";
    public const int ProtocolVersion = 1;

    private readonly HttpClient _http;
    private readonly OptionsSnapshot _snapshot;
    private readonly ILogger<BackendClient> _log;

    public BackendClient(OptionsSnapshot snapshot, ILogger<BackendClient> log)
    {
        _snapshot = snapshot;
        _log = log;

        var opts = snapshot.Active;
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            MaxConnectionsPerServer = Math.Max(2, opts.Limits.SendConcurrency),
            AutomaticDecompression = DecompressionMethods.None
        };

        handler.SslOptions = new SslClientAuthenticationOptions();

        if (!opts.Backend.ValidateCert)
        {
            _log.LogWarning("BACKEND TLS VALIDATION DISABLED (backend.validate_cert=false) — LAB ONLY, never for production");
            handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        }
        else if (!string.IsNullOrEmpty(opts.Backend.CaPath))
        {
            var ca = new X509Certificate2(opts.Backend.CaPath);
            handler.SslOptions.RemoteCertificateValidationCallback = (_, cert, _, _) =>
            {
                try
                {
                    using var chain = new X509Chain();
                    chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                    chain.ChainPolicy.CustomTrustStore.Add(ca);
                    chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                    return chain.Build(new X509Certificate2(cert!));
                }
                catch (Exception)
                {
                    return false;
                }
            };
        }

        // Belt-and-braces dual timeout: SendAndClassifyAsync cancels each
        // request at SendTimeoutMs via its own linked CTS (authoritative);
        // this HttpClient.Timeout is a hard floor slightly above it so the
        // cancellation path can never be bypassed (e.g. a hung read).
        _http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(Math.Max(30, opts.Limits.SendTimeoutMs + 5000))
        };
    }

    private string BaseUrl => _snapshot.Active.Backend.Url.TrimEnd('/');

    private void ApplyAuthAndProtocol(HttpRequestMessage req, string? token, string? sourceId)
    {
        req.Headers.TryAddWithoutValidation(ProtocolHeader, ProtocolVersion.ToString());
        req.Headers.TryAddWithoutValidation(SourceHeader, sourceId ?? _snapshot.Active.SourceId ?? "");
        req.Headers.TryAddWithoutValidation("User-Agent",
            $"hyveman-agent/{typeof(BackendClient).Assembly.GetName().Version?.ToString(3) ?? "0.0.0"} (+windows-agent; os={Environment.OSVersion.Version.Build})");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token ?? _snapshot.Active.Backend.Token ?? "");
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    /// <summary>
    /// POSTs one spooled log batch; classifies the outcome per PROTOCOL §13.3/§14.
    /// The caller owns retry timing (the spool file stays until 2xx).
    /// </summary>
    public async Task<SendResult> PostLogsAsync(byte[] body, bool gzip, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, BaseUrl + "/ingest/logs");
        ApplyAuthAndProtocol(req, null, null);
        req.Content = BuildJsonContent(body, gzip);
        return await SendAndClassifyAsync(req, parseLogs: true, ct).ConfigureAwait(false);
    }

    public async Task<SendResult> PostTelemetryAsync(byte[] body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, BaseUrl + "/ingest/telemetry");
        ApplyAuthAndProtocol(req, null, null);
        req.Content = BuildJsonContent(body, gzip: false);
        return await SendAndClassifyAsync(req, parseLogs: false, ct).ConfigureAwait(false);
    }

    public async Task<RegisterResponse?> RegisterAsync(RegisterRequest request, string regToken, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, BaseUrl + "/register");
        ApplyAuthAndProtocol(req, regToken, null);
        req.Content = BuildJsonContent(JsonSerializer.SerializeToUtf8Bytes(request), gzip: false);

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
        {
            _log.LogWarning("POST /register failed: {status} {body}", (int)resp.StatusCode, Truncate(body, 300));
            return null;
        }

        return JsonSerializer.Deserialize<RegisterResponse>(body);
    }

    /// <summary>
    /// GET /health — connectivity, and token introspection when a token is
    /// present (PROTOCOL §8). With a null/empty token the Authorization header
    /// is omitted entirely (the §8.1 connectivity-only variant); with a token,
    /// .source_id/.scopes in the response tell the caller whether it resolved.
    /// </summary>
    public async Task<HealthResponse?> HealthAsync(string? token, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, BaseUrl + "/health");
        req.Headers.TryAddWithoutValidation(ProtocolHeader, ProtocolVersion.ToString());
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!string.IsNullOrEmpty(token))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        try
        {
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return null;
            return JsonSerializer.Deserialize<HealthResponse>(body);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "GET /health failed");
            return null;
        }
    }

    private HttpContent BuildJsonContent(byte[] body, bool gzip)
    {
        ByteArrayContent content;
        if (gzip)
        {
            using var ms = new MemoryStream();
            using (var gz = new System.IO.Compression.GZipStream(ms, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
                gz.Write(body, 0, body.Length);
            content = new ByteArrayContent(ms.ToArray());
            content.Headers.ContentEncoding.Add("gzip");
        }
        else
        {
            content = new ByteArrayContent(body);
        }
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        return content;
    }

    private async Task<SendResult> SendAndClassifyAsync(HttpRequestMessage req, bool parseLogs, CancellationToken ct)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(_snapshot.Active.Limits.SendTimeoutMs));

            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, timeoutCts.Token).ConfigureAwait(false);
            var status = resp.StatusCode;

            if (status is >= HttpStatusCode.OK and < HttpStatusCode.BadRequest)
            {
                if (!parseLogs)
                    return new SendResult(SendOutcome.Accepted);

                var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                var parsed = JsonSerializer.Deserialize<LogsResponse>(body);
                if (parsed?.Rejected is { Count: > 0 } rejected && rejected.Any(r => r.Permanent))
                {
                    _log.LogWarning("Server permanently rejected {n} item(s) in batch (e.g. {reason}); quarantining batch",
                        rejected.Count, rejected[0].Reason);
                    return new SendResult(SendOutcome.Quarantine, Logs: parsed);
                }
                return new SendResult(SendOutcome.Accepted, Logs: parsed);
            }

            if (status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests or HttpStatusCode.BadGateway
                or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout
                or >= HttpStatusCode.InternalServerError)
            {
                var retryAfter = resp.Headers.RetryAfter?.Delta?.TotalSeconds is { } ra ? (int)ra : (int?)null;
                return new SendResult(SendOutcome.Retry, retryAfter);
            }

            var code = await TryReadErrorCodeAsync(resp, ct).ConfigureAwait(false);

            // 400 too_many_items / 413 payload_too_large → split & resend.
            if (status == (HttpStatusCode)413 || code == "too_many_items")
                return new SendResult(SendOutcome.Split, ErrorCode: code);

            // Credential/source-class 4xx (PROTOCOL §13.3): token_invalid,
            // token_revoked, wrong_scope, unknown_source, token_consumed. The
            // batch is valid — the credentials are the problem. Keep the spool
            // file (never quarantine a good batch); the sender surfaces
            // auth_rejected and retries slowly (SPEC-DEVIATIONS P1-3).
            if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                or HttpStatusCode.NotFound or HttpStatusCode.Gone)
            {
                _log.LogWarning("Auth/source error {status} from /ingest/logs (code {code}); keeping spool file — agent may need re-registration",
                    (int)status, code ?? "-");
                return new SendResult(SendOutcome.CredentialsInvalid, ErrorCode: code);
            }

            _log.LogWarning("Non-retryable {status} from /ingest/logs (code {code}); quarantining batch", (int)status, code ?? "-");
            return new SendResult(SendOutcome.Quarantine, ErrorCode: code);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new SendResult(SendOutcome.Retry); // send timeout
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Network error posting to {path}", req.RequestUri?.AbsolutePath);
            return new SendResult(SendOutcome.Retry); // TLS/DNS/conn reset
        }
    }

    private static async Task<string?> TryReadErrorCodeAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try
        {
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var err) &&
                err.TryGetProperty("code", out var code))
                return code.GetString();
        }
        catch (Exception) { /* non-JSON body */ }
        return null;
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "…";

    public void Dispose() => _http.Dispose();
}
