using Dapper;
using Hyveman.Application;
using Hyveman.Domain;

namespace Hyveman.Infrastructure.Sqlite;

public sealed class AuditStore(SqliteDb db) : IAuditStore
{
    public async Task RecordAsync(string? actor, string action, string? targetKind, string? targetId,
        string? detailJson, DateTimeOffset now, CancellationToken ct)
    {
        using var conn = StoreHelpers.Open(db);
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO audit_log(time, actor, action, target_kind, target_id, detail_json)
            VALUES (@Time, @Actor, @Action, @TargetKind, @TargetId, @DetailJson)
            """, new
        {
            Time = StoreHelpers.Fmt(now), Actor = actor, Action = action,
            TargetKind = targetKind, TargetId = targetId, DetailJson = detailJson,
        }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<AuditEntry>> ListAsync(AuditQuery q, CancellationToken ct)
    {
        var sql = "SELECT * FROM audit_log WHERE 1=1";
        var p = new Dictionary<string, object?>();
        if (q.Action is { } action) { sql += " AND action = @Action"; p["Action"] = action; }
        if (q.TargetKind is { } tk) { sql += " AND target_kind = @TargetKind"; p["TargetKind"] = tk; }
        if (q.From is { } from) { sql += " AND time >= @From"; p["From"] = StoreHelpers.Fmt(from); }
        if (q.To is { } to) { sql += " AND time < @To"; p["To"] = StoreHelpers.Fmt(to); }
        if (long.TryParse(q.Cursor, out var cid)) { sql += " AND id < @CId"; p["CId"] = cid; }
        sql += " ORDER BY id DESC LIMIT @Limit";
        p["Limit"] = q.Limit;

        using var conn = StoreHelpers.Open(db);
        var rows = await conn.QueryAsync(new CommandDefinition(sql, p, cancellationToken: ct));
        return rows.Select(r => new AuditEntry(
            StoreHelpers.ToLong(r.id), StoreHelpers.Parse((string)r.time), (string?)r.actor,
            (string)r.action, (string?)r.target_kind, (string?)r.target_id, (string?)r.detail_json)).ToList();
    }
}

public sealed class CredentialBlobStore(SqliteDb db) : ICredentialBlobStore
{
    public async Task<(string Id, byte[] Blob, int KeyVersion)?> GetAsync(string id, CancellationToken ct)
    {
        using var conn = StoreHelpers.Open(db);
        var row = await conn.QuerySingleOrDefaultAsync(new CommandDefinition(
            "SELECT id, blob_encrypted, key_version FROM credentials WHERE id = @id", new { id }, cancellationToken: ct));
        if (row is null) return null;
        return ((string)row.id, (byte[])row.blob_encrypted, (int)StoreHelpers.ToLong(row.key_version));
    }

    public async Task<string> InsertAsync(string kind, string label, byte[] blob, int keyVersion, DateTimeOffset now, CancellationToken ct)
    {
        var id = StoreHelpers.RandomId("crd_", 18);
        using var conn = StoreHelpers.Open(db);
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO credentials(id, kind, label, blob_encrypted, key_version, created)
            VALUES (@Id, @Kind, @Label, @Blob, @KeyVersion, @Created)
            """, new
        {
            Id = id, Kind = kind, Label = label, Blob = blob, KeyVersion = keyVersion,
            Created = StoreHelpers.Fmt(now),
        }, cancellationToken: ct));
        return id;
    }

    public async Task UpdateAsync(string id, byte[] blob, int keyVersion, DateTimeOffset now, CancellationToken ct)
    {
        using var conn = StoreHelpers.Open(db);
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE credentials SET blob_encrypted = @Blob, key_version = @KeyVersion, rotated = @Rotated WHERE id = @Id",
            new { Id = id, Blob = blob, KeyVersion = keyVersion, Rotated = StoreHelpers.Fmt(now) }, cancellationToken: ct));
    }

    public async Task DeleteAsync(string id, CancellationToken ct)
    {
        using var conn = StoreHelpers.Open(db);
        await conn.ExecuteAsync(new CommandDefinition("DELETE FROM credentials WHERE id = @id", new { id }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<CredentialMeta>> ListAsync(CancellationToken ct)
    {
        using var conn = StoreHelpers.Open(db);
        var rows = await conn.QueryAsync(new CommandDefinition(
            "SELECT id, kind, label, created, rotated FROM credentials ORDER BY label", cancellationToken: ct));
        return rows.Select(r => new CredentialMeta((string)r.id, (string)r.kind, (string)r.label,
            StoreHelpers.Parse((string)r.created), StoreHelpers.ParseOpt((string?)r.rotated))).ToList();
    }
}

public sealed class SessionStore(SqliteDb db) : ISessionStore
{
    public async Task<string> CreateAsync(DateTimeOffset now, TimeSpan lifetime, string userId, CancellationToken ct)
    {
        var raw = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var hash = StoreHelpers.HashToken(raw);
        using var conn = StoreHelpers.Open(db);
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO web_sessions(id_hash, user_id, created_at, expires_at, last_seen)
            VALUES (@Hash, @UserId, @Created, @Expires, @LastSeen)
            """, new
        {
            Hash = hash, UserId = userId, Created = StoreHelpers.Fmt(now), Expires = StoreHelpers.Fmt(now.Add(lifetime)),
            LastSeen = StoreHelpers.Fmt(now),
        }, cancellationToken: ct));
        return raw;
    }

    public async Task<WebSession?> ValidateAsync(string sessionId, DateTimeOffset now, TimeSpan lifetime, CancellationToken ct)
    {
        var hash = StoreHelpers.HashToken(sessionId);
        using var conn = StoreHelpers.Open(db);
        var r = await conn.QuerySingleOrDefaultAsync(new CommandDefinition(
            "SELECT * FROM web_sessions WHERE id_hash = @hash", new { hash }, cancellationToken: ct));
        if (r is null) return null;
        var session = new WebSession((string)r.id_hash, (string)r.user_id, StoreHelpers.Parse((string)r.created_at),
            StoreHelpers.Parse((string)r.expires_at), StoreHelpers.Parse((string)r.last_seen),
            StoreHelpers.ParseOpt((string?)r.revoked_at));
        if (session.RevokedAt is not null || session.ExpiresAt <= now)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                "DELETE FROM web_sessions WHERE id_hash = @hash", new { hash }, cancellationToken: ct));
            return null;
        }
        // Sliding expiry: each valid use extends the session by a *fixed*
        // lifetime from now. Recomputing from created_at would compound the
        // window without bound (D6); a fixed window keeps the server-side
        // record aligned with the 14-day cookie.
        var newExpiry = now.Add(lifetime);
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE web_sessions SET expires_at = @Expires, last_seen = @LastSeen WHERE id_hash = @hash",
            new { hash, Expires = StoreHelpers.Fmt(newExpiry), LastSeen = StoreHelpers.Fmt(now) }, cancellationToken: ct));
        return session with { ExpiresAt = newExpiry, LastSeen = now };
    }

    public async Task RevokeAsync(string sessionId, CancellationToken ct)
    {
        var hash = StoreHelpers.HashToken(sessionId);
        using var conn = StoreHelpers.Open(db);
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE web_sessions SET revoked_at = @Now WHERE id_hash = @hash",
            new { hash, Now = StoreHelpers.Fmt(DateTimeOffset.UtcNow) }, cancellationToken: ct));
    }

    public async Task RevokeAllForUserAsync(string userId, CancellationToken ct)
    {
        using var conn = StoreHelpers.Open(db);
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE web_sessions SET revoked_at = @Now WHERE user_id = @UserId AND revoked_at IS NULL",
            new { UserId = userId, Now = StoreHelpers.Fmt(DateTimeOffset.UtcNow) }, cancellationToken: ct));
    }

    public async Task CleanupExpiredAsync(DateTimeOffset now, CancellationToken ct)
    {
        using var conn = StoreHelpers.Open(db);
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM web_sessions WHERE expires_at < @Now OR revoked_at IS NOT NULL",
            new { Now = StoreHelpers.Fmt(now) }, cancellationToken: ct));
    }
}

public sealed class PasskeyStore(SqliteDb db) : IPasskeyStore
{
    public async Task<IReadOnlyList<PasskeyRecord>> ListAsync(CancellationToken ct)
    {
        using var conn = StoreHelpers.Open(db);
        var rows = await conn.QueryAsync(new CommandDefinition(
            "SELECT * FROM passkeys ORDER BY created", cancellationToken: ct));
        return rows.Select(Map).ToList();
    }

    public async Task<IReadOnlyList<PasskeyRecord>> ListByUserAsync(string userId, CancellationToken ct)
    {
        using var conn = StoreHelpers.Open(db);
        var rows = await conn.QueryAsync(new CommandDefinition(
            "SELECT * FROM passkeys WHERE user_id = @userId ORDER BY created",
            new { userId }, cancellationToken: ct));
        return rows.Select(Map).ToList();
    }

    public async Task<PasskeyRecord?> GetAsync(string id, CancellationToken ct)
    {
        using var conn = StoreHelpers.Open(db);
        var r = await conn.QuerySingleOrDefaultAsync(new CommandDefinition(
            "SELECT * FROM passkeys WHERE id = @id", new { id }, cancellationToken: ct));
        return r is null ? null : Map(r);
    }

    public async Task<PasskeyRecord?> GetByCredentialIdAsync(string credentialId, CancellationToken ct)
    {
        using var conn = StoreHelpers.Open(db);
        var r = await conn.QuerySingleOrDefaultAsync(new CommandDefinition(
            "SELECT * FROM passkeys WHERE credential_id = @credentialId", new { credentialId }, cancellationToken: ct));
        return r is null ? null : Map(r);
    }

    public async Task AddAsync(PasskeyRecord p, CancellationToken ct)
    {
        using var conn = StoreHelpers.Open(db);
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO passkeys(id, user_id, name, credential_id, public_key, sign_count, created)
            VALUES (@Id, @UserId, @Name, @CredentialId, @PublicKey, @SignCount, @Created)
            """, new
        {
            p.Id, p.UserId, p.Name, p.CredentialId, p.PublicKey, p.SignCount, Created = StoreHelpers.Fmt(p.Created),
        }, cancellationToken: ct));
    }

    public async Task RemoveAsync(string id, CancellationToken ct)
    {
        using var conn = StoreHelpers.Open(db);
        await conn.ExecuteAsync(new CommandDefinition("DELETE FROM passkeys WHERE id = @id", new { id }, cancellationToken: ct));
    }

    public async Task UpdateSignCountAsync(string id, uint signCount, DateTimeOffset at, CancellationToken ct)
    {
        using var conn = StoreHelpers.Open(db);
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE passkeys SET sign_count = @SignCount, last_used = @At WHERE id = @Id",
            new { Id = id, SignCount = signCount, At = StoreHelpers.Fmt(at) }, cancellationToken: ct));
    }

    public async Task<int> CountAsync(CancellationToken ct)
    {
        using var conn = StoreHelpers.Open(db);
        return (int)await conn.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(*) FROM passkeys", cancellationToken: ct));
    }

    public async Task<int> CountByUserAsync(string userId, CancellationToken ct)
    {
        using var conn = StoreHelpers.Open(db);
        return (int)await conn.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(*) FROM passkeys WHERE user_id = @userId",
            new { userId }, cancellationToken: ct));
    }

    private static PasskeyRecord Map(dynamic r) => new(
        (string)r.id, (string)r.user_id, (string)r.name, (string)r.credential_id, (string)r.public_key,
        (uint)StoreHelpers.ToLong(r.sign_count), StoreHelpers.Parse((string)r.created),
        StoreHelpers.ParseOpt((string?)r.last_used));
}

