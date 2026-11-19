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
