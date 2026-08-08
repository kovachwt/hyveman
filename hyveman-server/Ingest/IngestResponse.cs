using System.Text.Json;
using Hyveman.Server.Config;

namespace Hyveman.Server.Ingest;

/// <summary>
/// Guarantees every response (2xx and error, including 408/429/5xx/503) carries
/// "v", the error envelope when applicable, and the mandatory "commands": [] slot
/// (PROTOCOL §16). Endpoints never build the envelope by hand.
/// </summary>
public static class IngestResponse
{
    public static readonly JsonSerializerOptions JsonOpts = new(WireJsonContext.Default.Options)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static async Task Ok(HttpContext ctx, object body)
    {
        EchoHeaders(ctx);
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        await ctx.Response.WriteAsJsonAsync(body, JsonOpts);
    }

    public static Task Error(HttpContext ctx, int status, string code, string message, bool retryAfter = false, int retrySeconds = 0)
        => WriteError(ctx, status, code, message, null, retryAfter, retrySeconds);

    public static Task UnsupportedVersion(HttpContext ctx, string message)
        => WriteError(ctx, 400, "unsupported_version", message, new List<int> { ServerOptions.CurrentProtocolVersion }, false, 0);

    private static async Task WriteError(HttpContext ctx, int status, string code, string message,
        List<int>? supported, bool retryAfter, int retrySeconds)
    {
        EchoHeaders(ctx);
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        if (retryAfter)
            ctx.Response.Headers["Retry-After"] = retrySeconds > 0 ? retrySeconds.ToString() : "1";
        var body = new ErrorResponse
        {
            V = ServerOptions.CurrentProtocolVersion,
            Error = new ErrorBody { Code = code, Message = message, Supported = supported },
        };
        await ctx.Response.WriteAsJsonAsync(body, JsonOpts);
    }

    private static void EchoHeaders(HttpContext ctx)
    {
        ctx.Response.Headers["X-Hyveman-Protocol"] = ServerOptions.CurrentProtocolVersion.ToString();
    }
}
