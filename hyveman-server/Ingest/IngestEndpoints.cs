using Hyveman.Server.Auth;
using Hyveman.Server.Config;
using Hyveman.Server.Ingest.Middleware;

namespace Hyveman.Server.Ingest;

public static class IngestEndpoints
{
    public static void MapIngestApi(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("");
        group.MapPost("/register", RegisterAsync);
        group.MapPost("/ingest/logs", IngestLogsAsync);
        group.MapPost("/ingest/telemetry", IngestTelemetryAsync);
        group.MapGet("/health", HealthAsync);
    }

    private static async Task RegisterAsync(HttpContext ctx, RegistrationService svc)
    {
        var req = await ReadBodyAsync<RegisterRequest>(ctx, WireJsonContext.Default.RegisterRequest);
        if (req is null) return;   // error already written

        var rawToken = ctx.Items["hyveman.raw_token"] as string ?? ExtractRawToken(ctx);
        if (rawToken is null)
        {
            // AuthMiddleware normally handles this; belt & braces for direct routing.
            await IngestResponse.Error(ctx, 401, "token_missing", "Authorization: Bearer <reg_ token> is required");
            return;
        }

        var result = await svc.RegisterAsync(req, rawToken);
        if (!result.Ok)
        {
            await IngestResponse.Error(ctx, result.Status, result.ErrorCode!, result.ErrorMessage!);
            return;
        }

        await IngestResponse.Ok(ctx, new RegisterResponse
        {
            V = ServerOptions.CurrentProtocolVersion,
            SourceId = result.SourceId!,
            Token = result.Token!,
            Scopes = result.Scopes!,
            IssuedAt = result.IssuedAt!,
        });
    }

    private static async Task IngestLogsAsync(HttpContext ctx, LogIngestService svc, Observability.OwnMetrics metrics)
    {
        var sourceId = ctx.Items[IngestMiddleware.ItemsSourceId] as string
            ?? throw new InvalidOperationException("auth middleware must set source_id");
        var bodyBytes = (int)(ctx.Items[IngestMiddleware.ItemsBodyBytes] ?? 0);

        var req = await ReadBodyAsync<LogsRequest>(ctx, WireJsonContext.Default.LogsRequest);
        if (req is null) return;

        var result = await svc.IngestAsync(sourceId, req, bodyBytes);
        switch (result.Error)
        {
            case LogIngestService.BatchError.PayloadTooLarge:
                await IngestResponse.Error(ctx, 413, result.ErrorCode!, result.ErrorMessage!);
                return;
            case LogIngestService.BatchError.TooManyItems:
            case LogIngestService.BatchError.WrongItemKind:
            case LogIngestService.BatchError.BadJson:
                await IngestResponse.Error(ctx, 400, result.ErrorCode!, result.ErrorMessage!);
                return;
        }

        await IngestResponse.Ok(ctx, new LogsResponse
        {
            V = ServerOptions.CurrentProtocolVersion,
            Accepted = result.Accepted,
            Deduped = result.Deduped,
            Rejected = result.Rejected,
        });
    }

    private static async Task IngestTelemetryAsync(HttpContext ctx, TelemetryService svc)
    {
        var sourceId = ctx.Items[IngestMiddleware.ItemsSourceId] as string
            ?? throw new InvalidOperationException("auth middleware must set source_id");

        var req = await ReadBodyAsync<TelemetryRequest>(ctx, WireJsonContext.Default.TelemetryRequest);
        if (req is null) return;

        var result = await svc.IngestAsync(sourceId, req);
        if (!result.Ok)
        {
            await IngestResponse.Error(ctx, result.Status, result.ErrorCode!, result.ErrorMessage!);
            return;
        }

        await IngestResponse.Ok(ctx, new TelemetryResponse { V = ServerOptions.CurrentProtocolVersion, Accepted = true });
    }

    private static async Task HealthAsync(HttpContext ctx)
    {
        // 503 until migrations complete and the DB is writable (§3.3, PROTOCOL §8.2).
        if (!ctx.RequestServices.GetRequiredService<ServerReadiness>().IsReady)
        {
            await IngestResponse.Error(ctx, 503, "unavailable",
                "server is starting (migrations) — retry shortly", retryAfter: true, retrySeconds: 5);
            return;
        }

        var resp = new HealthResponse
        {
            V = ServerOptions.CurrentProtocolVersion,
            Ok = true,
            ServerTime = Common.WireTime.Now(),
            ServerVersion = ServerOptionsAssembly.Version,
        };
        if (ctx.Items[IngestMiddleware.ItemsSourceId] is string sid)
        {
            resp.SourceId = sid;
            resp.Scopes = AuthMiddleware.ParseScopes((string)ctx.Items[IngestMiddleware.ItemsScopes]!);
        }
        await IngestResponse.Ok(ctx, resp);
    }

    /// <summary>Read + deserialize the JSON body; writes 400 invalid_request on malformed JSON.</summary>
    private static async Task<T?> ReadBodyAsync<T>(HttpContext ctx, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
    {
        try
        {
            return await System.Text.Json.JsonSerializer.DeserializeAsync(ctx.Request.Body, typeInfo, ctx.RequestAborted);
        }
        catch (System.Text.Json.JsonException ex)
        {
            await IngestResponse.Error(ctx, 400, "invalid_request", $"malformed JSON: {ex.Message}");
            return default;
        }
    }

    private static string? ExtractRawToken(HttpContext ctx)
    {
        var auth = ctx.Request.Headers.Authorization.ToString();
        if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return auth["Bearer ".Length..].Trim();
        return null;
    }
}

/// <summary>Startup readiness flag: /health returns 503 until true (§3.3).</summary>
public sealed class ServerReadiness
{
    public volatile bool IsReady;
}
