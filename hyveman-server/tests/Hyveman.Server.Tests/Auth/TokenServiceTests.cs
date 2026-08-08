using Hyveman.Server.Auth;
using Hyveman.Server.Common;
using Hyveman.Server.Tests.TestInfra;

namespace Hyveman.Server.Tests.Auth;

/// <summary>Token resolution semantics (§12.1): hash lookup, revocation, consumption, expiry.</summary>
public sealed class TokenServiceTests
{
    [Fact]
    public async Task UnknownToken_IsInvalid()
    {
        using var db = await TestDb.CreateAsync();
        var svc = new TokenService(db.Db);

        var res = await svc.ResolveAsync("agt_does-not-exist");

        Assert.Equal(TokenResolveOutcome.Invalid, res.Outcome);
        Assert.Equal("token_invalid", res.ErrorCode);
    }

    [Fact]
    public async Task EmptyToken_IsInvalid()
    {
        using var db = await TestDb.CreateAsync();
        var svc = new TokenService(db.Db);
        Assert.Equal(TokenResolveOutcome.Invalid, (await svc.ResolveAsync("")).Outcome);
        Assert.Equal(TokenResolveOutcome.Invalid, (await svc.ResolveAsync(null!)).Outcome);
    }

    [Fact]
    public async Task ValidToken_ResolvesWithSource()
    {
        using var db = await TestDb.CreateAsync();
        const string raw = "agt_valid123";
        await db.Db.Writer.WithTransactionAsync(async conn =>
        {
            await db.Db.Sources.InsertAsync(conn, "src_1", "windows-agent", "HOST01", null);
            await db.Db.Tokens.InsertAsync(conn, "tok_1", "src_1", TokenHasher.Hash(raw), "[\"ingest\"]");
        });
        var svc = new TokenService(db.Db);

        var res = await svc.ResolveAsync(raw);

        Assert.Equal(TokenResolveOutcome.Ok, res.Outcome);
        Assert.NotNull(res.Source);
        Assert.Equal("HOST01", res.Source!.Name);
    }

    [Fact]
    public async Task RevokedToken_IsRevoked()
    {
        using var db = await TestDb.CreateAsync();
        const string raw = "agt_revoked1";
        await db.Db.Writer.WithTransactionAsync(async conn =>
        {
            await db.Db.Tokens.InsertAsync(conn, "tok_1", null, TokenHasher.Hash(raw), "[\"ingest\"]");
            await db.Db.Tokens.RevokeAsync(conn, "tok_1");
        });
        var svc = new TokenService(db.Db);

        var res = await svc.ResolveAsync(raw);

        Assert.Equal(TokenResolveOutcome.Revoked, res.Outcome);
        Assert.Equal("token_revoked", res.ErrorCode);
    }

    [Fact]
    public async Task ConsumedRegToken_IsConsumed()
    {
        using var db = await TestDb.CreateAsync();
        const string raw = "reg_usedup001";
        await db.Db.Writer.WithTransactionAsync(async conn =>
        {
            await db.Db.Tokens.InsertAsync(conn, "tok_1", null, TokenHasher.Hash(raw), "[\"register\"]");
            await db.Db.Tokens.MarkConsumedAsync(conn, "tok_1");
        });
        var svc = new TokenService(db.Db);

        var res = await svc.ResolveAsync(raw);

        Assert.Equal(TokenResolveOutcome.Consumed, res.Outcome);
        Assert.Equal("token_consumed", res.ErrorCode);
    }

    [Fact]
    public async Task ExpiredToken_IsInvalid()
    {
        using var db = await TestDb.CreateAsync();
        const string raw = "agt_expired1";
        await db.Db.Writer.WithTransactionAsync(async conn =>
        {
            await db.Db.Tokens.InsertAsync(conn, "tok_1", null, TokenHasher.Hash(raw), "[\"ingest\"]",
                expiresAt: WireTime.ToIsoMs(DateTimeOffset.UtcNow.AddMinutes(-5)));
        });
        var svc = new TokenService(db.Db);

        var res = await svc.ResolveAsync(raw);

        Assert.Equal(TokenResolveOutcome.Invalid, res.Outcome);
    }
}
