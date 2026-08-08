using Hyveman.Agent.Pipeline;
using Xunit;

namespace Hyveman.Agent.Tests;

/// <summary>
/// Bounded queue tests (AGENT.md §6.3, §16): drop-oldest semantics, exact
/// drop counting, never-blocking producers, FIFO order.
/// </summary>
public class BoundedQueueTests
{
    [Fact]
    public void Adds_And_Takes_In_Fifo_Order()
    {
        var q = new BoundedQueue<string>(10);
        Assert.Equal(0, q.TryAdd("a"));
        Assert.Equal(0, q.TryAdd("b"));
        Assert.Equal(0, q.TryAdd("c"));

        Assert.True(q.TryTake(out var x, TimeSpan.Zero));
        Assert.Equal("a", x);
        Assert.True(q.TryTake(out var y, TimeSpan.Zero));
        Assert.Equal("b", y);
        Assert.True(q.TryTake(out var z, TimeSpan.Zero));
        Assert.Equal("c", z);
        Assert.False(q.TryTake(out _, TimeSpan.Zero));
        Assert.Equal(0, q.Count);
    }

    [Fact]
    public void Drop_Oldest_When_Full_And_Counted_Exactly()
    {
        var q = new BoundedQueue<int>(3);
        Assert.Equal(0, q.TryAdd(1));
        Assert.Equal(0, q.TryAdd(2));
        Assert.Equal(0, q.TryAdd(3));

        Assert.Equal(1, q.TryAdd(4)); // drops 1
        Assert.Equal(1, q.TryAdd(5)); // drops 2
        Assert.Equal(1, q.TryAdd(6)); // drops 3
        Assert.Equal(3, q.Count);

        var items = q.Drain();
        Assert.Equal(new[] { 4, 5, 6 }, items);
    }

    [Fact]
    public void Take_Waits_Up_To_Timeout()
    {
        var q = new BoundedQueue<string>(3);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.False(q.TryTake(out _, TimeSpan.FromMilliseconds(300)));
        sw.Stop();
        Assert.InRange(sw.ElapsedMilliseconds, 250, 1500); // waited (approx) not instant
    }

    [Fact]
    public void Concurrent_Producers_Never_Block_And_Do_Not_Lose_Count()
    {
        var q = new BoundedQueue<int>(64);
        var dropped = 0L;
        var threads = Enumerable.Range(0, 8).Select(t => new Thread(() =>
        {
            for (int i = 0; i < 10_000; i++)
                Interlocked.Add(ref dropped, q.TryAdd(i));
        })).ToList();

        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());

        var drained = q.Drain();
        Assert.Equal(64, q.Capacity);
        Assert.True(drained.Count <= 64);
        // total produced = dropped + consumed (nothing else can vanish)
        Assert.Equal(8 * 10_000L, dropped + drained.Count);
    }

    [Fact]
    public void Drain_Is_Fifo_And_Empties()
    {
        var q = new BoundedQueue<int>(10);
        for (int i = 0; i < 5; i++) q.TryAdd(i);
        var items = q.Drain();
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, items);
        Assert.Equal(0, q.Count);
        Assert.True(q.IsEmpty);
    }
}
