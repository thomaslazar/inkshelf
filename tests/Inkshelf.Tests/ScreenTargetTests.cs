using Inkshelf;

namespace Inkshelf.Tests;

public class ScreenTargetTests
{
    [Fact]
    public void Parses_css_and_dpr_non_retina()
    {
        // Non-retina (current): cap = CSS size, dpr = 1 (image == page == CSS).
        var (w, h, dpr) = ScreenTarget.FromCookie("769x953x1.875");
        Assert.Equal(ScreenTarget.Retina ? 1442 : 769, w);
        Assert.Equal(ScreenTarget.Retina ? 1787 : 953, h);
        Assert.Equal(ScreenTarget.Retina ? 1.875 : 1.0, dpr, 3);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("769x953")]  // legacy 2-part is handled but not the 3-part path
    public void Missing_or_partial_cookie_is_safe(string? scr)
    {
        var (w, h, dpr) = ScreenTarget.FromCookie(scr);
        Assert.True(w >= 0 && h >= 0 && dpr > 0);
    }
}
