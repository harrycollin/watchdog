namespace KioskWatchdog.Core.Status;

public sealed class WatchdogStatusStore : IWatchdogStatusStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, WatchdogStatus> _byId =
        new(StringComparer.OrdinalIgnoreCase);

    public event EventHandler? Changed;

    public IReadOnlyList<WatchdogStatus> All
    {
        get
        {
            lock (_gate)
            {
                return _byId.Values.Select(s => s.Clone()).OrderBy(s => s.Id, StringComparer.OrdinalIgnoreCase).ToList();
            }
        }
    }

    public WatchdogStatus? Get(string applicationId)
    {
        lock (_gate)
        {
            return _byId.TryGetValue(applicationId, out var status) ? status.Clone() : null;
        }
    }

    public void Upsert(string applicationId, Action<WatchdogStatus> mutate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationId);
        ArgumentNullException.ThrowIfNull(mutate);

        lock (_gate)
        {
            if (!_byId.TryGetValue(applicationId, out var status))
            {
                status = new WatchdogStatus { Id = applicationId };
                _byId[applicationId] = status;
            }

            mutate(status);
            status.Id = applicationId;
            status.UpdatedAt = DateTimeOffset.UtcNow;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void RemoveMissing(IEnumerable<string> activeIds)
    {
        var keep = new HashSet<string>(activeIds, StringComparer.OrdinalIgnoreCase);
        lock (_gate)
        {
            var toRemove = _byId.Keys.Where(id => !keep.Contains(id)).ToList();
            if (toRemove.Count == 0)
                return;

            foreach (var id in toRemove)
                _byId.Remove(id);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public WatchdogStatusSnapshot CreateSnapshot()
    {
        lock (_gate)
        {
            var apps = _byId.Values
                .Select(s => s.Clone())
                .OrderBy(s => s.Id, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return new WatchdogStatusSnapshot
            {
                UpdatedAt = DateTimeOffset.UtcNow,
                Applications = apps
            };
        }
    }
}
