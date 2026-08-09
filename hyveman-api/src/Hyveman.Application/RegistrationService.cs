using Hyveman.Domain;
using Microsoft.Extensions.Logging;

namespace Hyveman.Application;

public sealed class RegistrationException(string code, int status, string message) : Exception(message)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
}

/// <summary>POST /register (PROTOCOL §5). One transaction: validate the reg_
/// token, resolve or create the (kind, hostname) source, mint and hash the
/// agt_ token, mark the reg_ token consumed, commit before responding.</summary>
public sealed class RegistrationService(
    IRegistrationTokenStore registrationTokens,
    ISourceStore sources,
    ITokenStore tokens,
    IAuditStore audit,
    IClock clock,
    ILogger<RegistrationService> log)
{
    /// <summary>Executes registration; returns the raw agent token (returned to
    /// the caller exactly once, PROTOCOL §5.3).</summary>
    public async Task<RegistrationOutcome> RegisterAsync(string rawRegToken, string kind, string hostname,
        string? agentVersion, string? osBuild, CancellationToken ct)
    {
        var now = clock.UtcNow;

        if (!SourceKinds.IsKnown(kind))
            throw new RegistrationException(Protocol.ErrorCodes.InvalidRequest, 400, $"unknown source kind '{kind}'");
        if (string.IsNullOrWhiteSpace(hostname) || hostname.Length > 255)
            throw new RegistrationException(Protocol.ErrorCodes.InvalidRequest, 400, "hostname must be 1..255 characters");

        var lookup = await registrationTokens.LookupAsync(rawRegToken, ct)
            ?? throw new RegistrationException(Protocol.ErrorCodes.TokenInvalid, 401, "unknown registration token");
        if (lookup.Revoked)
            throw new RegistrationException(Protocol.ErrorCodes.TokenRevoked, 401, "registration token revoked");
        if (lookup.ConsumedAt is not null)
            throw new RegistrationException(Protocol.ErrorCodes.TokenConsumed, 410, "registration token already consumed; reissue via the admin UI");
        if (lookup.ExpiresAt is { } expiry && expiry <= now)
            throw new RegistrationException(Protocol.ErrorCodes.TokenRevoked, 401, "registration token expired");
        if (lookup.Kind != kind)
            throw new RegistrationException(Protocol.ErrorCodes.InvalidRequest, 400,
                $"registration token is bound to kind '{lookup.Kind}', not '{kind}'");

        // (kind, hostname) is authoritative in v1 (PROTOCOL §5.2): reuse or create.
        var source = await sources.GetByKindNameAsync(kind, hostname, ct);
        if (source is null)
        {
            source = await sources.CreateAsync(kind, hostname, now, ct);
            await audit.RecordAsync(null, "source.created", "source", source.Id,
                $"{{\"kind\":\"{kind}\",\"name\":\"{hostname}\"}}", now, ct);
        }

        var rawToken = await tokens.CreateAgentTokenAsync(source.Id, [TokenKinds.ScopeIngest], now, ct);
        await registrationTokens.MarkConsumedAsync(lookup.Id, now, ct);
        await audit.RecordAsync(null, "token.minted", "source", source.Id,
            $"{{\"kind\":\"agent\",\"hostname\":\"{hostname}\",\"agent_version\":{Json(agentVersion)},\"os_build\":{Json(osBuild)}}}",
            now, ct);

        log.LogInformation("Registered source {sourceId} ({kind}/{hostname}); agent token minted", source.Id, kind, hostname);
        return new RegistrationOutcome(source.Id, source.Kind, source.Name, rawToken, [TokenKinds.ScopeIngest], now);
    }

    private static string Json(string? s) => s is null ? "null" : $"\"{s.Replace("\"", "\\\"")}\"";
}

public sealed record RegistrationOutcome(
    string SourceId,
    string Kind,
    string Name,
    string RawToken,
    string[] Scopes,
    DateTimeOffset IssuedAt);
