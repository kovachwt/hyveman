using Dapper;
using Hyveman.Application;
using Hyveman.Domain;

namespace Hyveman.Infrastructure.Sqlite;

public sealed class SourceStore(SqliteDb db) : ISourceStore
{
    public async Task<Source?> GetByIdAsync(string id, CancellationToken ct)
    {
        using var conn = StoreHelpers.Open(db);
        var row = await conn.QuerySingleOrDefaultAsync(new CommandDefinition(
            "SELECT id, kind, name, created_at FROM sources WHERE id = @id", new { id }, cancellationToken: ct));
        return row is null ? null : Map(row);
    }

    public async Task<Source?> GetByKindNameAsync(string kind, string name, CancellationToken ct)
    {
        using var conn = StoreHelpers.Open(db);
        var row = await conn.QuerySingleOrDefaultAsync(new CommandDefinition(
            "SELECT id, kind, name, created_at FROM sources WHERE kind = @kind AND name = @name",
            new { kind, name }, cancellationToken: ct));
        return row is null ? null : Map(row);
    }

    public async Task<Source> CreateAsync(string kind, string name, DateTimeOffset now, CancellationToken ct)
    {
        var source = new Source(StoreHelpers.RandomId("src_", 18), kind, name, now);
        using var conn = StoreHelpers.Open(db);
        await conn.ExecuteAsync(new CommandDefinition(
            "INSERT INTO sources(id, kind, name, created_at) VALUES (@Id, @Kind, @Name, @CreatedAt)",
            new { source.Id, source.Kind, source.Name, CreatedAt = StoreHelpers.Fmt(now) }, cancellationToken: ct));
        return source;
    }

    public async Task<IReadOnlyList<Source>> ListAsync(CancellationToken ct)
    {
        using var conn = StoreHelpers.Open(db);
        var rows = await conn.QueryAsync(new CommandDefinition(
            "SELECT id, kind, name, created_at FROM sources ORDER BY kind, name", cancellationToken: ct));
        return rows.Select(Map).ToList();
    }

    public async Task DeleteAsync(string id, CancellationToken ct)
    {
        using var conn = StoreHelpers.Open(db);
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM sources WHERE id = @id", new { id }, cancellationToken: ct));
    }

    private static Source Map(dynamic r) => new(r.id, r.kind, r.name, StoreHelpers.Parse(r.created_at));
}