public sealed class CeremonyStore(SqliteDb db) : ICeremonyStore
{
    public async Task SaveAsync(string challengeHash, string operation, string optionsJson, string? originContext,
        DateTimeOffset now, TimeSpan lifetime, CancellationToken ct)
    {
        using var conn = StoreHelpers.Open(db);
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO webauthn_challenges(challenge_hash, operation, options_json, created_at, expires_at, origin_context)
            VALUES (@Hash, @Operation, @OptionsJson, @Created, @Expires, @OriginContext)
            ON CONFLICT(challenge_hash) DO UPDATE SET
                operation = @Operation, options_json = @OptionsJson, created_at = @Created,
                expires_at = @Expires, origin_context = @OriginContext
            """, new
        {
            Hash = challengeHash, Operation = operation, OptionsJson = optionsJson,
            Created = StoreHelpers.Fmt(now), Expires = StoreHelpers.Fmt(now.Add(lifetime)), OriginContext = originContext,
        }, cancellationToken: ct));
    }

    public async Task<(string OptionsJson, string? OriginContext)?> TakeAsync(string challengeHash, string operation, DateTimeOffset now, CancellationToken ct)
    {
        using var conn = StoreHelpers.Open(db);
        using var tx = conn.BeginTransaction();
        var r = await conn.QuerySingleOrDefaultAsync(new CommandDefinition("""
            SELECT options_json, origin_context FROM webauthn_challenges
            WHERE challenge_hash = @Hash AND operation = @Operation AND expires_at > @Now
            """, new { Hash = challengeHash, Operation = operation, Now = StoreHelpers.Fmt(now) }, tx, cancellationToken: ct));
        if (r is null) return null;
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM webauthn_challenges WHERE challenge_hash = @Hash",
            new { Hash = challengeHash }, tx, cancellationToken: ct));
        tx.Commit();
        return ((string)r.options_json, (string?)r.origin_context);
    }

    public async Task CleanupExpiredAsync(DateTimeOffset now, CancellationToken ct)
    {
        using var conn = StoreHelpers.Open(db);
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM webauthn_challenges WHERE expires_at < @Now",
            new { Now = StoreHelpers.Fmt(now) }, cancellationToken: ct));
    }
}

public sealed class SettingsStore(SqliteDb db) : ISettingsStore
{
    public async Task<string?> GetAsync(string key, CancellationToken ct)
    {
        using var conn = StoreHelpers.Open(db);
        return await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT value FROM settings WHERE key = @key", new { key }, cancellationToken: ct));
    }

    public async Task SetAsync(string key, string value, DateTimeOffset now, CancellationToken ct)
    {
        using var conn = StoreHelpers.Open(db);
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO settings(key, value, updated_at) VALUES (@Key, @Value, @UpdatedAt)
            ON CONFLICT(key) DO UPDATE SET value = @Value, updated_at = @UpdatedAt
            """, new { Key = key, Value = value, UpdatedAt = StoreHelpers.Fmt(now) }, cancellationToken: ct));
    }
}

