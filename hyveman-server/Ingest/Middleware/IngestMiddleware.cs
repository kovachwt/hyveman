using System.IO.Compression;
using Hyveman.Server.Config;
using Hyveman.Server.RateLimit;

namespace Hyveman.Server.Ingest.Middleware;

/// <summary>
/// Request pipeline (order matters, §7.1):
/// ExceptionTrap → ProtocolVersionHeader → Gzip → ProtocolVersionBody → Auth → RateLimit → endpoint.
/// </summary>
public static class IngestMiddleware
{
    public const string ItemsVersion = "hyveman.v";
    public const string ItemsSourceId = "hyveman.source_id";
    public const string ItemsScopes = "hyveman.scopes";
    public const string ItemsBodyBytes = "hyveman.body_bytes";
    public const string ItemsRateBucket = "hyveman.rate_bucket";

    public static IApplicationBuilder UseIngestMiddleware(this IApplicationBuilder app) => app
        .UseMiddleware<ExceptionTrapMiddleware>()
        .UseMiddleware<ProtocolVersionHeaderMiddleware>()
        .UseMiddleware<GzipMiddleware>()
        .UseMiddleware<ProtocolVersionBodyMiddleware>()
        .UseMiddleware<AuthMiddleware>()
        .UseMiddleware<RateLimitMiddleware>();
}

/// <summary>Unhandled exception → 500 internal with request id (never leaks stack traces).</summary>
public sealed class ExceptionTrapMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionTrapMiddleware> _logger;

    public ExceptionTrapMiddleware(RequestDelegate next, ILogger<ExceptionTrapMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext ctx)
    {
        try
        {
            await _next(ctx);
        }
        catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested)
        {
            // Client went away mid-request; nothing to write.
        }
        catch (Exception ex)
        {
            var requestId = Guid.NewGuid().ToString("N")[..12];
            _logger.LogError(ex, "Unhandled exception (request {RequestId}) {Method} {Path}",
                requestId, ctx.Request.Method, ctx.Request.Path);
            if (!ctx.Response.HasStarted)
            {
                ctx.Response.Headers["X-Hyveman-RequestId"] = requestId;
                await IngestResponse.Error(ctx, 500, "internal", $"internal error (request {requestId})");
            }
        }
    }
}

/// <summary>Require X-Hyveman-Protocol header; range-check against the supported set (§7.2).</summary>
public sealed class ProtocolVersionHeaderMiddleware
{
    private readonly RequestDelegate _next;

    public ProtocolVersionHeaderMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext ctx)
    {
        var header = ctx.Request.Headers["X-Hyveman-Protocol"].ToString();
        if (!int.TryParse(header, out var v))
        {
            await IngestResponse.Error(ctx, 400, "missing_version",
                "X-Hyveman-Protocol header is required (PROTOCOL §3)");
            return;
        }
        if (v != ServerOptions.CurrentProtocolVersion)
        {
            await IngestResponse.UnsupportedVersion(ctx,
                $"protocol version {v} is not supported; supported: [1]");
            return;
        }
        ctx.Items[IngestMiddleware.ItemsVersion] = v;
        await _next(ctx);
    }
}

