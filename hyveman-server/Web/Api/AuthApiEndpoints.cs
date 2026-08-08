using Fido2NetLib;
using Fido2NetLib.Objects;
using Hyveman.Server.Auth;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Hyveman.Server.Web.Api;

/// <summary>
/// WebAuthn ceremony endpoints backing the /auth/setup and /auth/login pages (§11.3, §12.2).
/// The browser JS converts ArrayBuffers ↔ base64url; these endpoints speak plain JSON.
/// </summary>
public static class AuthApiEndpoints
{
    public static void MapAuthApi(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/auth/register-options", async (RegisterOptionsRequest req, PasskeyService svc) =>
        {
            if (await svc.HasPasskeysAsync())
                return Results.Conflict(new { error = "passkeys already registered" });
            var opts = await svc.CreateRegistrationOptionsAsync(req.Name ?? "Passkey");
            return Results.Ok(new
            {
                challenge = Base64Url.Encode(opts.Challenge),
                rp = new { name = "Hyveman", id = svc.RpId },
                user = new { id = Base64Url.Encode(opts.User.Id), name = "admin", displayName = opts.User.DisplayName },
                pubKeyCredParams = opts.PubKeyCredParams.Select(p => new { type = "public-key", alg = p.Alg }),
                timeout = opts.Timeout,
                attestation = "none",
                authenticatorSelection = new
                {
                    residentKey = opts.AuthenticatorSelection?.ResidentKey.ToString().ToLowerInvariant(),
                    userVerification = opts.AuthenticatorSelection?.UserVerification.ToString().ToLowerInvariant(),
                },
                excludeCredentials = new object[0],
                extensions = new { extensions = true },
            });
        });

        app.MapPost("/api/auth/register-verify", async (RegisterVerifyRequest req, PasskeyService svc) =>
        {
            if (await svc.HasPasskeysAsync())
                return Results.Conflict(new { error = "passkeys already registered" });
            if (req.Response is null || string.IsNullOrEmpty(req.Response.Id))
                return Results.BadRequest(new { error = "missing attestation response" });
            var response = new AuthenticatorAttestationRawResponse
            {
                Id = req.Response.Id,   // base64url string from the client
                RawId = Base64Url.Decode(req.Response.Id),
                Type = PublicKeyCredentialType.PublicKey,
                Response = new AuthenticatorAttestationRawResponse.AttestationResponse
                {
                    ClientDataJson = Base64Url.Decode(req.Response.ClientDataJson),
                    AttestationObject = Base64Url.Decode(req.Response.AttestationObject),
                    Transports = req.Response.Transports is null
                        ? null
                        : req.Response.Transports.Select(t => Enum.TryParse<AuthenticatorTransport>(t, true, out var at) ? at : AuthenticatorTransport.Internal).ToArray(),
                },
            };
            var (ok, error, name) = await svc.VerifyRegistrationAsync(response, req.Name ?? "Passkey");
            return ok ? Results.Ok(new { ok = true, name }) : Results.BadRequest(new { error });
        });

        app.MapPost("/api/auth/login-options", async (PasskeyService svc) =>
        {
            var opts = await svc.CreateAssertionOptionsAsync();
            return Results.Ok(new
            {
                challenge = Base64Url.Encode(opts.Challenge),
                rpId = svc.RpId,
                timeout = opts.Timeout,
                userVerification = opts.UserVerification?.ToString().ToLowerInvariant(),
                allowCredentials = opts.AllowCredentials.Select(c => new { type = "public-key", id = Base64Url.Encode(c.Id) }),
                extensions = new { extensions = true },
            });
        });

        app.MapPost("/api/auth/login-verify", async (LoginVerifyRequest req, HttpContext ctx, PasskeyService svc, SessionAuth sessions, Observability.OwnMetrics metrics) =>
        {
            if (req.Response is null || string.IsNullOrEmpty(req.Response.Id))
                return Results.BadRequest(new { error = "missing assertion response" });
            var response = new AuthenticatorAssertionRawResponse
            {
                Id = req.Response.Id,   // base64url string from the client
                RawId = Base64Url.Decode(req.Response.Id),
                Type = PublicKeyCredentialType.PublicKey,
                Response = new AuthenticatorAssertionRawResponse.AssertionResponse
                {
                    ClientDataJson = Base64Url.Decode(req.Response.ClientDataJson),
                    AuthenticatorData = Base64Url.Decode(req.Response.AuthenticatorData),
                    Signature = Base64Url.Decode(req.Response.Signature),
                    UserHandle = string.IsNullOrEmpty(req.Response.UserHandle) ? null : Base64Url.Decode(req.Response.UserHandle),
                },
            };
            var (ok, error, passkey) = await svc.VerifyAssertionAsync(response);
            if (!ok || passkey is null)
            {
                metrics.AuthFailures++;
                return Results.Unauthorized();
            }
            sessions.Issue(ctx, passkey);
            return Results.Ok(new { ok = true });
        });

        app.MapPost("/api/auth/logout", (HttpContext ctx, SessionAuth sessions) =>
        {
            sessions.Remove(ctx);
            return Results.Ok(new { ok = true });
        });

        app.MapGet("/api/auth/status", async (HttpContext ctx, SessionAuth sessions, PasskeyService svc) =>
        {
            var (ok, passkey) = await sessions.ValidateAsync(ctx);
            var hasPasskeys = await svc.HasPasskeysAsync();
            return Results.Ok(new { authenticated = ok, passkey = passkey?.Name, hasPasskeys, rpId = svc.RpId });
        });
    }

    public sealed record RegisterOptionsRequest(string? Name);
    public sealed class RegisterVerifyRequest
    {
        public string? Name { get; set; }
        public ClientResponseJson? Response { get; set; }
    }
    public sealed class LoginVerifyRequest
    {
        public ClientResponseJson? Response { get; set; }
    }
    public sealed class ClientResponseJson
    {
        public string Id { get; set; } = "";
        public string ClientDataJson { get; set; } = "";
        public string AttestationObject { get; set; } = "";
        public string AuthenticatorData { get; set; } = "";
        public string Signature { get; set; } = "";
        public string? UserHandle { get; set; }
        public List<string>? Transports { get; set; }
    }
}
