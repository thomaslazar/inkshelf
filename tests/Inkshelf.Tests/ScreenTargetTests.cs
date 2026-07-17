using Inkshelf.Convert;

namespace Inkshelf.Tests;

public class ScreenTargetTests
{
    [Fact]
    public void Non_retina_uses_css_size_and_dpr_1()
    {
        var t = ScreenTarget.FromCookie("769x953x1.875", retina: false);
        Assert.Equal(769, t.MaxW);
        Assert.Equal(953, t.MaxH);
        Assert.Equal(1.0, t.Dpr, 3);
    }

    [Fact]
    public void Retina_scales_cap_by_dpr_and_keeps_dpr()
    {
        var t = ScreenTarget.FromCookie("769x953x1.875", retina: true);
        Assert.Equal(1442, t.MaxW); // round(769 * 1.875)
        Assert.Equal(1787, t.MaxH); // round(953 * 1.875)
        Assert.Equal(1.875, t.Dpr, 3);
    }

    [Fact]
    public void Grayscale_flag_is_passed_through()
    {
        Assert.True(ScreenTarget.FromCookie("769x953x1", grayscale: true).Grayscale);
        Assert.False(ScreenTarget.FromCookie("769x953x1").Grayscale);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    public void Missing_or_unparseable_cookie_is_safe(string? scr)
    {
        var t = ScreenTarget.FromCookie(scr);
        Assert.Equal(0, t.MaxW);
        Assert.Equal(0, t.MaxH);
        Assert.Equal(1.0, t.Dpr, 3);
    }

    [Fact]
    public void Legacy_two_part_cookie_still_parses()
    {
        var t = ScreenTarget.FromCookie("769x953");
        Assert.Equal(769, t.MaxW);
        Assert.Equal(953, t.MaxH);
        Assert.Equal(1.0, t.Dpr, 3);
    }

    [Fact]
    public void Non_retina_clamps_oversized_dimensions()
    {
        var t = ScreenTarget.FromCookie("9999x9999x1");
        Assert.Equal(ScreenTarget.MaxDimension, t.MaxW);
        Assert.Equal(ScreenTarget.MaxDimension, t.MaxH);
    }

    [Fact]
    public void Retina_clamps_AFTER_multiplying_by_dpr()
    {
        // 3000 * 2 = 6000 → must clamp to MaxDimension (bug was clamping 3000 first).
        var t = ScreenTarget.FromCookie("3000x3000x2", retina: true);
        Assert.Equal(ScreenTarget.MaxDimension, t.MaxW);
        Assert.Equal(ScreenTarget.MaxDimension, t.MaxH);
    }

    [Fact]
    public void Dpr_is_bounded()
    {
        var t = ScreenTarget.FromCookie("10x10x999", retina: true);
        Assert.Equal(ScreenTarget.MaxDpr, t.Dpr, 3);
    }
}