/// <summary>Optional gzip request decompression; enforces max_batch_bytes on the decompressed body.</summary>
public sealed class GzipMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ServerOptions _opts;

    public GzipMiddleware(RequestDelegate next, ServerOptions opts)
    {
        _next = next;
        _opts = opts;
    }

    public async Task InvokeAsync(HttpContext ctx)
    {
        var encoding = ctx.Request.Headers.ContentEncoding.ToString().Trim();
        var contentType = ctx.Request.ContentType ?? "";
        var isJson = contentType.Contains("json", StringComparison.OrdinalIgnoreCase);

        if (isJson && ctx.Request.ContentLength is > 0 &&
            ctx.Request.ContentLength > _opts.Ingest.MaxBatchBytes)
        {
            await IngestResponse.Error(ctx, 413, "payload_too_large",
                $"request body exceeds max_batch_bytes ({_opts.Ingest.MaxBatchBytes}) — split and resend");
            return;
        }

        if (!string.IsNullOrEmpty(encoding) && !encoding.Equals("gzip", StringComparison.OrdinalIgnoreCase))
        {
            await IngestResponse.Error(ctx, 415, "invalid_request",
                $"unsupported Content-Encoding '{encoding}' (only gzip or identity)");
            return;
        }

        if (isJson && HttpMethods.IsPost(ctx.Request.Method))
        {
            // Always buffer JSON POST bodies (bounded by max_batch_bytes) so the version-lockstep
            // check and endpoints see a rewindable stream; gzip is decompressed and capped too.
            byte[] body;
            if (encoding.Equals("gzip", StringComparison.OrdinalIgnoreCase))
            {
                var compressed = await ReadBodyAsync(ctx.Request.Body, _opts.Ingest.MaxBatchBytes);
                if (compressed is null)
                {
                    await IngestResponse.Error(ctx, 413, "payload_too_large",
                        $"request body exceeds max_batch_bytes ({_opts.Ingest.MaxBatchBytes}) — split and resend");
                    return;
                }
                try
                {
                    await using var input = new MemoryStream(compressed);
                    await using var gz = new GZipStream(input, CompressionMode.Decompress);
                    var output = new MemoryStream();
                    await gz.CopyToAsync(output);
                    body = output.ToArray();
                }
                catch (InvalidDataException)
                {
                    await IngestResponse.Error(ctx, 400, "invalid_request", "malformed gzip body");
                    return;
                }
                if (body.Length > _opts.Ingest.MaxBatchBytes)
                {
                    await IngestResponse.Error(ctx, 413, "payload_too_large",
                        $"decompressed body exceeds max_batch_bytes ({_opts.Ingest.MaxBatchBytes}) — split and resend");
                    return;
                }
            }
            else
            {
                body = (await ReadBodyAsync(ctx.Request.Body, _opts.Ingest.MaxBatchBytes))!;
                if (body.Length > _opts.Ingest.MaxBatchBytes)
                {
                    await IngestResponse.Error(ctx, 413, "payload_too_large",
                        $"request body exceeds max_batch_bytes ({_opts.Ingest.MaxBatchBytes}) — split and resend");
                    return;
                }
            }
            ctx.Request.Body = new MemoryStream(body);
            ctx.Items[IngestMiddleware.ItemsBodyBytes] = body.Length;
        }
        else
        {
            ctx.Items[IngestMiddleware.ItemsBodyBytes] = (int)(ctx.Request.ContentLength ?? 0);
        }

        await _next(ctx);
    }

    /// <summary>Read up to <paramref name="cap"/>+1 bytes; returns null if the body exceeds the cap.</summary>
    private static async Task<byte[]?> ReadBodyAsync(Stream body, int cap)
    {
        using var ms = new MemoryStream();
        var buf = new byte[81920];
        var total = 0;
        int read;
        while ((read = await body.ReadAsync(buf)) > 0)
        {
            total += read;
            if (total > cap) return null;
            ms.Write(buf, 0, read);
        }
        return ms.ToArray();
    }
}

/// <summary>Body/header version lockstep (§7.2): a JSON body must carry the same "v" as the header.</summary>
public sealed class ProtocolVersionBodyMiddleware
{
    private readonly RequestDelegate _next;

    public ProtocolVersionBodyMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext ctx)
    {
        // Bodyless requests (GET /health) have no body version field; header is authoritative.
        if (!HttpMethods.IsPost(ctx.Request.Method))
        {
            await _next(ctx);
            return;
        }
        // Peek the first object's "v" without consuming the stream.
        if (ctx.Request.Body is MemoryStream)
        {
            var pos = ctx.Request.Body.Position;
            try
            {
                using var doc = await System.Text.Json.JsonDocument.ParseAsync(ctx.Request.Body);
                if (doc.RootElement.TryGetProperty("v", out var vEl) && vEl.ValueKind == System.Text.Json.JsonValueKind.Number)
                {
                    var bodyV = vEl.GetInt32();
                    var headerV = (int)ctx.Items[IngestMiddleware.ItemsVersion]!;
                    if (bodyV != headerV)
                    {
                        await IngestResponse.Error(ctx, 400, "invalid_request",
                            $"version_mismatch: X-Hyveman-Protocol={headerV} but body v={bodyV}");
                        return;
                    }
                }
            }
            catch (System.Text.Json.JsonException)
            {
                // Let the endpoint report malformed JSON with its own error.
            }
            finally
            {
                ctx.Request.Body.Position = pos;
            }
        }
        await _next(ctx);
    }
}

/// <summary>
/// Bearer-token auth (§7.3). /health is lenient: missing token OK, invalid supplied token
/// never 4xxes. Sets source_id/scopes in HttpContext.Items on success.
/// </summary>
public sealed class AuthMiddleware
{
    private readonly RequestDelegate _next;
    private readonly Auth.ITokenService _tokens;
    private readonly ILogger<AuthMiddleware> _logger;

