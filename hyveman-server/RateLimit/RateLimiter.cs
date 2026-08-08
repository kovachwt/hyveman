using System.Collections.Concurrent;
using Hyveman.Server.Config;

namespace Hyveman.Server.RateLimit;

/// <summary>
/// In-memory token buckets keyed by source_id (plus "__global__" and "__register__").
/// Sliding-window flavor: capacity = requests per minute, refilled continuously; a separate
/// byte budget per bucket. Over-budget → 429 + Retry-After (PROTOCOL §15, §7.4).
/// </summary>
public sealed class RateLimiter
{
    private sealed record Bucket(double Tokens, double Bytes, long LastRefillMs);

    private readonly ConcurrentDictionary<string, Bucket> _buckets = new();

    public (bool allowed, int retryAfter, long remaining) TryTake(
        string key, ServerOptions.RateLimitConfig cfg, int bodyBytes, bool useGlobal = true)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (useGlobal)
        {
            var global = TryTakeOne("__global__", _globalCfg, bodyBytes, now);
            if (!global.allowed) return global;
            var per = TryTakeOne(key, cfg, bodyBytes, now);
            if (!per.allowed) return per;
            // Return the min remaining of both budgets (requests).
            var rem = Math.Min(global.remaining, per.remaining);
            return (true, 0, rem);
        }
        return TryTakeOne(key, cfg, bodyBytes, now);
    }

    private ServerOptions.RateLimitConfig _globalCfg = new();
    public void SetGlobalConfig(ServerOptions.RateLimitConfig cfg) => _globalCfg = cfg;

    private (bool allowed, int retryAfter, long remaining) TryTakeOne(
        string key, ServerOptions.RateLimitConfig cfg, int bodyBytes, long nowMs)
    {
        var bucket = _buckets.GetOrAdd(key, _ => new Bucket(cfg.RequestsPerMin, cfg.BytesPerMin, nowMs));

        double elapsedS;
        Bucket updated;
        lock (bucket)
        {
            elapsedS = Math.Max(0, (nowMs - bucket.LastRefillMs) / 1000.0);
            var refill = elapsedS * cfg.RequestsPerMin / 60.0;
            var byteRefill = elapsedS * cfg.BytesPerMin / 60.0;
            updated = new Bucket(
                Math.Min(cfg.RequestsPerMin, bucket.Tokens + refill),
                Math.Min(cfg.BytesPerMin, bucket.Bytes + byteRefill),
                nowMs);
            _buckets[key] = updated;
        }

        if (updated.Tokens < 1 || updated.Bytes < bodyBytes)
        {
            // Retry-After = seconds until 1 request token is available.
            var needTokens = 1 - updated.Tokens;
            var needBytes = bodyBytes > updated.Bytes ? bodyBytes - updated.Bytes : 0;
            var secs = Math.Max(
                needTokens / (cfg.RequestsPerMin / 60.0),
                needBytes / (cfg.BytesPerMin / 60.0));
            return (false, Math.Max(1, (int)Math.Ceiling(secs)), 0);
        }

        _buckets[key] = updated with { Tokens = updated.Tokens - 1, Bytes = updated.Bytes - bodyBytes };
        return (true, 0, (long)Math.Floor(updated.Tokens));
    }

    /// <summary>Drop buckets that haven't been used for a while (RateLimitReaper).</summary>
    public void Reap(TimeSpan idle)
    {
        var cutoff = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - (long)idle.TotalMilliseconds;
        foreach (var kv in _buckets)
        {
            if (kv.Value.LastRefillMs < cutoff)
                _buckets.TryRemove(kv.Key, out _);
        }
    }

    public int BucketCount => _buckets.Count;
}
