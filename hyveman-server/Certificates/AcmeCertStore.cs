using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Hyveman.Server.Config;

namespace Hyveman.Server.Certificates;

/// <summary>
/// Owns the certificate served by the Kestrel TLS listener when Let's Encrypt is enabled.
///
/// The issued certificate chain (PFX) and the ACME account key live in
/// <c>&lt;data_dir&gt;/certs/</c> (part of the normal data-dir backup). The PFX password is
/// derived from server key K — nothing new to back up, and the private key is at rest only
/// on the server. On first run (or if the stored PFX is unreadable) a short-lived
/// self-signed bootstrap certificate is served so HTTPS works end-to-end until the ACME
/// order completes; <see cref="AcmeCertificateManager"/> swaps in the real certificate.
/// </summary>
public sealed class AcmeCertStore
{
    private const string PfxFriendlyName = "hyveman";
    private const string PfxPasswordContext = "hyveman-acme-pfx";

    private readonly object _gate = new();
    private readonly string _pfxPath;
    private readonly string _pfxPassword;
    private X509Certificate2 _current;
    private X509Certificate2? _previous;   // kept alive one extra cycle so in-flight TLS handshakes finish safely

    private AcmeCertStore(string certsDir, string pfxPassword, X509Certificate2 initial, bool isBootstrap)
    {
        _pfxPath = Path.Combine(certsDir, "cert.pfx");
        _pfxPassword = pfxPassword;
        _current = initial;
        IsBootstrap = isBootstrap;
    }

    /// <summary>True when serving the self-signed bootstrap cert (no issued PFX on disk yet).</summary>
    public bool IsBootstrap { get; private set; }

    /// <summary>The certificate Kestrel should present (bootstrap or issued).</summary>
    public X509Certificate2 Current
    {
        get { lock (_gate) return _current; }
    }

    /// <summary>PFX password derived from key K — deterministic per data dir, never stored.</summary>
    public string PfxPassword => _pfxPassword;

    public static string CertsDir(string dataDir) => Path.Combine(dataDir, "certs");
    public static string PfxPath(string dataDir) => Path.Combine(CertsDir(dataDir), "cert.pfx");
    public static string AccountKeyPath(string dataDir) => Path.Combine(CertsDir(dataDir), "account-key.pem");

    public static string PfxPasswordFromKey(byte[] key)
    {
        var ctx = System.Text.Encoding.UTF8.GetBytes(PfxPasswordContext);
        var input = new byte[key.Length + ctx.Length];
        Buffer.BlockCopy(key, 0, input, 0, key.Length);
        Buffer.BlockCopy(ctx, 0, input, key.Length, ctx.Length);
        return Convert.ToHexString(SHA256.HashData(input));
    }

    /// <summary>
    /// Load the issued PFX if present and decryptable, otherwise fall back to a fresh
    /// self-signed bootstrap certificate for the configured domains.
    /// </summary>
    public static AcmeCertStore LoadOrBootstrap(string dataDir, byte[] key, IReadOnlyList<string> domains)
    {
        var certsDir = CertsDir(dataDir);
        Directory.CreateDirectory(certsDir);
        DataDirectory.RestrictAcl(certsDir);
        var password = PfxPasswordFromKey(key);
        var pfxPath = Path.Combine(certsDir, "cert.pfx");
        var issued = TryLoadIssued(pfxPath, password);
        return new AcmeCertStore(certsDir, password, issued ?? CreateBootstrapCert(domains), isBootstrap: issued is null);
    }

    /// <summary>True when the current certificate must be (re)issued soon.</summary>
    public bool IsRenewalDue(int renewDays, DateTimeOffset now)
    {
        var cert = Current;
        return IsBootstrap || cert.NotAfter <= now.AddDays(renewDays);
    }

    /// <summary>Atomically persist the issued PFX and swap it into the TLS listener.</summary>
    public void Replace(byte[] pfxBytes)
    {
        if (pfxBytes is null || pfxBytes.Length == 0) throw new ArgumentException("pfxBytes must not be empty", nameof(pfxBytes));
        var cert = new X509Certificate2(pfxBytes, _pfxPassword, X509KeyStorageFlags.EphemeralKeySet);
        SavePfx(pfxBytes);
        lock (_gate)
        {
            _previous?.Dispose();
            _previous = _current;
            _current = cert;
        }
        IsBootstrap = false;
    }

    private void SavePfx(byte[] pfx)
    {
        var tmp = _pfxPath + ".tmp";
        File.WriteAllBytes(tmp, pfx);
        DataDirectory.RestrictAclFile(tmp);
        File.Move(tmp, _pfxPath, overwrite: true);
    }

    private static X509Certificate2? TryLoadIssued(string path, string password)
    {
        if (!File.Exists(path)) return null;
        try
        {
            return new X509Certificate2(path, password, X509KeyStorageFlags.EphemeralKeySet);
        }
        catch (Exception)
        {
            // Corrupt/undecryptable (e.g. restored from a backup without key K): fall back to
            // bootstrap; the renewal loop re-issues and overwrites the PFX.
            return null;
        }
    }

    /// <summary>Short-lived self-signed cert so HTTPS works from the very first boot, before the ACME order lands.</summary>
    public static X509Certificate2 CreateBootstrapCert(IReadOnlyList<string> domains)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest($"CN={domains[0]}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        foreach (var d in domains) san.AddDnsName(d);
        san.AddDnsName("localhost");
        req.CertificateExtensions.Add(san.Build());
        req.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, critical: false));   // serverAuth
        return req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(89));
    }
}
