internal sealed class SingleInstanceLock : IDisposable
{
    // Global also spans Windows login sessions. No profile/config path in the name:
    // different launch directories must still contend for the same instance lock.
    const string DefaultName = @"Global\lltop-single-instance";
    readonly Mutex mutex;
    bool disposed;

    SingleInstanceLock(Mutex mutex) => this.mutex = mutex;

    public static SingleInstanceLock? TryAcquire(string name = DefaultName)
    {
        var mutex = new Mutex(false, name);
        try
        {
            bool acquired;
            try { acquired = mutex.WaitOne(0); }
            catch (AbandonedMutexException) { acquired = true; }
            if (acquired) return new SingleInstanceLock(mutex);
            mutex.Dispose();
            return null;
        }
        catch
        {
            mutex.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        mutex.ReleaseMutex();
        mutex.Dispose();
        disposed = true;
    }
}
