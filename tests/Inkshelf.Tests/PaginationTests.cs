using Inkshelf.Pages;

namespace Inkshelf.Tests;

public class PaginationTests
{
    [Theory]
    [InlineData(0, 24, 100, 5, false, true)]   // first page
    [InlineData(4, 24, 100, 5, true, false)]   // last page (96..99)
    [InlineData(2, 24, 100, 5, true, true)]    // middle
    [InlineData(0, 24, 0, 0, false, false)]    // empty
    [InlineData(0, 24, 24, 1, false, false)]   // exactly one page
    public void Pager_math(int page, int limit, int total, int totalPages, bool hasPrev, bool hasNext)
    {
        var p = new Pager(page, limit, total);
        Assert.Equal(totalPages, p.TotalPages);
        Assert.Equal(hasPrev, p.HasPrev);
        Assert.Equal(hasNext, p.HasNext);
        Assert.Equal(page + 1, p.DisplayPage);
    }
}
