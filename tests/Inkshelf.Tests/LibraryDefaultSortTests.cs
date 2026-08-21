using System.Net;
using Inkshelf;
using Inkshelf.Abs;
using Inkshelf.Convert;
using Inkshelf.Pages;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;

namespace Inkshelf.Tests;

// The main listing defaults to newest-first, a FACET listing does not — ABS's own
// order is the meaningful one there (series sequence). Asserted on the outgoing
// ABS query, since that is where the default is applied.
public class LibraryDefaultSortTests
{
    private const string LibId = "lib1";

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "defsort-" + Guid.NewGuid().ToString("N"));
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, true); } catch (IOException) { } }
    }

    private static async Task<(string ItemsQuery, string Html)> GetListing(string query)
    {
        using var cacheDir = new TempDir();
        using var keysDir = new TempDir();
        string? itemsQuery = null;
        var stub = new StubHandler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path == "/api/libraries")
                return StubHandler.Json("""{"libraries":[{"id":"lib1","name":"Test Library","mediaType":"book"}]}""");
            if (path == $"/api/libraries/{LibId}/items")
            {
                itemsQuery = req.RequestUri!.Query;
                return StubHandler.Json(
                    """{"results":[{"id":"i1","media":{"metadata":{"title":"Dune"}}}],"total":1,"limit":10,"page":0}""");
            }
            if (path == $"/api/libraries/{LibId}/search")
                return StubHandler.Json("""{"book":[],"series":[{"series":{"id":"s1","name":"Dune"}}],"authors":[]}""");
            if (path == "/api/items/batch/get") return StubHandler.Json("""{"libraryItems":[]}""");
            if (path == "/api/me") return StubHandler.Json("""{"mediaProgress":[]}""");
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ABS_URL", "http://abs.local");
            b.UseSetting("CachePath", cacheDir.Path);
            b.UseSetting("DataProtectionKeysPath", keysDir.Path);
            b.ConfigureTestServices(services =>
            {
                services.Configure<HttpClientFactoryOptions>(nameof(AbsApiClient), o =>
                    o.HttpMessageHandlerBuilderActions.Add(hb => hb.PrimaryHandler = stub));
                var worker = services.FirstOrDefault(s => s.ImplementationType == typeof(ConvertWorker));
                if (worker is not null) services.Remove(worker);
            });
        });

        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var protector = factory.Services.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector("inkshelf.session.v1");
        var req = new HttpRequestMessage(HttpMethod.Get, $"/library/{LibId}{query}");
        req.Headers.Add("Cookie", $"inkshelf_session={Uri.EscapeDataString(protector.Protect("access\nrefresh"))}");
        var res = await client.SendAsync(req);
        return (itemsQuery ?? "", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Plain_listing_defaults_to_added_descending()
    {
        var (q, html) = await GetListing("");
        Assert.Contains("sort=addedAt", q);
        Assert.Contains("desc=1", q);
        // …and the sort bar says so, rather than showing an unsorted-looking bar.
        Assert.Contains("Added &#x2193;", html);
        // Next click turns sorting OFF, and says so in the query — an absent
        // sort now means the default, so the link would otherwise do nothing.
        Assert.Contains($"/library/{LibId}?sort={SortLinks.Off}\"", html);
    }

    [Fact]
    public async Task Off_sends_no_sort_and_cycles_back_to_ascending()
    {
        var (q, html) = await GetListing($"?sort={SortLinks.Off}");
        Assert.DoesNotContain("sort=", q);          // ABS decides the order
        Assert.DoesNotContain("&#x2193;", html);    // no arrow anywhere
        Assert.DoesNotContain("&#x2191;", html);
        Assert.Contains($"/library/{LibId}?sort=addedAt\"", html);   // → ascending
    }

    [Fact]
    public async Task Explicit_descending_cycles_to_off_not_to_the_default()
    {
        var (_, html) = await GetListing("?sort=addedAt&desc=1");
        Assert.Contains("Added &#x2193;", html);
        Assert.Contains($"/library/{LibId}?sort={SortLinks.Off}\"", html);
    }

    [Theory]
    [InlineData("?series=Dune")]
    [InlineData("?author=Herbert")]
    [InlineData("?filter=series.czE%3D")]
    public async Task Facet_listing_keeps_abs_order(string query)
    {
        var (q, html) = await GetListing(query);
        Assert.DoesNotContain("sort=", q);
        Assert.DoesNotContain("desc=", q);
        Assert.DoesNotContain("Added &#x2193;", html);
    }

    [Fact]
    public async Task An_explicit_sort_still_wins_on_the_plain_listing()
    {
        var (q, _) = await GetListing("?sort=media.metadata.title");
        Assert.Contains("sort=media.metadata.title", q);
        Assert.DoesNotContain("desc=1", q);
    }
}
