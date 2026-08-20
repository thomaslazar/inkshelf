namespace Inkshelf.Convert;

public class EpubCache
{
    private readonly string _dir;
    public EpubCache(string dir) { _dir = dir; Directory.CreateDirectory(_dir); }

    // EVERY knob that changes the bytes we write is part of the key: the downscale
    // target (maxW×maxH), grayscale, the spread mode, and the page scale. Two devices
    // with different screens — or the same device before and after the user changes a
    // setting — must never be served each other's variant.
    //
    // The spread letter is emitted ALWAYS, including for the default, so that files
    // cached by an older build (which had no spread handling at all) can never be
    // mistaken for a current one. Scale is emitted only when it is not 100.
    public string PathFor(string itemId, long size, long mtimeMs, int maxW, int maxH,
        bool grayscale = false, SpreadMode spread = SpreadMode.Fit, int scale = 100) =>
        Path.Combine(_dir, $"{itemId}-{size}-{mtimeMs}-{maxW}x{maxH}{(grayscale ? "-g" : "")}"
            + $"-{Letter(spread)}{(scale == 100 ? "" : $"-s{scale}")}.epub");

    // `out path` sits before the optional knobs so the existing call sites keep working.
    public bool TryGet(string itemId, long size, long mtimeMs, int maxW, int maxH, bool grayscale,
        out string path, SpreadMode spread = SpreadMode.Fit, int scale = 100)
    {
        path = PathFor(itemId, size, mtimeMs, maxW, maxH, grayscale, spread, scale);
        return File.Exists(path);
    }

    // One letter per spread mode. Deliberately NOT reusing the letters an earlier
    // build wrote ('h' for split, 'r' for rotate): those files were laid out
    // differently, so they must fall out as unrecognised rather than be misread as a
    // mode that now means something else. 's' is avoided too — it would be ambiguous
    // with the "-s95" scale suffix parsed alongside this letter.
    private static char Letter(SpreadMode m) => m switch
    {
        SpreadMode.SplitLeftFirst => 'l',
        SpreadMode.SplitRightFirst => 'm',
        SpreadMode.RotateLeft => 'a',   // anticlockwise
        SpreadMode.RotateRight => 'c',  // clockwise
        _ => 'f',
    };

    private static SpreadMode? ModeOf(char c) => c switch
    {
        'l' => SpreadMode.SplitLeftFirst,
        'm' => SpreadMode.SplitRightFirst,
        'a' => SpreadMode.RotateLeft,
        'c' => SpreadMode.RotateRight,
        'f' => SpreadMode.Fit,
        _ => null,
    };

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
        DateTime ConvertedAtUtc, SpreadMode Spread = SpreadMode.Fit, int Scale = 100);

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

        // Parsed in the reverse of PathFor's order: scale, spread, grayscale, dims.
        var scale = 100;
        var si = name.LastIndexOf("-s", StringComparison.Ordinal);
        if (si > 0 && int.TryParse(name[(si + 2)..], out var parsedScale))
        { scale = parsedScale; name = name[..si]; }

        // The spread letter is mandatory — a name without one was written by a build
        // that predates spread handling, and its pages are laid out differently.
        if (name.Length < 2 || name[^2] != '-' || ModeOf(name[^1]) is not { } spread) return null;
        name = name[..^2];

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
        return new CachedVariant(itemId, size, mtimeMs, maxW, maxH, grayscale, path,
            file.LastWriteTimeUtc, spread, scale);
    }
}
