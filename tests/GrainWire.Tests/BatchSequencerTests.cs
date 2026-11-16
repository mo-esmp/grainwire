using GrainWire.Protocol;
using GrainWire.Server.Internal;

namespace GrainWire.Tests;

public class BatchSequencerTests
{
    /// <summary>Epoch "e1" has order 1, "e2" has order 2, and so on.</summary>
    private static DeliveryBatch Batch(long number, string epoch = "e1", string room = "counter/r1", long? order = null) =>
        new() { Room = room, Epoch = epoch, Number = number, EpochOrder = order ?? long.Parse(epoch[1..]) };

    private static (BatchSequencer Sequencer, List<string> Published) Create(int gapTimeoutMs = 150)
    {
        List<string> published = [];
        BatchSequencer sequencer = new(batch =>
        {
            lock (published)
            {
                published.Add($"{batch.Epoch}:{batch.Number}");
            }

            return Task.CompletedTask;
        }, TimeSpan.FromMilliseconds(gapTimeoutMs));
        return (sequencer, published);
    }

    private static async Task<List<string>> Settle(List<string> published, int count, int timeoutMs = 2000)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            lock (published)
            {
                if (published.Count >= count)
                {
                    return [.. published];
                }
            }

            await Task.Delay(10);
        }

        lock (published)
        {
            return [.. published];
        }
    }

    [Fact]
    public async Task Batches_that_arrive_out_of_order_are_published_in_order()
    {
        (BatchSequencer? sequencer, List<string>? published) = Create();
        foreach (long number in new long[] { 2, 1, 4, 5, 3 })
        {
            sequencer.Accept(Batch(number));
        }

        Assert.Equal(["e1:1", "e1:2", "e1:3", "e1:4", "e1:5"], await Settle(published, 5));
    }

    [Fact]
    public async Task A_batch_that_never_arrives_is_skipped_after_a_short_wait_and_duplicates_are_dropped()
    {
        (BatchSequencer? sequencer, List<string>? published) = Create();
        sequencer.Accept(Batch(1));
        sequencer.Accept(Batch(3));
        Assert.Equal(["e1:1"], await Settle(published, 2, timeoutMs: 60));
        Assert.Equal(["e1:1", "e1:3"], await Settle(published, 2));

        sequencer.Accept(Batch(2));   // too late now
        sequencer.Accept(Batch(3));   // duplicate
        sequencer.Accept(Batch(4));
        Assert.Equal(["e1:1", "e1:3", "e1:4"], await Settle(published, 3));
    }

    [Fact]
    public async Task A_new_epoch_starts_from_one_and_late_batches_of_the_old_epoch_are_dropped()
    {
        (BatchSequencer? sequencer, List<string>? published) = Create();
        sequencer.Accept(Batch(1));
        sequencer.Accept(Batch(2));
        sequencer.Accept(Batch(1, epoch: "e2"));
        sequencer.Accept(Batch(3));                 // old epoch, late
        sequencer.Accept(Batch(2, epoch: "e2"));
        Assert.Equal(["e1:1", "e1:2", "e2:1", "e2:2"], await Settle(published, 4));
        await Task.Delay(250);
        Assert.Equal(4, published.Count);
    }

    // An old activation's batch can arrive after the new epoch has started, at a gateway that never saw the old epoch.
    [Fact]
    public async Task A_late_batch_of_an_older_epoch_is_dropped_even_if_that_epoch_was_never_seen()
    {
        (BatchSequencer? sequencer, List<string>? published) = Create();
        sequencer.Accept(Batch(1, epoch: "e2"));
        sequencer.Accept(Batch(1, epoch: "e1"));    // older, and new to this gateway
        sequencer.Accept(Batch(2, epoch: "e2"));    // the current epoch keeps flowing
        Assert.Equal(["e2:1", "e2:2"], await Settle(published, 2));
        await Task.Delay(250);
        Assert.Equal(2, published.Count);
        Assert.Equal(new KnownEpoch("e2", 2), sequencer.Known("counter/r1"));
    }

    [Fact]
    public async Task An_epoch_can_raise_its_own_order_and_is_then_ranked_by_it()
    {
        (BatchSequencer? sequencer, List<string>? published) = Create();
        Assert.Equal(default, sequencer.Known("counter/r1"));
        sequencer.Accept(Batch(1, epoch: "e1", order: 1));
        sequencer.Accept(Batch(2, epoch: "e1", order: 7));
        Assert.Equal(new KnownEpoch("e1", 7), sequencer.Known("counter/r1"));

        sequencer.Accept(Batch(1, epoch: "e5"));    // order 5: older than the raised order
        sequencer.Accept(Batch(1, epoch: "e7"));    // a tie is not newer either
        sequencer.Accept(Batch(1, epoch: "e8"));
        Assert.Equal(["e1:1", "e1:2", "e8:1"], await Settle(published, 3));
        await Task.Delay(250);
        Assert.Equal(3, published.Count);
    }

    // A room that produces batches faster than they can be published must not grow the gateway's memory without limit.
    [Fact]
    public async Task Batches_that_arrive_faster_than_they_are_published_are_dropped_beyond_a_bounded_backlog()
    {
        const int MaxBacklog = 100;
        TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        List<long> published = [];
        List<string> overflows = [];
        BatchSequencer sequencer = new(
            async batch =>
            {
                await gate.Task;   // publishing is stuck
                lock (published)
                {
                    published.Add(batch.Number);
                }
            },
            TimeSpan.FromMilliseconds(150),
            MaxBacklog,
            room =>
            {
                lock (overflows)
                {
                    overflows.Add(room);
                }
            });

        for (long number = 1; number <= 5000; number++)
        {
            sequencer.Accept(Batch(number));
        }

        // At most the backlog, plus the one batch that is being published, is held; the rest is gone.
        Assert.InRange(sequencer.Backlog("counter/r1"), MaxBacklog - 1, MaxBacklog);
        Assert.InRange(sequencer.Dropped, 5000 - MaxBacklog - 1, 5000 - MaxBacklog);
        Assert.Equal(["counter/r1"], overflows);   // reported once, not once per batch

        gate.SetResult();
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (sequencer.Backlog("counter/r1") > 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        await Task.Delay(50);
        long[] delivered;
        lock (published)
        {
            delivered = [.. published];
        }

        Assert.Equal(5000 - sequencer.Dropped, delivered.Length);
        Assert.Equal(delivered.Order(), delivered);   // what was kept is still in order

        // Once publishing has caught up, batches flow again.
        sequencer.Accept(Batch(5001));
        deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            lock (published)
            {
                if (published.Contains(5001))
                {
                    break;
                }
            }

            await Task.Delay(10);
        }

        lock (published)
        {
            Assert.Contains(5001, published);
        }
    }

    [Fact]
    public async Task Rooms_are_sequenced_independently()
    {
        (BatchSequencer? sequencer, List<string>? published) = Create();
        sequencer.Accept(Batch(2, room: "counter/a"));
        sequencer.Accept(Batch(1, room: "counter/b"));
        Assert.Equal(["e1:1"], await Settle(published, 1, timeoutMs: 60));
        sequencer.Accept(Batch(1, room: "counter/a"));
        Assert.Equal(3, (await Settle(published, 3)).Count);
    }
}
