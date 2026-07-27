using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Inkshelf;
using Inkshelf.Auth;

namespace Inkshelf.Tests;

// Mirrors the forced-vs-default Secure pair in TokenStoreTests / DeviceSettingsTests:
// Favorites.Set applies the same ForceSecureCookies rule.
public class FavoritesTests
{
    [Fact]
    public void Set_forces_secure_flag_when_configured()
    {
        var ctx = new DefaultHttpContext(); // IsHttps == false
        ctx.RequestServices = new ServiceCollection()
            .AddSingleton(new AbsOptions { ForceSecureCookies = true })
            .BuildServiceProvider();

        Favorites.Set(ctx.Response, "lib1");

        Assert.Contains("secure", ctx.Response.Headers.SetCookie.ToString(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Set_omits_secure_flag_on_http_by_default()
    {
        var ctx = new DefaultHttpContext(); // IsHttps == false, no AbsOptions → not forced
        ctx.RequestServices = new ServiceCollection().BuildServiceProvider();

        Favorites.Set(ctx.Response, "lib1");

        Assert.DoesNotContain("secure", ctx.Response.Headers.SetCookie.ToString(),
            StringComparison.OrdinalIgnoreCase);
    }
}
