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
/// WebAuthn ceremony orchestration (API.md §8.1, docs/MULTI-USER.md). The API
/// owns all ceremony state: challenges are short-lived, single-use, bound to
/// the intended operation, and validated against the configured RP ID and
/// expected origin by the Fido2 library.
///
/// Registration has three modes, dispatched by context:
///  - setup: no users exist; requires the trusted network (first-run wizard);
///  - invite: a valid single-use invite token, no session (self-service
///    account creation); the invite is re-validated at verify (S8) and
///    consumed atomically with user+passkey creation;
///  - authenticated: the session user registers another of their own keys.
///
/// The WebAuthn user handle is per-user (users.webauthn_user_handle), not the
/// historical shared single-admin handle.
/// </summary>
public sealed class WebAuthnService(
    Fido2Configuration fido2Config,
    IPasskeyStore passkeys,
    ICeremonyStore ceremonies,
    ISessionStore sessions,
    IUserStore users,
    IInvitationStore invitations,
    IAuditStore audit,
    IClock clock,
    Func<string?, bool> isTrustedNetwork,
    ILogger<WebAuthnService> log,
    TimeSpan sessionLifetime) : IWebAuthnService
{
    private static readonly TimeSpan CeremonyLifetime = TimeSpan.FromMinutes(5);

    /// <summary>JSON options for ceremony payloads. Fido2NetLib option types
    /// carry their own per-enum converters (spec values like "public-key",
    /// "discouraged", COSE algorithm numbers). The API's global
    /// JsonStringEnumConverter must NOT be applied to them, so the controller
    /// serializes options responses with these options (see AuthController).</summary>
    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly Fido2 _fido2 = new(fido2Config);

    public async Task<bool> IsSetupRequiredAsync(CancellationToken ct) => await users.CountAsync(ct) == 0;

    public async Task<object> BeginRegistrationAsync(string? name, string? inviteToken, string? userId,
        string? remoteIp, CancellationToken ct)
    {
        string mode;
        string? pendingHandle = null;
        string? inviteId = null;
        byte[] userHandle;
        string userName;
        string userDisplayName;

        if (inviteToken is not null)
        {
            if (userId is not null)
                throw new ValidationProblemException(new Dictionary<string, List<string>>
                {
                    ["inviteToken"] = ["You are already signed in; use the passkey page to add another passkey."],
                });
            var invite = await invitations.LookupAsync(inviteToken, ct);
            if (!IsValidInvite(invite))
                throw new ValidationProblemException(new Dictionary<string, List<string>>
                {
                    ["inviteToken"] = ["Invitation is invalid, expired or already used."],
                });
            if (invite!.ForUserId is not null)
                throw new ValidationProblemException(new Dictionary<string, List<string>>
                {
                    ["inviteToken"] = ["This invitation type is not supported yet."],
                });
            mode = "invite";
            inviteId = invite.Id;
            pendingHandle = RandomHandleB64();
            userHandle = DecodeHandle(pendingHandle);
            userName = "invitee";
            userDisplayName = "New Hyveman user";
        }
        else if (await users.CountAsync(ct) == 0)
        {
            if (!isTrustedNetwork(remoteIp))
                throw new ValidationProblemException(new Dictionary<string, List<string>>
                {
                    ["auth"] = ["First-run setup is only permitted from the trusted network."],
                });
            mode = "setup";
            pendingHandle = RandomHandleB64();
            userHandle = DecodeHandle(pendingHandle);
            userName = "admin";
            userDisplayName = "Hyveman Administrator";
        }
        else if (userId is not null)
        {
            var user = await users.GetAsync(userId, ct);
            if (user is null || user.Disabled)
                throw new ValidationProblemException(new Dictionary<string, List<string>>
                {
                    ["auth"] = ["Unknown or disabled account."],
                });
            mode = "user";
            userHandle = DecodeHandle(user.WebAuthnUserHandle);
            userName = user.Name;
            userDisplayName = user.DisplayName ?? user.Name;
        }
        else
        {
            throw new ValidationProblemException(new Dictionary<string, List<string>>
            {
                ["auth"] = ["Passkeys already exist; additional keys require an authenticated session or a valid invitation."],
            });
        }

        // Exclude the user's own existing keys (mode "user"); for setup/invite
        // exclude every enrolled credential so one authenticator cannot be
        // double-enrolled into the same RP.
        var descriptors = (mode == "user"
                ? await passkeys.ListByUserAsync(userId!, ct)
                : await passkeys.ListAsync(ct))
            .Select(p => new PublicKeyCredentialDescriptor(
                PublicKeyCredentialType.PublicKey, Convert.FromBase64String(p.CredentialId)))
            .ToList();

        var options = _fido2.RequestNewCredential(new RequestNewCredentialParams
        {
            User = new Fido2User
            {
                Id = userHandle,
                Name = userName,
                DisplayName = userDisplayName,
            },
            ExcludeCredentials = descriptors,
            AuthenticatorSelection = new AuthenticatorSelection
            {
                UserVerification = UserVerificationRequirement.Preferred,
            },
            AttestationPreference = AttestationConveyancePreference.None,
        });

        var context = new CeremonyContext(mode, PendingUserHandle: pendingHandle, InviteId: inviteId,
            IntendedName: string.IsNullOrWhiteSpace(name) ? null : name.Trim());
        await SaveCeremonyAsync(options.Challenge, "register", options, context, ct);
        log.LogInformation("WebAuthn registration ceremony begun (mode={mode}, remote={remote})", mode, remoteIp ?? "local");
        return options;
    }

    public async Task<RegistrationResult> CompleteRegistrationAsync(string responseJson, string origin,
        string? userId, string? remoteIp, CancellationToken ct)
    {
        RegisterVerifyEnvelope envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<RegisterVerifyEnvelope>(responseJson, JsonOptions)
                ?? throw new InvalidOperationException("empty registration envelope");
        }
        catch (JsonException ex)
        {
            throw new ValidationProblemException(new Dictionary<string, List<string>>
            {
                ["response"] = ["Malformed attestation response."],
            });
        }

        var clientData = ParseClientData(envelope.Response.Response.ClientDataJson);
        var challenge = DecodeChallenge(clientData, "Malformed registration challenge.");
        var stored = await ceremonies.TakeAsync(ChallengeHash(challenge), "register", clock.UtcNow, ct)
            ?? throw new ValidationProblemException(new Dictionary<string, List<string>>
            {
                ["challenge"] = ["Unknown, expired or already-used registration challenge."],
            });
        var context = ParseContext(stored.OriginContext);
        var options = JsonSerializer.Deserialize<CredentialCreateOptions>(stored.OptionsJson, JsonOptions)
            ?? throw new InvalidOperationException("stored options unreadable");

        // ── Re-validate the ceremony gate at verify (SECURITY-AUDIT S8) ────
        switch (context.Mode)
        {
            case "setup":
                if (await users.CountAsync(ct) != 0 || !isTrustedNetwork(remoteIp))
                    throw new ValidationProblemException(new Dictionary<string, List<string>>
                    {
                        ["auth"] = ["First-run setup is no longer permitted (users exist or untrusted network)."],
                    });
                userId = null;
                break;
            case "invite":
            {
                var invite = await invitations.LookupAsync(envelope.InviteToken ?? "", ct);
                if (!IsValidInvite(invite) || invite!.Id != context.InviteId)
                    throw new ValidationProblemException(new Dictionary<string, List<string>>
                    {
                        ["inviteToken"] = ["Invitation is invalid, expired or already used."],
                    });
                userId = null;
                break;
            }
            case "user":
            {
                // The controller requires an authenticated session for this
                // mode; the session user id is authoritative (never the body).
                var user = await users.GetAsync(userId ?? "", ct);
                if (user is null || user.Disabled)
                    throw new ValidationProblemException(new Dictionary<string, List<string>>
                    {
                        ["auth"] = ["Unknown or disabled account."],
                    });
                userId = user.Id;
                break;
            }
            default:
                throw new ValidationProblemException(new Dictionary<string, List<string>>
                {
                    ["challenge"] = ["Unknown ceremony context."],
                });
        }

        RegisteredPublicKeyCredential result;
        try
        {
            result = await _fido2.MakeNewCredentialAsync(new MakeNewCredentialParams
            {
                AttestationResponse = envelope.Response,
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
        var newUser = false;

        // New account (setup/invite): create the user row. A username
        // collision fails here — the invite is NOT consumed, so the invitee
        // can retry with a different name.
        if (userId is null)
        {
            var name = NormalizeUsername(envelope.Username);
            if (await users.GetByNameAsync(name, ct) is not null)
                throw new ValidationProblemException(new Dictionary<string, List<string>>
                {
                    ["username"] = [$"The name '{name}' is already taken."],
                });
            var user = new UserRecord(
                Id: "usr_" + RandomHex(18),
                Name: name,
                DisplayName: string.IsNullOrWhiteSpace(envelope.DisplayName) ? null : envelope.DisplayName.Trim(),
                WebAuthnUserHandle: context.PendingUserHandle!,
                Disabled: false,
                Created: now,
                CreatedBy: context.Mode == "invite" ? await InvitingUserIdAsync(context.InviteId!, ct) : "setup");
            await users.CreateAsync(user, ct);
            userId = user.Id;
            newUser = true;
            try
            {
                await audit.RecordAsync(user.CreatedBy ?? "setup", "user.created", "user", user.Id,
                    $"{{\"name\":\"{user.Name}\"}}", now, ct);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Failed to audit user.created for {userId}", user.Id);
            }
        }

        var passkey = new PasskeyRecord(
            Id: "pk_" + RandomHex(16),
            UserId: userId,
            Name: string.IsNullOrWhiteSpace(envelope.Name)
                ? (context.IntendedName ?? "default")
                : envelope.Name.Trim(),
            CredentialId: Convert.ToBase64String(result.Id),
            PublicKey: Convert.ToBase64String(result.PublicKey),
            SignCount: result.SignCount,
            Created: now,
            LastUsed: null);

        // Best-effort compensation: if a step below fails after the user row
        // was created (new account only), remove it so the invite stays usable
        // and no orphan account lingers.
        async Task RollbackUserAsync()
        {
            if (newUser) await users.DeleteAsync(userId!, ct);
        }
        try
        {
            await passkeys.AddAsync(passkey, ct);
        }
        catch (Exception)
        {
            await RollbackUserAsync();
            throw;
        }
        try
        {
            if (context.Mode == "invite")
                await invitations.MarkConsumedAsync(context.InviteId!, now, ct);
        }
        catch (Exception)
        {
            try { await passkeys.RemoveAsync(passkey.Id, ct); } catch { /* best effort */ }
            await RollbackUserAsync();
            throw;
        }

        try
        {
            await audit.RecordAsync((await users.GetAsync(userId, ct))?.Name ?? userId, "passkey.registered",
                "passkey", passkey.Id, null, now, ct);
            if (context.Mode == "invite")
            {
                var actor = (await users.GetAsync(userId, ct))?.Name ?? userId;
                await audit.RecordAsync(actor, "invitation.consumed", "invitation", context.InviteId, null, now, ct);
            }
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Failed to audit passkey registration for {userId}", userId);
        }

        // New accounts get a session immediately (setup/invite): the browser
        // has just registered the first passkey, so the user lands in the app.
        string? sessionId = null;
        if (newUser)
        {
            sessionId = await sessions.CreateAsync(now, sessionLifetime, userId, ct);
        }

        var userRecord = await users.GetAsync(userId, ct);
        log.LogInformation("WebAuthn passkey {passkeyId} registered (mode={mode}, origin {origin})",
            passkey.Id, context.Mode, origin);
        return new RegistrationResult(passkey.Id, sessionId, userRecord?.Id);
    }

    public async Task<object> BeginLoginAsync(CancellationToken ct)
    {
        // Every enabled user's passkeys are allowed; the presented credential
        // resolves the user server-side (docs/MULTI-USER.md).
        var passkeyRows = await passkeys.ListAsync(ct);
        var userRows = (await users.ListAsync(ct)).Where(u => !u.Disabled).Select(u => u.Id).ToHashSet();
        var descriptors = passkeyRows
            .Where(p => userRows.Contains(p.UserId))
            .Select(p => new PublicKeyCredentialDescriptor(
                PublicKeyCredentialType.PublicKey, Convert.FromBase64String(p.CredentialId)))
            .ToList();
        var options = _fido2.GetAssertionOptions(new GetAssertionOptionsParams
        {
            AllowedCredentials = descriptors,
            UserVerification = UserVerificationRequirement.Discouraged,
        });
        await SaveCeremonyAsync(options.Challenge, "login", options, null, ct);
        return options;
    }

    public async Task<string> CompleteLoginAsync(string responseJson, string origin, CancellationToken ct)
    {
        AuthenticatorAssertionRawResponse response;
        try
        {
            response = JsonSerializer.Deserialize<AuthenticatorAssertionRawResponse>(responseJson, JsonOptions)
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
        var stored = await ceremonies.TakeAsync(ChallengeHash(challenge), "login", clock.UtcNow, ct)
            ?? throw new ValidationProblemException(new Dictionary<string, List<string>>
            {
                ["challenge"] = ["Unknown, expired or already-used login challenge."],
            });
        var options = JsonSerializer.Deserialize<AssertionOptions>(stored.OptionsJson, JsonOptions)
            ?? throw new InvalidOperationException("stored options unreadable");

        var credentialId = Convert.ToBase64String(response.RawId);
        var passkey = await passkeys.GetByCredentialIdAsync(credentialId, ct)
            ?? throw new ValidationProblemException(new Dictionary<string, List<string>>
            {
                ["credential"] = ["Unknown credential."],
            });
        var user = await users.GetAsync(passkey.UserId, ct);
        if (user is null || user.Disabled)
            throw new ValidationProblemException(new Dictionary<string, List<string>>
            {
                ["credential"] = ["Unknown credential."],
            });

        VerifyAssertionResult result;
        try
        {
            // The credential id resolves the user authoritatively (credentials
            // are enrolled non-discoverable). Defense in depth: when the
            // authenticator returns a user handle (discoverable credentials),
            // it must match the stored per-user handle.
            if (response.Response.UserHandle is { Length: > 0 } returnedHandle &&
                !UserHandleMatches(returnedHandle, user.WebAuthnUserHandle))
            {
                throw new Fido2VerificationException("user handle does not match the credential's user");
            }
            result = await _fido2.MakeAssertionAsync(new MakeAssertionParams
            {
                AssertionResponse = response,
                OriginalOptions = options,
                StoredPublicKey = Convert.FromBase64String(passkey.PublicKey),
                StoredSignatureCounter = passkey.SignCount,
                IsUserHandleOwnerOfCredentialIdCallback = (_, _) => Task.FromResult(true),
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
        var sessionId = await sessions.CreateAsync(now, sessionLifetime, user.Id, ct);
        await audit.RecordAsync(user.Name, "auth.login", "passkey", passkey.Id, null, now, ct);
        log.LogInformation("WebAuthn login succeeded (user {userId}, passkey {passkeyId}, origin {origin})",
            user.Id, passkey.Id, origin);
        return sessionId;
    }

    public async Task<IReadOnlyList<PasskeyRecord>> ListPasskeysForUserAsync(string userId, CancellationToken ct)
        => await passkeys.ListByUserAsync(userId, ct);

    // ── helpers ────────────────────────────────────────────────────────────

    private static bool IsValidInvite(InvitationRecord? invite)
    {
        if (invite is null || invite.Revoked || invite.ConsumedAt is not null) return false;
        return invite.ExpiresAt is null || invite.ExpiresAt > DateTimeOffset.UtcNow;
    }

    private async Task<string?> InvitingUserIdAsync(string inviteId, CancellationToken ct)
    {
        var all = await invitations.ListAsync(ct);
        return all.FirstOrDefault(i => i.Id == inviteId)?.CreatedBy;
    }

    /// <summary>Non-discoverable credentials may omit the user handle in the
    /// assertion; the credential id already resolves the user authoritatively.
    /// When a handle is present, it must match the stored handle.</summary>
    private static bool UserHandleMatches(byte[]? returned, string storedB64)
    {
        if (returned is null || returned.Length == 0) return true;
        try
        {
            var expected = DecodeHandle(storedB64);
            return CryptographicOperations.FixedTimeEquals(returned, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private async Task SaveCeremonyAsync(byte[] challenge, string operation, object options,
        CeremonyContext? context, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(options, JsonOptions);
        await ceremonies.SaveAsync(ChallengeHash(challenge), operation, json,
            context is null ? null : JsonSerializer.Serialize(context, JsonOptions), clock.UtcNow, CeremonyLifetime, ct);
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

    private static CeremonyContext ParseContext(string? json)
    {
        if (string.IsNullOrEmpty(json))
            throw new ValidationProblemException(new Dictionary<string, List<string>>
            {
                ["challenge"] = ["Ceremony context is missing."],
            });
        try
        {
            return JsonSerializer.Deserialize<CeremonyContext>(json, JsonOptions) ?? throw new InvalidOperationException();
        }
        catch (JsonException)
        {
            throw new ValidationProblemException(new Dictionary<string, List<string>>
            {
                ["challenge"] = ["Ceremony context is unreadable."],
            });
        }
    }

    private async Task<bool> IsCredentialIdUniqueAsync(byte[] credentialId, CancellationToken ct)
    {
        var existing = await passkeys.GetByCredentialIdAsync(Convert.ToBase64String(credentialId), ct);
        return existing is null;
    }

    private static string NormalizeUsername(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ValidationProblemException(new Dictionary<string, List<string>>
            {
                ["username"] = ["A username is required."],
            });
        var trimmed = name.Trim();
        if (trimmed.Length < 2 || trimmed.Length > 64)
            throw new ValidationProblemException(new Dictionary<string, List<string>>
            {
                ["username"] = ["Username must be between 2 and 64 characters."],
            });
        if (!trimmed.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.'))
            throw new ValidationProblemException(new Dictionary<string, List<string>>
            {
                ["username"] = ["Username may contain letters, digits, '-', '_' and '.' only."],
            });
        return trimmed;
    }

    private static string RandomHex(int bytes) =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(bytes)).ToLowerInvariant();

    private static string RandomHandleB64() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));

    private static byte[] DecodeHandle(string b64) => Convert.FromBase64String(b64);

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

    /// <summary>Verify-request envelope: the attestation response plus the
    /// invite/name fields the frontend collects (docs/MULTI-USER.md).</summary>
    private sealed class RegisterVerifyEnvelope
    {
        public AuthenticatorAttestationRawResponse Response { get; set; } = null!;
        public string? InviteToken { get; set; }
        public string? Username { get; set; }
        public string? DisplayName { get; set; }
        public string? Name { get; set; }
    }

    /// <summary>Per-ceremony context persisted alongside the challenge: which
    /// registration mode this ceremony belongs to and the pending identity it
    /// will materialize at verify.</summary>
    private sealed record CeremonyContext(
        string Mode,
        string? PendingUserHandle = null,
        string? InviteId = null,
        string? IntendedName = null);
}
