using Inkshelf.Convert;

namespace Inkshelf.Tests;

public class ConvertQueueTests
{
    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), "cq-" + Guid.NewGuid().ToString("N") + ".epub");

    private static ConvertJob Job(string path) =>
        new("item1", "tok", path, new EbookMeta("T", "A", null, null, "item1"), new RenderTarget(100, 200, 1.0, false));

    [Fact]
    public void Enqueue_new_path_returns_Queued_and_writes_one_channel_item()
    {
        var q = new ConvertQueue();
        var path = TempPath();
        Assert.Equal(ConvertStatus.Queued, q.Enqueue(Job(path)));
        Assert.True(q.Reader.TryRead(out var job));
        Assert.Equal(path, job!.CachePath);
        Assert.False(q.Reader.TryRead(out _)); // exactly one
    }

    [Fact]
    public void Enqueue_is_idempotent_while_queued()
    {
        var q = new ConvertQueue();
        var path = TempPath();
        q.Enqueue(Job(path));
        Assert.Equal(ConvertStatus.Queued, q.Enqueue(Job(path))); // no-op
        Assert.True(q.Reader.TryRead(out _));
        Assert.False(q.Reader.TryRead(out _)); // still only one
    }

    [Fact]
    public void Status_is_Done_when_file_exists_and_clears_registry()
    {
        var q = new ConvertQueue();
        var path = TempPath();
        File.WriteAllText(path, "epub");
        try
        {
            q.Enqueue(Job(path)); // even if we thought it queued...
            Assert.Equal(ConvertStatus.Done, q.Status(path)); // file wins
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Status_is_None_for_unknown_path() =>
        Assert.Equal(ConvertStatus.None, new ConvertQueue().Status(TempPath()));

    [Fact]
    public void MarkFailed_shows_Failed_and_a_retry_reenqueues()
    {
        var q = new ConvertQueue();
        var path = TempPath();
        q.Enqueue(Job(path));
        q.Reader.TryRead(out _);
        q.MarkFailed(path);
        Assert.Equal(ConvertStatus.Failed, q.Status(path));
        Assert.Equal(ConvertStatus.Queued, q.Enqueue(Job(path))); // retry clears Failed
        Assert.True(q.Reader.TryRead(out _));
    }

    [Fact]
    public void Failed_sweeps_to_None_after_ttl()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var q = new ConvertQueue(() => now);
        var path = TempPath();
        q.Enqueue(Job(path));
        q.Reader.TryRead(out _);
        q.MarkFailed(path);
        now = now.AddMinutes(11); // past the 10-min TTL
        Assert.Equal(ConvertStatus.None, q.Status(path));
    }

    [Fact]
    public void MarkDone_removes_the_entry()
    {
        var q = new ConvertQueue();
        var path = TempPath();
        q.Enqueue(Job(path));
        q.Reader.TryRead(out _);
        q.MarkRunning(path);
        q.MarkDone(path);
        Assert.Equal(ConvertStatus.None, q.Status(path)); // no file, no entry
    }

    [Fact]
    public void FailureFor_returns_reason_and_bytes_while_failed()
    {
        var q = new ConvertQueue();
        var path = TempPath();
        q.Enqueue(Job(path));
        q.Reader.TryRead(out _);
        q.MarkFailed(path, ConvertFailReason.TooLarge, 1_500_000_000);
        var f = q.FailureFor(path);
        Assert.NotNull(f);
        Assert.Equal(ConvertFailReason.TooLarge, f!.Value.Reason);
        Assert.Equal(1_500_000_000, f.Value.ArchiveBytes);
    }

    [Fact]
    public void FailureFor_is_null_when_not_failed()
    {
        var q = new ConvertQueue();
        var path = TempPath();
        q.Enqueue(Job(path));            // Queued, not Failed
        Assert.Null(q.FailureFor(path));
        Assert.Null(q.FailureFor(TempPath())); // unknown path
    }

    [Fact]
    public void FailureFor_is_null_after_ttl()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var q = new ConvertQueue(() => now);
        var path = TempPath();
        q.Enqueue(Job(path));
        q.Reader.TryRead(out _);
        q.MarkFailed(path, ConvertFailReason.BadArchive);
        now = now.AddMinutes(11);
        Assert.Null(q.FailureFor(path));
    }
}
