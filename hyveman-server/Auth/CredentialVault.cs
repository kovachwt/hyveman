using System.Security.Cryptography;
using System.Text;

namespace Hyveman.Server.Auth;

/// <summary>
/// Single encrypted-at-rest store for all secrets (iDRAC creds, channel secrets,
/// server.json secret refs — §12.3, DESIGN §13 #13). Blobs are nonce‖ciphertext‖tag (AES-GCM).
/// </summary>
public interface ICredentialVault
{
    /// <summary>Store/overwrite a secret under a stable label. Returns the credential row id.</summary>
    Task<string> PutSecretAsync(string label, string kind, string plaintext, string actor);
    /// <summary>Decrypt a secret by label; null if absent.</summary>
    Task<string?> GetSecretAsync(string label);
    /// <summary>Re-encrypt (rotate) an existing secret, setting rotated=now.</summary>
    Task<bool> RotateSecretAsync(string label, string plaintext, string actor);
    /// <summary>Delete a credential by label.</summary>
    Task<bool> DeleteSecretAsync(string label, string actor);
    Task<List<CredentialMeta>> ListAsync();
    string? GetSecret(string label);   // synchronous (used by OptionsResolver at startup)
}

public sealed record CredentialMeta(string Id, string Kind, string Label, DateTimeOffset Created, DateTimeOffset? Rotated);

/// <summary>AES-GCM vault wrapping the 256-bit server key K (§5.2). Never machine-scoped DPAPI.</summary>
public sealed class AesGcmCredentialVault : ICredentialVault
{
    private readonly ReadOnlyMemory<byte> _key;
    private readonly Storage.Db _db;

    public AesGcmCredentialVault(byte[] key, Storage.Db db)
    {
        _key = key;
        _db = db;
    }

    public async Task<string> PutSecretAsync(string label, string kind, string plaintext, string actor)
    {
        var blob = Encrypt(plaintext);
        var id = Common.Ulid.Prefixed("cred_");
        await _db.Writer.WithTransactionAsync(async conn =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO credentials(id, kind, label, blob_encrypted)
                VALUES (@id, @kind, @label, @blob)
                ON CONFLICT(label) DO UPDATE SET kind=excluded.kind, blob_encrypted=excluded.blob_encrypted, rotated=strftime('%Y-%m-%dT%H:%M:%fZ','now')
                """;
            cmd.Parameters.AddWithValue("@id", id);
            cmd.Parameters.AddWithValue("@kind", kind);
            cmd.Parameters.AddWithValue("@label", label);
            cmd.Parameters.AddWithValue("@blob", blob);
            await cmd.ExecuteNonQueryAsync();
        });
        await _db.Audit.WriteAsync(actor, "credential.upsert", "credentials", label, $"{{\"kind\":\"{kind}\"}}");
        return id;
    }

    public async Task<string?> GetSecretAsync(string label)
        => await _db.Credentials.GetBlobByLabelAsync(label, Decrypt);

    public string? GetSecret(string label)
        => _db.Credentials.GetBlobByLabelSync(label, Decrypt);

    public async Task<bool> RotateSecretAsync(string label, string plaintext, string actor)
    {
        var blob = Encrypt(plaintext);
        var ok = await _db.Writer.WithTransactionAsync(async conn =>
            await Dapper.SqlMapper.ExecuteAsync(conn,
                "UPDATE credentials SET blob_encrypted=@blob, rotated=strftime('%Y-%m-%dT%H:%M:%fZ','now') WHERE label=@label",
                new { blob, label }) > 0);
        if (ok) await _db.Audit.WriteAsync(actor, "credential.rotate", "credentials", label, null);
        return ok;
    }

    public async Task<bool> DeleteSecretAsync(string label, string actor)
    {
        var ok = await _db.Writer.WithTransactionAsync(async conn =>
            await Dapper.SqlMapper.ExecuteAsync(conn, "DELETE FROM credentials WHERE label=@label", new { label }) > 0);
        if (ok) await _db.Audit.WriteAsync(actor, "credential.delete", "credentials", label, null);
        return ok;
    }

    public Task<List<CredentialMeta>> ListAsync() => _db.Credentials.ListAsync();

    private byte[] Encrypt(string plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var pt = Encoding.UTF8.GetBytes(plaintext);
        var ct = new byte[pt.Length];
        var tag = new byte[16];
        using var gcm = new AesGcm(_key.Span, 16);
        gcm.Encrypt(nonce, pt, ct, tag);
        var blob = new byte[12 + ct.Length + 16];
        nonce.CopyTo(blob, 0);
        ct.CopyTo(blob, 12);
        tag.CopyTo(blob, 12 + ct.Length);
        return blob;
    }

    internal string? Decrypt(byte[] blob)
    {
        if (blob.Length < 12 + 16) return null;
        var nonce = blob.AsSpan(0, 12);
        var ctLen = blob.Length - 12 - 16;
        var ct = blob.AsSpan(12, ctLen);
        var tag = blob.AsSpan(12 + ctLen, 16);
        var pt = new byte[ctLen];
        try
        {
            using var gcm = new AesGcm(_key.Span, 16);
            gcm.Decrypt(nonce, ct, tag, pt);
        }
        catch (CryptographicException)
        {
            return null;
        }
        return Encoding.UTF8.GetString(pt);
    }
}
