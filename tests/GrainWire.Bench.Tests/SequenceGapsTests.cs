namespace GrainWire.Bench.Tests;

public class SequenceGapsTests
{
    [Fact]
    public void Events_in_order_leave_no_gap()
    {
        SequenceGaps gaps = new();
        Assert.Equal(0, gaps.Observe("bench/a", 5));
        Assert.Equal(0, gaps.Observe("bench/a", 6));
        Assert.Equal(0, gaps.Observe("bench/a", 7));
        Assert.Equal(0, gaps.Total);
    }

    [Fact]
    public void Skipped_numbers_are_counted_as_a_gap()
    {
        SequenceGaps gaps = new();
        gaps.Observe("bench/a", 5);
        gaps.Observe("bench/a", 6);
        Assert.Equal(2, gaps.Observe("bench/a", 9));   // 7 and 8 never arrived
        Assert.Equal(1, gaps.Observe("bench/a", 11));  // 10
        Assert.Equal(3, gaps.Total);
    }

    [Fact]
    public void Each_room_has_its_own_numbers()
    {
        SequenceGaps gaps = new();
        gaps.Observe("bench/a", 5);
        gaps.Observe("bench/b", 40);
        Assert.Equal(0, gaps.Observe("bench/a", 6));
        Assert.Equal(1, gaps.Observe("bench/b", 42));
        Assert.Equal(1, gaps.Total);
    }

    [Fact]
    public void The_first_event_of_a_room_counts_nothing_because_what_came_before_is_not_known()
    {
        SequenceGaps gaps = new();
        Assert.Equal(0, gaps.Observe("bench/a", 17));
        Assert.Equal(0, gaps.Total);
    }

    [Fact]
    public void A_number_seen_again_or_older_counts_nothing()
    {
        SequenceGaps gaps = new();
        gaps.Observe("bench/a", 5);
        Assert.Equal(0, gaps.Observe("bench/a", 5));
        Assert.Equal(0, gaps.Observe("bench/a", 3));
        Assert.Equal(0, gaps.Observe("bench/a", 6));
        Assert.Equal(0, gaps.Total);
    }
}
