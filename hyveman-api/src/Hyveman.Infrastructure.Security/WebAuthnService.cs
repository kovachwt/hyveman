using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Fido2NetLib;
using Fido2NetLib.Objects;
using Hyveman.Application;
using Hyveman.Domain;
using Microsoft.Extensions.Logging;

namespace Hyveman.Infrastructure.Security;

/// <summary>
/// WebAuthn ceremony orchestration (API.md §8.1). The API owns all ceremony
/// state: challenges are short-lived, single-use, bound to the intended
/// operation, and validated against the configured RP ID and expected origin
/// by the Fido2 library. First-run registration is permitted unauthenticated
/// only while the passkeys table is empty and the request comes from the
/// configured trusted network.
/// </summary>
public sealed class WebAuthnService(
    Fido2Configuration fido2Config,
    IPasskeyStore passkeys,
    ICeremonyStore ceremonies,
    ISessionStore sessions,
    IAuditStore audit,
    IClock clock,
    Func<string?, bool> isTrustedNetwork,
    ILogger<WebAuthnService> log,
    TimeSpan sessionLifetime) : IWebAuthnService
{
    private static readonly TimeSpan CeremonyLifetime = TimeSpan.FromMinutes(5);
    private static readonly byte[] AdminUserId = SHA256.HashData(Encoding.UTF8.GetBytes("hyveman-single-admin"))[..16];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly Fido2 _fido2 = new(fido2Config);

    public async Task<bool> IsSetupRequiredAsync(CancellationToken ct) => await passkeys.CountAsync(ct) == 0;

    public async Task<object> BeginRegistrationAsync(string? name, bool sessionAuthenticated, string? remoteIp, CancellationToken ct)
    {
        var count = await passkeys.CountAsync(ct);
        if (count > 0 && !sessionAuthenticated)
            throw new ValidationProblemException(new Dictionary<string, List<string>>
            {
                ["auth"] = ["Passkeys already exist; additional keys require an authenticated session."],
            });
        if (count == 0 && !isTrustedNetwork(remoteIp))
            throw new ValidationProblemException(new Dictionary<string, List<string>>
            {
                ["auth"] = ["First-run setup is only permitted from the trusted network."],
            });

        var descriptors = (await passkeys.ListAsync(ct))
            .Select(p => new PublicKeyCredentialDescriptor(
                PublicKeyCredentialType.PublicKey, Convert.FromBase64String(p.CredentialId)))
            .ToList();

        var options = _fido2.RequestNewCredential(new RequestNewCredentialParams
        {
            User = new Fido2User
            {
                Id = AdminUserId,
                Name = "admin",
                DisplayName = "Hyveman Administrator",
            },
            ExcludeCredentials = descriptors,
            AuthenticatorSelection = new AuthenticatorSelection
            {
                UserVerification = UserVerificationRequirement.Preferred,
            },
            AttestationPreference = AttestationConveyancePreference.None,
        });

        await SaveCeremonyAsync(options.Challenge, "register", options, ct);
        log.LogInformation("WebAuthn registration ceremony begun (count={count}, remote={remote})", count, remoteIp ?? "local");
        return options;
    }

    public async Task<string> CompleteRegistrationAsync(string responseJson, string origin, CancellationToken ct)
    {
        AuthenticatorAttestationRawResponse response;
        try
        {
            response = JsonSerializer.Deserialize<AuthenticatorAttestationRawResponse>(responseJson, Json)
                ?? throw new InvalidOperationException("empty attestation response");
        }
        catch (JsonException ex)
        {
            throw new ValidationProblemException(new Dictionary<string, List<string>>
            {
                ["response"] = ["Malformed attestation response."],
            });
        }

        var clientData = ParseClientData(response.Response.ClientDataJson);
        var challenge = DecodeChallenge(clientData, "Malformed registration challenge.");
        var storedOptionsJson = await ceremonies.TakeAsync(ChallengeHash(challenge), "register", clock.UtcNow, ct)
            ?? throw new ValidationProblemException(new Dictionary<string, List<string>>
            {
                ["challenge"] = ["Unknown, expired or already-used registration challenge."],
            });

        var options = JsonSerializer.Deserialize<CredentialCreateOptions>(storedOptionsJson, Json)
            ?? throw new InvalidOperationException("stored options unreadable");

        RegisteredPublicKeyCredential result;
        try
        {
            result = await _fido2.MakeNewCredentialAsync(new MakeNewCredentialParams
            {
                AttestationResponse = response,
                OriginalOptions = options,
                IsCredentialIdUniqueToUserCallback = (p, _) => IsCredentialIdUniqueAsync(p.CredentialId, ct),
            }, ct);
        }
        catch (Fido2VerificationException ex)
        {
            log.LogWarning("WebAuthn registration verification failed: {error}", ex.Message);
            throw new ValidationProblemException(new Dictionary<string, List<string>>
            {
                ["attestation"] = [$"Attestation verification failed: {ex.Message}"],
            });
        }

        var now = clock.UtcNow;
        var passkey = new PasskeyRecord(
            Id: "pk_" + RandomHex(16),
            Name: "default",
            CredentialId: Convert.ToBase64String(result.Id),
            PublicKey: Convert.ToBase64String(result.PublicKey),
            SignCount: result.SignCount,
            Created: now,
            LastUsed: null);
        await passkeys.AddAsync(passkey, ct);
        await audit.RecordAsync("admin", "passkey.registered", "passkey", passkey.Id, null, now, ct);
        log.LogInformation("WebAuthn passkey {passkeyId} registered (origin {origin})", passkey.Id, origin);
        return passkey.Id;
    }

    public async Task<object> BeginLoginAsync(CancellationToken ct)
    {
        var descriptors = (await passkeys.ListAsync(ct))
            .Select(p => new PublicKeyCredentialDescriptor(
                PublicKeyCredentialType.PublicKey, Convert.FromBase64String(p.CredentialId)))
            .ToList();
        var options = _fido2.GetAssertionOptions(new GetAssertionOptionsParams
        {
            AllowedCredentials = descriptors,
            UserVerification = UserVerificationRequirement.Discouraged,
        });
        await SaveCeremonyAsync(options.Challenge, "login", options, ct);
        return options;
    }

    public async Task<string> CompleteLoginAsync(string responseJson, string origin, CancellationToken ct)
    {
        AuthenticatorAssertionRawResponse response;
        try
        {
            response = JsonSerializer.Deserialize<AuthenticatorAssertionRawResponse>(responseJson, Json)
                ?? throw new InvalidOperationException("empty assertion response");
        }
        catch (JsonException)
        {
            throw new ValidationProblemException(new Dictionary<string, List<string>>
            {
                ["response"] = ["Malformed assertion response."],
            });
        }

        var clientData = ParseClientData(response.Response.ClientDataJson);
        var challenge = DecodeChallenge(clientData, "Malformed login challenge.");
        var storedOptionsJson = await ceremonies.TakeAsync(ChallengeHash(challenge), "login", clock.UtcNow, ct)
            ?? throw new ValidationProblemException(new Dictionary<string, List<string>>
            {
                ["challenge"] = ["Unknown, expired or already-used login challenge."],
            });
        var options = JsonSerializer.Deserialize<AssertionOptions>(storedOptionsJson, Json)
            ?? throw new InvalidOperationException("stored options unreadable");

        var credentialId = Convert.ToBase64String(response.RawId);
        var passkey = await passkeys.GetByCredentialIdAsync(credentialId, ct)
            ?? throw new ValidationProblemException(new Dictionary<string, List<string>>
            {
                ["credential"] = ["Unknown credential."],
            });

        VerifyAssertionResult result;
        try
        {
            result = await _fido2.MakeAssertionAsync(new MakeAssertionParams
            {
                AssertionResponse = response,
                OriginalOptions = options,
                StoredPublicKey = Convert.FromBase64String(passkey.PublicKey),
                StoredSignatureCounter = passkey.SignCount,
                IsUserHandleOwnerOfCredentialIdCallback = (_, _) => Task.FromResult(true), // single admin
            }, ct);
        }
        catch (Fido2VerificationException ex)
        {
            log.LogWarning("WebAuthn login verification failed: {error}", ex.Message);
            throw new ValidationProblemException(new Dictionary<string, List<string>>
            {
                ["assertion"] = [$"Assertion verification failed: {ex.Message}"],
            });
        }

        var now = clock.UtcNow;
        await passkeys.UpdateSignCountAsync(passkey.Id, result.SignCount, now, ct);
        var sessionId = await sessions.CreateAsync(now, sessionLifetime, ct);
        await audit.RecordAsync("admin", "auth.login", "passkey", passkey.Id, null, now, ct);
        log.LogInformation("WebAuthn login succeeded (passkey {passkeyId}, origin {origin})", passkey.Id, origin);
        return sessionId;
    }

    public async Task<IReadOnlyList<PasskeyRecord>> ListPasskeysAsync(CancellationToken ct) => await passkeys.ListAsync(ct);

    public async Task RemovePasskeyAsync(string id, CancellationToken ct)
    {
        var passkey = await passkeys.GetAsync(id, ct)
            ?? throw new NotFoundException($"passkey '{id}' not found");
        if (await passkeys.CountAsync(ct) <= 1)
            throw new ValidationProblemException(new Dictionary<string, List<string>>
            {
                ["id"] = ["Cannot remove the final usable passkey; register another first (console reset is the only other path)."],
            });
        await passkeys.RemoveAsync(id, ct);
        await audit.RecordAsync("admin", "passkey.removed", "passkey", id, null, clock.UtcNow, ct);
    }

    private async Task SaveCeremonyAsync(byte[] challenge, string operation, object options, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(options, Json);
        await ceremonies.SaveAsync(ChallengeHash(challenge), operation, json, clock.UtcNow, CeremonyLifetime, ct);
    }

    private static string ChallengeHash(byte[] challenge) =>
        Convert.ToHexString(SHA256.HashData(challenge)).ToLowerInvariant();

    private static JsonDocument ParseClientData(byte[] clientDataJson)
    {
        try
        {
            return JsonDocument.Parse(clientDataJson);
        }
        catch (JsonException)
        {
            throw new ValidationProblemException(new Dictionary<string, List<string>>
            {
                ["response"] = ["clientDataJSON is not valid JSON."],
            });
        }
    }

    private async Task<bool> IsCredentialIdUniqueAsync(byte[] credentialId, CancellationToken ct)
    {
        var existing = await passkeys.GetByCredentialIdAsync(Convert.ToBase64String(credentialId), ct);
        return existing is null;
    }

    private static string RandomHex(int bytes) =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(bytes)).ToLowerInvariant();

    /// <summary>
    /// Decode the challenge from clientDataJSON. The client echoes the
    /// challenge from the server options verbatim into clientDataJSON, where
    /// it is base64url (RFC 4648 §5: '-'/'_' instead of '+'/'/', padding
    /// stripped). Also accepts padded standard base64 for robustness.
    /// </summary>
    private static byte[] DecodeChallenge(JsonDocument clientData, string malformedMessage)
    {
        var raw = clientData.RootElement.TryGetProperty("challenge", out var el) ? el.GetString() : null;
        if (raw is null)
            throw new ValidationProblemException(new Dictionary<string, List<string>> { ["challenge"] = [malformedMessage] });
        var b = raw.Replace('-', '+').Replace('_', '/');
        try
        {
            return (b.Length % 4) switch
            {
                0 => Convert.FromBase64String(b),
                2 => Convert.FromBase64String(b + "=="),
                3 => Convert.FromBase64String(b + "="),
                _ => throw new FormatException(),
            };
        }
        catch (FormatException)
        {
            throw new ValidationProblemException(new Dictionary<string, List<string>> { ["challenge"] = [malformedMessage] });
        }
    }
}
