using Dapper;
using Hyveman.Application;
using Hyveman.Domain;

namespace Hyveman.Infrastructure.Sqlite;

/// <summary>Web console users (docs/MULTI-USER.md).</summary>
public sealed class UserStore(SqliteDb db) : IUserStore
{
    public async Task<IReadOnlyList<UserRecord>> ListAsync(CancellationToken ct)
    {
        using var conn = StoreHelpers.Open(db);
        var rows = await conn.QueryAsync(new CommandDefinition(
            "SELECT * FROM users ORDER BY name", cancellationToken: ct));
        return rows.Select(Map).ToList();
    }

    public async Task<UserRecord?> GetAsync(string id, CancellationToken ct)
    {
        using var conn = StoreHelpers.Open(db);
        var r = await conn.QuerySingleOrDefaultAsync(new CommandDefinition(
            "SELECT * FROM users WHERE id = @id", new { id }, cancellationToken: ct));
        return r is null ? null : Map(r);
    }

    public async Task<UserRecord?> GetByNameAsync(string name, CancellationToken ct)
    {
        using var conn = StoreHelpers.Open(db);
        var r = await conn.QuerySingleOrDefaultAsync(new CommandDefinition(
            "SELECT * FROM users WHERE name = @name", new { name }, cancellationToken: ct));
        return r is null ? null : Map(r);
    }

    public async Task<UserRecord> CreateAsync(UserRecord user, CancellationToken ct)
    {
        using var conn = StoreHelpers.Open(db);
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO users(id, name, display_name, webauthn_user_handle, disabled, created, created_by)
            VALUES (@Id, @Name, @DisplayName, @Handle, @Disabled, @Created, @CreatedBy)
            """, new
        {
            user.Id, user.Name, user.DisplayName, Handle = user.WebAuthnUserHandle,
            Disabled = user.Disabled ? 1 : 0, Created = StoreHelpers.Fmt(user.Created), user.CreatedBy,
        }, cancellationToken: ct));
        return user;
    }

    public async Task<bool> SetDisabledAsync(string id, bool disabled, CancellationToken ct)
    {
        using var conn = StoreHelpers.Open(db);
        return await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE users SET disabled = @Disabled WHERE id = @id",
            new { id, Disabled = disabled ? 1 : 0 }, cancellationToken: ct)) > 0;
    }

    public async Task DeleteAsync(string id, CancellationToken ct)
    {
        using var conn = StoreHelpers.Open(db);
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM users WHERE id = @id", new { id }, cancellationToken: ct));
    }

    public async Task<int> CountAsync(CancellationToken ct)
    {
        using var conn = StoreHelpers.Open(db);
        return (int)await conn.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(*) FROM users", cancellationToken: ct));
    }

    public async Task<int> CountEnabledAsync(CancellationToken ct)
    {
        using var conn = StoreHelpers.Open(db);
        return (int)await conn.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(*) FROM users WHERE disabled = 0", cancellationToken: ct));
    }

    private static UserRecord Map(dynamic r) => new(
        (string)r.id, (string)r.name, (string?)r.display_name, (string)r.webauthn_user_handle,
        StoreHelpers.ToLong(r.disabled) == 1, StoreHelpers.Parse((string)r.created),
        (string?)r.created_by);
}

/// <summary>Single-use user invitations (docs/MULTI-USER.md); tokens are
/// stored hashed, never plaintext.</summary>
public sealed class InvitationStore(SqliteDb db) : IInvitationStore
{
    public async Task<(string Id, string RawToken)> CreateAsync(string? createdBy, string? forUserId,
        TimeSpan? lifetime, DateTimeOffset now, CancellationToken ct)
    {
        var raw = "inv_" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        var id = StoreHelpers.RandomId("invite_", 18);
        using var conn = StoreHelpers.Open(db);
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO invitations(id, token_hash, created_by, for_user_id, created, expires_at)
            VALUES (@Id, @Hash, @CreatedBy, @ForUserId, @Created, @ExpiresAt)
            """, new
        {
            Id = id,
            Hash = StoreHelpers.HashToken(raw),
            CreatedBy = createdBy,
            ForUserId = forUserId,
            Created = StoreHelpers.Fmt(now),
            ExpiresAt = lifetime is { } l ? StoreHelpers.Fmt(now.Add(l)) : null,
        }, cancellationToken: ct));
        return (id, raw);
    }

    public async Task<InvitationRecord?> LookupAsync(string rawToken, CancellationToken ct)
    {
        var hash = StoreHelpers.HashToken(rawToken);
        using var conn = StoreHelpers.Open(db);
        var r = await conn.QuerySingleOrDefaultAsync(new CommandDefinition(
            "SELECT * FROM invitations WHERE token_hash = @hash", new { hash }, cancellationToken: ct));
        return r is null ? null : Map(r);
    }

    public async Task<bool> MarkConsumedAsync(string id, DateTimeOffset at, CancellationToken ct)
    {
        using var conn = StoreHelpers.Open(db);
        return await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE invitations SET consumed_at = @At WHERE id = @Id AND consumed_at IS NULL",
            new { Id = id, At = StoreHelpers.Fmt(at) }, cancellationToken: ct)) > 0;
    }

    public async Task<bool> RevokeAsync(string id, CancellationToken ct)
    {
        using var conn = StoreHelpers.Open(db);
        return await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE invitations SET revoked = 1 WHERE id = @id AND revoked = 0",
            new { id }, cancellationToken: ct)) > 0;
    }

    public async Task<IReadOnlyList<InvitationRecord>> ListAsync(CancellationToken ct)
    {
        using var conn = StoreHelpers.Open(db);
        var rows = await conn.QueryAsync(new CommandDefinition(
            "SELECT * FROM invitations ORDER BY created DESC", cancellationToken: ct));
        return rows.Select(Map).ToList();
    }

    private static InvitationRecord Map(dynamic r) => new(
        (string)r.id, (string?)r.created_by, (string?)r.for_user_id,
        StoreHelpers.Parse((string)r.created), StoreHelpers.ParseOpt((string?)r.expires_at),
        StoreHelpers.ParseOpt((string?)r.consumed_at), StoreHelpers.ToLong(r.revoked) == 1);
}
