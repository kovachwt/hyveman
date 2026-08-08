using Hyveman.Agent.Pipeline;
using Xunit;

namespace Hyveman.Agent.Tests;

/// <summary>
/// Property-style tests for the two spool caps (AGENT.md §4.1, §19.A):
/// "free bytes never below min_free_bytes after any accepted write;
/// total never above max_bytes" (random batch sizes).
/// </summary>
public class SpoolCapsTests
{
    [Theory]
    [InlineData(1000, 5000, 12345)]
    [InlineData(1024 * 1024, 5L * 1024 * 1024 * 1024, 123456789)]
    public void Invariant_Holds_Over_Random_Writes(long maxBytes, long minFreeBytes, int seed)
    {
        var caps = new SpoolCaps(maxBytes, minFreeBytes);
        var rnd = new Random(seed);

        long total = 0;
        long free = minFreeBytes + rnd.Next(0, 10_000_000); // start above the floor

        for (int i = 0; i < 5000; i++)
        {
            var write = rnd.Next(1, 200_000);

            if (caps.WouldAllow(total, free, write))
            {
                total += write;
                free -= write;
            }
            else
            {
                // Simulate drop-oldest: free up space until the write fits or
                // nothing is left to drop.
                while (total > 0 && !caps.WouldAllow(total, free, write))
                {
                    var freed = rnd.Next(1, (int)Math.Min(total, 300_000) + 1);
                    total -= freed;
                    free += freed;
                }
                // If we ran out of spool to drop, the write must be rejected —
                // and the invariant must still hold.
            }

            Assert.True(total <= maxBytes, "total must never exceed max_bytes");
            Assert.True(free >= minFreeBytes, "free must never drop below min_free_bytes");
        }
    }

    [Fact]
    public void Write_Rejected_When_Total_Exceeds_Max()
    {
        var caps = new SpoolCaps(maxBytes: 1000, minFreeBytes: 100);
        Assert.True(caps.WouldAllow(900, 1000, 100));
        Assert.False(caps.WouldAllow(901, 1000, 100)); // 1001 > 1000
    }

    [Fact]
    public void Write_Rejected_When_Free_Would_Drop_Below_Floor()
    {
        var caps = new SpoolCaps(maxBytes: 10_000, minFreeBytes: 1000);
        Assert.True(caps.WouldAllow(0, 1500, 500));
        Assert.False(caps.WouldAllow(0, 1499, 500)); // 999 < 1000
    }

    [Fact]
    public void Both_Caps_Checked_Together()
    {
        var caps = new SpoolCaps(maxBytes: 10_000, minFreeBytes: 1000);
        // Total ok, free not.
        Assert.False(caps.WouldAllow(0, 1100, 101));
        // Free ok, total not.
        Assert.False(caps.WouldAllow(9900, 1_000_000, 101));
        // Both ok.
        Assert.True(caps.WouldAllow(9900, 2000, 100));
    }

    [Fact]
    public void Zero_Byte_Write_Always_Allowed_When_Within_Caps()
    {
        var caps = new SpoolCaps(maxBytes: 100, minFreeBytes: 50);
        Assert.True(caps.WouldAllow(100, 50, 0));
    }
}
