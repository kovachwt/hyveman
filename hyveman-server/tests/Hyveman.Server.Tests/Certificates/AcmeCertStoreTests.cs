using System.Security.Cryptography.X509Certificates;
using Hyveman.Server.Certificates;

namespace Hyveman.Server.Tests.Certificates;

/// <summary>
/// AcmeCertStore: pfx password derivation from key K, bootstrap fallback, atomic swap +
/// persistence round-trip, and the renewal-due decision.
/// </summary>
public sealed class AcmeCertStoreTests
{
    private static string TempDataDir()
        => Path.Combine(Path.GetTempPath(), "hyveman-cert-tests-" + Guid.NewGuid().ToString("N"));

    private static byte[] SomeKey() => System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);

    [Fact]
    public void PfxPassword_IsDeterministic_AndKeySpecific()
    {
        var keyA = SomeKey();
        var keyB = SomeKey();

        Assert.Equal(AcmeCertStore.PfxPasswordFromKey(keyA), AcmeCertStore.PfxPasswordFromKey(keyA));
        Assert.Equal(64, AcmeCertStore.PfxPasswordFromKey(keyA).Length);   // SHA-256 hex
        Assert.NotEqual(AcmeCertStore.PfxPasswordFromKey(keyA), AcmeCertStore.PfxPasswordFromKey(keyB));
    }

    [Fact]
    public void LoadOrBootstrap_FallsBackToSelfSigned_WhenNoIssuedPfx()
    {
        var dataDir = TempDataDir();
        try
        {
            var store = AcmeCertStore.LoadOrBootstrap(dataDir, SomeKey(), new[] { "hyveman.example.com" });

            Assert.True(store.IsBootstrap);
            Assert.True(store.Current.HasPrivateKey);
            Assert.Contains("hyveman.example.com", store.Current.GetNameInfo(X509NameType.DnsName, false));
            // Sanity: bootstrap cert is self-signed and short-lived.
            Assert.Equal(store.Current.Subject, store.Current.Issuer);
            Assert.True(store.Current.NotAfter - store.Current.NotBefore < TimeSpan.FromDays(100));
        }
        finally
        {
            Directory.Delete(dataDir, recursive: true);
        }
    }

    [Fact]
    public void Replace_SwapsCertificate_AndPersistsAcrossReload()
    {
        var dataDir = TempDataDir();
        try
        {
            var key = SomeKey();
            var store = AcmeCertStore.LoadOrBootstrap(dataDir, key, new[] { "hyveman.example.com" });
            var bootstrapThumbprint = store.Current.Thumbprint;

            // Build a stand-in "issued" pfx: a self-signed cert exported with the store's password.
            var issued = MakeCert("CN=hyveman.example.com", DateTimeOffset.UtcNow.AddDays(80));
            var pfx = issued.Export(X509ContentType.Pfx, store.PfxPassword);

            store.Replace(pfx);

            Assert.False(store.IsBootstrap);
            Assert.NotEqual(bootstrapThumbprint, store.Current.Thumbprint);
            Assert.Equal(issued.Thumbprint, store.Current.Thumbprint);
            Assert.True(store.Current.HasPrivateKey);
            Assert.True(File.Exists(Path.Combine(AcmeCertStore.CertsDir(dataDir), "cert.pfx")));

            // A fresh store over the same data dir + same key must pick up the persisted pfx.
            var reloaded = AcmeCertStore.LoadOrBootstrap(dataDir, key, new[] { "hyveman.example.com" });
            Assert.False(reloaded.IsBootstrap);
            Assert.Equal(issued.Thumbprint, reloaded.Current.Thumbprint);
        }
        finally
        {
            Directory.Delete(dataDir, recursive: true);
        }
    }

    [Fact]
    public void Replace_WrongPassword_IsRejected()
    {
        var dataDir = TempDataDir();
        try
        {
            var store = AcmeCertStore.LoadOrBootstrap(dataDir, SomeKey(), new[] { "hyveman.example.com" });
            var issued = MakeCert("CN=hyveman.example.com", DateTimeOffset.UtcNow.AddDays(80));
            var pfx = issued.Export(X509ContentType.Pfx, "some-other-password");

            Assert.Throws<System.Security.Cryptography.CryptographicException>(() => store.Replace(pfx));
        }
        finally
        {
            Directory.Delete(dataDir, recursive: true);
        }
    }

    [Fact]
    public void IsRenewalDue_RespectsRenewWindow_AndBootstrapFlag()
    {
        var dataDir = TempDataDir();
        try
        {
            var store = AcmeCertStore.LoadOrBootstrap(dataDir, SomeKey(), new[] { "hyveman.example.com" });
            var now = DateTimeOffset.UtcNow;

            // Bootstrap cert is not a real cert → always due.
            Assert.True(store.IsRenewalDue(30, now));

            // Expiring in 60 days: due at renew_days 89, not at 30.
            store.Replace(MakeCert("CN=hyveman.example.com", now.AddDays(60)).Export(X509ContentType.Pfx, store.PfxPassword));
            Assert.False(store.IsRenewalDue(30, now));
            Assert.True(store.IsRenewalDue(89, now));

            // Already-expired cert → due regardless of window.
            store.Replace(MakeCert("CN=hyveman.example.com", now.AddDays(-1)).Export(X509ContentType.Pfx, store.PfxPassword));
            Assert.True(store.IsRenewalDue(30, now));
        }
        finally
        {
            Directory.Delete(dataDir, recursive: true);
        }
    }

    private static X509Certificate2 MakeCert(string subject, DateTimeOffset notAfter)
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var req = new CertificateRequest(subject, rsa, System.Security.Cryptography.HashAlgorithmName.SHA256,
            System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        return req.CreateSelfSigned(notAfter.AddDays(-1), notAfter);
    }
}
