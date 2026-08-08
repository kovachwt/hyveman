using System.Security.Cryptography;
using System.Text;
using Fido2NetLib;
using Fido2NetLib.Objects;
using Hyveman.Server.Common;
using Hyveman.Server.Config;
using Hyveman.Server.Storage;
using Hyveman.Server.Storage.Repos;
using PasskeyRepo = Hyveman.Server.Storage.Repos.PasskeyRepository;

namespace Hyveman.Server.Auth;

/// <summary>
/// WebAuthn/FIDO2 server-side ceremonies (§12.2, DESIGN §8): passkey-only, RP ID from
/// config/rp_id.txt (explicit, never runtime hostname). Multiple passkeys per install.
/// </summary>
public sealed class PasskeyService
{
    private readonly Fido2 _fido2;
    private readonly Db _db;
    private readonly string _rpId;

    // In-memory pending ceremonies (challenge → full options JSON). Single admin; memory is fine.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, PendingCeremony> Pending = new();

    private sealed record PendingCeremony(string OptionsJson, byte[] Challenge, DateTimeOffset Expires);

    public PasskeyService(ServerOptions opts, string dataDir, Db db, ICredentialVault vault)
    {
        _db = db;
        _rpId = DataDirectory.LoadRpId(dataDir);

        // Origins: the configured URLs' https origins (plus localhost for the setup wizard).
        var origins = new HashSet<string>();
        foreach (var url in opts.Urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var u))
                origins.Add($"{u.Scheme}://{u.Host}:{u.Port}");
        }
        origins.Add("https://localhost:443");
        origins.Add("https://localhost");

        _fido2 = new Fido2(new Fido2Configuration
        {
            ServerDomain = _rpId,
            ServerName = "Hyveman",
            Origins = origins,
            TimestampDriftTolerance = 300_000,
        });
    }

    public string RpId => _rpId;

    public async Task<bool> HasPasskeysAsync() => await _db.Passkeys.CountAsync() > 0;

    // ── Registration ceremony ──────────────────────────────────────────────
    public async Task<CredentialCreateOptions> CreateRegistrationOptionsAsync(string passkeyName)
    {
        var userId = Encoding.UTF8.GetBytes("hyveman-admin");
        var opts = _fido2.RequestNewCredential(new RequestNewCredentialParams
        {
            User = new Fido2User { Id = userId, Name = "admin", DisplayName = passkeyName },
            AuthenticatorSelection = new AuthenticatorSelection
            {
                ResidentKey = ResidentKeyRequirement.Preferred,
                UserVerification = UserVerificationRequirement.Preferred,
            },
            AttestationPreference = AttestationConveyancePreference.None,
            Extensions = new AuthenticationExtensionsClientInputs { Extensions = true },
        });
        Pending[CacheKey(opts.Challenge)] = new PendingCeremony(opts.ToJson(), opts.Challenge, DateTimeOffset.UtcNow.AddMinutes(5));
        return opts;
    }

    public async Task<(bool ok, string error, string? name)> VerifyRegistrationAsync(
        AuthenticatorAttestationRawResponse response, string passkeyName)
    {
        var challenge = ParseClientDataChallenge(response.Response.ClientDataJson);
        if (challenge is null || !Pending.TryRemove(CacheKey(challenge), out var pending))
            return (false, "no pending registration ceremony (refresh and retry)", null);
        if (pending.Expires < DateTimeOffset.UtcNow)
            return (false, "registration ceremony expired (refresh and retry)", null);

        try
        {
            var result = await _fido2.MakeNewCredentialAsync(new MakeNewCredentialParams
            {
                AttestationResponse = response,
                OriginalOptions = CredentialCreateOptions.FromJson(pending.OptionsJson),
                IsCredentialIdUniqueToUserCallback = static (_, _) => Task.FromResult(true),
            });
            var credIdB64 = Base64Url.Encode(result.Id);

            await _db.Writer.WithTransactionAsync(async conn =>
            {
                await PasskeyRepo.InsertAsync(conn, Ulid.Prefixed("pk_"), passkeyName, credIdB64, result.PublicKey);
                await _db.Audit.WriteAsync(passkeyName, "passkey.register", "passkeys", credIdB64, null);
            });
            return (true, "", passkeyName);
        }
        catch (Exception ex)
        {
            return (false, $"WebAuthn verification failed: {ex.Message}", null);
        }
    }

    // ── Assertion (login) ceremony ─────────────────────────────────────────
    public async Task<AssertionOptions> CreateAssertionOptionsAsync()
    {
        var passkeys = await _db.Passkeys.ListAsync();
        var allowed = passkeys
            .Select(p => Base64Url.Decode(p.CredentialId))
            .Where(b => b is { Length: > 0 })
            .Select(id => new PublicKeyCredentialDescriptor(PublicKeyCredentialType.PublicKey, id, null))
            .ToList();

        var opts = _fido2.GetAssertionOptions(new GetAssertionOptionsParams
        {
            AllowedCredentials = allowed,
            UserVerification = UserVerificationRequirement.Preferred,
            Extensions = new AuthenticationExtensionsClientInputs { Extensions = true },
        });
        Pending[CacheKey(opts.Challenge)] = new PendingCeremony(opts.ToJson(), opts.Challenge, DateTimeOffset.UtcNow.AddMinutes(5));
        return opts;
    }

    public async Task<(bool ok, string error, PasskeyRow? passkey)> VerifyAssertionAsync(AuthenticatorAssertionRawResponse response)
    {
        var challenge = ParseClientDataChallenge(response.Response.ClientDataJson);
        if (challenge is null || !Pending.TryRemove(CacheKey(challenge), out var pending))
            return (false, "no pending login ceremony (refresh and retry)", null);

        var credIdB64 = response.Id;   // client's credential.id is a base64url string
        var passkey = await _db.Passkeys.GetByCredentialIdAsync(credIdB64);
        if (passkey is null)
            return (false, "unknown credential", null);

        try
        {
            var verify = await _fido2.MakeAssertionAsync(new MakeAssertionParams
            {
                AssertionResponse = response,
                OriginalOptions = AssertionOptions.FromJson(pending.OptionsJson),
                StoredPublicKey = passkey.PublicKey,
                StoredSignatureCounter = (uint)passkey.SignCount,
                IsUserHandleOwnerOfCredentialIdCallback = static (_, _) => Task.FromResult(true),
            });
            var now = WireTime.NowMs();
            await _db.Writer.WithTransactionAsync(async conn =>
            {
                await PasskeyRepo.UpdateSignCountAsync(conn, passkey.Id, verify.SignCount, now);
                await _db.Audit.WriteAsync(passkey.Name, "auth.login", "passkeys", passkey.Id, null);
            });
            return (true, "", passkey);
        }
        catch (Exception ex)
        {
            return (false, $"assertion failed: {ex.Message}", null);
        }
    }

    private static byte[]? ParseClientDataChallenge(byte[] clientDataJson)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(clientDataJson);
            if (doc.RootElement.TryGetProperty("challenge", out var ch))
                return Base64Url.Decode(ch.GetString() ?? "");
        }
        catch (System.Text.Json.JsonException) { }
        return null;
    }

    private static string CacheKey(byte[] challenge) => Convert.ToHexString(challenge);
}

/// <summary>Base64Url helpers matching the Fido2 lib's encoding (RFC 4648 §5, no padding).</summary>
public static class Base64Url
{
    public static string Encode(byte[] data)
        => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] Decode(string s)
    {
        var t = s.Replace('-', '+').Replace('_', '/');
        switch (t.Length % 4)
        {
            case 2: t += "=="; break;
            case 3: t += "="; break;
        }
        return Convert.FromBase64String(t);
    }
}
