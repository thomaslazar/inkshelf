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

    // Bump a served file's timestamp so EnforceCap treats recently-used entries as
    // "new" (approximate LRU — serving a file doesn't otherwise touch its mtime).
    public void Touch(string path)
    {
        try { if (File.Exists(path)) File.SetLastWriteTimeUtc(path, DateTime.UtcNow); }
        catch (IOException) { }
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

    // Evict oldest-by-write-time entries until total cache bytes are under the cap.
    // No-op when maxBytes <= 0 or already under. Best-effort (ignores IO races).
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
}
