using System.Net;
using Inkshelf.Abs;
using Inkshelf.Convert;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;

namespace Inkshelf.Tests;

// The read toggle's transport contract. A tap with JavaScript posts xhr=1 and
// wants no redirect body to throw away; a tap without it wants the redirect that
// has always been there. Both paths call the same ABS PATCH.
public class ReadEndpointTests
{
    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "read-" + Guid.NewGuid().ToString("N"));
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, true); } catch (IOException) { } }
    }

    private const string ItemId = "item1";

    // Records the PATCH so a test can prove the ABS call really happened, which is
    // what separates "204 because it worked" from "204 because nothing ran".
    private sealed class Recorder
    {
        public int Patches;
        public string? LastBody;
    }

    private static StubHandler MakeStub(Recorder rec) => new(req =>
    {
        var path = req.RequestUri!.AbsolutePath;
        if (path == $"/api/me/progress/{ItemId}" && req.Method == HttpMethod.Patch)
        {
            rec.Patches++;
            rec.LastBody = req.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    });

    private static WebApplicationFactory<Program> CreateFactory(StubHandler stub, string cachePath, string keysPath) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ABS_URL", "http://abs.local");
            b.UseSetting("CachePath", cachePath);
            b.UseSetting("DataProtectionKeysPath", keysPath);
            b.ConfigureTestServices(services =>
            {
                services.Configure<HttpClientFactoryOptions>(nameof(AbsApiClient), o =>
                    o.HttpMessageHandlerBuilderActions.Add(hb => hb.PrimaryHandler = stub));
                var worker = services.FirstOrDefault(s => s.ImplementationType == typeof(ConvertWorker));
                if (worker is not null) services.Remove(worker);
            });
        });

    private static string SessionCookie(WebApplicationFactory<Program> factory)
    {
        var dp = factory.Services.GetRequiredService<IDataProtectionProvider>();
        var protector = dp.CreateProtector("inkshelf.session.v1");
        return $"inkshelf_session={Uri.EscapeDataString(protector.Protect("access\nrefresh"))}";
    }

    // The endpoint validates antiforgery, so a real token plus its cookie is
    // needed. GET /login issues both.
    private static async Task<HttpResponseMessage> PostReadAsync(
        WebApplicationFactory<Program> factory, HttpClient client, string url, string read)
    {
        var token = await GetAntiforgeryTokenAsync(client);
        var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["read"] = read,
                ["return"] = "/converted",
            }),
        };
        req.Headers.Add("Cookie", SessionCookie(factory));
        return await client.SendAsync(req);
    }

    [Fact]
    public async Task Xhr_read_returns_204_and_still_patches_abs()
    {
        using var cacheDir = new TempDir();
        using var keysDir = new TempDir();
        var rec = new Recorder();
        using var factory = CreateFactory(MakeStub(rec), cacheDir.Path, keysDir.Path);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var res = await PostReadAsync(factory, client, $"/read/{ItemId}?xhr=1", "1");

        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);
        Assert.Equal(1, rec.Patches);
        Assert.Contains("\"isFinished\":true", rec.LastBody);
    }

    [Fact]
    public async Task Non_xhr_read_still_redirects_to_the_return_url()
    {
        using var cacheDir = new TempDir();
        using var keysDir = new TempDir();
        var rec = new Recorder();
        using var factory = CreateFactory(MakeStub(rec), cacheDir.Path, keysDir.Path);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var res = await PostReadAsync(factory, client, $"/read/{ItemId}", "1");

        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Equal("/converted", res.Headers.Location?.OriginalString);
        Assert.Equal(1, rec.Patches);
    }

    [Fact]
    public async Task Unmarking_sends_isFinished_false()
    {
        // The form posts the ABSOLUTE desired state, so read=0 must reach ABS as
        // false rather than toggling whatever is stored.
        using var cacheDir = new TempDir();
        using var keysDir = new TempDir();
        var rec = new Recorder();
        using var factory = CreateFactory(MakeStub(rec), cacheDir.Path, keysDir.Path);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var res = await PostReadAsync(factory, client, $"/read/{ItemId}?xhr=1", "0");

        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);
        Assert.Contains("\"isFinished\":false", rec.LastBody);
    }

    [Fact]
    public async Task Xhr_in_the_form_body_is_ignored_and_still_redirects()
    {
        // xhr binds from the query string only. A form field of the same name
        // must not be able to opt into the no-redirect response.
        using var cacheDir = new TempDir();
        using var keysDir = new TempDir();
        var rec = new Recorder();
        using var factory = CreateFactory(MakeStub(rec), cacheDir.Path, keysDir.Path);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var token = await GetAntiforgeryTokenAsync(client);
        var req = new HttpRequestMessage(HttpMethod.Post, $"/read/{ItemId}")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["read"] = "1",
                ["return"] = "/converted",
                ["xhr"] = "1",
            }),
        };
        req.Headers.Add("Cookie", SessionCookie(factory));
        var res = await client.SendAsync(req);

        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Equal(1, rec.Patches);
    }

    [Fact]
    public async Task Xhr_read_without_a_valid_antiforgery_token_returns_bad_request()
    {
        // xhr=1 must not bypass antiforgery validation.
        using var cacheDir = new TempDir();
        using var keysDir = new TempDir();
        var rec = new Recorder();
        using var factory = CreateFactory(MakeStub(rec), cacheDir.Path, keysDir.Path);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var req = new HttpRequestMessage(HttpMethod.Post, $"/read/{ItemId}?xhr=1")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["read"] = "1" }),
        };
        req.Headers.Add("Cookie", SessionCookie(factory));
        var res = await client.SendAsync(req);

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal(0, rec.Patches);
    }

    [Fact]
    public async Task Xhr_read_with_token_but_no_session_returns_401_not_a_redirect()
    {
        // Finding 1: an expired session must not let an XHR read follow a 302 to
        // /login (200, no-JS-visible body) and have the script read that as
        // success. The auth middleware must answer 401 plain text for xhr=1
        // instead, same as any other NonHtmlEndpoint.
        using var cacheDir = new TempDir();
        using var keysDir = new TempDir();
        var rec = new Recorder();
        using var factory = CreateFactory(MakeStub(rec), cacheDir.Path, keysDir.Path);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var token = await GetAntiforgeryTokenAsync(client);
        var req = new HttpRequestMessage(HttpMethod.Post, $"/read/{ItemId}?xhr=1")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["read"] = "1",
            }),
        };
        var res = await client.SendAsync(req);

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        Assert.Equal(0, rec.Patches);
    }

    [Fact]
    public async Task Non_xhr_read_with_token_but_no_session_still_redirects_to_login()
    {
        // Pinned alongside the test above: without xhr=1 the existing redirect
        // behaviour must be unchanged.
        using var cacheDir = new TempDir();
        using var keysDir = new TempDir();
        var rec = new Recorder();
        using var factory = CreateFactory(MakeStub(rec), cacheDir.Path, keysDir.Path);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var token = await GetAntiforgeryTokenAsync(client);
        var req = new HttpRequestMessage(HttpMethod.Post, $"/read/{ItemId}")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["read"] = "1",
            }),
        };
        var res = await client.SendAsync(req);

        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Equal("/login", res.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task An_offsite_return_is_still_rejected_by_the_guard()
    {
        // The open-redirect guard predates this change and must survive it.
        using var cacheDir = new TempDir();
        using var keysDir = new TempDir();
        var rec = new Recorder();
        using var factory = CreateFactory(MakeStub(rec), cacheDir.Path, keysDir.Path);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var token = await GetAntiforgeryTokenAsync(client);
        var req = new HttpRequestMessage(HttpMethod.Post, $"/read/{ItemId}")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["read"] = "1",
                ["return"] = "//evil.example/x",
            }),
        };
        req.Headers.Add("Cookie", SessionCookie(factory));
        var res = await client.SendAsync(req);

        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Equal("/", res.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task A_return_with_a_row_anchor_survives_the_guard()
    {
        // The no-JavaScript path depends on this: the fragment has to reach the
        // Location header, because a 302 whose Location has none does not inherit
        // one from the POST target.
        using var cacheDir = new TempDir();
        using var keysDir = new TempDir();
        var rec = new Recorder();
        using var factory = CreateFactory(MakeStub(rec), cacheDir.Path, keysDir.Path);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var token = await GetAntiforgeryTokenAsync(client);
        var req = new HttpRequestMessage(HttpMethod.Post, $"/read/{ItemId}")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["read"] = "1",
                ["return"] = $"/converted#item-{ItemId}",
            }),
        };
        req.Headers.Add("Cookie", SessionCookie(factory));
        var res = await client.SendAsync(req);

        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Equal($"/converted#item-{ItemId}", res.Headers.Location?.OriginalString);
    }

    private static async Task<string> GetAntiforgeryTokenAsync(HttpClient client)
    {
        var html = await (await client.GetAsync("/login")).Content.ReadAsStringAsync();
        var match = System.Text.RegularExpressions.Regex.Match(
            html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success, "Expected an antiforgery token in /login response.");
        return match.Groups[1].Value;
    }
}
