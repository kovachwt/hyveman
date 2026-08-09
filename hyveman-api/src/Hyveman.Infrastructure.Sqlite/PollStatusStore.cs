using Dapper;
using Hyveman.Application;

namespace Hyveman.Infrastructure.Sqlite;

public sealed class PollStatusStore(SqliteDb db) : IPollStatusStore
{
    public async Task<PollStatusRecord?> GetAsync(string hostId, CancellationToken ct)
    {
        using var conn = StoreHelpers.Open(db);
        var r = await conn.QuerySingleOrDefaultAsync(new CommandDefinition(
            "SELECT * FROM poll_status WHERE host_id = @hostId", new { hostId }, cancellationToken: ct));
        if (r is null) return null;
        return new PollStatusRecord((string)r.host_id, StoreHelpers.Parse((string)r.last_poll),
            StoreHelpers.ParseOpt((string?)r.last_success), (string?)r.last_error,
            (int)StoreHelpers.ToLong(r.failures));
    }

    public async Task MarkSuccessAsync(string hostId, DateTimeOffset at, CancellationToken ct)
    {
        using var conn = StoreHelpers.Open(db);
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO poll_status(host_id, last_poll, last_success, last_error, failures)
            VALUES (@HostId, @At, @At, NULL, 0)
            ON CONFLICT(host_id) DO UPDATE SET
                last_poll = @At, last_success = @At, last_error = NULL, failures = 0
            """, new { HostId = hostId, At = StoreHelpers.Fmt(at) }, cancellationToken: ct));
    }

    public async Task MarkFailureAsync(string hostId, DateTimeOffset at, string? error, CancellationToken ct)
    {
        using var conn = StoreHelpers.Open(db);
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO poll_status(host_id, last_poll, last_error, failures)
            VALUES (@HostId, @At, @Error, 1)
            ON CONFLICT(host_id) DO UPDATE SET
                last_poll = @At, last_error = @Error, failures = failures + 1
            """, new { HostId = hostId, At = StoreHelpers.Fmt(at), Error = error is { Length: > 300 } ? error[..300] : error },
            cancellationToken: ct));
    }
}
