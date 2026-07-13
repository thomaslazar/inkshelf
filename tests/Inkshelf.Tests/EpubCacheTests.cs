using Inkshelf.Convert;

namespace Inkshelf.Tests;

public class EpubCacheTests
{
    private static string TempDir() { var d = Path.Combine(Path.GetTempPath(), "ic-" + Path.GetRandomFileName()); Directory.CreateDirectory(d); return d; }

    [Fact]
    public void PathFor_uses_id_size_mtime()
    {
        var c = new EpubCache(TempDir());
        Assert.EndsWith("i1-100-200.epub", c.PathFor("i1", 100, 200));
    }

    [Fact]
    public void RemoveForItem_deletes_all_variants()
    {
        var dir = TempDir();
        var c = new EpubCache(dir);
        File.WriteAllText(c.PathFor("i1", 1, 1), "a");
        File.WriteAllText(c.PathFor("i1", 2, 2), "b");
        File.WriteAllText(c.PathFor("i2", 1, 1), "c");
        c.RemoveForItem("i1");
        Assert.False(c.TryGet("i1", 1, 1, out _));
        Assert.False(c.TryGet("i1", 2, 2, out _));
        Assert.True(c.TryGet("i2", 1, 1, out _));
    }
}
