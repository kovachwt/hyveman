using Hyveman.Domain;
using Microsoft.Extensions.Logging;

namespace Hyveman.Application;

public sealed class RegistrationException(string code, int status, string message) : Exception(message)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
}

/// <summary>POST /register (PROTOCOL §5). Validates the request shape, runs the
/// atomic registration unit (one BEGIN IMMEDIATE transaction: token check,
/// (kind, hostname) source resolve/create, agt_ token mint, reg_ token
/// consumption — API.md §6.2) and maps the outcome to protocol errors. Audit
/// rows are written after the commit; a failure there surfaces as a 500 whose
/// retry hits the documented 410 token_consumed recovery path (§5.4).</summary>
public sealed class RegistrationService(
    IRegistrationUnit registrationUnit,
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

        var result = await registrationUnit.ExecuteAsync(rawRegToken, kind, hostname, now, ct);
        switch (result.Status)
        {
            case RegistrationStatus.UnknownToken:
                throw new RegistrationException(Protocol.ErrorCodes.TokenInvalid, 401, "unknown registration token");
            case RegistrationStatus.Revoked:
                throw new RegistrationException(Protocol.ErrorCodes.TokenRevoked, 401, "registration token revoked");
            case RegistrationStatus.Expired:
                throw new RegistrationException(Protocol.ErrorCodes.TokenRevoked, 401, "registration token expired");
            case RegistrationStatus.Consumed:
                throw new RegistrationException(Protocol.ErrorCodes.TokenConsumed, 410, "registration token already consumed; reissue via the admin UI");
            case RegistrationStatus.KindMismatch:
                throw new RegistrationException(Protocol.ErrorCodes.InvalidRequest, 400,
                    $"registration token is bound to kind '{result.BoundKind}', not '{kind}'");
            case RegistrationStatus.Ok:
                break;
            default:
                throw new InvalidOperationException($"unexpected registration status '{result.Status}'");
        }

        if (result.SourceCreated)
            await audit.RecordAsync(null, "source.created", "source", result.SourceId,
                $"{{\"kind\":\"{kind}\",\"name\":\"{hostname}\"}}", now, ct);
        await audit.RecordAsync(null, "token.minted", "source", result.SourceId,
            $"{{\"kind\":\"agent\",\"hostname\":\"{hostname}\",\"agent_version\":{Json(agentVersion)},\"os_build\":{Json(osBuild)}}}",
            now, ct);

        log.LogInformation("Registered source {sourceId} ({kind}/{hostname}); agent token minted", result.SourceId, kind, hostname);
        return new RegistrationOutcome(result.SourceId!, result.SourceKind!, result.SourceName!,
            result.RawToken!, result.Scopes!, result.IssuedAt!.Value);
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
