using Hyveman.Server.Config;
using Hyveman.Server.RateLimit;

namespace Hyveman.Server.Tests.RateLimit;

/// <summary>Token-bucket semantics (§7.4, PROTOCOL §15): per-key + global budgets, bytes, refill, reaping.</summary>
public sealed class RateLimiterTests
{
    private static ServerOptions.RateLimitConfig Cfg(int reqPerMin, int bytesPerMin) => new()
    {
        RequestsPerMin = reqPerMin,
        BytesPerMin = bytesPerMin,
    };

    [Fact]
    public void AllowsUpToCapacity_ThenDenies()
    {
        var limiter = new RateLimiter();
        var cfg = Cfg(3, 10_000_000);

        Assert.True(limiter.TryTake("src_1", cfg, 0).allowed);
        Assert.True(limiter.TryTake("src_1", cfg, 0).allowed);
        Assert.True(limiter.TryTake("src_1", cfg, 0).allowed);
        var denied = limiter.TryTake("src_1", cfg, 0);
        Assert.False(denied.allowed);
        Assert.True(denied.retryAfter > 0);
    }

    [Fact]
    public void ByteBudget_IsEnforcedIndependently()
    {
        var limiter = new RateLimiter();
        var cfg = Cfg(1000, 100); // 100 bytes/min

        Assert.True(limiter.TryTake("src_1", cfg, 60).allowed);   // 60 used, 40 left
        var denied = limiter.TryTake("src_1", cfg, 60);           // needs 60 > 40
        Assert.False(denied.allowed);
        Assert.Equal(12, denied.retryAfter); // 20 bytes at 100/60 per sec → 12 s
    }

    [Fact]
    public void GlobalBudget_IsSharedAcrossKeys()
    {
        var limiter = new RateLimiter();
        limiter.SetGlobalConfig(Cfg(2, 10_000_000));
        var perKey = Cfg(100, 10_000_000);

        Assert.True(limiter.TryTake("src_a", perKey, 0).allowed);
        Assert.True(limiter.TryTake("src_b", perKey, 0).allowed);
        Assert.False(limiter.TryTake("src_a", perKey, 0).allowed); // global exhausted
    }

    [Fact]
    public void PerKeyBudgets_AreIndependent()
    {
        var limiter = new RateLimiter();
        var cfg = Cfg(1, 10_000_000);

        Assert.True(limiter.TryTake("src_a", cfg, 0).allowed);
        Assert.True(limiter.TryTake("src_b", cfg, 0).allowed); // fresh bucket, own budget
        Assert.False(limiter.TryTake("src_a", cfg, 0).allowed);
    }

    [Fact]
    public void GlobalBudget_CanBeBypassed()
    {
        var limiter = new RateLimiter();
        limiter.SetGlobalConfig(Cfg(0, 0));
        var perKey = Cfg(5, 10_000_000);

        Assert.True(limiter.TryTake("src_1", perKey, 0, useGlobal: false).allowed);
    }

    [Fact]
    public async Task Buckets_RefillOverTime()
    {
        var limiter = new RateLimiter();
        var cfg = Cfg(60, 10_000_000); // 1 token/s

        Assert.True(limiter.TryTake("src_1", cfg, 0).allowed);
        await Task.Delay(1100); // > 1 s refill
        Assert.True(limiter.TryTake("src_1", cfg, 0).allowed);
    }

    [Fact]
    public void Reap_RemovesIdleBuckets()
    {
        var limiter = new RateLimiter();
        var cfg = Cfg(3, 10_000_000);

        limiter.TryTake("src_1", cfg, 0, useGlobal: false);
        limiter.TryTake("src_2", cfg, 0, useGlobal: false);
        Assert.Equal(2, limiter.BucketCount);

        // Negative idle ⇒ cutoff is in the future ⇒ every bucket counts as idle.
        limiter.Reap(TimeSpan.FromMilliseconds(-1));
        Assert.Equal(0, limiter.BucketCount);
    }
}
