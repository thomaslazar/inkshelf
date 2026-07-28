using Inkshelf;

namespace Inkshelf.Tests;

public class DownloadMarksTests
{
    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "marks-" + Guid.NewGuid().ToString("N"));
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, true); } catch (IOException) { } }
    }

    private const string Did = "abc123def4560000";

    [Fact]
    public void Add_then_Read_round_trips()
    {
        using var dir = new TempDir();
        var m = new DownloadMarks(dir.Path);
        m.Add(Did, DownloadMarks.RawKey("item1", null));
        Assert.Contains(DownloadMarks.RawKey("item1", null), m.Read(Did));
    }

    [Fact]
    public void Read_of_an_unknown_device_is_empty()
    {
        using var dir = new TempDir();
        Assert.Empty(new DownloadMarks(dir.Path).Read("neverseen00000000"));
    }

    [Fact]
    public void Raw_and_epub_keys_are_distinct()
    {
        // THE important one. Both actions sit in the same row; a shared key would
        // make downloading the raw ebook light up the EPUB action as fetched.
        using var dir = new TempDir();
        var m = new DownloadMarks(dir.Path);
        m.Add(Did, DownloadMarks.RawKey("item1", null));
        var set = m.Read(Did);
        Assert.Contains(DownloadMarks.RawKey("item1", null), set);
        Assert.DoesNotContain(DownloadMarks.EpubKey("item1", null), set);
    }

    [Fact]
    public void Primary_and_per_file_keys_are_distinct()
    {
        using var dir = new TempDir();
        var m = new DownloadMarks(dir.Path);
        m.Add(Did, DownloadMarks.RawKey("item1", "14237"));
        var set = m.Read(Did);
        Assert.Contains(DownloadMarks.RawKey("item1", "14237"), set);
        Assert.DoesNotContain(DownloadMarks.RawKey("item1", null), set);
    }

    [Fact]
    public void Devices_do_not_see_each_other()
    {
        using var dir = new TempDir();
        var m = new DownloadMarks(dir.Path);
        m.Add(Did, DownloadMarks.RawKey("item1", null));
        Assert.Empty(m.Read("0000111122223333"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("../../etc/passwd")]
    [InlineData("a/b")]
    [InlineData("..")]
    public void A_hostile_or_blank_device_id_reads_empty_and_writes_nothing(string did)
    {
        using var dir = new TempDir();
        var m = new DownloadMarks(dir.Path);
        m.Add(did, DownloadMarks.RawKey("item1", null));

        Assert.Empty(m.Read(did));
        // Nothing was created anywhere under the marks dir, and no fallback file
        // pooled the request into a shared bucket.
        Assert.Empty(Directory.GetFiles(dir.Path, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void Adding_the_same_key_twice_does_not_duplicate_it()
    {
        using var dir = new TempDir();
        var m = new DownloadMarks(dir.Path);
        m.Add(Did, DownloadMarks.RawKey("item1", null));
        m.Add(Did, DownloadMarks.RawKey("item1", null));
        Assert.Single(m.Read(Did));
        Assert.Single(File.ReadAllLines(Path.Combine(dir.Path, Did)));
    }

    [Fact]
    public void Concurrent_Add_calls_do_not_lose_or_corrupt_marks()
    {
        using var dir = new TempDir();
        var m = new DownloadMarks(dir.Path);
        const int count = 200;
        var keys = Enumerable.Range(0, count)
            .Select(i => DownloadMarks.RawKey($"item{i}", null)).ToArray();

        Parallel.ForEach(keys, key => m.Add(Did, key));

        var lines = File.ReadAllLines(Path.Combine(dir.Path, Did));
        Assert.DoesNotContain(lines, l => l.Length == 0);
        var set = m.Read(Did);
        foreach (var key in keys) Assert.Contains(key, set);
    }

    [Fact]
    public void Prune_deletes_stale_devices_and_keeps_fresh_ones()
    {
        using var dir = new TempDir();
        var m = new DownloadMarks(dir.Path);
        m.Add("staleaaaaaaaaaaa", DownloadMarks.RawKey("old", null));
        m.Add("freshbbbbbbbbbbb", DownloadMarks.RawKey("new", null));
        File.SetLastWriteTimeUtc(Path.Combine(dir.Path, "staleaaaaaaaaaaa"),
            DateTime.UtcNow - TimeSpan.FromDays(31));

        m.Prune(TimeSpan.FromDays(30));

        Assert.Empty(m.Read("staleaaaaaaaaaaa"));
        Assert.Single(m.Read("freshbbbbbbbbbbb"));
    }

    [Fact]
    public void Read_refreshes_the_files_timestamp_so_an_active_device_is_not_pruned()
    {
        // "Untouched" must mean "this device hasn't used the app", not "hasn't
        // downloaded" — otherwise browsing for a month prunes your marks mid-use.
        using var dir = new TempDir();
        var m = new DownloadMarks(dir.Path);
        m.Add(Did, DownloadMarks.RawKey("item1", null));
        var path = Path.Combine(dir.Path, Did);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow - TimeSpan.FromDays(10));

        m.Read(Did);

        Assert.True(File.GetLastWriteTimeUtc(path) > DateTime.UtcNow - TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void Read_does_not_rewrite_a_recently_touched_file()
    {
        // Rate-limited so rendering doesn't amplify into a write per request.
        using var dir = new TempDir();
        var m = new DownloadMarks(dir.Path);
        m.Add(Did, DownloadMarks.RawKey("item1", null));
        var path = Path.Combine(dir.Path, Did);
        var stamp = DateTime.UtcNow - TimeSpan.FromHours(2);
        File.SetLastWriteTimeUtc(path, stamp);

        m.Read(Did);

        Assert.Equal(stamp, File.GetLastWriteTimeUtc(path));
    }
}
