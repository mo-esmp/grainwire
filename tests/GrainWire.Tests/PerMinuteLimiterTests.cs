using GrainWire.Server.Internal;

namespace GrainWire.Tests;

public class PerMinuteLimiterTests
{
    [Fact]
    public void Callers_that_arrive_together_cannot_take_more_slots_than_the_limit()
    {
        PerMinuteLimiter limiter = new(10);
        int taken = 0;
        Parallel.For(0, 200, _ =>
        {
            if (limiter.TryReserve("alice", out long _))
            {
                Interlocked.Increment(ref taken);
            }
        });

        Assert.Equal(10, taken);
        Assert.False(limiter.TryReserve("alice", out long _));
        Assert.True(limiter.TryReserve("bob", out long _));   // every key has its own slots
    }

    [Fact]
    public void A_released_slot_can_be_taken_again()
    {
        PerMinuteLimiter limiter = new(2);
        Assert.True(limiter.TryReserve("alice", out long first));
        Assert.True(limiter.TryReserve("alice", out long _));
        Assert.False(limiter.TryReserve("alice", out long _));

        limiter.Release("alice", first);
        Assert.True(limiter.TryReserve("alice", out long _));
        Assert.False(limiter.TryReserve("alice", out long _));

        limiter.Release("nobody", 123);   // unknown keys are ignored
    }

    [Fact]
    public void Keys_with_nothing_left_in_their_window_are_forgotten()
    {
        long now = 1_000_000;
        PerMinuteLimiter limiter = new(3, () => now);
        for (int i = 0; i < 10_000; i++)
        {
            Assert.True(limiter.TryReserve("player-" + i, out long _));
        }

        Assert.True(limiter.TryReserve("released", out long ticket));
        limiter.Release("released", ticket);

        limiter.Sweep();
        Assert.Equal(10_000, limiter.KeyCount);   // still inside the minute; only the released key is gone

        now += 61_000;
        Assert.True(limiter.TryReserve("recent", out long _));
        limiter.Sweep();
        Assert.Equal(1, limiter.KeyCount);

        // A forgotten key starts again with all its slots.
        Assert.True(limiter.TryReserve("player-1", out long _));
        Assert.True(limiter.TryReserve("player-1", out long _));
        Assert.True(limiter.TryReserve("player-1", out long _));
        Assert.False(limiter.TryReserve("player-1", out long _));
    }

    [Fact]
    public void Sweeping_while_slots_are_taken_never_loses_a_slot()
    {
        PerMinuteLimiter limiter = new(5);
        using CancellationTokenSource stop = new();
        Task sweeper = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                limiter.Sweep();
            }
        });

        // Each key is taken and given back many times; if a sweep ever threw away a window that had just been
        // handed out, the slot taken in it would be invisible and the count below would go over the limit.
        Parallel.For(0, 200, i =>
        {
            string key = "key-" + (i % 20);
            for (int round = 0; round < 200; round++)
            {
                if (limiter.TryReserve(key, out long ticket))
                {
                    limiter.Release(key, ticket);
                }
            }
        });

        stop.Cancel();
        sweeper.Wait();

        int taken = 0;
        while (limiter.TryReserve("key-0", out long _))
        {
            taken++;
        }

        Assert.Equal(5, taken);
    }

    [Fact]
    public void A_limit_of_zero_allows_nothing()
    {
        Assert.False(new PerMinuteLimiter(0).TryReserve("alice", out long _));
    }
}
