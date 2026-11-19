namespace GrainWire.Bench.Tests;

public class SlopeTests
{
    [Fact]
    public void The_slope_is_the_growth_per_added_connection()
    {
        Fit fit = Slope.Between(3_000, 130_000_000, 12_000, 400_000_000);
        Assert.Equal(30_000, fit.PerConnection, precision: 6);
        Assert.Equal(40_000_000, fit.Fixed, precision: 3);
    }

    [Fact]
    public void A_cost_that_does_not_grow_gives_a_slope_of_zero()
    {
        Fit fit = Slope.Between(3_000, 50_000_000, 12_000, 50_000_000);
        Assert.Equal(0, fit.PerConnection, precision: 6);
        Assert.Equal(50_000_000, fit.Fixed, precision: 3);
    }

    [Fact]
    public void The_order_of_the_two_points_does_not_matter()
    {
        Assert.Equal(Slope.Between(3_000, 130_000_000, 12_000, 400_000_000), Slope.Between(12_000, 400_000_000, 3_000, 130_000_000));
    }

    [Fact]
    public void Two_runs_at_the_same_count_cannot_give_a_slope()
    {
        Assert.Throws<ArgumentException>(() => Slope.Between(3_000, 1, 3_000, 2));
    }
}
