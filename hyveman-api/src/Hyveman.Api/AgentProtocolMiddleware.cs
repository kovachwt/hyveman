using System.IO.Compression;
using System.Text.Json;
using Hyveman.Application;
using Hyveman.Domain;
using Hyveman.Protocol;
using Microsoft.Extensions.Primitives;

namespace Hyveman.Api;

/// <summary>
/// Agent protocol pipeline (API.md §5.1) for /register, /ingest/logs,
/// /ingest/telemetry and /health:
/// 1. correlation/trace id; 2. HTTPS; 3. X-Hyveman-Protocol before body/auth;
/// 4. Content-Encoding (identity|gzip) with a 4 MiB decompressed cap;
/// 5. JSON + body v check; 6. bearer auth → source/scopes; 7. rate limits;
/// 8. endpoint/source-kind validation; 9. application command → protocol
/// response. All envelopes carry the server's current v, X-Hyveman-Protocol
/// and the reserved commands array (PROTOCOL §3/§13/§16).
/// </summary>
public sealed class AgentProtocolMiddleware(RequestDelegate next)
{
    public const int MaxBodyBytes = ProtocolValidation.MaxBodyBytes;

    public async Task InvokeAsync(HttpContext ctx)
    {
        var path = ctx.Request.Path.Value ?? "";
        var isRegister = path == "/register" && ctx.Request.Method == HttpMethods.Post;
        var isLogs = path == "/ingest/logs" && ctx.Request.Method == HttpMethods.Post;
        var isTelemetry = path == "/ingest/telemetry" && ctx.Request.Method == HttpMethods.Post;
        var isHealth = path == "/health" && ctx.Request.Method == HttpMethods.Get;
        if (!isRegister && !isLogs && !isTelemetry && !isHealth)
        {
            await next(ctx);
            return;
        }

        ctx.TraceIdentifier = Guid.NewGuid().ToString("n")[..16];
        var log = ctx.RequestServices.GetRequiredService<ILogger<AgentProtocolMiddleware>>();
        var opts = ctx.RequestServices.GetRequiredService<HyvemanOptions>();

        // 2. HTTPS enforcement (PROTOCOL §2). X-Forwarded-Proto is honored via
        // ForwardedHeaders; dev/test can opt out explicitly.
        if (!ctx.Request.IsHttps && !opts.AllowInsecureHttp)
        {
            await WriteErrorAsync(ctx, 400, ErrorCodes.InvalidRequest, "https required; plain HTTP is rejected");
            return;
        }

        // 3. Protocol version header before anything else (PROTOCOL §3).
        if (!ctx.Request.Headers.TryGetValue(ProtocolVersion.HeaderName, out var header) ||
            StringValues.IsNullOrEmpty(header))
        {
            await WriteErrorAsync(ctx, 400, ErrorCodes.MissingVersion,
                "missing X-Hyveman-Protocol header", supported: ProtocolVersion.Supported);
            return;
        }
        if (!int.TryParse(header.ToString(), out var clientVersion) || !ProtocolVersion.Supported.Contains(clientVersion))
        {
            await WriteErrorAsync(ctx, 400, ErrorCodes.UnsupportedVersion,
                $"unsupported protocol version '{header}'", supported: ProtocolVersion.Supported);
            return;
        }

        // 4. Content-Encoding: identity/absent or gzip only (PROTOCOL §12).
        var encoding = ctx.Request.Headers.ContentEncoding.ToString();
        var gzip = encoding.Equals("gzip", StringComparison.OrdinalIgnoreCase);
        if (encoding.Length > 0 && !gzip && !encoding.Equals("identity", StringComparison.OrdinalIgnoreCase))
        {
            await WriteErrorAsync(ctx, 415, ErrorCodes.UnsupportedMediaType,
                $"unsupported Content-Encoding '{encoding}'");
            return;
        }
        if (!string.IsNullOrEmpty(ctx.Request.ContentType) &&
            !ctx.Request.ContentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
        {
            await WriteErrorAsync(ctx, 415, ErrorCodes.UnsupportedMediaType,
                "Content-Type must be application/json");
            return;
        }

        // Global request budget (PROTOCOL §15).
        var limiter = ctx.RequestServices.GetRequiredService<RateLimiterRegistry>();
        var now = DateTimeOffset.UtcNow;
        var global = limiter.AcquireGlobal(now);
        if (!global.Allowed)
        {
            await WriteErrorAsync(ctx, 429, ErrorCodes.TooManyRequests, "global rate limit exceeded",
                retryAfter: (int)Math.Ceiling(global.RetryAfter.TotalSeconds));
            return;
        }
        ctx.Response.Headers["X-RateLimit-Remaining"] = global.Remaining.ToString();

        try
        {
            if (isHealth)
            {
                await HandleHealthAsync(ctx);
                return;
            }

            // 5. Read + decompress the body with the 4 MiB reassembled cap.
            var body = await ReadBodyAsync(ctx, gzip);
            if (body is null)
            {
                await WriteErrorAsync(ctx, 413, ErrorCodes.PayloadTooLarge,
                    $"request body exceeds {MaxBodyBytes / 1024 / 1024} MiB decompressed limit");
                return;
            }

            JsonDocument? doc = null;
            try
            {
                doc = JsonDocument.Parse(body);
            }
            catch (JsonException)
            {
                await WriteErrorAsync(ctx, 400, ErrorCodes.InvalidRequest, "malformed JSON body");
                return;
            }
            using (doc)
            {
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                {
                    await WriteErrorAsync(ctx, 400, ErrorCodes.InvalidRequest, "request body must be a JSON object");
                    return;
                }

                // Body v: a present value different from the supported header is
                // invalid; a body v never substitutes for the header (PROTOCOL §3).
                if (doc.RootElement.TryGetProperty("v", out var vProp))
                {
                    if (vProp.ValueKind != JsonValueKind.Number || vProp.GetInt32() != ProtocolVersion.Current)
                    {
                        await WriteErrorAsync(ctx, 400, ErrorCodes.InvalidRequest,
                            $"body v does not match protocol version {ProtocolVersion.Current}");
                        return;
                    }
                }
                else
                {
                    await WriteErrorAsync(ctx, 400, ErrorCodes.InvalidRequest, "missing body v");
                    return;
                }

                if (isRegister)
                    await HandleRegisterAsync(ctx, doc.RootElement, body);
                else if (isLogs)
                    await HandleLogsAsync(ctx, doc.RootElement, body);
                else
                    await HandleTelemetryAsync(ctx, doc.RootElement, body);
            }
        }
        catch (RegistrationException ex)
        {
            await WriteErrorAsync(ctx, ex.Status, ex.Code, ex.Message);
        }
        catch (ProtocolException ex)
        {
            await WriteErrorAsync(ctx, ex.Status, ex.Code, ex.Message);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Unhandled error on agent endpoint {path}", path);
            await WriteErrorAsync(ctx, 500, ErrorCodes.Internal, "internal server error");
        }
    }

    private async Task HandleHealthAsync(HttpContext ctx)
    {
        var readiness = ctx.RequestServices.GetRequiredService<IReadinessCheck>();
        var opts = ctx.RequestServices.GetRequiredService<HyvemanOptions>();
        var log = ctx.RequestServices.GetRequiredService<ILogger<AgentProtocolMiddleware>>();

        var response = new HealthResponse
        {
            V = ProtocolVersion.Current,
            Ok = true,
            ServerTime = ProtocolVersion.FormatUtc(DateTimeOffset.UtcNow),
            ServerVersion = opts.ServerVersion,
            Commands = [],
        };

        // Optional token introspection (PROTOCOL §8.1): never enforced here.
        if (TryGetBearer(ctx, out var rawToken) && rawToken.StartsWith(TokenKinds.Agent, StringComparison.Ordinal))
        {
            var tokens = ctx.RequestServices.GetRequiredService<ITokenStore>();
            var auth = await tokens.AuthenticateAsync(rawToken, ctx.RequestAborted);
            if (auth is { } ok)
            {
                response.SourceId = ok.SourceId;
                response.Scopes = ok.Scopes;
            }
        }

        if (!await readiness.IsReadyAsync(ctx.RequestAborted))
        {
            ctx.Response.Headers.RetryAfter = "30";
            await WriteErrorAsync(ctx, 503, ErrorCodes.Unavailable, "server not ready", retryAfter: 30);
            return;
        }

        await WriteResponseAsync(ctx, 200, ProtocolEnvelope.Serialize(response));
    }

    private async Task HandleRegisterAsync(HttpContext ctx, JsonElement root, byte[] body)
    {
        var log = ctx.RequestServices.GetRequiredService<ILogger<AgentProtocolMiddleware>>();
        var limiter = ctx.RequestServices.GetRequiredService<RateLimiterRegistry>();

        if (!TryGetBearer(ctx, out var rawToken))
        {
            await WriteErrorAsync(ctx, 401, ErrorCodes.TokenMissing, "missing Authorization header");
            return;
        }
        if (!rawToken.StartsWith(TokenKinds.Registration, StringComparison.Ordinal))
        {
            // An agent token used here is a scope problem (PROTOCOL §4.3).
            var tokens = ctx.RequestServices.GetRequiredService<ITokenStore>();
            if (await tokens.AuthenticateAsync(rawToken, ctx.RequestAborted) is not null)
            {
                await WriteErrorAsync(ctx, 403, ErrorCodes.WrongScope, "token does not have register scope");
                return;
            }
            await WriteErrorAsync(ctx, 401, ErrorCodes.TokenInvalid, "registration endpoint requires a reg_ token");
            return;
        }

        // Registration budget keyed by network (PROTOCOL §15).
        var reg = limiter.AcquireRegistration(ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown", DateTimeOffset.UtcNow);
        if (!reg.Allowed)
        {
            await WriteErrorAsync(ctx, 429, ErrorCodes.TooManyRequests, "registration rate limit exceeded",
                retryAfter: (int)Math.Ceiling(reg.RetryAfter.TotalSeconds));
            return;
        }

        // Structural schema check (forward-compatible, PROTOCOL §6.7).
        var schemaErrors = ProtocolSchema.Validator.Validate("#", System.Text.Encoding.UTF8.GetString(body));
        if (schemaErrors.Count > 0)
        {
            log.LogDebug("Register schema rejected: {errors}", string.Join("; ", schemaErrors.Take(3)));
            await WriteErrorAsync(ctx, 400, ErrorCodes.InvalidRequest, "invalid register request");
            return;
        }

        var registration = ctx.RequestServices.GetRequiredService<RegistrationService>();
        var req = JsonSerializer.Deserialize<RegisterRequest>(root.GetRawText(), ProtocolJson.Options)!;
        var outcome = await registration.RegisterAsync(rawToken, req.Kind!, req.Hostname!,
            req.AgentVersion, req.OsBuild, ctx.RequestAborted);

        var response = new RegisterResponse
        {
            V = ProtocolVersion.Current,
            SourceId = outcome.SourceId,
            Token = outcome.RawToken,
            Scopes = outcome.Scopes,
            IssuedAt = ProtocolVersion.FormatUtc(outcome.IssuedAt),
            Commands = [],
        };
        await WriteResponseAsync(ctx, 200, ProtocolEnvelope.Serialize(response));
    }

    private async Task HandleLogsAsync(HttpContext ctx, JsonElement root, byte[] body)
    {
        var log = ctx.RequestServices.GetRequiredService<ILogger<AgentProtocolMiddleware>>();
        var auth = await AuthenticateIngestAsync(ctx);
        if (auth is null) return;
        var (sourceId, sourceKind) = auth.Value;

        WarnSourceHintMismatch(ctx, root, sourceId);

        var req = JsonSerializer.Deserialize<LogBatchRequest>(root.GetRawText(), ProtocolJson.Options);
        if (req?.Items is null)
        {
            await WriteErrorAsync(ctx, 400, ErrorCodes.InvalidRequest, "items must be an array");
            return;
        }

        var service = ctx.RequestServices.GetRequiredService<LogIngestService>();
        var result = await service.IngestAsync(sourceId, sourceKind, req.Items, ctx.RequestAborted);
        var response = new LogsResponse
        {
            V = ProtocolVersion.Current,
            Accepted = result.Accepted,
            Deduped = result.Deduped,
            Rejected = result.Rejected.Select(r => new RejectedItem
            {
                RecordId = r.RecordId,
                DedupScope = r.DedupScope,
                Reason = r.Reason,
                Permanent = r.Permanent,
            }).ToList(),
            Commands = [],
        };
        await WriteResponseAsync(ctx, 200, ProtocolEnvelope.Serialize(response));
    }

    private async Task HandleTelemetryAsync(HttpContext ctx, JsonElement root, byte[] body)
    {
        var log = ctx.RequestServices.GetRequiredService<ILogger<AgentProtocolMiddleware>>();
        var auth = await AuthenticateIngestAsync(ctx);
        if (auth is null) return;
        var (sourceId, sourceKind) = auth.Value;

        WarnSourceHintMismatch(ctx, root, sourceId);

        // Whole-batch semantics: structural schema check first (PROTOCOL §6.7).
        var schemaErrors = ProtocolSchema.Validator.Validate("#", System.Text.Encoding.UTF8.GetString(body));
        if (schemaErrors.Count > 0)
        {
            log.LogDebug("Telemetry schema rejected: {errors}", string.Join("; ", schemaErrors.Take(3)));
            await WriteErrorAsync(ctx, 400, ErrorCodes.InvalidRequest, "invalid telemetry request");
            return;
        }

        var req = JsonSerializer.Deserialize<TelemetryRequest>(root.GetRawText(), ProtocolJson.Options);
        if (req?.Items is null)
        {
            await WriteErrorAsync(ctx, 400, ErrorCodes.InvalidRequest, "items must be an array");
            return;
        }

        var service = ctx.RequestServices.GetRequiredService<TelemetryService>();
        await service.ProcessAsync(sourceId, req.Items, ctx.RequestAborted);
        var response = new TelemetryResponse { V = ProtocolVersion.Current, Accepted = true, Commands = [] };
        await WriteResponseAsync(ctx, 200, ProtocolEnvelope.Serialize(response));
    }

    /// <summary>Authenticates ingest endpoints: agt_ token, ingest scope,
    /// per-source rate limit. Returns null after writing the error response.</summary>
    private async Task<(string SourceId, string SourceKind)?> AuthenticateIngestAsync(HttpContext ctx)
    {
        var log = ctx.RequestServices.GetRequiredService<ILogger<AgentProtocolMiddleware>>();
        var limiter = ctx.RequestServices.GetRequiredService<RateLimiterRegistry>();

        if (!TryGetBearer(ctx, out var rawToken))
        {
            await WriteErrorAsync(ctx, 401, ErrorCodes.TokenMissing, "missing Authorization header");
            return null;
        }
        var tokens = ctx.RequestServices.GetRequiredService<ITokenStore>();
        var auth = await tokens.AuthenticateAsync(rawToken, ctx.RequestAborted);
        if (auth is null)
        {
            // A known registration token used here is a scope problem, not an
            // unknown credential (PROTOCOL §4.3: wrong_scope, never 401).
            if (rawToken.StartsWith(TokenKinds.Registration, StringComparison.Ordinal) &&
                await IsKnownRegistrationTokenAsync(ctx, rawToken))
            {
                await WriteErrorAsync(ctx, 403, ErrorCodes.WrongScope, "token does not have ingest scope");
                return null;
            }
            // Distinguish revoked from invalid/missing source via the store.
            var revoked = await tokens.IsRevokedAsync(rawToken, ctx.RequestAborted);
            if (revoked)
                await WriteErrorAsync(ctx, 401, ErrorCodes.TokenRevoked, "token revoked; re-register");
            else if (await tokens.SourceMissingAsync(rawToken, ctx.RequestAborted))
                await WriteErrorAsync(ctx, 404, ErrorCodes.UnknownSource, "token's source no longer exists; re-register");
            else
                await WriteErrorAsync(ctx, 401, ErrorCodes.TokenInvalid, "invalid token");
            return null;
        }

        if (!auth.Scopes.Contains(TokenKinds.ScopeIngest))
        {
            await WriteErrorAsync(ctx, 403, ErrorCodes.WrongScope, "token does not have ingest scope");
            return null;
        }

        var perSource = limiter.AcquirePerSource(auth.SourceId, DateTimeOffset.UtcNow);
        if (!perSource.Allowed)
        {
            await WriteErrorAsync(ctx, 429, ErrorCodes.TooManyRequests, "per-source rate limit exceeded",
                retryAfter: (int)Math.Ceiling(perSource.RetryAfter.TotalSeconds));
            return null;
        }
        ctx.Response.Headers["X-RateLimit-Remaining"] = perSource.Remaining.ToString();
        return (auth.SourceId, auth.SourceKind);
    }

    private static void WarnSourceHintMismatch(HttpContext ctx, JsonElement root, string sourceId)
    {
        var log = ctx.RequestServices.GetRequiredService<ILogger<AgentProtocolMiddleware>>();
        if (ctx.Request.Headers.TryGetValue("X-Hyveman-Source", out var header) &&
            !header.ToString().Equals(sourceId, StringComparison.Ordinal))
            log.LogWarning("X-Hyveman-Source header {hint} does not match token source {sourceId}",
                header, sourceId);
        if (root.TryGetProperty("source", out var src) && src.ValueKind == JsonValueKind.String &&
            !src.GetString()!.Equals(sourceId, StringComparison.Ordinal))
            log.LogWarning("Body source hint {hint} does not match token source {sourceId}",
                src.GetString(), sourceId);
    }

    private static async Task<bool> IsKnownRegistrationTokenAsync(HttpContext ctx, string rawToken)
    {
        var store = ctx.RequestServices.GetRequiredService<IRegistrationTokenStore>();
        var lookup = await store.LookupAsync(rawToken, ctx.RequestAborted);
        return lookup is not null && !lookup.Revoked && lookup.ConsumedAt is null;
    }

    private static bool TryGetBearer(HttpContext ctx, out string token)
    {
        token = "";
        var auth = ctx.Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(auth)) return false;
        const string prefix = "Bearer ";
        if (!auth.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        token = auth[prefix.Length..].Trim();
        return token.Length > 0;
    }

    private static async Task<byte[]?> ReadBodyAsync(HttpContext ctx, bool gzip)
    {
        if (ctx.Request.ContentLength is { } cl && cl > MaxBodyBytes) return null;
        using var ms = new MemoryStream();
        var buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            var read = await ctx.Request.Body.ReadAsync(buffer, ctx.RequestAborted);
            if (read == 0) break;
            total += read;
            if (total > MaxBodyBytes) return null;
            ms.Write(buffer, 0, read);
        }
        var raw = ms.ToArray();
        if (!gzip) return raw;
        try
        {
            using var input = new MemoryStream(raw);
            using var gz = new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            long outTotal = 0;
            while (true)
            {
                var read = gz.Read(buffer, 0, buffer.Length);
                if (read == 0) break;
                outTotal += read;
                if (outTotal > MaxBodyBytes) return null;
                output.Write(buffer, 0, read);
            }
            return output.ToArray();
        }
        catch (InvalidDataException)
        {
            return []; // marker: malformed gzip → empty body → JSON parse error
        }
    }

    private static Task WriteErrorAsync(HttpContext ctx, int status, string code, string message,
        int[]? supported = null, int? retryAfter = null)
        => WriteResponseAsync(ctx, status, ProtocolEnvelope.Serialize(ProtocolEnvelope.Error(code, message, supported)), retryAfter);

    private static Task WriteResponseAsync(HttpContext ctx, int status, string json, int? retryAfter = null)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.Headers[ProtocolVersion.HeaderName] = ProtocolVersion.Current.ToString();
        ctx.Response.ContentType = "application/json; charset=utf-8";
        if (retryAfter is { } ra) ctx.Response.Headers.RetryAfter = ra.ToString();
        return ctx.Response.WriteAsync(json, ctx.RequestAborted);
    }
}
