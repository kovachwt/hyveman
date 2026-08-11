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

        if (args.Length < 2 || args[1] is not ("reset" or "list-passkeys" or "remove-passkey" or "list-users"))
        {
            await stdout.WriteLineAsync("Usage: hyveman-api auth <reset|list-passkeys|remove-passkey <id>|list-users> [--data-dir <path>]");
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
                    // Full identity reset (docs/MULTI-USER.md §8): users cascade
                    // passkeys; sessions and invitations are cleared too. The
                    // first-run setup wizard re-opens (users table empty).
                    await conn.ExecuteAsync("DELETE FROM invitations; DELETE FROM users; DELETE FROM webauthn_challenges; DELETE FROM web_sessions;");
                    await conn.ExecuteAsync(
                        "INSERT INTO audit_log(time, actor, action) VALUES ($t, 'console', 'auth.reset')",
                        new { t = DateTimeOffset.UtcNow.ToString(TimeFormat.Full) });
                    await stdout.WriteLineAsync("Users, passkeys, sessions and invitations cleared. First-run setup is available again from the trusted network.");
                    return 0;
                }
                case "list-passkeys":
                {
                    var rows = await conn.QueryAsync("""
                        SELECT p.id, p.name, p.created, p.last_used, u.name AS user_name
                        FROM passkeys p JOIN users u ON u.id = p.user_id ORDER BY p.created
                        """);
                    var any = false;
                    foreach (var r in rows)
                    {
                        any = true;
                        await stdout.WriteLineAsync($"{r.id}\t{r.user_name}\t{r.name}\t{(string)r.created}\t{(string?)r.last_used ?? "-"}");
                    }
                    if (!any) await stdout.WriteLineAsync("(no passkeys registered)");
                    return 0;
                }
                case "list-users":
                {
                    var rows = await conn.QueryAsync("SELECT id, name, display_name, disabled, created FROM users ORDER BY name");
                    var any = false;
                    foreach (var r in rows)
                    {
                        any = true;
                        var disabled = (long)r.disabled == 1 ? "disabled" : "enabled";
                        await stdout.WriteLineAsync($"{r.id}\t{r.name}\t{(string?)r.display_name ?? "-"}\t{disabled}\t{(string)r.created}");
                    }
                    if (!any) await stdout.WriteLineAsync("(no users)");
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
                        await stderr.WriteLineAsync("WARNING: this was the final passkey; no login path remains until a user is created (setup wizard or invite).");
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