public sealed class SavedSearchStore(SqliteDb db) : ISavedSearchStore
{
    public async Task<IReadOnlyList<SavedSearchRecord>> ListAsync(CancellationToken ct)
    {
        using var conn = StoreHelpers.Open(db);
        var rows = await conn.QueryAsync(new CommandDefinition(
            "SELECT * FROM saved_searches ORDER BY name", cancellationToken: ct));
        return rows.Select(Map).ToList();
    }

    public async Task<SavedSearchRecord?> GetAsync(string id, CancellationToken ct)
    {
        using var conn = StoreHelpers.Open(db);
        var r = await conn.QuerySingleOrDefaultAsync(new CommandDefinition(
            "SELECT * FROM saved_searches WHERE id = @id", new { id }, cancellationToken: ct));
        return r is null ? null : Map(r);
    }

    public async Task<SavedSearchRecord> CreateAsync(SavedSearchRecord s, CancellationToken ct)
    {
        using var conn = StoreHelpers.Open(db);
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO saved_searches(id, name, filter_json, created_at, updated_at)
            VALUES (@Id, @Name, @FilterJson, @CreatedAt, @UpdatedAt)
            """, new
        {
            s.Id, s.Name, s.FilterJson, CreatedAt = StoreHelpers.Fmt(s.CreatedAt), UpdatedAt = StoreHelpers.Fmt(s.UpdatedAt),
        }, cancellationToken: ct));
        return s;
    }

    public async Task<bool> UpdateAsync(SavedSearchRecord s, DateTimeOffset expectedUpdatedAt, CancellationToken ct)
    {
        using var conn = StoreHelpers.Open(db);
        return await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE saved_searches SET name = @Name, filter_json = @FilterJson, updated_at = @UpdatedAt
            WHERE id = @Id AND updated_at = @Expected
            """, new
        {
            s.Id, s.Name, s.FilterJson, UpdatedAt = StoreHelpers.Fmt(s.UpdatedAt),
            Expected = StoreHelpers.Fmt(expectedUpdatedAt),
        }, cancellationToken: ct)) > 0;
    }

    public async Task DeleteAsync(string id, CancellationToken ct)
    {
        using var conn = StoreHelpers.Open(db);
        await conn.ExecuteAsync(new CommandDefinition("DELETE FROM saved_searches WHERE id = @id", new { id }, cancellationToken: ct));
    }

    private static SavedSearchRecord Map(dynamic r) => new(
        (string)r.id, (string)r.name, (string)r.filter_json,
        StoreHelpers.Parse((string)r.created_at), StoreHelpers.Parse((string)r.updated_at));
}

