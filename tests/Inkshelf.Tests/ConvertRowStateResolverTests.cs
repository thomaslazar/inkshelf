using Inkshelf.Abs;
using Inkshelf.Convert;
using Inkshelf.Pages;

namespace Inkshelf.Tests;

public class ConvertRowStateResolverTests
{
    private static string TempDirPath()
    { var d = Path.Combine(Path.GetTempPath(), "crs-" + Path.GetRandomFileName()); Directory.CreateDirectory(d); return d; }

    private static AbsItem Item(string fmt) =>
        new("i1", new AbsMedia(new AbsMetadata("T", null, null), EbookFormat: fmt));

    private static AbsBatchMedia Media(long size = 10, long mtime = 20) =>
        new(new AbsBatchMetadata(), new AbsEbookFile("cbz", new AbsEbookFileMetadata("f.cbz", size, mtime)));

    private static readonly RenderTarget Target = new(800, 1000, 1.0, false);

    [Fact]
    public void Non_comic_is_not_convertible()
    {
        var r = ConvertRowStateResolver.Resolve(Item("epub"), Media(), Target, new EpubCache(TempDirPath()), new ConvertQueue());
        Assert.Equal(ConvertRowState.NotConvertible, r);
    }

    [Fact]
    public void Comic_without_ebookfile_metadata_is_not_convertible()
    {
        var media = new AbsBatchMedia(new AbsBatchMetadata(), new AbsEbookFile("cbz", null));
        var r = ConvertRowStateResolver.Resolve(Item("cbz"), media, Target, new EpubCache(TempDirPath()), new ConvertQueue());
        Assert.Equal(ConvertRowState.NotConvertible, r);
    }

    [Fact]
    public void Comic_with_cached_file_is_cached()
    {
        var dir = TempDirPath();
        var cache = new EpubCache(dir);
        File.WriteAllText(cache.PathFor("i1", 10, 20, 800, 1000), "e");
        var r = ConvertRowStateResolver.Resolve(Item("cbz"), Media(), Target, cache, new ConvertQueue());
        Assert.Equal(ConvertRowState.Cached, r);
    }

    [Fact]
    public void Comic_with_nothing_cached_is_convert()
    {
        var r = ConvertRowStateResolver.Resolve(Item("cbz"), Media(), Target, new EpubCache(TempDirPath()), new ConvertQueue());
        Assert.Equal(ConvertRowState.Convert, r);
    }
}
