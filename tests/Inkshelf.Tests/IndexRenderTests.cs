using System.Net;
using Inkshelf.Abs;
using Inkshelf.Convert;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;

namespace Inkshelf.Tests;

public class IndexRenderTests
{
    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "index-" + Guid.NewGuid().ToString("N"));
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, true); } catch (IOException) { } }
    }

    // Rendering the logout form's @Html.AntiForgeryToken() makes the real
    // antiforgery service stamp Cache-Control: no-store on its own, on every
    // request — which would mask a missing/removed no-store block on the page
    // and make the header test below pass regardless. Swap in a fake with no
    // such side effect so that test exercises only the page's own header.
    private sealed class SilentAntiforgery : IAntiforgery
    {
        private static readonly AntiforgeryTokenSet Tokens = new("req", "cookie", "__RequestVerificationToken", "RequestVerificationToken");
        public AntiforgeryTokenSet GetAndStoreTokens(HttpContext httpContext) => Tokens;
        public AntiforgeryTokenSet GetTokens(HttpContext httpContext) => Tokens;
        public Task<bool> IsRequestValidAsync(HttpContext httpContext) => Task.FromResult(true);
        public void SetCookieTokenAndHeader(HttpContext httpContext) { }
        public Task ValidateRequestAsync(HttpContext httpContext) => Task.CompletedTask;
    }

    private static StubHandler MakeStub() => new(req =>
        req.RequestUri!.AbsolutePath == "/api/libraries"
            ? StubHandler.Json("""{"libraries":[]}""")
            : new HttpResponseMessage(HttpStatusCode.NotFound));

    private static async Task<HttpResponseMessage> GetIndexResponse(string session, string lang)
    {
        using var cacheDir = new TempDir();
        using var keysDir = new TempDir();
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ABS_URL", "http://abs.local");
            b.UseSetting("CachePath", cacheDir.Path);
            b.UseSetting("DataProtectionKeysPath", keysDir.Path);
            b.ConfigureTestServices(services =>
            {
                services.Configure<HttpClientFactoryOptions>(nameof(AbsApiClient), o =>
                    o.HttpMessageHandlerBuilderActions.Add(hb => hb.PrimaryHandler = MakeStub()));
                var worker = services.FirstOrDefault(s => s.ImplementationType == typeof(ConvertWorker));
                if (worker is not null) services.Remove(worker);
                services.AddSingleton<IAntiforgery, SilentAntiforgery>();
            });
        });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var dp = factory.Services.GetRequiredService<IDataProtectionProvider>();
        var protector = dp.CreateProtector("inkshelf.session.v1");
        var req = new HttpRequestMessage(HttpMethod.Get, "/");
        req.Headers.Add("Cookie",
            $"inkshelf_session={Uri.EscapeDataString(protector.Protect(session))}; "
            + $"inkshelf_settings=retina=1&gray=0&lang={lang}&fav=");

        return await client.SendAsync(req);
    }

    private static async Task<string> GetIndexHtml(string session, string lang) =>
        await (await GetIndexResponse(session, lang)).Content.ReadAsStringAsync();

    [Fact]
    public async Task The_version_line_names_the_logged_in_user()
    {
        // Shared deployment: which account a reader is signed in as is otherwise
        // invisible. Reads from the session cookie, so no ABS call is involved.
        var html = await GetIndexHtml(session: "acc\nref\nalice", lang: "");

        Assert.Contains($"Inkshelf v{AppVersion.Current}", html);
        Assert.Contains("User: alice", html);
    }

    [Fact]
    public async Task The_version_line_stays_bare_when_no_username_is_stored()
    {
        // A cookie from before the username was stored must render exactly today's
        // line — the version and nothing appended to it.
        var html = await GetIndexHtml(session: "acc\nref", lang: "");

        Assert.Contains($"Inkshelf v{AppVersion.Current}</small>", html);
        Assert.DoesNotContain("User:", html);
    }

    [Fact]
    public async Task The_label_is_localised()
    {
        var html = await GetIndexHtml(session: "acc\nref\nalice", lang: "de");

        Assert.Contains("Benutzer: alice", html);
        Assert.DoesNotContain("User: alice", html);
    }

    [Fact]
    public async Task The_page_is_never_cached()
    {
        // It now names the signed-in user: a cached copy served after a different
        // family member logs in would show the previous account's name.
        var response = await GetIndexResponse(session: "acc\nref\nalice", lang: "");

        Assert.True(response.Headers.CacheControl?.NoStore == true, "Expected Cache-Control: no-store.");
    }
}
