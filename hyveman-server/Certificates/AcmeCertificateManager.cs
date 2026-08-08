using Certes;
using Certes.Acme;
using Certes.Acme.Resource;
using Hyveman.Server.Config;

namespace Hyveman.Server.Certificates;

/// <summary>
/// Let's Encrypt (ACME v2) certificate lifecycle: registers the account key, issues the
/// first certificate (http-01 challenge, served by <see cref="AcmeHttpMiddleware"/>), and
/// renews before expiry. Runs as a background service so a certificate problem never blocks
/// startup — Kestrel serves the bootstrap/previous certificate meanwhile, and issuance is
/// retried with exponential backoff.
///
/// State: <c>&lt;data_dir&gt;/certs/account-key.pem</c> (ACME account key, registered once
/// and reused for every order) and <c>&lt;data_dir&gt;/certs/cert.pfx</c> (issued chain,
/// swapped into <see cref="AcmeCertStore"/>). Both are swept up by the normal data-dir
/// backup. Certificate validity: 90 days; renewal at <c>tls.lets_encrypt.renew_days</c>
/// (default 30) days before expiry.
/// </summary>
public sealed class AcmeCertificateManager : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(12);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromHours(1);
    private static readonly TimeSpan ValidationTimeout = TimeSpan.FromMinutes(5);

    private readonly ILogger<AcmeCertificateManager> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ServerOptions _options;
    private readonly AcmeCertStore _certStore;
    private readonly Http01ChallengeStore _challengeStore;
    private readonly string _dataDir;

    public AcmeCertificateManager(
        ILogger<AcmeCertificateManager> logger,
        IHttpClientFactory httpClientFactory,
        ServerOptions options,
        AcmeCertStore certStore,
        Http01ChallengeStore challengeStore,
        string dataDir)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _options = options;
        _certStore = certStore;
        _challengeStore = challengeStore;
        _dataDir = dataDir;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var le = _options.Tls.LetsEncrypt;
        if (!le.Enabled) return;

        var backoff = TimeSpan.FromMinutes(1);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await EnsureCertificateAsync(le, stoppingToken);
                backoff = TimeSpan.FromMinutes(1);
                await Task.Delay(CheckInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ACME: certificate issuance/renewal failed; next attempt in {Seconds}s", backoff.TotalSeconds);
                try { await Task.Delay(backoff, stoppingToken); }
                catch (OperationCanceledException) { break; }
                backoff = TimeSpan.FromMinutes(Math.Min(backoff.TotalMinutes * 2, MaxBackoff.TotalMinutes));
            }
        }
    }

    private async Task EnsureCertificateAsync(ServerOptions.LetsEncryptOptions le, CancellationToken ct)
    {
        if (!_certStore.IsRenewalDue(le.RenewDays, DateTimeOffset.UtcNow))
        {
            _logger.LogInformation(
                "ACME: certificate for {Domains} valid until {NotAfter}; next check in {Hours}h",
                string.Join(", ", le.Domains), _certStore.Current.NotAfter, CheckInterval.TotalHours);
            return;
        }

        _logger.LogInformation("ACME: requesting certificate for {Domains} ({Endpoint})",
            string.Join(", ", le.Domains), le.Staging ? "staging" : "production");

        var accountKey = LoadAccountKey() ?? KeyFactory.NewKey(KeyAlgorithm.ES256);
        var directory = le.Staging ? WellKnownServers.LetsEncryptStagingV2 : WellKnownServers.LetsEncryptV2;
        using var httpClient = _httpClientFactory.CreateClient("acme");
        var acme = new AcmeContext(directory, accountKey, new AcmeHttpClient(directory, httpClient));

        // Idempotent: with an already-registered key this updates the contact/ToS acceptance;
        // with a fresh key it creates the account. One call covers both paths.
        await acme.NewAccount(le.Email!, termsOfServiceAgreed: true);
        SaveAccountKey(accountKey);
        _logger.LogInformation("ACME: account key ready ({KeyPath})", AcmeCertStore.AccountKeyPath(_dataDir));

        var order = await acme.NewOrder(le.Domains);
        var authorizations = await order.Authorizations();

        // Stage every challenge response before asking the CA to validate any of them.
        var challenges = new List<IChallengeContext>();
        try
        {
            foreach (var authz in authorizations)
            {
                var challenge = await authz.Http();
                _challengeStore.Set(challenge.Token, challenge.KeyAuthz);
                challenges.Add(challenge);
                _logger.LogDebug("ACME: http-01 challenge staged for {Identifier}", authz.Location);
            }
            foreach (var challenge in challenges)
                await challenge.Validate();
            foreach (var challenge in challenges)
                await WaitForValidationAsync(challenge, ct);
        }
        finally
        {
            foreach (var challenge in challenges)
                _challengeStore.Remove(challenge.Token);
        }

        var certKey = KeyFactory.NewKey(KeyAlgorithm.ES256);
        var chain = await order.Generate(new CsrInfo { CommonName = le.Domains[0] }, certKey);
        var pfx = chain.ToPfx(certKey).Build("hyveman", _certStore.PfxPassword);
        var issued = new System.Security.Cryptography.X509Certificates.X509Certificate2(
            pfx, _certStore.PfxPassword,
            System.Security.Cryptography.X509Certificates.X509KeyStorageFlags.EphemeralKeySet);
        _certStore.Replace(pfx);

        _logger.LogInformation("ACME: certificate issued for {Domains} — {Subject}, expires {NotAfter} (staging={Staging})",
            string.Join(", ", le.Domains), issued.Subject, issued.NotAfter, le.Staging);
    }

    private static async Task WaitForValidationAsync(IChallengeContext challenge, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + ValidationTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var resource = await challenge.Resource();
            if (resource.Status == ChallengeStatus.Valid) return;
            if (resource.Status == ChallengeStatus.Invalid)
                throw new InvalidOperationException(
                    $"ACME: http-01 challenge for token '{challenge.Token}' failed validation: {DescribeError(resource.Error)}");
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }
        throw new TimeoutException($"ACME: challenge for token '{challenge.Token}' did not validate within {ValidationTimeout.TotalMinutes:0} minutes");
    }

    private static string DescribeError(AcmeError? error)
        => error is null ? "no error detail" : $"{error.Type} ({error.Detail})";

    private Certes.IKey? LoadAccountKey()
    {
        var path = AcmeCertStore.AccountKeyPath(_dataDir);
        if (!File.Exists(path)) return null;
        try
        {
            return KeyFactory.FromPem(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            // Unreadable key: register a fresh account (a new key overwrites the file).
            _logger.LogWarning(ex, "ACME: account key at {Path} is unreadable — registering a new account", path);
            return null;
        }
    }

    private void SaveAccountKey(Certes.IKey accountKey)
    {
        var path = AcmeCertStore.AccountKeyPath(_dataDir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, accountKey.ToPem());
        DataDirectory.RestrictAclFile(tmp);
        File.Move(tmp, path, overwrite: true);
    }
}
