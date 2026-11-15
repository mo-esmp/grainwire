using GrainWire.Protocol;
using GrainWire.Server.Internal;

namespace GrainWire.Tests;

public class BatchSequencerTests
{
    private static DeliveryBatch Batch(long number, string epoch = "e1", string room = "counter/r1") => new() { Room = room, Epoch = epoch, Number = number };

    private static (BatchSequencer Sequencer, List<string> Published) Create(int gapTimeoutMs = 150)
    {
        var published = new List<string>();
        var sequencer = new BatchSequencer(batch =>
        {
            lock (published) published.Add($"{batch.Epoch}:{batch.Number}");
            return Task.CompletedTask;
        }, TimeSpan.FromMilliseconds(gapTimeoutMs));
        return (sequencer, published);
    }

    private static async Task<List<string>> Settle(List<string> published, int count, int timeoutMs = 2000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            lock (published)
            {
                if (published.Count >= count) return published.ToList();
            }

            await Task.Delay(10);
        }

        lock (published) return published.ToList();
    }

    [Fact]
    public async Task Batches_that_arrive_out_of_order_are_published_in_order()
    {
        var (sequencer, published) = Create();
        foreach (var number in new long[] { 2, 1, 4, 5, 3 }) sequencer.Accept(Batch(number));
        Assert.Equal(["e1:1", "e1:2", "e1:3", "e1:4", "e1:5"], await Settle(published, 5));
    }

    [Fact]
    public async Task A_batch_that_never_arrives_is_skipped_after_a_short_wait_and_duplicates_are_dropped()
    {
        var (sequencer, published) = Create();
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
        var (sequencer, published) = Create();
        sequencer.Accept(Batch(1));
        sequencer.Accept(Batch(2));
        sequencer.Accept(Batch(1, epoch: "e2"));
        sequencer.Accept(Batch(3));                 // old epoch, late
        sequencer.Accept(Batch(2, epoch: "e2"));
        Assert.Equal(["e1:1", "e1:2", "e2:1", "e2:2"], await Settle(published, 4));
        await Task.Delay(250);
        Assert.Equal(4, published.Count);
    }

    [Fact]
    public async Task Rooms_are_sequenced_independently()
    {
        var (sequencer, published) = Create();
        sequencer.Accept(Batch(2, room: "counter/a"));
        sequencer.Accept(Batch(1, room: "counter/b"));
        Assert.Equal(["e1:1"], await Settle(published, 1, timeoutMs: 60));
        sequencer.Accept(Batch(1, room: "counter/a"));
        Assert.Equal(3, (await Settle(published, 3)).Count);
    }
}