public sealed class MaintenanceWindowStore(SqliteDb db) : IMaintenanceWindowStore
{
    public async Task<IReadOnlyList<MaintenanceWindowRecord>> ListAsync(CancellationToken ct)
    {
        using var conn = StoreHelpers.Open(db);
        var rows = await conn.QueryAsync(new CommandDefinition(
            "SELECT * FROM maintenance_windows ORDER BY start", cancellationToken: ct));
        return rows.Select(Map).ToList();
    }

    public async Task<MaintenanceWindowRecord?> GetAsync(string id, CancellationToken ct)
    {
        using var conn = StoreHelpers.Open(db);
        var r = await conn.QuerySingleOrDefaultAsync(new CommandDefinition(
            "SELECT * FROM maintenance_windows WHERE id = @id", new { id }, cancellationToken: ct));
        return r is null ? null : Map(r);
    }

    public async Task<MaintenanceWindowRecord> CreateAsync(MaintenanceWindowRecord w, CancellationToken ct)
    {
        using var conn = StoreHelpers.Open(db);
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO maintenance_windows(id, host_id, start, end, reason, created_by, created_at)
            VALUES (@Id, @HostId, @Start, @End, @Reason, @CreatedBy, @CreatedAt)
            """, new
        {
            w.Id, w.HostId, Start = StoreHelpers.Fmt(w.Start), End = StoreHelpers.Fmt(w.End),
            w.Reason, w.CreatedBy, CreatedAt = StoreHelpers.Fmt(w.CreatedAt),
        }, cancellationToken: ct));
        return w;
    }

    public async Task<bool> UpdateAsync(MaintenanceWindowRecord w, DateTimeOffset expectedCreatedAt, CancellationToken ct)
    {
        using var conn = StoreHelpers.Open(db);
        return await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE maintenance_windows SET host_id = @HostId, start = @Start, end = @End, reason = @Reason
            WHERE id = @Id AND created_at = @Expected
            """, new
        {
            w.Id, w.HostId, Start = StoreHelpers.Fmt(w.Start), End = StoreHelpers.Fmt(w.End), w.Reason,
            Expected = StoreHelpers.Fmt(expectedCreatedAt),
        }, cancellationToken: ct)) > 0;
    }

    public async Task DeleteAsync(string id, CancellationToken ct)
    {
        using var conn = StoreHelpers.Open(db);
        await conn.ExecuteAsync(new CommandDefinition("DELETE FROM maintenance_windows WHERE id = @id", new { id }, cancellationToken: ct));
    }

    public async Task<bool> IsInWindowAsync(string? hostId, DateTimeOffset at, CancellationToken ct)
    {
        using var conn = StoreHelpers.Open(db);
        var n = await conn.ExecuteScalarAsync<long>(new CommandDefinition("""
            SELECT COUNT(*) FROM maintenance_windows
            WHERE start <= @At AND end > @At AND (host_id = @HostId OR host_id IS NULL)
            """, new { At = StoreHelpers.Fmt(at), HostId = hostId }, cancellationToken: ct));
        return n > 0;
    }

    public async Task<IReadOnlyList<MaintenanceWindowRecord>> ActiveWindowsAsync(DateTimeOffset at, CancellationToken ct)
    {
        using var conn = StoreHelpers.Open(db);
        var rows = await conn.QueryAsync(new CommandDefinition(
            "SELECT * FROM maintenance_windows WHERE start <= @At AND end > @At",
            new { At = StoreHelpers.Fmt(at) }, cancellationToken: ct));
        return rows.Select(Map).ToList();
    }

    public async Task DeleteExpiredAsync(DateTimeOffset now, CancellationToken ct)
    {
        using var conn = StoreHelpers.Open(db);
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM maintenance_windows WHERE end < @Now", new { Now = StoreHelpers.Fmt(now) }, cancellationToken: ct));
    }

    private static MaintenanceWindowRecord Map(dynamic r) => new(
        (string)r.id, (string?)r.host_id, StoreHelpers.Parse((string)r.start),
        StoreHelpers.Parse((string)r.end), (string?)r.reason, (string?)r.created_by,
        StoreHelpers.Parse((string)r.created_at));
}
