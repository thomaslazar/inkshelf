namespace Inkshelf.Convert;

public class EpubCache
{
    private readonly string _dir;
    public EpubCache(string dir) { _dir = dir; Directory.CreateDirectory(_dir); }

    // The downscale target (maxW×maxH) AND grayscale are part of the key: two
    // devices with different screen resolutions, or colour vs grayscale, must not be
    // served each other's variant.
    public string PathFor(string itemId, long size, long mtimeMs, int maxW, int maxH, bool grayscale = false) =>
        Path.Combine(_dir, $"{itemId}-{size}-{mtimeMs}-{maxW}x{maxH}{(grayscale ? "-g" : "")}.epub");

    public bool TryGet(string itemId, long size, long mtimeMs, int maxW, int maxH, bool grayscale, out string path)
    {
        path = PathFor(itemId, size, mtimeMs, maxW, maxH, grayscale);
        return File.Exists(path);
    }

    public void RemoveForItem(string itemId)
    {
        foreach (var f in Directory.EnumerateFiles(_dir, $"{itemId}-*.epub"))
        {
            try { File.Delete(f); } catch (IOException) { }
        }
    }

    // Delete orphan .tmp files (a crash/shutdown between EpubWriter's temp write
    // and its atomic rename leaves one). Called once at worker startup.
    public void SweepTemp()
    {
        foreach (var f in Directory.EnumerateFiles(_dir, "*.tmp"))
        {
            try { File.Delete(f); } catch (IOException) { }
        }
    }

    // Evict oldest-by-conversion-time entries until total cache bytes are under the
    // cap. FIFO, not LRU, and deliberately so: this cache bridges one expensive
    // conversion to one download, after which the EPUB lives on the reader. Nothing
    // re-stamps a served file, so write time stays the conversion time — which is
    // also what /converted sorts on. No-op when maxBytes <= 0 or already under.
    // Best-effort (ignores IO races).
    public void EnforceCap(long maxBytes)
    {
        if (maxBytes <= 0) return;
        var files = new DirectoryInfo(_dir).GetFiles("*.epub");
        var total = files.Sum(f => f.Length);
        if (total <= maxBytes) return;
        foreach (var f in files.OrderBy(f => f.LastWriteTimeUtc))
        {
            if (total <= maxBytes) break;
            try { total -= f.Length; f.Delete(); } catch (IOException) { }
        }
    }

    // One cached EPUB, decoded back into its cache-key parts. Mirrors PathFor.
    // NOTE two different timestamps live here: MtimeMs is the SOURCE ebook file's
    // mtime in ABS, part of the cache key so a changed source invalidates the
    // entry. ConvertedAtUtc is when WE wrote this EPUB. Anything user-facing about
    // "when was this converted" wants ConvertedAtUtc.
    public sealed record CachedVariant(
        string ItemId, long Size, long MtimeMs, int MaxW, int MaxH, bool Grayscale, string Path,
        DateTime ConvertedAtUtc);

    // Enumerate cached EPUBs, parsing each filename back into its parts. Parsed
    // RIGHT-TO-LEFT (dims, then mtime, then size) so an item id containing '-'
    // (a UUID) survives intact. Filenames that don't match PathFor are skipped.
    public IEnumerable<CachedVariant> ListVariants()
    {
        foreach (var f in new DirectoryInfo(_dir).EnumerateFiles("*.epub"))
        {
            if (TryParse(f) is { } v) yield return v;
        }
    }

    private static CachedVariant? TryParse(FileInfo file)
    {
        var path = file.FullName;
        var name = System.IO.Path.GetFileNameWithoutExtension(path); // drops ".epub"
        var grayscale = name.EndsWith("-g", StringComparison.Ordinal);
        if (grayscale) name = name[..^2];

        // remaining: {itemId}-{size}-{mtimeMs}-{maxW}x{maxH}
        var d1 = name.LastIndexOf('-');
        if (d1 < 0) return null;
        var dims = name[(d1 + 1)..];
        var xi = dims.IndexOf('x');
        if (xi <= 0
            || !int.TryParse(dims[..xi], out var maxW)
            || !int.TryParse(dims[(xi + 1)..], out var maxH)) return null;

        name = name[..d1]; // {itemId}-{size}-{mtimeMs}
        var d2 = name.LastIndexOf('-');
        if (d2 < 0 || !long.TryParse(name[(d2 + 1)..], out var mtimeMs)) return null;

        name = name[..d2]; // {itemId}-{size}
        var d3 = name.LastIndexOf('-');
        if (d3 < 0 || !long.TryParse(name[(d3 + 1)..], out var size)) return null;

        var itemId = name[..d3];
        if (itemId.Length == 0) return null;
        return new CachedVariant(itemId, size, mtimeMs, maxW, maxH, grayscale, path, file.LastWriteTimeUtc);
    }
}
