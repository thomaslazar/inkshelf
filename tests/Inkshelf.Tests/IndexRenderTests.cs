using System.Net;
using Inkshelf.Abs;
using Inkshelf.Convert;
using Microsoft.AspNetCore.DataProtection;
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

    private static StubHandler MakeStub() => new(req =>
        req.RequestUri!.AbsolutePath == "/api/libraries"
            ? StubHandler.Json("""{"libraries":[]}""")
            : new HttpResponseMessage(HttpStatusCode.NotFound));

    private static async Task<string> GetIndexHtml(string session, string lang)
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
            });
        });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var dp = factory.Services.GetRequiredService<IDataProtectionProvider>();
        var protector = dp.CreateProtector("inkshelf.session.v1");
        var req = new HttpRequestMessage(HttpMethod.Get, "/");
        req.Headers.Add("Cookie",
            $"inkshelf_session={Uri.EscapeDataString(protector.Protect(session))}; "
            + $"inkshelf_settings=retina=1&gray=0&lang={lang}&fav=");

        return await (await client.SendAsync(req)).Content.ReadAsStringAsync();
    }

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
        // line — no separator, no empty label.
        var html = await GetIndexHtml(session: "acc\nref", lang: "");

        Assert.Contains($"Inkshelf v{AppVersion.Current}", html);
        Assert.DoesNotContain("User:", html);
        Assert.DoesNotContain("&#8212;", html);
    }

    [Fact]
    public async Task The_label_is_localised()
    {
        var html = await GetIndexHtml(session: "acc\nref\nalice", lang: "de");

        Assert.Contains("Benutzer: alice", html);
        Assert.DoesNotContain("User: alice", html);
    }
}
