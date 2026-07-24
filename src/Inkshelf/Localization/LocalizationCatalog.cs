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
        => Load([dir], logger);

    // Load and merge *.json across dirs, in order: languages union, and later
    // dirs win per-key (so an override dir can add a language or replace a few
    // strings without copying the whole baseline file). Malformed/unreadable
    // files are logged and skipped — loading must never crash the sidecar.
    public static LocalizationCatalog Load(IReadOnlyList<string> dirs, ILogger? logger = null)
    {
        var merged = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in dirs)
        {
            if (!Directory.Exists(dir)) continue;
            string[] files;
            try
            {
                files = Directory.GetFiles(dir, "*.json");
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Cannot list locale directory {Dir}", dir);
                continue;
            }
            foreach (var file in files)
            {
                var lang = Path.GetFileNameWithoutExtension(file);
                try
                {
                    var map = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file));
                    if (map is null) continue;
                    if (!merged.TryGetValue(lang, out var acc))
                        merged[lang] = acc = new Dictionary<string, string>();
                    foreach (var kv in map) acc[kv.Key] = kv.Value; // later dir/file wins
                }
                catch (Exception ex)
                {
                    logger?.LogWarning(ex, "Skipping malformed locale file {File}", file);
                }
            }
        }
        var frozen = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in merged) frozen[kv.Key] = kv.Value;
        return new LocalizationCatalog(frozen);
    }
}
