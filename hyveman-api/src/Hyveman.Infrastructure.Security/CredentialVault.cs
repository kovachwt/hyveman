using System.Security.Cryptography;
using Hyveman.Application;
using Hyveman.Domain;

namespace Hyveman.Infrastructure.Security;

/// <summary>Credential vault (API.md §10.1, DESIGN §7): AES-GCM encryption with
/// a fresh 12-byte nonce per value; the database stores nonce‖ciphertext‖tag,
/// key version and metadata. The key K lives in the protected data directory
/// and is loaded once at startup; a vault failure must never make log
/// ingestion silently discard events (callers surface readiness separately).</summary>
public sealed class CredentialVault(ICredentialBlobStore store, string keyPath, IClock clock) : ICredentialVault
{
    private const int KeyVersion = 1;
    private readonly Lazy<byte[]> _key = new(() => LoadOrCreateKey(keyPath));

    private byte[] Key => _key.Value;

    public async Task<string> StoreAsync(string kind, string label, string plaintextJson, CancellationToken ct)
    {
        var now = clock.UtcNow;
        return await store.InsertAsync(kind, label, Encrypt(plaintextJson), KeyVersion, now, ct);
    }

    public async Task<string?> LoadAsync(string id, CancellationToken ct)
    {
        var row = await store.GetAsync(id, ct);
        if (row is null) return null;
        return Decrypt(row.Value.Blob);
    }

    public async Task UpdateAsync(string id, string plaintextJson, CancellationToken ct)
    {
        await store.UpdateAsync(id, Encrypt(plaintextJson), KeyVersion, clock.UtcNow, ct);
    }

    public Task DeleteAsync(string id, CancellationToken ct) => store.DeleteAsync(id, ct);

    public Task<IReadOnlyList<CredentialMeta>> ListAsync(CancellationToken ct) => store.ListAsync(ct);

    private byte[] Encrypt(string plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plain = System.Text.Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(Key, 16);
        aes.Encrypt(nonce, plain, cipher, tag);
        var result = new byte[nonce.Length + cipher.Length + tag.Length];
        Buffer.BlockCopy(nonce, 0, result, 0, 12);
        Buffer.BlockCopy(cipher, 0, result, 12, cipher.Length);
        Buffer.BlockCopy(tag, 0, result, 12 + cipher.Length, 16);
        return result;
    }

    private string Decrypt(byte[] blob)
    {
        if (blob.Length < 12 + 16) throw new CryptographicException("vault blob too short");
        var nonce = blob.AsSpan(0, 12);
        var tag = blob.AsSpan(blob.Length - 16, 16);
        var cipher = blob.AsSpan(12, blob.Length - 12 - 16);
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(Key, 16);
        aes.Decrypt(nonce, cipher, tag, plain);
        return System.Text.Encoding.UTF8.GetString(plain);
    }

    private static byte[] LoadOrCreateKey(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        if (File.Exists(path))
        {
            var key = File.ReadAllBytes(path);
            if (key.Length != 32)
                throw new CryptographicException($"vault key at {path} must be 32 bytes (got {key.Length})");
            return key;
        }
        var fresh = RandomNumberGenerator.GetBytes(32);
        File.WriteAllBytes(path, fresh);
        RestrictFile(path);
        return fresh;
    }

    private static void RestrictFile(string path)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception)
        {
            // best-effort ACL; the data directory policy remains the operator's duty
        }
    }

    /// <summary>Verifies the key file is present/valid; used by the readiness
    /// check. Does not touch the database.</summary>
    public void CheckKey() => _ = Key;
}
