using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

// tools/mint-reg-token — insert a registration token directly into the API's
// SQLite DB, mirroring RegistrationTokenStore.CreateAsync (IdentityStores.cs)
// exactly: raw = "reg_" + 48 hex chars, id = "rt_" + 36 hex chars, SHA-256 hex
// hash stored, created in the API's TimeFormat.Full.
//
// Intended for dev / test environments (e.g. staging agent enrollment without
// the web UI). In production the web UI → Sources → "New registration token"
// is the supported path (INSTALL §4.5). The raw token is printed ONCE — paste
// it into the agent's agent.json and delete this output.
//
//   dotnet run --project tools/mint-reg-token -- [--db <path> | --data-dir <dir>]
//                                              [--id <id>] [--kind <kind>]
// Wrapped by tools/mint-reg-token.ps1.

string? db = null, dataDir = null, id = null;
var kind = "windows-agent";
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--db" when i + 1 < args.Length: db = args[++i]; break;
        case "--data-dir" when i + 1 < args.Length: dataDir = args[++i]; break;
        case "--id" when i + 1 < args.Length: id = args[++i]; break;
        case "--kind" when i + 1 < args.Length: kind = args[++i]; break;
        default:
            Console.Error.WriteLine($"error: unknown argument '{args[i]}'");
            return 2;
    }
}

db ??= dataDir is { Length: > 0 } d ? Path.Combine(d, "hyveman.db") : null;
db ??= Environment.GetEnvironmentVariable("HYVEMAN_DATA_DIR") is { Length: > 0 } e
    ? Path.Combine(e, "hyveman.db")
    : null;
db ??= FindDefaultDb();
if (db is null || !File.Exists(db))
{
    Console.Error.WriteLine($"error: database not found ({db ?? "no --db/--data-dir/HYVEMAN_DATA_DIR and no devdata/api/hyveman.db under CWD"})");
    return 2;
}

var raw = "reg_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
var tokenId = id ?? "rt_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(18)).ToLowerInvariant();
var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
var created = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

using var conn = new SqliteConnection($"Data Source={db}");
conn.Open();

using (var exists = conn.CreateCommand())
{
    exists.CommandText = "SELECT 1 FROM registration_tokens WHERE id = @id";
    exists.Parameters.AddWithValue("@id", tokenId);
    if (exists.ExecuteScalar() is not null)
    {
        Console.Error.WriteLine($"error: registration_tokens id '{tokenId}' already exists; pick a different --id");
        return 1;
    }
}

using (var insert = conn.CreateCommand())
{
    insert.CommandText = """
        INSERT INTO registration_tokens(id, token_hash, kind, created, created_by)
        VALUES (@id, @hash, @kind, @created, @createdBy)
        """;
    insert.Parameters.AddWithValue("@id", tokenId);
    insert.Parameters.AddWithValue("@hash", hash);
    insert.Parameters.AddWithValue("@kind", kind);
    insert.Parameters.AddWithValue("@created", created);
    insert.Parameters.AddWithValue("@createdBy", "tools/mint-reg-token");
    insert.ExecuteNonQuery();
}

Console.WriteLine($"Inserted {tokenId} (kind={kind}) into {db}");
Console.WriteLine($"RAW REG TOKEN (use in agent.json, then delete this output): {raw}");
return 0;

static string? FindDefaultDb()
{
    var dir = new DirectoryInfo(Environment.CurrentDirectory);
    for (var i = 0; i < 5 && dir is not null; i++, dir = dir.Parent)
    {
        var candidate = Path.Combine(dir.FullName, "devdata", "api", "hyveman.db");
        if (File.Exists(candidate))
            return candidate;
    }
    return null;
}
