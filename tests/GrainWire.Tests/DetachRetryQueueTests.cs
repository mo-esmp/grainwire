using System.Collections.Concurrent;
using System.Diagnostics;
using GrainWire.Server.Internal;

namespace GrainWire.Tests;

/// <summary>Disconnect notices that failed are retried without holding up whoever asked for the retry.</summary>
public class DetachRetryQueueTests
{
    private static readonly ConnectionId s_connection = new("client-0000000000000000", 1);

    [Fact]
    public async Task Starting_a_round_does_not_wait_for_rooms_that_do_not_answer()
    {
        TaskCompletionSource never = new();
        ConcurrentBag<string> told = [];
        DetachRetryQueue queue = new(
            (room, _) =>
            {
                if (room.Id.StartsWith("stuck", StringComparison.Ordinal))
                {
                    return never.Task;
                }

                told.Add(room.Id);
                return Task.CompletedTask;
            },
            budget: TimeSpan.FromMilliseconds(300));

        for (int i = 0; i < 20; i++)
        {
            queue.Add(new RoomAddress("counter", "stuck-" + i), s_connection);
        }

        queue.Add(new RoomAddress("counter", "fine"), s_connection);

        // This is what a lease renewal does: it must come back at once, whatever the rooms are doing.
        Stopwatch watch = Stopwatch.StartNew();
        Task round = queue.Start(CancellationToken.None);
        Assert.True(watch.ElapsedMilliseconds < 100, $"Start took {watch.ElapsedMilliseconds} ms");
        Assert.Same(round, queue.Start(CancellationToken.None));   // one round at a time

        // Every attempt has a time budget and several run at once: 20 stuck rooms take about 3 budgets, not 20.
        await round.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(watch.ElapsedMilliseconds < 3000, $"The round took {watch.ElapsedMilliseconds} ms");
        Assert.Equal(["fine"], told);
        Assert.Equal(20, queue.Count);   // the stuck ones wait for the next round
    }

    [Fact]
    public async Task A_failed_retry_is_kept_and_a_later_round_delivers_it()
    {
        bool roomIsBack = false;
        int attempts = 0;
        DetachRetryQueue queue = new(
            (_, _) =>
            {
                Interlocked.Increment(ref attempts);
                return roomIsBack ? Task.CompletedTask : Task.FromException(new TimeoutException("The room did not answer."));
            },
            budget: TimeSpan.FromSeconds(1));

        Assert.True(queue.Start(CancellationToken.None).IsCompleted);   // nothing to do
        queue.Add(new RoomAddress("counter", "r1"), s_connection);
        await queue.Start(CancellationToken.None);
        Assert.Equal(1, queue.Count);

        roomIsBack = true;
        await queue.Start(CancellationToken.None);
        Assert.Equal(0, queue.Count);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task A_round_takes_a_limited_number_and_leaves_the_rest_for_the_next()
    {
        int attempts = 0;
        DetachRetryQueue queue = new(
            (_, _) =>
            {
                Interlocked.Increment(ref attempts);
                return Task.CompletedTask;
            },
            budget: TimeSpan.FromSeconds(1),
            concurrency: 4,
            perRound: 10);

        for (int i = 0; i < 25; i++)
        {
            queue.Add(new RoomAddress("counter", "r" + i), s_connection);
        }

        await queue.Start(CancellationToken.None);
        Assert.Equal(10, attempts);
        Assert.Equal(15, queue.Count);
    }

    [Fact]
    public async Task When_the_gateway_is_stopping_nothing_is_lost()
    {
        using CancellationTokenSource stopping = new();
        stopping.Cancel();
        int attempts = 0;
        DetachRetryQueue queue = new(
            (_, _) =>
            {
                Interlocked.Increment(ref attempts);
                return Task.CompletedTask;
            },
            budget: TimeSpan.FromSeconds(1));

        queue.Add(new RoomAddress("counter", "r1"), s_connection);
        await queue.Start(stopping.Token);
        Assert.Equal(0, attempts);
        Assert.Equal(1, queue.Count);
    }
}
