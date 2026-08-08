using Hyveman.Server.Auth;

namespace Hyveman.Server.Tests.Auth;

public sealed class TokenHasherTests
{
    [Fact]
    public void Hash_IsDeterministicLowercaseHex()
    {
        var h1 = TokenHasher.Hash("agt_abc123");
        var h2 = TokenHasher.Hash("agt_abc123");

        Assert.Equal(h1, h2);
        Assert.Equal(64, h1.Length); // SHA-256
        Assert.Matches("^[0-9a-f]{64}$", h1);
    }

    [Fact]
    public void Hash_IsNotTheRawToken()
    {
        const string raw = "reg_supersecret";
        var hash = TokenHasher.Hash(raw);
        Assert.DoesNotContain(raw, hash);
    }

    [Fact]
    public void NewRawToken_HasPrefixAndExpectedLength()
    {
        var t = TokenHasher.NewRawToken("agt_");
        // 32 random bytes → ceil(256/5) = 52 base32 chars.
        Assert.StartsWith("agt_", t);
        Assert.Equal(4 + 52, t.Length);
    }

    [Fact]
    public void NewRawToken_UsesOnlyBase32UrlAlphabet()
    {
        var t = TokenHasher.NewRawToken("reg_");
        Assert.Matches("^reg_[A-Z2-7]+$", t);
    }

    [Fact]
    public void NewRawToken_IsUniqueAndHashesDifferently()
    {
        var a = TokenHasher.NewRawToken("agt_");
        var b = TokenHasher.NewRawToken("agt_");
        Assert.NotEqual(a, b);
        Assert.NotEqual(TokenHasher.Hash(a), TokenHasher.Hash(b));
    }
}
