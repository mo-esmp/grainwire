namespace GrainWire.Bench.Tests;

public class PortBudgetTests
{
    [Fact]
    public void There_is_room_when_the_free_ports_cover_the_connections_and_a_margin()
    {
        Assert.True(PortBudget.IsEnough(rangeSize: 16_384, inUse: 1_000, needed: 12_000));
    }

    [Fact]
    public void Ports_held_in_time_wait_by_the_last_connections_leave_no_room()
    {
        // 12,000 connections were just closed; their ports wait about two minutes before they can be used again.
        Assert.False(PortBudget.IsEnough(rangeSize: 16_384, inUse: 12_000, needed: 12_000));
    }

    [Fact]
    public void The_margin_is_kept_for_the_servers_and_everything_else_on_the_machine()
    {
        Assert.False(PortBudget.IsEnough(rangeSize: 16_384, inUse: 4_384, needed: 12_000));
        Assert.True(PortBudget.IsEnough(rangeSize: 16_384, inUse: 4_384 - PortBudget.Margin, needed: 12_000));
    }
}
