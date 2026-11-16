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
    public void A_limit_of_zero_allows_nothing()
    {
        Assert.False(new PerMinuteLimiter(0).TryReserve("alice", out long _));
    }
}
