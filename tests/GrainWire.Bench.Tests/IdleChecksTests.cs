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
    public void Every_client_with_all_its_deliveries_passes()
    {
        IdleChecks.RequireDeliveries([40, 40, 41, 40], expectedEach: 40);
    }

    [Fact]
    public void Deliveries_that_never_arrived_stop_the_run()
    {
        // Two clients, 40 commands, every "said" event dropped by the gateway: the commands all succeeded, nothing arrived.
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => IdleChecks.RequireDeliveries([0, 0], expectedEach: 40));
        Assert.Equal("Only 0 of 80 deliveries arrived in the activity phase; 2 of 2 clients are short. The fan-out was not exercised.", error.Message);
    }

    [Fact]
    public void A_client_that_is_short_stops_the_run_even_when_the_total_adds_up()
    {
        // One client got the deliveries of another twice over; the aggregate (160) hides the receiver that got nothing.
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => IdleChecks.RequireDeliveries([40, 80, 0, 40], expectedEach: 40));
        Assert.Equal("Only 120 of 160 deliveries arrived in the activity phase; 1 of 4 clients are short. The fan-out was not exercised.", error.Message);
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
