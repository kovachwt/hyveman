using System.Text.Json;
using Hyveman.Server.Config;

namespace Hyveman.Server.Tests.Config;

/// <summary>
/// OptionsResolver validation of the tls.lets_encrypt block: a valid config loads, and each
/// misconfiguration fails fast with a clear message (SERVER.md §5.3).
/// </summary>
public sealed class LetsEncryptOptionsTests
{
    private static string NewDataDir()
        => Path.Combine(Path.GetTempPath(), "hyveman-options-tests-" + Guid.NewGuid().ToString("N"));

    private static void WriteConfig(string dataDir, ServerOptions opts)
    {
        Directory.CreateDirectory(Path.Combine(dataDir, "config"));
        File.WriteAllText(DataDirectory.ConfigFilePath(dataDir), JsonSerializer.Serialize(opts, OptionsResolver.JsonOpts));
    }

    private static ServerOptions ValidLe() => new()
    {
        Urls = "https://0.0.0.0:443",
        Tls = new ServerOptions.TlsOptions
        {
            LetsEncrypt = new ServerOptions.LetsEncryptOptions
            {
                Enabled = true,
                Domains = new() { "hyveman.example.com" },
                Email = "admin@example.com",
            },
        },
    };

    private static string LoadError(ServerOptions opts)
    {
        var dataDir = NewDataDir();
        try
        {
            WriteConfig(dataDir, opts);
            var ex = Assert.Throws<InvalidOperationException>(() => OptionsResolver.Load(dataDir, null));
            return ex.Message;
        }
        finally
        {
            Directory.Delete(dataDir, recursive: true);
        }
    }

    [Fact]
    public void ValidLetsEncryptConfig_PassesValidation()
    {
        var dataDir = NewDataDir();
        try
        {
            WriteConfig(dataDir, ValidLe());
            var opts = OptionsResolver.Load(dataDir, null);
            Assert.True(opts.Tls.LetsEncrypt.Enabled);
            Assert.Equal(new[] { "hyveman.example.com" }, opts.Tls.LetsEncrypt.Domains);
        }
        finally
        {
            Directory.Delete(dataDir, recursive: true);
        }
    }

    [Fact]
    public void LetsEncrypt_WithCertPath_IsRejected()
    {
        var opts = ValidLe();
        opts.Tls.CertPath = "config/cert.pfx";

        var msg = LoadError(opts);
        Assert.Contains("cannot be combined", msg);
    }

    [Fact]
    public void LetsEncrypt_WithoutDomains_IsRejected()
    {
        var opts = ValidLe();
        opts.Tls.LetsEncrypt.Domains.Clear();

        var msg = LoadError(opts);
        Assert.Contains("at least one domain", msg);
    }

    [Theory]
    [InlineData("*.example.com")]       // wildcard needs dns-01, not http-01
    [InlineData("hyveman")]             // single label — not publicly validatable
    [InlineData("hy_man.example.com")]  // underscore
    [InlineData("-hyveman.example.com")]// leading hyphen
    [InlineData("hyveman-.example.com")]// trailing hyphen in label
    [InlineData("hyveman..com")]        // empty label
    [InlineData("hyveman.example.com/")]// slash
    public void LetsEncrypt_InvalidDomain_IsRejected(string domain)
    {
        var opts = ValidLe();
        opts.Tls.LetsEncrypt.Domains = new() { domain };

        var msg = LoadError(opts);
        Assert.Contains("not a valid public DNS name", msg);
    }

    [Fact]
    public void LetsEncrypt_MultipleDomains_AreAccepted()
    {
        var opts = ValidLe();
        opts.Tls.LetsEncrypt.Domains = new() { "hyveman.example.com", "hyveman-backup.example.com" };

        var dataDir = NewDataDir();
        try
        {
            WriteConfig(dataDir, opts);
            var loaded = OptionsResolver.Load(dataDir, null);
            Assert.Equal(2, loaded.Tls.LetsEncrypt.Domains.Count);
        }
        finally
        {
            Directory.Delete(dataDir, recursive: true);
        }
    }

    [Fact]
    public void LetsEncrypt_WithoutEmail_IsRejected()
    {
        var opts = ValidLe();
        opts.Tls.LetsEncrypt.Email = null;

        var msg = LoadError(opts);
        Assert.Contains("email is required", msg);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(100)]
    public void LetsEncrypt_RenewDaysOutOfRange_IsRejected(int renewDays)
    {
        var opts = ValidLe();
        opts.Tls.LetsEncrypt.RenewDays = renewDays;

        var msg = LoadError(opts);
        Assert.Contains("renew_days must be 1..89", msg);
    }

    [Fact]
    public void LetsEncrypt_HttpPortCollidingWithUrls_IsRejected()
    {
        var opts = ValidLe();
        opts.Tls.LetsEncrypt.HttpPort = 443;

        var msg = LoadError(opts);
        Assert.Contains("collides with an https port", msg);
    }

    [Fact]
    public void LetsEncrypt_CountsAsCertificateSource_OnNonWindows()
    {
        // Mirrors the Linux guard: LE-enabled config must not trip the
        // "tls.cert_path is required on non-Windows" error. The check itself is
        // OS-gated, so this just documents the intended combination.
        var dataDir = NewDataDir();
        try
        {
            WriteConfig(dataDir, ValidLe());
            var opts = OptionsResolver.Load(dataDir, null);
            Assert.True(opts.Tls.LetsEncrypt.Enabled);
        }
        finally
        {
            Directory.Delete(dataDir, recursive: true);
        }
    }
}
