using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Inkshelf.Localization;

// Immutable in-memory translation catalog loaded once at startup from
// <lang>.json files. English has no file: the source string is the key, and a
// miss returns the key verbatim. See the UI localisation design spec.
public sealed class LocalizationCatalog
{
    public const string NameKey = "$name";

    // lang code (case-insensitive) → (English key → translation)
    private readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> _byLang;

    private LocalizationCatalog(IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> byLang)
        => _byLang = byLang;

    // Loaded language codes (English is implicit and not listed).
    public IReadOnlyCollection<string> Languages => (IReadOnlyCollection<string>)_byLang.Keys;

    public bool Has(string lang) => _byLang.ContainsKey(lang);

    // Translation for key in lang, or the key itself (English fallback) when the
    // language is unknown/null or the key is absent/empty.
    public string Get(string? lang, string key)
        => lang is not null && _byLang.TryGetValue(lang, out var d)
           && d.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v) ? v : key;

    // Menu label: the file's "$name", else the bare code.
    public string DisplayName(string lang)
        => _byLang.TryGetValue(lang, out var d) && d.TryGetValue(NameKey, out var n)
           && !string.IsNullOrWhiteSpace(n) ? n : lang;

    // Load every *.json in dir. A malformed/unreadable file is logged and skipped
    // — a bad translation file must never crash the sidecar. Missing dir → empty.
    public static LocalizationCatalog Load(string dir, ILogger? logger = null)
    {
        var result = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(dir))
        {
            string[] files;
            try
            {
                files = Directory.GetFiles(dir, "*.json");
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Cannot list locale directory {Dir}", dir);
                files = [];
            }
            foreach (var file in files)
            {
                var lang = Path.GetFileNameWithoutExtension(file);
                try
                {
                    var map = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file));
                    if (map is not null) result[lang] = map;
                }
                catch (Exception ex)
                {
                    logger?.LogWarning(ex, "Skipping malformed locale file {File}", file);
                }
            }
        }
        return new LocalizationCatalog(result);
    }
}
