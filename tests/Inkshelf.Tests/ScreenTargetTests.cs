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

    [Fact]
    public void An_override_beats_a_perfectly_good_probe()
    {
        var t = ScreenTarget.FromCookie("769x953x1.875", retina: true, over: new ScreenOverride(1000, 2000, 2));
        Assert.Equal(1000, t.MaxW);
        Assert.Equal(2000, t.MaxH);
        Assert.Equal(2, t.Dpr);
    }

    [Fact]
    public void An_override_works_with_no_probe_at_all()
    {
        // The whole point: FromCookie used to return (0,0,1) the moment the cookie
        // was missing and never look further, so there was no cap — no downscaling,
        // and SpreadMode.Fit had no box to letterbox a spread onto.
        var t = ScreenTarget.FromCookie(null, over: new ScreenOverride(1000, 2000, 1));
        Assert.Equal(1000, t.MaxW);
        Assert.Equal(2000, t.MaxH);
    }

    [Fact]
    public void An_override_honours_retina_by_converting_at_the_css_size()
    {
        // The entered numbers are physical pixels. retina off means the same thing here
        // as on the probe path — convert at the CSS size, dpr 1 — so the page lays out
        // identically with a quarter of the pixels. If this were ignored, retina would
        // be an inert control whenever an override was set.
        var on = ScreenTarget.FromCookie(null, retina: true, over: new ScreenOverride(1000, 2000, 2));
        Assert.Equal((1000, 2000, 2.0), (on.MaxW, on.MaxH, on.Dpr));

        var off = ScreenTarget.FromCookie(null, retina: false, over: new ScreenOverride(1000, 2000, 2));
        Assert.Equal((500, 1000, 1.0), (off.MaxW, off.MaxH, off.Dpr));
    }

    [Fact]
    public void An_override_is_clamped_to_the_same_bounds_as_the_probe()
    {
        // retina: true so the entered numbers are used as-is — with retina off they are
        // divided by the ratio, which is a different test (see the retina case above).
        var t = ScreenTarget.FromCookie(null, retina: true, over: new ScreenOverride(99999, 99999, 99));
        Assert.Equal(ScreenTarget.MaxDimension, t.MaxW);
        Assert.Equal(ScreenTarget.MaxDimension, t.MaxH);
        Assert.Equal(ScreenTarget.MaxDpr, t.Dpr);
    }

    [Fact]
    public void An_incomplete_override_falls_back_to_the_probe()
    {
        var t = ScreenTarget.FromCookie("769x953x1.875", over: new ScreenOverride(0, 2000, 1));
        Assert.Equal(769, t.MaxW);
        Assert.Equal(953, t.MaxH);
    }

    [Fact]
    public void An_override_carries_the_other_knobs_through()
    {
        var t = ScreenTarget.FromCookie(null, grayscale: true, spread: SpreadMode.RotateLeft, scale: 90,
            over: new ScreenOverride(800, 1000, 1));
        Assert.True(t.Grayscale);
        Assert.Equal(SpreadMode.RotateLeft, t.Spread);
        Assert.Equal(90, t.Scale);
    }
}
