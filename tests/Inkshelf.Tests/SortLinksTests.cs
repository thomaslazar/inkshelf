using Inkshelf.Pages;

namespace Inkshelf.Tests;

public class SortLinksTests
{
    [Fact]
    public void Inactive_field_goes_ascending()
    { var (s, d) = SortLinks.Next("addedAt", "media.metadata.title", false); Assert.Equal("addedAt", s); Assert.False(d); }

    [Fact]
    public void Ascending_active_goes_descending()
    { var (s, d) = SortLinks.Next("t", "t", false); Assert.Equal("t", s); Assert.True(d); }

    [Fact]
    public void Descending_active_turns_off()
    { var (s, d) = SortLinks.Next("t", "t", true); Assert.Null(s); Assert.False(d); }

    [Theory]
    [InlineData("t", "t", false, " ↑")]
    [InlineData("t", "t", true, " ↓")]
    [InlineData("t", "x", false, "")]
    public void Arrow_reflects_state(string field, string cur, bool desc, string expected)
        => Assert.Equal(expected, SortLinks.Arrow(field, cur, desc));
}
