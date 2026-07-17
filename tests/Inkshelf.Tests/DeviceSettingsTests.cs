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
    public void Read_parses_both_flags()
    {
        Assert.Equal(new DeviceSettings(true, false), DeviceSettings.Read(RequestWithCookie("10")));
        Assert.Equal(new DeviceSettings(false, true), DeviceSettings.Read(RequestWithCookie("01")));
        Assert.Equal(new DeviceSettings(true, true), DeviceSettings.Read(RequestWithCookie("11")));
    }

    [Fact]
    public void Serialize_round_trips_through_read()
    {
        var s = new DeviceSettings(true, false);
        Assert.Equal(s, DeviceSettings.Read(RequestWithCookie(s.Serialize())));
    }

    [Fact]
    public void Set_writes_essential_root_path_cookie_with_value()
    {
        var ctx = new DefaultHttpContext();
        DeviceSettings.Set(ctx.Response, new DeviceSettings(true, true));
        var setCookie = ctx.Response.Headers.SetCookie.ToString();
        Assert.Contains($"{DeviceSettings.Cookie}=11", setCookie);
        Assert.Contains("path=/", setCookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Set_forces_secure_flag_when_configured()
    {
        var ctx = new DefaultHttpContext(); // IsHttps == false
        var services = new ServiceCollectionStub(new AbsOptions { ForceSecureCookies = true });
        ctx.RequestServices = services;
        DeviceSettings.Set(ctx.Response, new DeviceSettings(false, false));
        Assert.Contains("secure", ctx.Response.Headers.SetCookie.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Set_omits_secure_flag_on_http_by_default()
    {
        var ctx = new DefaultHttpContext(); // IsHttps == false, no AbsOptions → ForceSecureCookies false
        DeviceSettings.Set(ctx.Response, new DeviceSettings(false, false));
        Assert.DoesNotContain("secure", ctx.Response.Headers.SetCookie.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    // Minimal IServiceProvider that returns one AbsOptions instance (mirrors how
    // RequestServices.GetService<AbsOptions>() resolves in production).
    private sealed class ServiceCollectionStub(AbsOptions options) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType == typeof(AbsOptions) ? options : null;
    }
}
