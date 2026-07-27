using Inkshelf.Convert;

namespace Inkshelf.Tests;

public class ConvertLockTests
{
    [Fact]
    public async Task Same_key_serializes()
    {
        var l = new ConvertLock();
        var first = await l.AcquireAsync("A", default);
        var secondTask = l.AcquireAsync("A", default);
        Assert.False(secondTask.IsCompleted); // blocked while first is held
        first.Dispose();
        var second = await secondTask;         // proceeds after release
        second.Dispose();
    }

    [Fact]
    public async Task Different_keys_run_concurrently()
    {
        var l = new ConvertLock();
        var a = await l.AcquireAsync("A", default);
        var bTask = l.AcquireAsync("B", default);
        Assert.True(bTask.IsCompleted);        // B not blocked by A
        a.Dispose();
        (await bTask).Dispose();
    }

    [Fact]
    public async Task Releases_clean_up_the_map()
    {
        var l = new ConvertLock();
        (await l.AcquireAsync("A", default)).Dispose();
        (await l.AcquireAsync("B", default)).Dispose();
        Assert.Equal(0, l.ActiveKeys);
    }

    [Fact]
    public async Task Canceling_a_queued_acquire_unwinds_its_ref_without_stranding_the_lock()
    {
        var l = new ConvertLock();
        var held = await l.AcquireAsync("A", default);

        using var cts = new CancellationTokenSource();
        var queued = l.AcquireAsync("A", cts.Token);   // blocked behind `held`
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);

        Assert.Equal(1, l.ActiveKeys);                 // canceled ref undone, holder's remains
        held.Dispose();
        Assert.Equal(0, l.ActiveKeys);                 // map self-cleaned

        // The real regression guard: a cancel that wrongly consumed a permit would
        // leave the semaphore at 0 and hang this acquire.
        var again = l.AcquireAsync("A", default);
        Assert.True(again.IsCompleted);
        (await again).Dispose();
        Assert.Equal(0, l.ActiveKeys);
    }
}
