using ChatSample.Server.Data;

namespace GrainWire.Tests;

public class ChatStoreTests
{
    [Theory]
    [InlineData(" Alice ", "alice")]
    [InlineData("bob_1.x-y", "bob_1.x-y")]
    [InlineData("ab", null)]
    [InlineData("a-name-that-is-25-chars-x", null)]
    [InlineData("bad name", null)]
    [InlineData("élan", null)]
    [InlineData(null, null)]
    public void Usernames_are_normalized_or_refused(string? raw, string? expected) =>
        Assert.Equal(expected, Rules.NormalizeUsername(raw));

    [Fact]
    public void Text_title_and_password_limits()
    {
        Assert.Equal("hi", Rules.CleanText("  hi \n"));
        Assert.Null(Rules.CleanText("   "));
        Assert.NotNull(Rules.CleanText(new string('x', 2000)));
        Assert.Null(Rules.CleanText(new string('x', 2001)));
        Assert.Null(Rules.CleanTitle(new string('x', 61)));
        Assert.False(Rules.IsValidPassword("1234567"));
        Assert.True(Rules.IsValidPassword("12345678"));
        Assert.Equal(Rules.DirectKey("bob", "alice"), Rules.DirectKey("alice", "bob"));
        Assert.Equal("alice:bob", Rules.DirectKey("bob", "alice"));
    }
}
