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

        // No session cookie → AbsAuthHandler finds no token → throws AbsAuthException
        // before any network call → the auth middleware redirects to /login.
        var response = await client.GetAsync("/cover/abc123?w=120");

        Assert.Equal(System.Net.HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/login", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Index_without_favorite_redirects_to_login_when_no_session()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var res = await client.GetAsync("/");
        // No fav cookie, no session -> AbsAuthException -> /login
        Assert.Equal(System.Net.HttpStatusCode.Redirect, res.StatusCode);
        Assert.Equal("/login", res.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Index_with_favorite_redirects_to_that_library()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var req = new HttpRequestMessage(HttpMethod.Get, "/");
        req.Headers.Add("Cookie", "inkshelf_fav_library=lib9");
        var res = await client.SendAsync(req);
        Assert.Equal(System.Net.HttpStatusCode.Redirect, res.StatusCode);
        Assert.Equal("/library/lib9", res.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Index_with_favorite_and_all_bypasses_redirect()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var req = new HttpRequestMessage(HttpMethod.Get, "/?all=1");
        req.Headers.Add("Cookie", "inkshelf_fav_library=lib9");
        var res = await client.SendAsync(req);
        // Bypasses fav redirect; no session -> falls through to /login
        Assert.Equal(System.Net.HttpStatusCode.Redirect, res.StatusCode);
        Assert.Equal("/login", res.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Convert_status_without_session_redirects_to_login()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        // No session → GetItemDetailAsync throws AbsAuthException → middleware → /login.
        var res = await client.GetAsync("/convert/abc?status=1");
        Assert.Equal(System.Net.HttpStatusCode.Redirect, res.StatusCode);
        Assert.Equal("/login", res.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Settings_post_sets_cookie_and_redirects()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var token = await GetAntiforgeryTokenAsync(client);
        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["retina"] = "on",
            // grayscale checkbox unchecked → not sent
        });

        var response = await client.PostAsync("/settings", content);

        Assert.Equal(System.Net.HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/settings", response.Headers.Location?.OriginalString);
        var setCookie = response.Headers.TryGetValues("Set-Cookie", out var v) ? string.Join(";", v) : "";
        Assert.Contains("inkshelf_settings=10", setCookie); // retina on, grayscale off → "10"
    }

    [Fact]
    public async Task Settings_post_without_antiforgery_returns_bad_request()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.PostAsync("/settings", content: null);

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Settings_get_renders_form_with_checkboxes()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var html = await (await client.GetAsync("/settings")).Content.ReadAsStringAsync();

        Assert.Contains("name=\"retina\"", html);
        Assert.Contains("name=\"grayscale\"", html);
        Assert.Contains("action=\"/settings\"", html);
        Assert.Contains("__RequestVerificationToken", html);
    }

    [Fact]
    public async Task Settings_get_checks_boxes_from_cookie()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var req = new HttpRequestMessage(HttpMethod.Get, "/settings");
        // "10" is DeviceSettings.Serialize()'s positional-flags format: retina=1, grayscale=0.
        req.Headers.Add("Cookie", "inkshelf_settings=10");
        var html = await (await client.SendAsync(req)).Content.ReadAsStringAsync();

        // retina checkbox is checked, grayscale is not. Assert the retina input carries "checked".
        var retinaInput = System.Text.RegularExpressions.Regex.Match(html, "<input[^>]*name=\"retina\"[^>]*>").Value;
        Assert.Contains("checked", retinaInput);
        var grayInput = System.Text.RegularExpressions.Regex.Match(html, "<input[^>]*name=\"grayscale\"[^>]*>").Value;
        Assert.DoesNotContain("checked", grayInput);
    }

    [Fact]
    public async Task Read_post_without_antiforgery_returns_bad_request()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.PostAsync("/read/item1", content: null);

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Read_post_with_token_but_no_session_redirects_to_login()
    {
        // Valid antiforgery token (the client stores the matching cookie from /login),
        // but no session. Antiforgery passes → handler calls SetReadAsync → AbsAuthHandler
        // finds no token → AbsAuthException → the auth middleware redirects to /login.
        // A 302→/login (not a 400) proves the endpoint is mapped, antiforgery validated,
        // and the handler reached the ABS call path. Mirrors Cover_WithoutSession.
        using var factory = CreateFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var token = await GetAntiforgeryTokenAsync(client);
        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["read"] = "1",
        });

        var response = await client.PostAsync("/read/item1", content);

        Assert.Equal(System.Net.HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/login", response.Headers.Location?.OriginalString);
    }
}
