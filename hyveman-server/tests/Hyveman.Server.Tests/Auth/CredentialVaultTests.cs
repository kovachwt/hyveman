using System.Security.Cryptography;
using Dapper;
using Hyveman.Server.Auth;
using Hyveman.Server.Tests.TestInfra;

namespace Hyveman.Server.Tests.Auth;

/// <summary>Exercises the AES-GCM vault against a real migrated SQLite DB (§12.3, DESIGN §7/§13 #13).</summary>
public sealed class CredentialVaultTests
{
    private static byte[] NewKey() => RandomNumberGenerator.GetBytes(32);

    [Fact]
    public async Task PutAndGet_RoundTrips()
    {
        using var db = await TestDb.CreateAsync();
        var vault = new AesGcmCredentialVault(NewKey(), db.Db);

        await vault.PutSecretAsync("host01-idrac", "idrac", "admin\nHunter2!", "test");
        var secret = await vault.GetSecretAsync("host01-idrac");

        Assert.Equal("admin\nHunter2!", secret);
    }

    [Fact]
    public async Task Upsert_SameLabelOverwritesAndRotatesTimestamp()
    {
        using var db = await TestDb.CreateAsync();
        var vault = new AesGcmCredentialVault(NewKey(), db.Db);

        await vault.PutSecretAsync("webhook", "webhook", "old-secret", "test");
        await vault.PutSecretAsync("webhook", "webhook", "new-secret", "test");

        Assert.Equal("new-secret", await vault.GetSecretAsync("webhook"));
        var meta = await vault.ListAsync();
        var row = Assert.Single(meta);
        Assert.NotNull(row.Rotated);
    }

    [Fact]
    public async Task Rotate_ReencryptsAndKeepsSecretReadable()
    {
        using var db = await TestDb.CreateAsync();
        var vault = new AesGcmCredentialVault(NewKey(), db.Db);
        await vault.PutSecretAsync("telegram", "telegram", "token-v1", "test");

        var ok = await vault.RotateSecretAsync("telegram", "token-v2", "test");

        Assert.True(ok);
        Assert.Equal("token-v2", await vault.GetSecretAsync("telegram"));
    }

    [Fact]
    public async Task Delete_RemovesCredential()
    {
        using var db = await TestDb.CreateAsync();
        var vault = new AesGcmCredentialVault(NewKey(), db.Db);
        await vault.PutSecretAsync("idrac", "idrac", "u\np", "test");

        var deleted = await vault.DeleteSecretAsync("idrac", "test");

        Assert.True(deleted);
        Assert.Null(await vault.GetSecretAsync("idrac"));
        Assert.Empty(await vault.ListAsync());
    }

    [Fact]
    public async Task Get_AbsentLabelReturnsNull()
    {
        using var db = await TestDb.CreateAsync();
        var vault = new AesGcmCredentialVault(NewKey(), db.Db);
        Assert.Null(await vault.GetSecretAsync("does-not-exist"));
    }

    [Fact]
    public async Task WrongKey_CannotDecrypt()
    {
        using var db = await TestDb.CreateAsync();
        var vaultA = new AesGcmCredentialVault(NewKey(), db.Db);
        await vaultA.PutSecretAsync("idrac", "idrac", "secret", "test");

        // A restore onto a machine with a different key K must yield null, not garbage.
        var vaultB = new AesGcmCredentialVault(NewKey(), db.Db);
        Assert.Null(await vaultB.GetSecretAsync("idrac"));
    }

    [Fact]
    public async Task TamperedBlob_CannotDecrypt()
    {
        using var db = await TestDb.CreateAsync();
        var vault = new AesGcmCredentialVault(NewKey(), db.Db);
        await vault.PutSecretAsync("idrac", "idrac", "secret", "test");

        // Flip one byte of the stored ciphertext directly in the DB (GCM tag must fail).
        await db.Db.Writer.WithTransactionAsync(async conn =>
        {
            var blob = (await conn.QueryFirstAsync<byte[]>("SELECT blob_encrypted FROM credentials WHERE label='idrac'")).ToArray();
            blob[^1] ^= 0xFF;
            await conn.ExecuteAsync("UPDATE credentials SET blob_encrypted=@blob WHERE label='idrac'", new { blob });
        });

        Assert.Null(await vault.GetSecretAsync("idrac"));
    }
}
