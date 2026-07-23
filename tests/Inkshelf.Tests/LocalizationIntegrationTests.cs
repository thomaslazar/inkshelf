using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Inkshelf.Tests;

public class LocalizationIntegrationTests : IClassFixture<LocalizationIntegrationTests.Factory>
{
    private readonly Factory _factory;
    public LocalizationIntegrationTests(Factory factory) => _factory = factory;

    // GET /login renders without touching ABS, so it exercises catalog+localizer+view.
    public class Factory : WebApplicationFactory<Program>
    {
        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.ConfigureHostConfiguration(c =>
                c.AddInMemoryCollection(new Dictionary<string, string?> { ["ABS_URL"] = "http://abs.invalid" }));
            return base.CreateHost(builder);
        }
    }

    [Fact]
    public async Task Login_page_is_english_by_default()
    {
        var client = _factory.CreateClient();
        var html = await (await client.GetAsync("/login")).Content.ReadAsStringAsync();
        Assert.Contains("Log in", html);
        Assert.Contains("Password", html);
    }

    [Fact]
    public async Task Login_page_is_german_with_de_cookie()
    {
        var client = _factory.CreateClient();
        var req = new HttpRequestMessage(HttpMethod.Get, "/login");
        req.Headers.Add("Cookie", "inkshelf_settings=00de");
        var html = await (await client.SendAsync(req)).Content.ReadAsStringAsync();
        Assert.Contains("Anmelden", html);    // "Log in"
        Assert.Contains("Passwort", html);    // "Password"
    }

    [Fact]
    public async Task Login_page_is_german_via_accept_language()
    {
        var client = _factory.CreateClient();
        var req = new HttpRequestMessage(HttpMethod.Get, "/login");
        req.Headers.Add("Accept-Language", "de-DE,de;q=0.9,en;q=0.8");
        var html = await (await client.SendAsync(req)).Content.ReadAsStringAsync();
        Assert.Contains("Anmelden", html);
    }
}
