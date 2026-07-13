namespace Inkshelf.Convert;

public class EpubCache
{
    private readonly string _dir;
    public EpubCache(string dir) { _dir = dir; Directory.CreateDirectory(_dir); }

    // The downscale target (maxW×maxH) is part of the key: two devices with
    // different screen resolutions must not be served each other's variant.
    public string PathFor(string itemId, long size, long mtimeMs, int maxW, int maxH) =>
        Path.Combine(_dir, $"{itemId}-{size}-{mtimeMs}-{maxW}x{maxH}.epub");

    public bool TryGet(string itemId, long size, long mtimeMs, int maxW, int maxH, out string path)
    {
        path = PathFor(itemId, size, mtimeMs, maxW, maxH);
        return File.Exists(path);
    }

    public void RemoveForItem(string itemId)
    {
        foreach (var f in Directory.EnumerateFiles(_dir, $"{itemId}-*.epub"))
        {
            try { File.Delete(f); } catch (IOException) { }
        }
    }
}
