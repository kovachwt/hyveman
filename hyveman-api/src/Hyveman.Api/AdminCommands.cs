using Dapper;
using Hyveman.Infrastructure.Sqlite;

namespace Hyveman.Api;

/// <summary>Local administrative commands (API.md §8.3): hyveman-api auth
/// reset | list-passkeys | remove-passkey &lt;id&gt;. There is deliberately no
/// remote recovery path.</summary>
public static class AdminCommands
{
    public static async Task<int> RunAsync(string[] args)
    {
        var dataDir = ResolveDataDir(args);
        var (stdout, stderr) = (Console.Out, Console.Error);

        if (args.Length < 2 || args[1] is not ("reset" or "list-passkeys" or "remove-passkey"))
        {
            await stdout.WriteLineAsync("Usage: hyveman-api auth <reset|list-passkeys|remove-passkey <id>> [--data-dir <path>]");
            return 1;
        }

        try
        {
            var db = new SqliteDb(Path.Combine(dataDir, "hyveman.db"));
            db.Migrate();
            using var conn = db.Open();

            switch (args[1])
            {
                case "reset":
                {
                    await conn.ExecuteAsync("DELETE FROM passkeys; DELETE FROM webauthn_challenges;");
                    await conn.ExecuteAsync(
                        "INSERT INTO audit_log(time, actor, action) VALUES ($t, 'console', 'auth.reset')",
                        new { t = DateTimeOffset.UtcNow.ToString(TimeFormat.Full) });
                    await stdout.WriteLineAsync("Passkeys and ceremony state cleared. First-run setup is available again from the trusted network.");
                    return 0;
                }
                case "list-passkeys":
                {
                    var rows = await conn.QueryAsync("SELECT id, name, created, last_used FROM passkeys ORDER BY created");
                    var any = false;
                    foreach (var r in rows)
                    {
                        any = true;
                        await stdout.WriteLineAsync($"{r.id}\t{r.name}\t{(string)r.created}\t{(string?)r.last_used ?? "-"}");
                    }
                    if (!any) await stdout.WriteLineAsync("(no passkeys registered)");
                    return 0;
                }
                case "remove-passkey":
                {
                    if (args.Length < 3)
                    {
                        await stderr.WriteLineAsync("remove-passkey requires a passkey id (see auth list-passkeys)");
                        return 1;
                    }
                    var count = await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM passkeys");
                    var id = args[2];
                    var removed = await conn.ExecuteAsync("DELETE FROM passkeys WHERE id = @id", new { id });
                    if (removed == 0)
                    {
                        await stderr.WriteLineAsync($"passkey '{id}' not found");
                        return 1;
                    }
                    if (count <= 1)
                        await stderr.WriteLineAsync("WARNING: this was the final passkey; first-run setup is now available again.");
                    await conn.ExecuteAsync(
                        "INSERT INTO audit_log(time, actor, action, target_kind, target_id) VALUES ($t, 'console', 'passkey.removed', 'passkey', $id)",
                        new { t = DateTimeOffset.UtcNow.ToString(TimeFormat.Full), id });
                    await stdout.WriteLineAsync($"passkey '{id}' removed");
                    return 0;
                }
                default:
                    return 1;
            }
        }
        catch (Exception ex)
        {
            await stderr.WriteLineAsync($"Command failed: {ex.Message}");
            return 1;
        }
    }

    private static string ResolveDataDir(string[] args)
    {
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i] == "--data-dir" && i + 1 < args.Length)
                return args[i + 1];
            if (args[i].StartsWith("--data-dir=", StringComparison.Ordinal))
                return args[i]["--data-dir=".Length..];
        }
        return Environment.GetEnvironmentVariable("HYVEMAN_DATA_DIR") ?? "data";
    }
}