public sealed class TokenStore(SqliteDb db) : ITokenStore
{
    public async Task<TokenAuthResult?> AuthenticateAsync(string rawToken, CancellationToken ct)
    {
        var hash = StoreHelpers.HashToken(rawToken);
        using var conn = StoreHelpers.Open(db);
        var row = await conn.QuerySingleOrDefaultAsync(new CommandDefinition("""
            SELECT t.id AS token_id, t.source_id, s.kind AS source_kind, t.scopes,
                   t.revoked, t.expires_at
            FROM tokens t JOIN sources s ON s.id = t.source_id
            WHERE t.token_hash = @hash
            """, new { hash }, cancellationToken: ct));
        if (row is null) return null;
        if (row.revoked == 1L) return null;
        if (row.expires_at is not null && StoreHelpers.Parse(row.expires_at) <= DateTimeOffset.UtcNow) return null;

        // last_used hygiene is best-effort; a failure must not fail auth.
        try
        {
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE tokens SET last_used = @now WHERE id = @id",
                new { id = (string)row.token_id, now = StoreHelpers.Fmt(DateTimeOffset.UtcNow) }, cancellationToken: ct));
        }
        catch (Exception)
        {
            // ignore
        }
        var scopes = System.Text.Json.JsonSerializer.Deserialize<string[]>((string)row.scopes) ?? [];
        return new TokenAuthResult((string)row.token_id, (string)row.source_id, (string)row.source_kind, scopes);
    }

    public async Task<bool> IsRevokedAsync(string rawToken, CancellationToken ct)
    {
        var hash = StoreHelpers.HashToken(rawToken);
        using var conn = StoreHelpers.Open(db);
        var row = await conn.QuerySingleOrDefaultAsync(new CommandDefinition(
            "SELECT revoked FROM tokens WHERE token_hash = @hash", new { hash }, cancellationToken: ct));
        return row is not null && (long)row.revoked == 1;
    }

    public async Task<bool> SourceMissingAsync(string rawToken, CancellationToken ct)
    {
        var hash = StoreHelpers.HashToken(rawToken);
        using var conn = StoreHelpers.Open(db);
        var row = await conn.QuerySingleOrDefaultAsync(new CommandDefinition("""
            SELECT t.source_id, s.id AS source_exists
            FROM tokens t LEFT JOIN sources s ON s.id = t.source_id
            WHERE t.token_hash = @hash
            """, new { hash }, cancellationToken: ct));
        return row is not null && row.source_exists is null;
    }

    public async Task<string> CreateAgentTokenAsync(string sourceId, string[] scopes, DateTimeOffset now, CancellationToken ct)
    {
        var raw = "agt_" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        using var conn = StoreHelpers.Open(db);
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO tokens(id, source_id, token_hash, prefix, scopes, created)
            VALUES (@Id, @SourceId, @Hash, 'agt_', @Scopes, @Created)
            """, new
        {
            Id = StoreHelpers.RandomId("tok_", 18),
            SourceId = sourceId,
            Hash = StoreHelpers.HashToken(raw),
            Scopes = System.Text.Json.JsonSerializer.Serialize(scopes),
            Created = StoreHelpers.Fmt(now),
        }, cancellationToken: ct));
        return raw;
    }

    public async Task<IReadOnlyList<TokenInfo>> ListForSourceAsync(string sourceId, CancellationToken ct)
    {
        using var conn = StoreHelpers.Open(db);
        var rows = await conn.QueryAsync(new CommandDefinition(
            "SELECT id, source_id, prefix, scopes, created, last_used, revoked, expires_at FROM tokens WHERE source_id = @sourceId ORDER BY created",
            new { sourceId }, cancellationToken: ct));
        return rows.Select(r => new TokenInfo(
            (string)r.id, (string)r.source_id, (string)r.prefix,
            System.Text.Json.JsonSerializer.Deserialize<string[]>((string)r.scopes) ?? [],
            StoreHelpers.Parse((string)r.created),
            StoreHelpers.ParseOpt((string?)r.last_used),
            (long)r.revoked == 1,
            StoreHelpers.ParseOpt((string?)r.expires_at))).ToList();
    }

    public async Task<bool> RevokeAsync(string tokenId, CancellationToken ct)
    {
        using var conn = StoreHelpers.Open(db);
        return await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE tokens SET revoked = 1 WHERE id = @id AND revoked = 0",
            new { id = tokenId }, cancellationToken: ct)) > 0;
    }

    public Task TouchAsync(string tokenId, DateTimeOffset at, CancellationToken ct) => Task.CompletedTask;
}

public sealed class RegistrationTokenStore(SqliteDb db) : IRegistrationTokenStore
{
    public async Task<(string Id, string RawToken)> CreateAsync(string kind, TimeSpan? lifetime,
        string? createdBy, DateTimeOffset now, CancellationToken ct)
    {
        var raw = "reg_" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        var id = StoreHelpers.RandomId("rt_", 18);
        using var conn = StoreHelpers.Open(db);
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO registration_tokens(id, token_hash, kind, created, expires_at, created_by)
            VALUES (@Id, @Hash, @Kind, @Created, @ExpiresAt, @CreatedBy)
            """, new
        {
            Id = id,
            Hash = StoreHelpers.HashToken(raw),
            Kind = kind,
            Created = StoreHelpers.Fmt(now),
            ExpiresAt = lifetime is { } l ? StoreHelpers.Fmt(now.Add(l)) : null,
            CreatedBy = createdBy,
        }, cancellationToken: ct));
        return (id, raw);
    }

    public async Task<RegistrationTokenLookup?> LookupAsync(string rawToken, CancellationToken ct)
    {
        var hash = StoreHelpers.HashToken(rawToken);
        using var conn = StoreHelpers.Open(db);
        var row = await conn.QuerySingleOrDefaultAsync(new CommandDefinition(
            "SELECT id, kind, revoked, expires_at, consumed_at FROM registration_tokens WHERE token_hash = @hash",
            new { hash }, cancellationToken: ct));
        if (row is null) return null;
        return new RegistrationTokenLookup((string)row.id, (string)row.kind, (long)row.revoked == 1,
            StoreHelpers.ParseOpt((string?)row.expires_at), StoreHelpers.ParseOpt((string?)row.consumed_at));
    }

    public async Task MarkConsumedAsync(string id, DateTimeOffset at, CancellationToken ct)
    {
        using var conn = StoreHelpers.Open(db);
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE registration_tokens SET consumed_at = @at WHERE id = @id",
            new { id, at = StoreHelpers.Fmt(at) }, cancellationToken: ct));
    }

    public async Task<bool> RevokeAsync(string id, CancellationToken ct)
    {
        using var conn = StoreHelpers.Open(db);
        return await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE registration_tokens SET revoked = 1 WHERE id = @id AND revoked = 0",
            new { id }, cancellationToken: ct)) > 0;
    }

    public async Task<IReadOnlyList<RegistrationTokenInfo>> ListAsync(CancellationToken ct)
    {
        using var conn = StoreHelpers.Open(db);
        var rows = await conn.QueryAsync(new CommandDefinition(
            "SELECT id, kind, created, expires_at, consumed_at, revoked FROM registration_tokens ORDER BY created DESC",
            cancellationToken: ct));
        return rows.Select(r => new RegistrationTokenInfo(
            (string)r.id, (string)r.kind, StoreHelpers.Parse((string)r.created),
            StoreHelpers.ParseOpt((string?)r.expires_at), StoreHelpers.ParseOpt((string?)r.consumed_at),
            (long)r.revoked == 1)).ToList();
    }
}
