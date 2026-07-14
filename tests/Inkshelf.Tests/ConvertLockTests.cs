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
}
