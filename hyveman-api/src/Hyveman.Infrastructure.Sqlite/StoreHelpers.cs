using System.Security.Cryptography;
using System.Text;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Hyveman.Infrastructure.Sqlite;

/// <summary>Shared helpers for SQLite repositories.</summary>
public static class StoreHelpers
{
    /// <summary>SHA-256 hex of a raw token; the database never stores the raw
    /// value (DESIGN §5.1, API.md §6.1).</summary>
    public static string HashToken(string rawToken) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken))).ToLowerInvariant();

    /// <summary>Cryptographically random id in the given alphabet (hex).</summary>
    public static string RandomId(string prefix, int bytes) =>
        prefix + Convert.ToHexString(RandomNumberGenerator.GetBytes(bytes)).ToLowerInvariant();

    public static string Fmt(DateTimeOffset dt) => dt.ToUniversalTime().ToString(TimeFormat.Full);

    public static DateTimeOffset Parse(string s) =>
        DateTimeOffset.ParseExact(s, TimeFormat.Full, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal);

    public static DateTimeOffset? ParseOpt(string? s) => s is null ? null : Parse(s);

    public static long ToLong(object? v) => v switch
    {
        long l => l,
        int i => i,
        _ => 0,
    };

    public static double ToDouble(object? v) => v switch
    {
        double d => d,
        long l => l,
        int i => i,
        _ => 0,
    };

    public static SqliteConnection Open(SqliteDb db) => db.Open();
}
