using Inkshelf.Localization;

namespace Inkshelf.Tests;

public class LocalizationCatalogTests
{
    private static string WriteLocales(params (string lang, string json)[] files)
    {
        var dir = Path.Combine(Path.GetTempPath(), "loc-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        foreach (var (lang, json) in files)
            File.WriteAllText(Path.Combine(dir, $"{lang}.json"), json);
        return dir;
    }

    [Fact]
    public void Get_returns_translation_on_hit()
    {
        var c = LocalizationCatalog.Load(WriteLocales(("de", """{"Download":"Herunterladen"}""")));
        Assert.Equal("Herunterladen", c.Get("de", "Download"));
    }

    [Fact]
    public void Get_falls_back_to_key_on_miss_or_unknown_lang()
    {
        var c = LocalizationCatalog.Load(WriteLocales(("de", """{"Download":"Herunterladen"}""")));
        Assert.Equal("Save", c.Get("de", "Save"));      // missing key
        Assert.Equal("Download", c.Get("fr", "Download")); // unknown lang
        Assert.Equal("Download", c.Get(null, "Download")); // English
    }

    [Fact]
    public void Malformed_file_is_skipped_not_thrown()
    {
        var c = LocalizationCatalog.Load(WriteLocales(
            ("de", "{ this is not json "),
            ("es", """{"Download":"Descargar"}""")));
        Assert.False(c.Has("de"));
        Assert.Equal("Descargar", c.Get("es", "Download"));
    }

    [Fact]
    public void Missing_directory_yields_empty_catalog()
    {
        var c = LocalizationCatalog.Load(Path.Combine(Path.GetTempPath(), "nope-" + Path.GetRandomFileName()));
        Assert.Empty(c.Languages);
        Assert.Equal("Download", c.Get("de", "Download"));
    }

    [Fact]
    public void DisplayName_uses_name_key_then_falls_back_to_code()
    {
        var c = LocalizationCatalog.Load(WriteLocales(
            ("de", """{"$name":"Deutsch","Download":"Herunterladen"}"""),
            ("es", """{"Download":"Descargar"}""")));
        Assert.Equal("Deutsch", c.DisplayName("de"));
        Assert.Equal("es", c.DisplayName("es"));
    }
}
