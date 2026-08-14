using System.Collections.Concurrent;

namespace Hyveman.Api;

/// <summary>Fixed-window in-memory rate limiter with Retry-After (PROTOCOL §15).
/// Aggressive limits are a defense, not a normal operating condition.</summary>
public sealed class FixedWindowLimiter(int limit, TimeSpan window)
{
    private readonly object _lock = new();
    private readonly Queue<DateTimeOffset> _hits = new();
    private DateTimeOffset _windowStart = DateTimeOffset.MinValue;

    public (bool Allowed, int Remaining, TimeSpan RetryAfter) TryAcquire(DateTimeOffset now)
    {
        lock (_lock)
        {
            if (_windowStart == DateTimeOffset.MinValue || now - _windowStart >= window)
            {
                _windowStart = now;
                _hits.Clear();
            }
            while (_hits.Count > 0 && now - _hits.Peek() >= window)
                _hits.Dequeue();

            if (_hits.Count >= limit)
            {
                var oldest = _hits.Count > 0 ? _hits.Peek() : now;
                var retryAfter = window - (now - oldest);
                return (false, 0, retryAfter > TimeSpan.Zero ? retryAfter : window);
            }
            _hits.Enqueue(now);
            return (true, limit - _hits.Count, TimeSpan.Zero);
        }
    }
}

/// <summary>Named limiter buckets shared across the process.</summary>
public sealed class RateLimiterRegistry(RateLimitOptions options)
{
    private readonly ConcurrentDictionary<string, FixedWindowLimiter> _limiters = new();

    public static class Buckets
    {
        public const string Global = "global";
        public const string Auth = "auth";
        public const string Registration = "registration";
        public const string Source = "source";
        public const string AgentNetwork = "agent-network";
    }

    private FixedWindowLimiter For(string bucket, int limit, int windowS)
        => _limiters.GetOrAdd(bucket, _ => new FixedWindowLimiter(limit, TimeSpan.FromSeconds(windowS)));

    public (bool Allowed, int Remaining, TimeSpan RetryAfter) AcquireGlobal(DateTimeOffset now)
        => For(Buckets.Global, options.GlobalPerMinute, 60).TryAcquire(now);

    /// <summary>Per-network budget for agent-protocol endpoints
    /// (SECURITY-REVIEW-2026-08-14 M2): bounds the work spent on traffic from
    /// one client network — authenticated or not — before any database lookup
    /// or body read, so unauthenticated floods cannot starve legitimate
    /// agents or the shared budgets.</summary>
    public (bool Allowed, int Remaining, TimeSpan RetryAfter) AcquireAgentNetwork(string networkKey, DateTimeOffset now)
        => For($"{Buckets.AgentNetwork}:{networkKey}", options.AgentNetworkPerMinute, 60).TryAcquire(now);

    public (bool Allowed, int Remaining, TimeSpan RetryAfter) AcquirePerSource(string sourceId, DateTimeOffset now)
        => For($"{Buckets.Source}:{sourceId}", options.PerSourcePerMinute, 60).TryAcquire(now);

    public (bool Allowed, int Remaining, TimeSpan RetryAfter) AcquireRegistration(string networkKey, DateTimeOffset now)
        => For($"{Buckets.Registration}:{networkKey}", options.RegistrationPerMinute, 60).TryAcquire(now);

    public (bool Allowed, int Remaining, TimeSpan RetryAfter) AcquireAuth(string networkKey, DateTimeOffset now)
        => For($"{Buckets.Auth}:{networkKey}", options.AuthPerMinute, 60).TryAcquire(now);
}
