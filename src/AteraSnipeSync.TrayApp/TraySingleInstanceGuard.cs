namespace AteraSnipeSync.TrayApp;

/// <summary>
/// Owns the per-session Tray mutex and prevents a second normal UI process while allowing elevated helper processes.
/// </summary>
internal sealed class TraySingleInstanceGuard : IDisposable
{
    private const string MutexName = @"Local\AteraSnipeSync.TrayApp";
    private readonly Mutex _mutex;
    private bool _ownsMutex;

    private TraySingleInstanceGuard(Mutex mutex)
    {
        _mutex = mutex;
        _ownsMutex = true;
    }

    public static bool TryAcquire(out TraySingleInstanceGuard? guard)
    {
        var mutex = new Mutex(initiallyOwned: false, MutexName);
        try
        {
            if (!mutex.WaitOne(0))
            {
                mutex.Dispose();
                guard = null;
                return false;
            }
        }
        catch (AbandonedMutexException)
        {
            // The abandoned mutex is acquired by the current process.
        }

        guard = new TraySingleInstanceGuard(mutex);
        return true;
    }

    public void Dispose()
    {
        if (_ownsMutex)
        {
            _mutex.ReleaseMutex();
            _ownsMutex = false;
        }

        _mutex.Dispose();
    }
}
