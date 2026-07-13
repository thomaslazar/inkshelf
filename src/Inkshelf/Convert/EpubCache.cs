namespace Inkshelf.Convert;

public class EpubCache
{
    private readonly string _dir;
    public EpubCache(string dir) { _dir = dir; Directory.CreateDirectory(_dir); }

    public string PathFor(string itemId, long size, long mtimeMs) =>
        Path.Combine(_dir, $"{itemId}-{size}-{mtimeMs}.epub");

    public bool TryGet(string itemId, long size, long mtimeMs, out string path)
    {
        path = PathFor(itemId, size, mtimeMs);
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
