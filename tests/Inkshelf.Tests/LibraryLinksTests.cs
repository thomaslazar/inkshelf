using Inkshelf.Pages;

namespace Inkshelf.Tests;

public class LibraryLinksTests
{
    private static LibraryLinks Links(string? filter = null, string? author = null,
        string? series = null, string? sort = null, bool desc = false) =>
        new("lib1", filter, author, series, sort, desc);

    [Fact]
    public void FilterHref_encodes_group_and_id()
    {
        // Compare against the same encoder LibraryLinks uses, so the test doesn't
        // hard-code AbsFilter's internal encoding.
        var expected = $"/library/lib1?filter={Uri.EscapeDataString(Inkshelf.Abs.AbsFilter.Encode("authors", "a1"))}";
        Assert.Equal(expected, Links().FilterHref("authors", "a1"));
    }

    [Fact]
    public void AuthorHref_escapes_name()
    {
        Assert.Equal("/library/lib1?author=A%20%26%20B", Links().AuthorHref("A & B"));
    }

    [Fact]
    public void SeriesHref_strips_sequence_suffix()
    {
        Assert.Equal("/library/lib1?series=Saga", Links().SeriesHref("Saga #3"));
    }

    [Fact]
    public void SeriesHref_keeps_comma_in_name()
    {
        Assert.Equal("/library/lib1?series=Saga%2C%20Part%202", Links().SeriesHref("Saga, Part 2 #1"));
    }

    [Fact]
    public void ListingHref_bare_when_no_facet_or_overrides()
    {
        Assert.Equal("/library/lib1", Links().ListingHref(null, false, 1));
    }

    [Fact]
    public void ListingHref_carries_filter_sort_desc_and_page()
    {
        var url = Links(filter: "authors.YTE=").ListingHref("addedAt", true, 3);
        Assert.Equal("/library/lib1?filter=authors.YTE%3D&sort=addedAt&desc=1&page=3", url);
    }

    [Fact]
    public void ListingHref_omits_page_one()
    {
        Assert.Equal("/library/lib1?sort=addedAt", Links().ListingHref("addedAt", false, 1));
    }

    [Fact]
    public void SortHref_carries_facet_and_resets_page()
    {
        var url = Links(author: "Herbert").SortHref("addedAt");
        Assert.StartsWith("/library/lib1?author=Herbert&sort=addedAt", url);
        Assert.DoesNotContain("page=", url);   // page always resets to 1 on a sort change
    }
}
