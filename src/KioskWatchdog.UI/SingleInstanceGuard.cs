namespace KioskWatchdog;

/// <summary>
/// Ensures only one configuration UI process runs per user session.
/// A second launch signals the existing instance to show its window, then exits.
/// </summary>
internal sealed class SingleInstanceGuard : IDisposable
{
    private const string MutexName = @"Local\KioskWatchdog.UI.SingleInstance";
    private const string ShowEventName = @"Local\KioskWatchdog.UI.ShowWindow";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _showEvent;
    private CancellationTokenSource? _cts;
    private bool _disposed;

    private SingleInstanceGuard(Mutex mutex, EventWaitHandle showEvent)
    {
        _mutex = mutex;
        _showEvent = showEvent;
    }

    /// <summary>
    /// Acquires the UI single-instance lock. If another UI is running, signals it to show and returns false.
    /// </summary>
    public static bool TryEnter(out SingleInstanceGuard? guard)
    {
        var mutex = new Mutex(initiallyOwned: false, name: MutexName);
        bool acquired;
        try
        {
            acquired = mutex.WaitOne(TimeSpan.Zero);
        }
        catch (AbandonedMutexException)
        {
            // Previous owner crashed; we now own the mutex.
            acquired = true;
        }

        var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);

        if (!acquired)
        {
            showEvent.Set();
            showEvent.Dispose();
            mutex.Dispose();
            guard = null;
            return false;
        }

        guard = new SingleInstanceGuard(mutex, showEvent);
        return true;
    }

    public void StartListening(Action onShowRequested)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        var thread = new Thread(() =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (_showEvent.WaitOne(500) && !token.IsCancellationRequested)
                        onShowRequested();
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
            }
        })
        {
            IsBackground = true,
            Name = "KioskWatchdog.SingleInstance"
        };
        thread.Start();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _cts?.Cancel();
        _cts?.Dispose();

        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Not owned (should not happen for the primary instance).
        }

        _mutex.Dispose();
        _showEvent.Dispose();
    }
}
