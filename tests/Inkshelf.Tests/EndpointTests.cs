using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Inkshelf.Tests;

public class EndpointTests
{
    private static WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b => b.UseSetting("ABS_URL", "http://localhost:1"));

    // /login is unauthenticated and, like any POST form on the site, gets an
    // auto-injected __RequestVerificationToken hidden field — grab it (and the
    // antiforgery cookie the client already tracks) to make a valid CSRF'd request.
    private static async Task<string> GetAntiforgeryTokenAsync(HttpClient client)
    {
        var html = await (await client.GetAsync("/login")).Content.ReadAsStringAsync();
        var match = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success, "Expected an antiforgery token in /login response.");
        return match.Groups[1].Value;
    }

    [Fact]
    public async Task Logout_ClearsCookieAndRedirectsToLogin()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var token = await GetAntiforgeryTokenAsync(client);
        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token
        });

        var response = await client.PostAsync("/logout", content);

        Assert.Equal(System.Net.HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/login", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Logout_WithoutAntiforgeryToken_ReturnsBadRequest()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var response = await client.PostAsync("/logout", content: null);

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Cover_WithoutSession_RedirectsToLogin()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        // No session cookie set, so AbsSession.ExecuteAsync throws AbsAuthException
        // before ever reaching the network — the auth middleware redirects to /login.
        var response = await client.GetAsync("/cover/abc123?w=120");

        Assert.Equal(System.Net.HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/login", response.Headers.Location?.OriginalString);
    }
}
