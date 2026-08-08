using Hyveman.Server.Notifications;

namespace Hyveman.Server.Tests.Notifications;

/// <summary>Webhook SSRF guard (§10.1): loopback/private/link-local/metadata blocked unless allowlisted.</summary>
public sealed class SsrfGuardTests
{
    private static readonly string[] NoAllow = Array.Empty<string>();

    [Theory]
    [InlineData("http://127.0.0.1:8080/hook")]
    [InlineData("http://localhost/hook")]
    [InlineData("http://10.1.2.3/hook")]
    [InlineData("http://172.16.0.1/hook")]
    [InlineData("http://172.31.255.255/hook")]
    [InlineData("http://192.168.1.1/hook")]
    [InlineData("http://169.254.169.254/latest/meta-data")] // cloud metadata
    [InlineData("http://169.254.10.10/hook")]               // link-local
    [InlineData("http://100.64.0.1/hook")]                  // CGNAT
    [InlineData("http://[fe80::1]/hook")]                   // IPv6 link-local
    public void PrivateOrSpecialDestinations_AreBlocked(string url)
    {
        var (ok, reason) = SsrfGuard.IsAllowed(url, allowPrivate: false, NoAllow);
        Assert.False(ok);
        Assert.NotEmpty(reason);
    }

    [Theory]
    [InlineData("https://8.8.8.8/hook")]
    [InlineData("http://8.8.4.4/hook")]
    public void PublicDestinations_AreAllowed(string url)
    {
        Assert.True(SsrfGuard.IsAllowed(url, allowPrivate: false, NoAllow).ok);
    }

    [Fact]
    public void AllowPrivate_PermitsPrivateNetworks()
    {
        Assert.True(SsrfGuard.IsAllowed("http://10.0.0.5/hook", allowPrivate: true, NoAllow).ok);
        Assert.False(SsrfGuard.IsAllowed("http://127.0.0.1/hook", allowPrivate: true, NoAllow).ok); // loopback still blocked
    }

    [Fact]
    public void AllowedHosts_ExplicitlyPermitInternalTargets()
    {
        Assert.True(SsrfGuard.IsAllowed("http://10.0.0.5/hook", allowPrivate: false, new[] { "10.0.0.5" }).ok);
        Assert.True(SsrfGuard.IsAllowed("https://intranet.local/hook", allowPrivate: false, new[] { "intranet.local" }).ok);
    }

    [Theory]
    [InlineData("ftp://example.com/hook")]
    [InlineData("file:///etc/passwd")]
    [InlineData("example.com/hook")]
    [InlineData("/relative/path")]
    [InlineData("")]
    public void NonHttpOrMalformedUrls_AreRejected(string url)
    {
        var (ok, reason) = SsrfGuard.IsAllowed(url, allowPrivate: false, NoAllow);
        Assert.False(ok);
        Assert.NotEmpty(reason);
    }

    [Fact]
    public void UnresolvableHost_IsRejected()
    {
        // RFC 2606 reserved TLD: guaranteed NXDOMAIN.
        var (ok, reason) = SsrfGuard.IsAllowed("http://no-such-host.invalid/hook", allowPrivate: false, NoAllow);
        Assert.False(ok);
        Assert.Equal("DNS resolution failed", reason);
    }
}
