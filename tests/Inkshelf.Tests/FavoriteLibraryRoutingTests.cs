using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Inkshelf.Abs;
using Inkshelf.Auth;
using Inkshelf.Convert;
using Inkshelf.Pages;

namespace Inkshelf.Tests;

// A stale favorite-library cookie (e.g. one saved against a different ABS) must
// not blow up: the Index must not redirect into a library the current ABS
// doesn't have, and a direct hit on an unknown library must not 500.
public class FavoriteLibraryRoutingTests
{
    // AbsApiClient answering GET /api/libraries with the given libraries. Any
    // other call throws, so a test that reaches the items endpoint fails loudly.
    private static AbsApiClient LibrariesClient(params string[] ids)
    {
        var arr = string.Join(",", ids.Select(id => $"{{\"id\":\"{id}\",\"name\":\"Lib {id}\",\"mediaType\":\"book\"}}"));
        return new AbsApiClient(new HttpClient(new StubHandler(req =>
            req.RequestUri!.AbsolutePath == "/api/libraries"
                ? StubHandler.Json($"{{\"libraries\":[{arr}]}}")
                : throw new InvalidOperationException($"unexpected call: {req.RequestUri.AbsolutePath}")))
        { BaseAddress = new Uri("http://abs.local") });
    }

    private static T WithContext<T>(T model, string? favCookie) where T : PageModel
    {
        var http = new DefaultHttpContext();
        if (favCookie is not null) http.Request.Headers.Cookie = $"{Favorites.Cookie}={favCookie}";
        model.PageContext = new PageContext { HttpContext = http };
        return model;
    }

    [Fact]
    public async Task Index_redirects_to_a_favorite_that_exists_on_this_ABS()
    {
        var model = WithContext(new IndexModel(LibrariesClient("lib-1", "lib-2")), favCookie: "lib-2");
        var result = await model.OnGetAsync(all: null, default);
        var redirect = Assert.IsType<RedirectResult>(result);
        Assert.Equal("/library/lib-2", redirect.Url);
    }

    [Fact]
    public async Task Index_drops_a_stale_favorite_and_shows_the_list()
    {
        var model = WithContext(new IndexModel(LibrariesClient("lib-1", "lib-2")), favCookie: "gone-from-other-abs");
        var result = await model.OnGetAsync(all: null, default);

        Assert.IsType<PageResult>(result);                 // no redirect into the missing library
        Assert.Equal(2, model.Libraries.Count);            // the real list is shown instead
        var setCookie = model.Response.Headers.SetCookie.ToString();
        Assert.Contains(Favorites.Cookie, setCookie);      // the stale cookie is cleared
        Assert.Contains("expires=Thu, 01 Jan 1970", setCookie);
    }

    [Fact]
    public async Task Index_with_no_favorite_shows_the_list()
    {
        var model = WithContext(new IndexModel(LibrariesClient("lib-1")), favCookie: null);
        Assert.IsType<PageResult>(await model.OnGetAsync(all: null, default));
        Assert.Single(model.Libraries);
    }

    [Fact]
    public async Task Library_redirects_home_for_an_unknown_id_instead_of_500()
    {
        using var dir = new TempCacheDir();
        var model = WithContext(
            new LibraryModel(LibrariesClient("lib-1"), new EpubCache(dir.Path), new ConvertQueue()),
            favCookie: null);
        model.Id = "not-here"; // e.g. a stale favorite from another ABS, hit directly
        var result = await model.OnGetAsync();
        var redirect = Assert.IsType<RedirectResult>(result);
        Assert.Equal("/", redirect.Url);
    }

    private sealed class TempCacheDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "favrt-" + Guid.NewGuid().ToString("N"));
        public TempCacheDir() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, true); } catch (IOException) { } }
    }
}
