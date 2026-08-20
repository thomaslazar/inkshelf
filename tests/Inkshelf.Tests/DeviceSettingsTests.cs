using Inkshelf;
using Inkshelf.Auth;
using Inkshelf.Convert;
using Microsoft.AspNetCore.Http;

namespace Inkshelf.Tests;

public class DeviceSettingsTests
{
    private static HttpRequest RequestWithCookie(string? value)
    {
        var ctx = new DefaultHttpContext();
        if (value is not null) ctx.Request.Headers.Cookie = $"{DeviceSettings.Cookie}={value}";
        return ctx.Request;
    }

    private static HttpRequest RequestWithCookies(string? settings, string? legacyFav)
    {
        var ctx = new DefaultHttpContext();
        var parts = new List<string>();
        if (settings is not null) parts.Add($"{DeviceSettings.Cookie}={settings}");
        if (legacyFav is not null) parts.Add($"{DeviceSettings.LegacyFavCookie}={legacyFav}");
        if (parts.Count > 0) ctx.Request.Headers.Cookie = string.Join("; ", parts);
        return ctx.Request;
    }

    [Fact]
    public void Read_absent_cookie_returns_default()
    {
        Assert.Equal(DeviceSettings.Default, DeviceSettings.Read(RequestWithCookie(null)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("r=&g=")]
    public void Read_malformed_cookie_returns_default(string value)
    {
        Assert.Equal(DeviceSettings.Default, DeviceSettings.Read(RequestWithCookie(value)));
    }

    [Fact]
    public void Read_explicit_00_is_both_off_distinct_from_default()
    {
        var v = DeviceSettings.Read(RequestWithCookie("00"));
        Assert.Equal(new DeviceSettings(false, false, ""), v);
        Assert.NotEqual(DeviceSettings.Default, v); // absent/malformed default to retina on
    }

    [Fact]
    public void Read_parses_both_flags()
    {
        Assert.Equal(new DeviceSettings(true, false, ""), DeviceSettings.Read(RequestWithCookie("10")));
        Assert.Equal(new DeviceSettings(false, true, ""), DeviceSettings.Read(RequestWithCookie("01")));
        Assert.Equal(new DeviceSettings(true, true, ""), DeviceSettings.Read(RequestWithCookie("11")));
    }

    [Fact]
    public void Serialize_round_trips_through_read()
    {
        var s = new DeviceSettings(true, false, "");
        Assert.Equal(s, DeviceSettings.Read(RequestWithCookie(s.Serialize())));
    }

    [Fact]
    public void Set_writes_essential_root_path_cookie_with_value()
    {
        var ctx = new DefaultHttpContext();
        DeviceSettings.Set(ctx.Response, new DeviceSettings(true, true, ""));
        var setCookie = ctx.Response.Headers.SetCookie.ToString();
        Assert.Contains($"{DeviceSettings.Cookie}=retina%3D1%26gray%3D1", setCookie);
        Assert.Contains("path=/", setCookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Set_forces_secure_flag_when_configured()
    {
        var ctx = new DefaultHttpContext(); // IsHttps == false
        var services = new ServiceCollectionStub(new AbsOptions { ForceSecureCookies = true });
        ctx.RequestServices = services;
        DeviceSettings.Set(ctx.Response, new DeviceSettings(false, false, ""));
        Assert.Contains("secure", ctx.Response.Headers.SetCookie.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Set_omits_secure_flag_on_http_by_default()
    {
        var ctx = new DefaultHttpContext(); // IsHttps == false, no AbsOptions → ForceSecureCookies false
        DeviceSettings.Set(ctx.Response, new DeviceSettings(false, false, ""));
        Assert.DoesNotContain("secure", ctx.Response.Headers.SetCookie.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Serialize_emits_keyed_pairs()
    {
        Assert.Equal("retina=1&gray=0&lang=de&fav=&did=&spread=splitleftfirst&scale=100&ovr=0&ovrw=0&ovrh=0&ovrd=0", new DeviceSettings(true, false, "de").Serialize());
        Assert.Equal("retina=1&gray=1&lang=&fav=&did=&spread=splitleftfirst&scale=100&ovr=0&ovrw=0&ovrh=0&ovrd=0", new DeviceSettings(true, true, "").Serialize());
    }

    [Fact]
    public void Read_parses_keyed_pairs()
    {
        var s = DeviceSettings.Read(RequestWithCookie("retina=0&gray=1&lang=de&fav="));
        Assert.False(s.Retina);
        Assert.True(s.Grayscale);
        Assert.Equal("de", s.Lang);
    }

    [Fact]
    public void Read_absent_key_falls_back_to_the_documented_default_not_false()
    {
        // Only gray is present. Retina must stay ON — it defaults on, and a naive
        // `q["retina"] == "1"` would silently turn it off.
        var s = DeviceSettings.Read(RequestWithCookie("gray=1"));
        Assert.True(s.Retina);
        Assert.True(s.Grayscale);
        Assert.Equal("", s.Lang);
    }

    [Fact]
    public void Read_unknown_keys_are_ignored()
    {
        var s = DeviceSettings.Read(RequestWithCookie("retina=0&whatever=9&lang=fr"));
        Assert.False(s.Retina);
        Assert.Equal("fr", s.Lang);
    }

    [Fact]
    public void Read_legacy_positional_cookie_still_works()
    {
        Assert.Equal(new DeviceSettings(true, false, "de"), DeviceSettings.Read(RequestWithCookie("10de")));
        Assert.Equal(new DeviceSettings(false, true, ""), DeviceSettings.Read(RequestWithCookie("01")));
    }

    [Fact]
    public void Set_then_Read_roundtrips_through_real_cookie_escaping()
    {
        // The `&` and `=` are escaped to %26/%3D on the way out and unescaped on the
        // way in. This test exists so a framework change can't break that silently.
        var ctx = new DefaultHttpContext();
        var written = DeviceSettings.Set(ctx.Response, new DeviceSettings(false, true, "pt-br"));

        var value = ctx.Response.Headers.SetCookie.ToString().Split(';')[0].Split('=', 2)[1];
        Assert.Contains("%26", value);              // the separators really are escaped
        var ctx2 = new DefaultHttpContext();
        ctx2.Request.Headers.Cookie = $"{DeviceSettings.Cookie}={value}";

        var read = DeviceSettings.Read(ctx2.Request);
        // Set mints a Did, so compare against what it actually wrote rather than
        // a fresh record (which would default Did to "").
        Assert.Equal(written, read);
    }

    [Fact]
    public void Read_parses_flags_and_lang()
    {
        var s = DeviceSettings.Read(RequestWithCookie("10de"));
        Assert.True(s.Retina);
        Assert.False(s.Grayscale);
        Assert.Equal("de", s.Lang);
    }

    [Fact]
    public void Read_legacy_two_char_cookie_has_empty_lang()
    {
        var s = DeviceSettings.Read(RequestWithCookie("10"));
        Assert.True(s.Retina);
        Assert.Equal("", s.Lang);
    }

    [Fact]
    public void Read_junk_lang_sanitises_to_empty()
    {
        Assert.Equal("", DeviceSettings.Read(RequestWithCookie("10DE!")).Lang);
        Assert.Equal("", DeviceSettings.Read(RequestWithCookie("10toolongcode")).Lang);
    }

    [Fact]
    public void Read_accepts_region_code()
    {
        Assert.Equal("pt-br", DeviceSettings.Read(RequestWithCookie("00pt-br")).Lang);
    }

    [Fact]
    public void Read_accepts_script_subtag_up_to_eight_chars()
    {
        Assert.Equal("zh-hant", DeviceSettings.Read(RequestWithCookie("00zh-hant")).Lang);
    }

    [Fact]
    public void Serialize_includes_fav()
    {
        var s = new DeviceSettings(true, false, "de") { Fav = "lib_abc" };
        Assert.Equal("retina=1&gray=0&lang=de&fav=lib_abc&did=&spread=splitleftfirst&scale=100&ovr=0&ovrw=0&ovrh=0&ovrd=0", s.Serialize());
    }

    [Fact]
    public void Read_parses_fav()
    {
        Assert.Equal("lib_abc", DeviceSettings.Read(RequestWithCookie("retina=1&gray=0&lang=&fav=lib_abc")).Fav);
    }

    [Fact]
    public void Read_picks_up_the_legacy_fav_cookie_when_the_key_is_absent()
    {
        // Legacy positional settings + the old separate favorite cookie: the state
        // every device is in at deploy time.
        Assert.Equal("lib_old", DeviceSettings.Read(RequestWithCookies("10de", "lib_old")).Fav);
        // Also when there is no settings cookie at all.
        Assert.Equal("lib_old", DeviceSettings.Read(RequestWithCookies(null, "lib_old")).Fav);
    }

    [Fact]
    public void An_empty_fav_key_does_not_resurrect_the_legacy_cookie()
    {
        // `fav=` present-but-empty means deliberately un-favorited. Falling back to
        // the legacy cookie here would bring back a favorite the user just cleared.
        var s = DeviceSettings.Read(RequestWithCookies("retina=1&gray=0&lang=&fav=", "lib_old"));
        Assert.Equal("", s.Fav);
    }

    [Fact]
    public void Set_deletes_the_legacy_fav_cookie()
    {
        var ctx = new DefaultHttpContext();
        DeviceSettings.Set(ctx.Response, new DeviceSettings(true, false, "") { Fav = "lib_x" });
        var setCookie = ctx.Response.Headers.SetCookie.ToString();
        // Deletion is a Set-Cookie with an expiry in the past.
        Assert.Contains(DeviceSettings.LegacyFavCookie, setCookie);
        Assert.Contains("expires=Thu, 01 Jan 1970", setCookie, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("x&retina=0", "")]           // the injection the form value could carry
    [InlineData("lib_a-b_9", "lib_a-b_9")]   // legitimate ABS id shapes survive
    [InlineData("has space", "")]
    [InlineData("semi;colon", "")]
    [InlineData("per%cent", "")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "")] // 65 chars, all allowed
    public void Fav_is_sanitized_on_the_way_into_the_cookie(string raw, string expected)
    {
        var s = new DeviceSettings(true, false, "") { Fav = raw };
        Assert.Equal($"retina=1&gray=0&lang=&fav={expected}&did=&spread=splitleftfirst&scale=100&ovr=0&ovrw=0&ovrh=0&ovrd=0", s.Serialize());
    }

    [Fact]
    public void Fav_is_sanitized_on_the_way_out_of_the_cookie()
    {
        // A hand-edited cookie must not smuggle an unsafe id into Index's redirect.
        // URL-escaped, as a browser would actually send it after a hand edit — a
        // raw space would make the request-cookie parser reject the whole cookie
        // before Read ever reaches SanitizeId.
        var v = DeviceSettings.Read(RequestWithCookie("retina%3D1%26gray%3D0%26lang%3D%26fav%3Da%20b"));
        Assert.Equal("", v.Fav);
    }

    [Fact]
    public void Set_mints_a_device_id_when_absent_and_returns_it()
    {
        var ctx = new DefaultHttpContext();
        var written = DeviceSettings.Set(ctx.Response, new DeviceSettings(true, false, "de"));

        Assert.NotEqual("", written.Did);
        Assert.Equal(16, written.Did.Length);
        Assert.Matches("^[0-9a-f]{16}$", written.Did);
        // ...and it really went into the cookie, not just the return value.
        Assert.Contains($"did%3D{written.Did}", ctx.Response.Headers.SetCookie.ToString());
    }

    [Fact]
    public void Set_keeps_an_existing_device_id()
    {
        var ctx = new DefaultHttpContext();
        var written = DeviceSettings.Set(ctx.Response,
            new DeviceSettings(true, false, "") { Did = "abc123def456abcd" });
        Assert.Equal("abc123def456abcd", written.Did);
    }

    [Fact]
    public void Read_parses_the_device_id()
    {
        var s = DeviceSettings.Read(RequestWithCookie("retina=1&gray=0&lang=&fav=&did=feedface00001111"));
        Assert.Equal("feedface00001111", s.Did);
    }

    [Fact]
    public void Read_sanitizes_a_hostile_device_id_to_empty()
    {
        // The id becomes part of a file path, so a traversal shape must collapse.
        Assert.Equal("", DeviceSettings.Read(RequestWithCookie("retina=1&gray=0&lang=&fav=&did=../../etc/passwd")).Did);
        Assert.Equal("", DeviceSettings.Read(RequestWithCookie("retina=1&gray=0&lang=&fav=&did=a/b")).Did);
    }

    [Fact]
    public void Two_mints_differ()
    {
        var a = DeviceSettings.Set(new DefaultHttpContext().Response, DeviceSettings.Default).Did;
        var b = DeviceSettings.Set(new DefaultHttpContext().Response, DeviceSettings.Default).Did;
        Assert.NotEqual(a, b);
    }

    // Minimal IServiceProvider that returns one AbsOptions instance (mirrors how
    // RequestServices.GetService<AbsOptions>() resolves in production).
    private sealed class ServiceCollectionStub(AbsOptions options) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType == typeof(AbsOptions) ? options : null;
    }

    [Fact]
    public void Spread_round_trips_and_defaults_to_split()
    {
        // A cookie written before this setting existed has no spread key: it must
        // land on the documented default, not on the enum's zero value.
        Assert.Equal(SpreadMode.SplitLeftFirst, DeviceSettings.Read(RequestWithCookie("retina=1&gray=0&lang=&fav=")).Spread);
        Assert.Equal(SpreadMode.RotateRight, DeviceSettings.Read(RequestWithCookie("retina=1&gray=0&lang=&fav=&spread=rotateright")).Spread);
        Assert.Equal(SpreadMode.RotateLeft, DeviceSettings.Read(RequestWithCookie("retina=1&gray=0&lang=&fav=&spread=rotateleft")).Spread);
        Assert.Equal(SpreadMode.SplitRightFirst, DeviceSettings.Read(RequestWithCookie("retina=1&gray=0&lang=&fav=&spread=splitrightfirst")).Spread);
        // A value an earlier build wrote is no longer a mode: fall back to the default
        // rather than guessing which of the two new directions was meant.
        Assert.Equal(SpreadMode.SplitLeftFirst, DeviceSettings.Read(RequestWithCookie("retina=1&gray=0&lang=&fav=&spread=split")).Spread);
        Assert.Equal(SpreadMode.Fit, DeviceSettings.Read(RequestWithCookie("retina=1&gray=0&lang=&fav=&spread=fit")).Spread);
        Assert.Equal(SpreadMode.SplitLeftFirst, DeviceSettings.Read(RequestWithCookie("retina=1&gray=0&lang=&fav=&spread=nonsense")).Spread);
    }

    [Fact]
    public void Scale_round_trips_and_defaults_to_100()
    {
        // A cookie written before this setting existed has no scale key: it must land on
        // 100, not on 0, or every page would be laid out at zero size.
        Assert.Equal(100, DeviceSettings.Read(RequestWithCookie("retina=1&gray=0&lang=&fav=")).Scale);
        Assert.Equal(90, DeviceSettings.Read(RequestWithCookie("retina=1&gray=0&lang=&fav=&scale=90")).Scale);
        // A hand-edited cookie must not mint an absurd page size.
        Assert.Equal(100, DeviceSettings.Read(RequestWithCookie("retina=1&gray=0&lang=&fav=&scale=5")).Scale);
        Assert.Equal(100, DeviceSettings.Read(RequestWithCookie("retina=1&gray=0&lang=&fav=&scale=400")).Scale);
        Assert.Equal(100, DeviceSettings.Read(RequestWithCookie("retina=1&gray=0&lang=&fav=&scale=abc")).Scale);
    }

    [Fact]
    public void Screen_override_round_trips_through_the_cookie()
    {
        var s = new DeviceSettings(true, false, "de")
        {
            OverrideScreen = true,
            OverrideW = 1264,
            OverrideH = 1680,
            OverrideDpr = 1.875,
        };
        var wire = s.Serialize();
        Assert.Contains("ovr=1", wire);
        Assert.Contains("ovrw=1264", wire);
        Assert.Contains("ovrh=1680", wire);
        Assert.Contains("ovrd=1.875", wire);   // invariant, never "1,875"

        var read = DeviceSettings.Read(RequestWithCookie(wire));
        Assert.True(read.OverrideScreen);
        Assert.Equal(1264, read.OverrideW);
        Assert.Equal(1680, read.OverrideH);
        Assert.Equal(1.875, read.OverrideDpr);
    }

    [Fact]
    public void Screen_override_defaults_to_off_and_empty()
    {
        // A cookie written before this setting existed.
        var s = DeviceSettings.Read(RequestWithCookie("retina=1&gray=0&lang=&fav="));
        Assert.False(s.OverrideScreen);
        Assert.Equal(0, s.OverrideW);
        Assert.Equal(0, s.OverrideH);
        Assert.Equal(0, s.OverrideDpr);
        Assert.Null(s.ActiveOverride);
    }

    [Fact]
    public void Screen_override_accepts_a_comma_decimal_ratio()
    {
        // The UI is translated; a German-locale user typing 1,875 must not
        // silently fall through to the invalid-value path. The comma is
        // percent-encoded here (%2C) the way a real Cookie header carries it —
        // ASP.NET's own cookie-header parser splits raw, unescaped commas as if
        // they separated multiple header values, which would corrupt every other
        // field in this cookie before Read ever saw it.
        var s = DeviceSettings.Read(RequestWithCookie("retina=1&gray=0&lang=de&fav=&ovr=1&ovrw=800&ovrh=1000&ovrd=1%2C875"));
        Assert.Equal(1.875, s.OverrideDpr);
    }

    [Theory]
    [InlineData("0", "1000", "1")]        // zero width
    [InlineData("-5", "1000", "1")]       // negative width
    [InlineData("99999", "1000", "1")]    // past MaxDimension
    [InlineData("800", "1000", "0")]      // zero ratio
    [InlineData("800", "1000", "99")]     // past MaxDpr
    [InlineData("800", "1000", "abc")]    // unparseable ratio
    public void Screen_override_rejects_values_out_of_range(string w, string h, string dpr)
    {
        // A hand-edited cookie must not mint an absurd page size: the value is
        // dropped to 0, which makes the override inactive rather than dangerous.
        var s = DeviceSettings.Read(RequestWithCookie($"retina=1&gray=0&lang=&fav=&ovr=1&ovrw={w}&ovrh={h}&ovrd={dpr}"));
        Assert.Null(s.ActiveOverride);
    }

    [Fact]
    public void Active_override_needs_the_flag_and_all_three_numbers()
    {
        var numbers = new DeviceSettings(true, false, "") { OverrideW = 800, OverrideH = 1000, OverrideDpr = 2 };
        Assert.Null(numbers.ActiveOverride);                                  // flag off
        Assert.Null((numbers with { OverrideScreen = true, OverrideH = 0 }).ActiveOverride);

        var on = numbers with { OverrideScreen = true };
        Assert.Equal(new ScreenOverride(800, 1000, 2), on.ActiveOverride);
    }
}
