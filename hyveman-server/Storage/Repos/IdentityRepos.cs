using Dapper;
using Microsoft.Data.Sqlite;

namespace Hyveman.Server.Storage.Repos;

public sealed record SourceRow(string Id, string Kind, string Name, string? BootId, string Created);

public sealed class SourceRepository
{
    private readonly SqliteFactory _factory;
    private readonly SqliteWriter _writer;

    public SourceRepository(SqliteFactory factory, SqliteWriter writer)
    {
        _factory = factory;
        _writer = writer;
    }

    public async Task<SourceRow?> FindByKindNameAsync(string kind, string name)
    {
        await using var conn = _factory.OpenReadOnly();
        return await conn.QueryFirstOrDefaultAsync<SourceRow>(
            "SELECT id, kind, name, boot_id, created FROM sources WHERE kind=@kind AND name=@name",
            new { kind, name });
    }

    public async Task<SourceRow?> GetAsync(string id)
    {
        await using var conn = _factory.OpenReadOnly();
        return await conn.QueryFirstOrDefaultAsync<SourceRow>(
            "SELECT id, kind, name, boot_id, created FROM sources WHERE id=@id", new { id });
    }

    public async Task<List<SourceRow>> ListAsync()
    {
        await using var conn = _factory.OpenReadOnly();
        var rows = await conn.QueryAsync<SourceRow>("SELECT id, kind, name, boot_id, created FROM sources ORDER BY name");
        return rows.ToList();
    }

    public Task InsertAsync(SqliteConnection conn, string id, string kind, string name, string? bootId)
        => conn.ExecuteAsync(
            "INSERT INTO sources(id, kind, name, boot_id) VALUES (@id, @kind, @name, @bootId)",
            new { id, kind, name, bootId });

    public Task UpdateBootIdAsync(SqliteConnection conn, string id, string bootId)
        => conn.ExecuteAsync("UPDATE sources SET boot_id=@bootId WHERE id=@id", new { id, bootId });

    public Task DeleteAsync(SqliteConnection conn, string id)
        => conn.ExecuteAsync("DELETE FROM sources WHERE id=@id", new { id });
}

public sealed record TokenRow(string Id, string? SourceId, string TokenHash, string Scopes,
    string Created, string? LastUsed, bool Revoked, string? ConsumedAt, string? ExpiresAt, string? BoundKind);

public sealed class TokenRepository
{
    private readonly SqliteFactory _factory;

    public TokenRepository(SqliteFactory factory) => _factory = factory;

    public Task InsertAsync(SqliteConnection conn, string id, string? sourceId, string tokenHash,
        string scopesJson, string? expiresAt = null, string? boundKind = null)
        => conn.ExecuteAsync("""
            INSERT INTO tokens(id, source_id, token_hash, scopes, expires_at, bound_kind)
            VALUES (@id, @sourceId, @tokenHash, @scopes, @expiresAt, @boundKind)
            """, new { id, sourceId, tokenHash, scopes = scopesJson, expiresAt, boundKind });

    public async Task<TokenRow?> ResolveByHashAsync(string tokenHash)
    {
        await using var conn = _factory.OpenReadOnly();
        return await conn.QueryFirstOrDefaultAsync<TokenRow>(
            """
            SELECT id, source_id, token_hash, scopes, created, last_used, revoked, consumed_at, expires_at, bound_kind
            FROM tokens WHERE token_hash=@tokenHash
            """, new { tokenHash });
    }

    public Task MarkConsumedAsync(SqliteConnection conn, string id)
        => conn.ExecuteAsync("UPDATE tokens SET consumed_at=strftime('%Y-%m-%dT%H:%M:%fZ','now') WHERE id=@id", new { id });

    public Task TouchAsync(SqliteConnection conn, string id, string now)
        => conn.ExecuteAsync("UPDATE tokens SET last_used=@now WHERE id=@id", new { id, now });

    public async Task<List<TokenRow>> ListAsync()
    {
        await using var conn = _factory.OpenReadOnly();
        var rows = await conn.QueryAsync<TokenRow>(
            """
            SELECT id, source_id, token_hash, scopes, created, last_used, revoked, consumed_at, expires_at, bound_kind
            FROM tokens ORDER BY created DESC LIMIT 200
            """);
        return rows.ToList();
    }

    public async Task<bool> RevokeAsync(SqliteConnection conn, string id)
        => await conn.ExecuteAsync("UPDATE tokens SET revoked=1 WHERE id=@id", new { id }) > 0;
}
