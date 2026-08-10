namespace KioskWatchdog.Core.Status;

public sealed class WatchdogStatusStore : IWatchdogStatusStore
{
    private readonly object _gate = new();
    private WatchdogStatus _current = new();

    public event EventHandler? Changed;

    public WatchdogStatus Current
    {
        get
        {
            lock (_gate)
            {
                return _current.Clone();
            }
        }
    }

    public void Update(Action<WatchdogStatus> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);

        lock (_gate)
        {
            mutate(_current);
            _current.UpdatedAt = DateTimeOffset.UtcNow;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}
