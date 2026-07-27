using Inkshelf;
using Inkshelf.Auth;
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
        Assert.Equal("retina=1&gray=0&lang=de&fav=", new DeviceSettings(true, false, "de").Serialize());
        Assert.Equal("retina=1&gray=1&lang=&fav=", new DeviceSettings(true, true, "").Serialize());
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
        DeviceSettings.Set(ctx.Response, new DeviceSettings(false, true, "pt-br"));

        var value = ctx.Response.Headers.SetCookie.ToString().Split(';')[0].Split('=', 2)[1];
        Assert.Contains("%26", value);              // the separators really are escaped
        var ctx2 = new DefaultHttpContext();
        ctx2.Request.Headers.Cookie = $"{DeviceSettings.Cookie}={value}";

        var read = DeviceSettings.Read(ctx2.Request);
        Assert.Equal(new DeviceSettings(false, true, "pt-br"), read);
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

    // Minimal IServiceProvider that returns one AbsOptions instance (mirrors how
    // RequestServices.GetService<AbsOptions>() resolves in production).
    private sealed class ServiceCollectionStub(AbsOptions options) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType == typeof(AbsOptions) ? options : null;
    }
}
