using Xunit;

public sealed class SingleInstanceLockTests
{
    [Fact]
    public void RejectsAnotherOwnerAndAllowsRestartAfterRelease()
    {
        var name = "lltop-test-" + Guid.NewGuid().ToString("N");
        using (var first = SingleInstanceLock.TryAcquire(name))
        {
            Assert.NotNull(first);
            SingleInstanceLock? second = null;
            var contender = new Thread(() => second = SingleInstanceLock.TryAcquire(name));
            contender.Start();
            Assert.True(contender.Join(TimeSpan.FromSeconds(5)));
            Assert.Null(second);
        }
        using var restarted = SingleInstanceLock.TryAcquire(name);
        Assert.NotNull(restarted);
    }

    [Fact]
    public void RecoversWhenOwnerExitsWithoutReleasing()
    {
        var name = "lltop-test-" + Guid.NewGuid().ToString("N");
        // Keep a handle open so this exercises abandonment, not object recreation.
        using var observer = new Mutex(false, name);
        var owner = new Thread(() =>
        {
            var mutex = new Mutex(false, name);
            mutex.WaitOne();
            mutex.Dispose();
        });
        owner.Start();
        Assert.True(owner.Join(TimeSpan.FromSeconds(5)));
        using var recovered = SingleInstanceLock.TryAcquire(name);
        Assert.NotNull(recovered);
    }
}
