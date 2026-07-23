using Inkshelf.Auth;
using Inkshelf.Localization;
using Microsoft.AspNetCore.Http;

namespace Inkshelf.Tests;

public class LocalizerTests
{
    private static string WriteDe()
    {
        var dir = Path.Combine(Path.GetTempPath(), "loc-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "de.json"),
            """{"Download":"Herunterladen","Page {0} of {1}":"Seite {0} von {1}"}""");
        return dir;
    }

    private static Localizer ForRequest(Action<HttpRequest> setup)
    {
        var catalog = LocalizationCatalog.Load(WriteDe());
        var ctx = new DefaultHttpContext();
        setup(ctx.Request);
        var accessor = new HttpContextAccessor { HttpContext = ctx };
        return new Localizer(catalog, accessor);
    }

    [Fact]
    public void Explicit_cookie_choice_wins()
    {
        var l = ForRequest(r =>
        {
            r.Headers.Cookie = $"{DeviceSettings.Cookie}=10de";
            r.Headers.AcceptLanguage = "en-US";
        });
        Assert.Equal("Herunterladen", l["Download"]);
    }

    [Fact]
    public void Explicit_english_overrides_german_browser()
    {
        var l = ForRequest(r =>
        {
            r.Headers.Cookie = $"{DeviceSettings.Cookie}=10en";
            r.Headers.AcceptLanguage = "de-DE,de;q=0.9";
        });
        Assert.Equal("Download", l["Download"]);
    }

    [Fact]
    public void No_choice_falls_back_to_accept_language()
    {
        var l = ForRequest(r => r.Headers.AcceptLanguage = "de-DE,de;q=0.9,en;q=0.8");
        Assert.Equal("Herunterladen", l["Download"]);
    }

    [Fact]
    public void No_match_falls_back_to_english()
    {
        var l = ForRequest(r => r.Headers.AcceptLanguage = "fr-FR,fr;q=0.9");
        Assert.Equal("Download", l["Download"]);
    }

    [Fact]
    public void Format_args_applied_to_resolved_template()
    {
        var l = ForRequest(r => r.Headers.Cookie = $"{DeviceSettings.Cookie}=10de");
        Assert.Equal("Seite 2 von 5", l["Page {0} of {1}", 2, 5]);
    }

    [Fact]
    public void Mismatched_args_return_unformatted_template_instead_of_throwing()
    {
        var l = ForRequest(r => r.Headers.Cookie = $"{DeviceSettings.Cookie}=10en");
        Assert.Equal("Page {0} of {1}", l["Page {0} of {1}", 5]);
    }
}
