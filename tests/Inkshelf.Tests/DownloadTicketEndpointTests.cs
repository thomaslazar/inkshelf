using System.Net;
using Inkshelf.Abs;
using Inkshelf.Convert;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Inkshelf.Tests;

// A download manager on an e-reader re-requests the URL with NO cookies (issue
// #40). These tests are that request: no cookie header anywhere.
public class DownloadTicketEndpointTests
{
    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tix-" + Guid.NewGuid().ToString("N"));
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, true); } catch (IOException) { } }
    }

    private const string Did = "abc123def4560000";

    // TimeProvider is abstract with a virtual GetUtcNow, so a fake needs no package.
    private sealed class FakeClock : TimeProvider
    {
        public DateTimeOffset Utc = new(2026, 8, 24, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Utc;
    }

    // ABS_URL points at a dead port on purpose: anything that reaches ABS fails, so
    // a passing test proves the ticket path never needed it.
    private static WebApplicationFactory<Program> CreateFactory(string cachePath, string keysPath,
        Action<IServiceCollection>? extra = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ABS_URL", "http://localhost:1");
            b.UseSetting("CachePath", cachePath);
            b.UseSetting("DataProtectionKeysPath", keysPath);
            b.ConfigureTestServices(services =>
            {
                var worker = services.FirstOrDefault(s => s.ImplementationType == typeof(ConvertWorker));
                if (worker is not null) services.Remove(worker);
                extra?.Invoke(services);
            });
        });

    private static string CachedEpub(string dir, string body = "EPUBBYTES")
    {
        var path = Path.Combine(dir, "item1-1-2-800x1000-f.epub");
        File.WriteAllText(path, body);
        return path;
    }

    [Fact]
    public async Task A_cookie_less_convert_download_with_a_ticket_streams_the_cached_file()
    {
        using var cache = new TempDir();
        using var keys = new TempDir();
        using var factory = CreateFactory(cache.Path, keys.Path);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var t = factory.Services.GetRequiredService<DownloadTickets>()
            .MintEpub("item1", null, Did, "Author - Title.epub", CachedEpub(cache.Path));

        var res = await client.GetAsync($"/convert/item1?t={t}");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("EPUBBYTES", await res.Content.ReadAsStringAsync());
        Assert.Equal("Author - Title.epub", res.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        Assert.Contains("bytes", res.Headers.AcceptRanges);   // a physical file can resume
    }

    [Fact]
    public async Task A_convert_ticket_marks_the_download_against_its_own_device()
    {
        // The cookie-less request has no settings cookie either, so without the
        // ticket's did the app would mint a fresh one per download — the trail of
        // four device ids in 90 minutes seen on the shine.
        using var cache = new TempDir();
        using var keys = new TempDir();
        using var factory = CreateFactory(cache.Path, keys.Path);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var t = factory.Services.GetRequiredService<DownloadTickets>()
            .MintEpub("item1", null, Did, "Author - Title.epub", CachedEpub(cache.Path));

        var res = await client.GetAsync($"/convert/item1?t={t}");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains(DownloadMarks.EpubKey("item1", null),
            factory.Services.GetRequiredService<DownloadMarks>().Read(Did));
        // No new device id minted: the ticket already carried one.
        res.Headers.TryGetValues("Set-Cookie", out var setCookies);
        Assert.DoesNotContain("inkshelf_settings", string.Join(";", setCookies ?? []));
    }

    [Theory]
    [InlineData("&status=1")]
    [InlineData("&warm=1")]
    [InlineData("&fresh=1")]
    public async Task A_ticket_authorises_bytes_only_never_a_kick_or_a_poll(string extra)
    {
        using var cache = new TempDir();
        using var keys = new TempDir();
        using var factory = CreateFactory(cache.Path, keys.Path);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var t = factory.Services.GetRequiredService<DownloadTickets>()
            .MintEpub("item1", null, Did, "Author - Title.epub", CachedEpub(cache.Path));

        var res = await client.GetAsync($"/convert/item1?t={t}{extra}");

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task A_ticket_cannot_be_replayed_on_another_item()
    {
        using var cache = new TempDir();
        using var keys = new TempDir();
        using var factory = CreateFactory(cache.Path, keys.Path);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var t = factory.Services.GetRequiredService<DownloadTickets>()
            .MintEpub("item1", null, Did, "Author - Title.epub", CachedEpub(cache.Path));

        var res = await client.GetAsync($"/convert/other?t={t}");

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task A_convert_poll_re_stamps_the_ticket_so_a_long_conversion_link_survives()
    {
        // The poll itself is never authorised (no cookie, and a ticket is bytes-only),
        // but hitting it must still re-stamp the ticket's idle window (see
        // ConvertEndpoints: Redeem runs unconditionally, before the bytes-only guard).
        using var cache = new TempDir();
        using var keys = new TempDir();
        var clock = new FakeClock();
        using var factory = CreateFactory(cache.Path, keys.Path,
            services => services.AddSingleton(new DownloadTickets(clock)));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var t = factory.Services.GetRequiredService<DownloadTickets>()
            .MintEpub("item1", null, Did, "Author - Title.epub", CachedEpub(cache.Path));

        clock.Utc = clock.Utc.AddMinutes(10);   // inside the 15-minute window
        var poll = await client.GetAsync($"/convert/item1?t={t}&status=1");
        Assert.Equal(HttpStatusCode.Unauthorized, poll.StatusCode);   // a poll never authorises

        clock.Utc = clock.Utc.AddMinutes(10);   // 20m since mint, only 10m since the poll
        var res = await client.GetAsync($"/convert/item1?t={t}");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("EPUBBYTES", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_ticket_whose_cache_file_was_evicted_falls_through_to_the_cookie_path()
    {
        using var cache = new TempDir();
        using var keys = new TempDir();
        using var factory = CreateFactory(cache.Path, keys.Path);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var cached = CachedEpub(cache.Path);
        var t = factory.Services.GetRequiredService<DownloadTickets>()
            .MintEpub("item1", null, Did, "Author - Title.epub", cached);
        File.Delete(cached);

        var res = await client.GetAsync($"/convert/item1?t={t}");

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task An_expired_or_bogus_ticket_falls_through_to_the_cookie_path()
    {
        // Additive, never a gate: with no cookie either, that path is today's 401 —
        // never a worse outcome than before tickets existed.
        using var cache = new TempDir();
        using var keys = new TempDir();
        using var factory = CreateFactory(cache.Path, keys.Path);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        CachedEpub(cache.Path);

        var res = await client.GetAsync("/convert/item1?t=Ab_1Cd-2Ef3Gh4Ij5Kl6");

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        Assert.StartsWith("text/plain", res.Content.Headers.ContentType?.ToString());
    }

    [Fact]
    public async Task A_ticket_wins_over_a_session_cookie_so_both_requests_take_one_path()
    {
        // The browser's request and the manager's must be served identically; ABS is
        // unreachable here, so a 200 proves the cookie path was not taken.
        using var cache = new TempDir();
        using var keys = new TempDir();
        using var factory = CreateFactory(cache.Path, keys.Path);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var t = factory.Services.GetRequiredService<DownloadTickets>()
            .MintEpub("item1", null, Did, "Author - Title.epub", CachedEpub(cache.Path));
        var protector = factory.Services.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector("inkshelf.session.v1");
        var req = new HttpRequestMessage(HttpMethod.Get, $"/convert/item1?t={t}");
        req.Headers.Add("Cookie", $"inkshelf_session={Uri.EscapeDataString(protector.Protect("access\nrefresh"))}");

        var res = await client.SendAsync(req);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("EPUBBYTES", await res.Content.ReadAsStringAsync());
    }
}
