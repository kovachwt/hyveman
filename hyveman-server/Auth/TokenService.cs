using System.Collections.Concurrent;
using Hyveman.Server.Common;
using Hyveman.Server.Storage;
using Hyveman.Server.Storage.Repos;

namespace Hyveman.Server.Auth;

public enum TokenResolveOutcome
{
    Ok,
    Invalid,      // unknown / malformed hash / expired
    Revoked,
    Consumed,     // 410
    UnknownSource // source deleted → 404
}

public sealed record TokenResolution(TokenResolveOutcome Outcome, TokenRow? Token, SourceRow? Source, string? ErrorCode);

public interface ITokenService
{
    Task<TokenResolution> ResolveAsync(string rawToken);
}

/// <summary>
/// Agent token lifecycle (§12.1, §7.3). Constant-time lookup by SHA-256 hash;
/// raw tokens never stored. last_used updates are batched to avoid a write per request.
/// </summary>
public sealed class TokenService : ITokenService
{
    private readonly Db _db;
    private readonly ConcurrentQueue<(string tokenId, string now)> _touchQueue = new();
    private readonly System.Threading.Timer _flushTimer;

    public TokenService(Db db, TimeProvider? timeProvider = null)
    {
        _db = db;
        _flushTimer = new System.Threading.Timer(_ => FlushTouches().GetAwaiter().GetResult(), null,
            TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    public async Task<TokenResolution> ResolveAsync(string rawToken)
    {
        if (string.IsNullOrEmpty(rawToken)) return new(TokenResolveOutcome.Invalid, null, null, "token_invalid");
        var row = await _db.Tokens.ResolveByHashAsync(TokenHasher.Hash(rawToken));
        if (row is null) return new(TokenResolveOutcome.Invalid, null, null, "token_invalid");
        if (row.Revoked) return new(TokenResolveOutcome.Revoked, row, null, "token_revoked");
        if (row.ConsumedAt is not null) return new(TokenResolveOutcome.Consumed, row, null, "token_consumed");
        if (row.ExpiresAt is not null
            && (!WireTime.TryParseUtc(row.ExpiresAt, out var exp) || exp <= DateTimeOffset.UtcNow))
            return new(TokenResolveOutcome.Invalid, row, null, "token_invalid"); // expired → invalid; agent re-registers

        SourceRow? source = null;
        if (row.SourceId is not null)
        {
            source = await _db.Sources.GetAsync(row.SourceId);
            if (source is null) return new(TokenResolveOutcome.UnknownSource, row, null, "unknown_source");
        }
        _touchQueue.Enqueue((row.Id, WireTime.NowMs()));
        return new(TokenResolveOutcome.Ok, row, source, null);
    }

    private async Task FlushTouches()
    {
        if (_touchQueue.IsEmpty) return;
        var batch = new List<(string id, string now)>();
        while (_touchQueue.TryDequeue(out var item)) batch.Add(item);
        if (batch.Count == 0) return;
        try
        {
            await _db.Writer.WithTransactionAsync(async conn =>
            {
                foreach (var (id, now) in batch)
                    await _db.Tokens.TouchAsync(conn, id, now);
            });
        }
        catch (Exception ex)
        {
            // Best-effort; never fail requests on hygiene writes.
            Serilog.Log.Warning(ex, "Failed to flush token last_used updates");
            foreach (var item in batch) _touchQueue.Enqueue(item);
        }
    }
}