    public AuthMiddleware(RequestDelegate next, Auth.ITokenService tokens, ILogger<AuthMiddleware> logger)
    {
        _next = next;
        _tokens = tokens;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext ctx)
    {
        var path = ctx.Request.Path.Value ?? "";
        var isHealth = path.Equals("/health", StringComparison.OrdinalIgnoreCase);
        var isRegister = path.Equals("/register", StringComparison.OrdinalIgnoreCase);

        var auth = ctx.Request.Headers.Authorization.ToString();
        string? rawToken = null;
        if (!string.IsNullOrEmpty(auth))
        {
            if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                rawToken = auth["Bearer ".Length..].Trim();
            else
            {
                if (!isHealth)
                {
                    await IngestResponse.Error(ctx, 401, "token_invalid", "Authorization must be 'Bearer <token>'");
                    return;
                }
            }
        }

        if (string.IsNullOrEmpty(rawToken))
        {
            if (isHealth) { await _next(ctx); return; }   // connectivity-only health
            await IngestResponse.Error(ctx, 401, "token_missing", "Authorization: Bearer <token> is required");
            return;
        }

        var res = await _tokens.ResolveAsync(rawToken);
        if (isHealth)
        {
            // Lenient: never 4xx on /health; validity surfaces via source_id/scopes presence.
            if (res.Outcome == Auth.TokenResolveOutcome.Ok)
            {
                ctx.Items[IngestMiddleware.ItemsSourceId] = res.Source!.Id;
                ctx.Items[IngestMiddleware.ItemsScopes] = res.Token!.Scopes;
            }
            await _next(ctx);
            return;
        }

        switch (res.Outcome)
        {
            case Auth.TokenResolveOutcome.Invalid:
                await IngestResponse.Error(ctx, 401, "token_invalid", "unknown, malformed, or expired token");
                return;
            case Auth.TokenResolveOutcome.Revoked:
                await IngestResponse.Error(ctx, 401, "token_revoked", "token has been revoked");
                return;
            case Auth.TokenResolveOutcome.Consumed:
                await IngestResponse.Error(ctx, 410, "token_consumed", "token already consumed (single-use)");
                return;
            case Auth.TokenResolveOutcome.UnknownSource:
                await IngestResponse.Error(ctx, 404, "unknown_source", "token's source no longer exists — re-register");
                return;
        }

        // Scope check (per endpoint): register token on non-/register, or ingest token on /register.
        var scopes = ParseScopes(res.Token!.Scopes);
        if (isRegister && !scopes.Contains("register"))
        {
            await IngestResponse.Error(ctx, 403, "wrong_scope", "register endpoint requires a register-scoped token");
            return;
        }
        if (!isRegister && !scopes.Contains("ingest"))
        {
            await IngestResponse.Error(ctx, 403, "wrong_scope", $"endpoint requires ingest scope; token has {res.Token.Scopes}");
            return;
        }

        if (res.Source is not null)
        {
            ctx.Items[IngestMiddleware.ItemsSourceId] = res.Source.Id;
            ctx.Items[IngestMiddleware.ItemsScopes] = res.Token.Scopes;
        }

        // Corroborating identity check (§4.2): body source / X-Hyveman-Source are hints only.
        var headerSource = ctx.Request.Headers["X-Hyveman-Source"].ToString();
        if (!string.IsNullOrEmpty(headerSource) && res.Source is not null && headerSource != res.Source.Id)
            _logger.LogWarning("X-Hyveman-Source {Claimed} differs from token source {Actual} (possible misconfig)",
                headerSource, res.Source.Id);

        await _next(ctx);
    }

    public static List<string> ParseScopes(string scopesJson)
    {
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<List<string>>(scopesJson) ?? new List<string>();
        }
        catch
        {
            return new List<string>();
        }
    }
}

/// <summary>Per-source + global token-bucket rate limiting (§7.4, PROTOCOL §15).</summary>
public sealed class RateLimitMiddleware
{
    private readonly RequestDelegate _next;
    private readonly RateLimiter _limiter;
    private readonly ServerOptions _opts;

    public RateLimitMiddleware(RequestDelegate next, RateLimiter limiter, ServerOptions opts)
    {
        _next = next;
        _limiter = limiter;
        _opts = opts;
    }

    public async Task InvokeAsync(HttpContext ctx)
    {
        var path = ctx.Request.Path.Value ?? "";
        if (!path.StartsWith("/ingest/", StringComparison.Ordinal) && path != "/register")
        {
            await _next(ctx);
            return;
        }

        // Bucket key: token's source_id; reg_ tokens (no source) get the small __register__ budget.
        string bucket;
        var cfg = _opts.Ingest.PerSourceRate;
        var sourceId = ctx.Items[IngestMiddleware.ItemsSourceId] as string;
        if (path == "/register" || sourceId is null)
        {
            bucket = "__register__";
            cfg = _opts.Ingest.RegisterRate;
        }
        else
        {
            bucket = sourceId;
        }

        var bodyBytes = (int)(ctx.Items[IngestMiddleware.ItemsBodyBytes] ?? 0);
        var (allowed, retryAfter, remaining) = _limiter.TryTake(bucket, cfg, bodyBytes);
        if (!allowed)
        {
            ctx.Response.Headers["X-RateLimit-Remaining"] = remaining.ToString();
            await IngestResponse.Error(ctx, 429, "too_many_requests",
                $"rate limit exceeded for {bucket}", retryAfter: true, retrySeconds: retryAfter);
            return;
        }
        ctx.Response.Headers["X-RateLimit-Remaining"] = remaining.ToString();
        await _next(ctx);
    }
}
