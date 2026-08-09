using System.Security.Cryptography;
using Dapper;
using Hyveman.Application;

namespace Hyveman.Infrastructure.Sqlite;

/// <summary>Accepted-on-first-use iDRAC certificate pins (API.md §9.1). The
/// pin set is per host and lives in SQLite so it survives restarts; clearing
/// a pin requires an operator action (API endpoint or DB surgery).</summary>
public sealed class IdracCertStore(SqliteDb db) : IIdracCertStore
{
    public async Task<IdracCertPin?> GetPinAsync(string hostId, CancellationToken ct)
    {
        using var conn = StoreHelpers.Open(db);
        var r = await conn.QuerySingleOrDefaultAsync(new CommandDefinition(
            "SELECT host_id, fingerprint, cert_der, accepted_at FROM idrac_trusted_certs WHERE host_id = @HostId",
            new { HostId = hostId }, cancellationToken: ct));
        if (r is null) return null;
        return new IdracCertPin((string)r.host_id, (string)r.fingerprint,
            (byte[])r.cert_der, StoreHelpers.Parse((string)r.accepted_at));
    }

    public async Task<string?> GetFingerprintAsync(string hostId, CancellationToken ct)
    {
        using var conn = StoreHelpers.Open(db);
        return await conn.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
            "SELECT fingerprint FROM idrac_trusted_certs WHERE host_id = @HostId",
            new { HostId = hostId }, cancellationToken: ct));
    }

    public async Task SetAsync(string hostId, byte[] certDer, string fingerprint, DateTimeOffset at, CancellationToken ct)
    {
        using var conn = StoreHelpers.Open(db);
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO idrac_trusted_certs(host_id, fingerprint, cert_der, accepted_at)
            VALUES (@HostId, @Fingerprint, @CertDer, @At)
            ON CONFLICT(host_id) DO UPDATE SET
                fingerprint = @Fingerprint, cert_der = @CertDer, accepted_at = @At
            """, new { HostId = hostId, Fingerprint = fingerprint, CertDer = certDer, At = StoreHelpers.Fmt(at) },
            cancellationToken: ct));
    }

    public async Task DeleteAsync(string hostId, CancellationToken ct)
    {
        using var conn = StoreHelpers.Open(db);
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM idrac_trusted_certs WHERE host_id = @HostId",
            new { HostId = hostId }, cancellationToken: ct));
    }
}
