namespace GrainWire.Bench.Tests;

public class IdleChecksTests
{
    [Fact]
    public void A_full_population_passes()
    {
        IdleChecks.RequireAll("connections", 3_000, 3_000);
    }

    [Fact]
    public void A_partial_population_stops_the_run_and_says_what_to_do()
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => IdleChecks.RequireAll("connections", 2_871, 3_000));
        Assert.Contains("2,871 of 3,000 connections", error.Message);
        Assert.Contains("--connections", error.Message);
    }

    [Fact]
    public void No_failed_command_passes_and_one_stops_the_run()
    {
        IdleChecks.RequireNoFailures(0);
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => IdleChecks.RequireNoFailures(12));
        Assert.Equal("12 commands failed in the activity phase; the connections were not active.", error.Message);
    }

    [Fact]
    public void Every_client_with_all_its_deliveries_passes_with_none_missing()
    {
        Assert.Equal(0, IdleChecks.RequireDeliveries([40, 40, 41, 40], expectedEach: 40));
    }

    [Fact]
    public void A_small_loss_passes_and_is_counted()
    {
        // The full run at 3,000 connections: two clients were each one event short. The gateway publishes at QoS 0 and skips
        // a batch that comes more than 250 ms late, so a few events can be lost by design.
        long[] received = [.. Enumerable.Repeat(40L, 3_000)];
        received[17] = 39;
        received[2_404] = 39;
        Assert.Equal(2, IdleChecks.RequireDeliveries(received, expectedEach: 40));
    }

    [Fact]
    public void Deliveries_that_never_arrived_stop_the_run()
    {
        // Two clients, 40 commands, every "said" event dropped by the gateway: the commands all succeeded, nothing arrived.
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => IdleChecks.RequireDeliveries([0, 0], expectedEach: 40));
        Assert.Equal("80 of 80 deliveries never arrived in the activity phase; 2 of 2 clients are short, the worst by 40 of 40. At most 0.1% in all and half of any client's are tolerated: the fan-out was not exercised.", error.Message);
    }

    [Fact]
    public void A_client_that_is_short_stops_the_run_even_when_the_total_adds_up()
    {
        // One client got the deliveries of another twice over; the aggregate (160) hides the receiver that got nothing.
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => IdleChecks.RequireDeliveries([40, 80, 0, 40], expectedEach: 40));
        Assert.Equal("40 of 160 deliveries never arrived in the activity phase; 1 of 4 clients are short, the worst by 40 of 40. At most 0.1% in all and half of any client's are tolerated: the fan-out was not exercised.", error.Message);
    }

    [Fact]
    public void The_second_full_run_passes_though_its_loss_fell_on_few_clients()
    {
        // 62 of 120,000 lost, on five clients, the worst 16 of its 40: batches skipped by the gateway come in runs.
        long[] received = [.. Enumerable.Repeat(40L, 3_000)];
        received[3] = 24;
        received[400] = 24;
        received[1_201] = 26;
        received[2_002] = 28;
        received[2_999] = 36;
        Assert.Equal(62, IdleChecks.RequireDeliveries(received, expectedEach: 40));
    }

    [Fact]
    public void A_client_that_lost_half_of_its_deliveries_passes()
    {
        long[] received = [.. Enumerable.Repeat(40L, 3_000)];
        received[9] = 20;
        Assert.Equal(20, IdleChecks.RequireDeliveries(received, expectedEach: 40));
    }

    [Fact]
    public void A_client_that_lost_more_than_half_stops_the_run_though_the_total_is_small()
    {
        // 21 of 120,000 is far under 0.1%, but one client got only 19 of its 40.
        long[] received = [.. Enumerable.Repeat(40L, 3_000)];
        received[9] = 19;
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => IdleChecks.RequireDeliveries(received, expectedEach: 40));
        Assert.StartsWith("21 of 120,000 deliveries never arrived in the activity phase; 1 of 3,000 clients are short, the worst by 21 of 40.", error.Message);
    }

    [Fact]
    public void A_loss_of_more_than_a_thousandth_stops_the_run_though_no_client_is_far_short()
    {
        // 121 clients one short each: 121 of 120,000 is just over 0.1%.
        long[] received = [.. Enumerable.Repeat(40L, 3_000)];
        for (int i = 0; i < 121; i++)
        {
            received[i] = 39;
        }

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => IdleChecks.RequireDeliveries(received, expectedEach: 40));
        Assert.StartsWith("121 of 120,000 deliveries never arrived", error.Message);
    }

    [Fact]
    public void A_loss_of_exactly_a_thousandth_passes()
    {
        long[] received = [.. Enumerable.Repeat(40L, 3_000)];
        for (int i = 0; i < 120; i++)
        {
            received[i] = 39;
        }

        Assert.Equal(120, IdleChecks.RequireDeliveries(received, expectedEach: 40));
    }

    [Theory]
    [InlineData(100_000_000, 100_900_000, true)]    // 0.9% apart
    [InlineData(100_000_000, 99_100_000, true)]
    [InlineData(100_000_000, 101_100_000, false)]   // 1.1% apart
    [InlineData(0, 0, true)]
    public void A_heap_is_stable_when_two_samples_are_within_one_percent(long first, long second, bool stable)
    {
        Assert.Equal(stable, IdleChecks.IsStable(first, second));
    }
}
