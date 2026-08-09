using System.Globalization;
using Microsoft.Data.Sqlite;

// tools/dbquery — SQLite peek tool for a Hyveman server data dir (dev or
// production). Wrapped by tools/query-db.ps1; can also be run directly:
//   dotnet run --project tools/dbquery -- [--db <path> | --data-dir <dir>] [SQL...]
//
// With SQL: runs it and prints `col=val | col=val` rows.
// Without SQL: prints the default inspection set (dev-state / ops dashboard).
//
// DB path resolution (first hit wins):
//   --db <path>            explicit file
//   --data-dir <dir>       <dir>\hyveman.db   (matches the API's --data-dir)
//   env HYVEMAN_DATA_DIR   <env>\hyveman.db
//   walk up from CWD       devdata/api/hyveman.db (dev-stack fallback)

string? db = null, dataDir = null;
var sqlArgs = new List<string>();
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--db" when i + 1 < args.Length:
            db = args[++i];
            break;
        case "--data-dir" when i + 1 < args.Length:
            dataDir = args[++i];
            break;
        case "--":
            for (var j = i + 1; j < args.Length; j++)
                sqlArgs.Add(args[j]);
            i = args.Length;
            break;
        default:
            sqlArgs.Add(args[i]);
            break;
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

using var conn = new SqliteConnection($"Data Source={db}");
conn.Open();

if (sqlArgs.Count > 0)
{
    Query(conn, null, string.Join(" ", sqlArgs));
    return 0;
}

(string Label, string Sql)[] defaultQueries =
{
    ("tables", "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name"),
    ("sources", "SELECT * FROM sources"),
    ("tokens", "SELECT id, source_id, prefix, scopes, revoked, created FROM tokens"),
    ("hosts", "SELECT * FROM hosts"),
    ("agent_status", "SELECT * FROM agent_status"),
    ("vms", "SELECT * FROM vms"),
    ("events (latest 5)", "SELECT source_id, channel, event_id, severity, facility, time, substr(message,1,80) AS msg FROM events ORDER BY id DESC LIMIT 5"),
    ("alerts (latest 10)", "SELECT * FROM alerts ORDER BY last_seen DESC LIMIT 10"),
    ("rules", "SELECT * FROM rules"),
    ("passkeys", "SELECT * FROM passkeys"),
    ("web_sessions", "SELECT * FROM web_sessions"),
    ("logon_stats (latest 10)", "SELECT * FROM logon_stats ORDER BY rowid DESC LIMIT 10"),
    ("audit_log (latest 10)", "SELECT * FROM audit_log ORDER BY id DESC LIMIT 10"),
    ("settings", "SELECT * FROM settings"),
    ("schema_migrations", "SELECT * FROM schema_migrations"),
    ("counts",
     "SELECT (SELECT COUNT(*) FROM events) AS events," +
     " (SELECT COUNT(*) FROM alerts) AS alerts," +
     " (SELECT COUNT(*) FROM health_snapshots) AS health_snapshots," +
     " (SELECT COUNT(*) FROM metrics) AS metrics," +
     " (SELECT COUNT(*) FROM vms) AS vms," +
     " (SELECT COUNT(*) FROM sources) AS sources"),
};

foreach (var (label, sql) in defaultQueries)
    Query(conn, label, sql);

return 0;

// ── helpers ────────────────────────────────────────────────────────────────

/// <summary>Runs one query, printing `col=val | col=val` rows. Per-query
/// failures are reported and don't abort the rest of a default run.</summary>
static void Query(SqliteConnection conn, string? label, string sql)
{
    if (label is not null)
        Console.WriteLine($"== {label} ==");

    try
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        using var r = cmd.ExecuteReader();

        var rows = 0;
        while (r.Read())
        {
            var vals = new List<string>();
            for (var i = 0; i < r.FieldCount; i++)
                vals.Add($"{r.GetName(i)}={Format(r.GetValue(i))}");
            Console.WriteLine(string.Join(" | ", vals));
            rows++;
        }

        if (r.FieldCount > 0)
        {
            if (rows == 0) Console.WriteLine("(0 rows)");
        }
        else
        {
            Console.WriteLine($"(statement executed, {r.RecordsAffected} row(s) affected)");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"ERROR: {ex.Message}");
    }
}

static string Format(object? v) => v switch
{
    null => "",
    byte[] b => $"{b.Length} bytes",
    DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
    DateTimeOffset dto => dto.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture),
    _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? ""
};

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
