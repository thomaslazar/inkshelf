using Inkshelf.Convert;

namespace Inkshelf.Tests;

// The name is minted at page render (into a ticket) and derived again on the
// cookie path. One helper, so the two can never hand the reader different names.
public class EpubNameTests
{
    [Fact]
    public void Joins_author_and_title()
        => Assert.Equal("Alan Moore - Watchmen.epub", EpubName.For("Alan Moore", "Watchmen"));

    [Fact]
    public void Falls_back_when_metadata_is_missing()
        => Assert.Equal("Unknown - Untitled.epub", EpubName.For(null, ""));

    [Fact]
    public void Replaces_characters_a_filename_cannot_hold()
        => Assert.Equal("A_B - C_D.epub", EpubName.For("A/B", "C/D"));
}
