using Inkshelf.Convert;

namespace Inkshelf.Tests;

public class EpubCacheTests
{
    private static string TempDirPath() { var d = Path.Combine(Path.GetTempPath(), "ic-" + Path.GetRandomFileName()); Directory.CreateDirectory(d); return d; }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "inkshelf-tests-" + Guid.NewGuid().ToString("N"));
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, true); } catch (IOException) { } }
    }

    [Fact]
    public void PathFor_uses_id_size_mtime_and_cap()
    {
        var c = new EpubCache(TempDirPath());
        Assert.EndsWith("i1-100-200-1730x2246.epub", c.PathFor("i1", 100, 200, 1730, 2246));
    }

    [Fact]
    public void PathFor_differs_by_cap()
    {
        var c = new EpubCache(TempDirPath());
        Assert.NotEqual(c.PathFor("i1", 100, 200, 800, 1000), c.PathFor("i1", 100, 200, 1730, 2246));
    }

    [Fact]
    public void PathFor_grayscale_differs_from_colour()
    {
        var c = new EpubCache(TempDirPath());
        Assert.NotEqual(
            c.PathFor("i1", 100, 200, 800, 1000, grayscale: false),
            c.PathFor("i1", 100, 200, 800, 1000, grayscale: true));
    }

    [Fact]
    public void PathFor_grayscale_uses_g_marker()
    {
        var c = new EpubCache(TempDirPath());
        Assert.EndsWith("i1-100-200-800x1000-g.epub", c.PathFor("i1", 100, 200, 800, 1000, grayscale: true));
        Assert.EndsWith("i1-100-200-800x1000.epub", c.PathFor("i1", 100, 200, 800, 1000, grayscale: false));
    }

    [Fact]
    public void SweepTemp_deletes_tmp_but_keeps_epub()
    {
        using var dir = new TempDir();
        var cache = new EpubCache(dir.Path);
        File.WriteAllText(Path.Combine(dir.Path, "a.epub.tmp"), "partial");
        File.WriteAllText(Path.Combine(dir.Path, "b.epub"), "real");
        cache.SweepTemp();
        Assert.False(File.Exists(Path.Combine(dir.Path, "a.epub.tmp")));
        Assert.True(File.Exists(Path.Combine(dir.Path, "b.epub")));
    }

    [Fact]
    public void RemoveForItem_deletes_all_variants()
    {
        var dir = TempDirPath();
        var c = new EpubCache(dir);
        File.WriteAllText(c.PathFor("i1", 1, 1, 0, 0), "a");
        File.WriteAllText(c.PathFor("i1", 2, 2, 800, 1000), "b");
        File.WriteAllText(c.PathFor("i2", 1, 1, 0, 0), "c");
        c.RemoveForItem("i1");
        Assert.False(c.TryGet("i1", 1, 1, 0, 0, false, out _));
        Assert.False(c.TryGet("i1", 2, 2, 800, 1000, false, out _));
        Assert.True(c.TryGet("i2", 1, 1, 0, 0, false, out _));
    }

    [Fact]
    public void EnforceCap_deletes_oldest_until_under_cap()
    {
        var dir = Path.Combine(Path.GetTempPath(), "inkshelf-cache-" + Guid.NewGuid().ToString("N"));
        var cache = new Inkshelf.Convert.EpubCache(dir);
        try
        {
            // three 100-byte files, ages oldest→newest
            var now = DateTime.UtcNow;
            for (var i = 0; i < 3; i++)
            {
                var p = Path.Combine(dir, $"item{i}-1-1-10x10.epub");
                File.WriteAllBytes(p, new byte[100]);
                File.SetLastWriteTimeUtc(p, now.AddMinutes(i)); // item0 oldest, item2 newest
            }
            cache.EnforceCap(250); // room for ~2 files → oldest (item0) evicted
            Assert.False(File.Exists(Path.Combine(dir, "item0-1-1-10x10.epub")));
            Assert.True(File.Exists(Path.Combine(dir, "item2-1-1-10x10.epub")));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Touch_bumps_last_write_time()
    {
        var dir = Path.Combine(Path.GetTempPath(), "inkshelf-cache-" + Guid.NewGuid().ToString("N"));
        var cache = new Inkshelf.Convert.EpubCache(dir);
        try
        {
            var p = Path.Combine(dir, "x-1-1-10x10.epub");
            File.WriteAllBytes(p, new byte[10]);
            File.SetLastWriteTimeUtc(p, DateTime.UtcNow.AddDays(-1));
            cache.Touch(p);
            Assert.True(File.GetLastWriteTimeUtc(p) > DateTime.UtcNow.AddMinutes(-1));
        }
        finally { Directory.Delete(dir, true); }
    }
}
