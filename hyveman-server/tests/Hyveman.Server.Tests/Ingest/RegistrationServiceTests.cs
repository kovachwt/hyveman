using Hyveman.Server.Auth;
using Hyveman.Server.Ingest;
using Hyveman.Server.Tests.TestInfra;

namespace Hyveman.Server.Tests.Ingest;

/// <summary>
/// POST /register semantics (§13 #18, PROTOCOL §5): single-use reg_ token → agt_ token,
/// reinstall reuse, boot_id disambiguation, kind binding.
/// </summary>
public sealed class RegistrationServiceTests
{
    private sealed class Harness : IDisposable
    {
        public TestDb Db { get; }
        public RegistrationService Service { get; }
        private int _tokenSeq;

        public Harness()
        {
            Db = TestDb.CreateAsync().GetAwaiter().GetResult();
            Service = new RegistrationService(Db.Db, new TokenService(Db.Db));
        }

        /// <summary>Insert a fresh reg_ token bound to a kind (or unbound when null).</summary>
        public async Task<string> IssueRegTokenAsync(string? boundKind = "windows-agent", string[]? scopes = null)
        {
            var raw = $"reg_test{++_tokenSeq}";
            var scopesJson = System.Text.Json.JsonSerializer.Serialize(scopes ?? new[] { "register" });
            await Db.Db.Writer.WithTransactionAsync(async conn =>
                await Db.Db.Tokens.InsertAsync(conn, $"tok_reg{_tokenSeq}", null,
                    TokenHasher.Hash(raw), scopesJson, boundKind: boundKind));
            return raw;
        }

        public static RegisterRequest Req(string? hostname = "HOST01", string? kind = "windows-agent", string? bootId = "boot-1")
            => new() { V = 1, Kind = kind, Hostname = hostname, BootId = bootId };

        public void Dispose() => Db.Dispose();
    }

    [Fact]
    public async Task Register_MintsAgtTokenAndSource()
    {
        using var h = new Harness();
        var regToken = await h.IssueRegTokenAsync();

        var res = await h.Service.RegisterAsync(Harness.Req(), regToken);

        Assert.True(res.Ok);
        Assert.Equal(200, res.Status);
        Assert.StartsWith("src_", res.SourceId);
        Assert.StartsWith("agt_", res.Token);
        Assert.Contains("ingest", res.Scopes);

        // Source exists, token resolves, raw reg token is consumed.
        var source = await h.Db.Db.Sources.GetAsync(res.SourceId!);
        Assert.Equal("HOST01", source!.Name);

        var resolved = await new TokenService(h.Db.Db).ResolveAsync(res.Token!);
        Assert.Equal(TokenResolveOutcome.Ok, resolved.Outcome);
    }

    [Fact]
    public async Task RegToken_IsSingleUse()
    {
        using var h = new Harness();
        var regToken = await h.IssueRegTokenAsync();

        var first = await h.Service.RegisterAsync(Harness.Req(), regToken);
        var second = await h.Service.RegisterAsync(Harness.Req(), regToken);

        Assert.True(first.Ok);
        Assert.False(second.Ok);
        Assert.Equal(410, second.Status);
        Assert.Equal("token_consumed", second.ErrorCode);
    }

    [Fact]
    public async Task Reinstall_WithSameBootId_ReusesSource()
    {
        using var h = new Harness();
        var first = await h.Service.RegisterAsync(Harness.Req(bootId: "boot-1"), await h.IssueRegTokenAsync());
        var second = await h.Service.RegisterAsync(Harness.Req(bootId: "boot-1"), await h.IssueRegTokenAsync());

        Assert.True(second.Ok);
        Assert.Equal(first.SourceId, second.SourceId);
        Assert.Equal(1, (await h.Db.Db.Sources.ListAsync()).Count);
    }

    [Fact]
    public async Task DifferentBootId_DisambiguatesHostname()
    {
        using var h = new Harness();
        var first = await h.Service.RegisterAsync(Harness.Req(bootId: "boot-1"), await h.IssueRegTokenAsync());
        var second = await h.Service.RegisterAsync(Harness.Req(bootId: "boot-2"), await h.IssueRegTokenAsync());

        Assert.True(second.Ok);
        Assert.NotEqual(first.SourceId, second.SourceId);
        var sources = await h.Db.Db.Sources.ListAsync();
        Assert.Equal(2, sources.Count);
        Assert.Contains(sources, s => s.Name == "HOST01-2");
    }

    [Fact]
    public async Task KindMismatch_IsRejected()
    {
        using var h = new Harness();
        var regToken = await h.IssueRegTokenAsync(boundKind: "windows-agent");

        var res = await h.Service.RegisterAsync(Harness.Req(kind: "linux-agent"), regToken);

        Assert.False(res.Ok);
        Assert.Equal(400, res.Status);
        Assert.Contains("kind_mismatch", res.ErrorMessage);
    }

    [Fact]
    public async Task MissingKindOrHostname_IsRejected()
    {
        using var h = new Harness();
        var regToken = await h.IssueRegTokenAsync();

        var noKind = await h.Service.RegisterAsync(Harness.Req(kind: null), regToken);
        var noHost = await h.Service.RegisterAsync(Harness.Req(hostname: null), regToken);

        Assert.Equal(400, noKind.Status);
        Assert.Equal(400, noHost.Status);
    }

    [Fact]
    public async Task UnknownRegToken_IsRejected()
    {
        using var h = new Harness();
        var res = await h.Service.RegisterAsync(Harness.Req(), "reg_bogus");
        Assert.Equal(401, res.Status);
        Assert.Equal("token_invalid", res.ErrorCode);
    }

    [Fact]
    public async Task TokenWithoutRegisterScope_IsRejected()
    {
        using var h = new Harness();
        var regToken = await h.IssueRegTokenAsync(scopes: new[] { "ingest" });

        var res = await h.Service.RegisterAsync(Harness.Req(), regToken);

        Assert.Equal(403, res.Status);
        Assert.Equal("wrong_scope", res.ErrorCode);
    }
}
