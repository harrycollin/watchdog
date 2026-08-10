using KioskWatchdog.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace KioskWatchdog.Core.Restart;

public sealed class RestartManager
{
    private readonly IClock _clock;
    private readonly ILogger<RestartManager>? _logger;
    private readonly object _gate = new();
    private readonly Queue<DateTimeOffset> _restartTimestamps = new();

    private DateTimeOffset? _lastRestartAt;
    private bool _limitReached;

    public RestartManager(IClock? clock = null, ILogger<RestartManager>? logger = null)
    {
        _clock = clock ?? new SystemClock();
        _logger = logger;
    }

    public int RestartCount
    {
        get
        {
            lock (_gate)
            {
                PruneExpiredUnsafe(TimeSpan.FromMinutes(10));
                return _restartTimestamps.Count;
            }
        }
    }

    public DateTimeOffset? LastRestartAt
    {
        get { lock (_gate) return _lastRestartAt; }
    }

    public bool LimitReached
    {
        get { lock (_gate) return _limitReached; }
    }

    public bool CanRestart(int maxRestarts, TimeSpan window)
    {
        lock (_gate)
        {
            PruneExpiredUnsafe(window);

            if (_restartTimestamps.Count >= maxRestarts)
            {
                if (!_limitReached)
                {
                    _limitReached = true;
                    _logger?.LogCritical(
                        "Restart limit reached: {Count} restarts within {Window}. Stopping automatic restarts.",
                        _restartTimestamps.Count,
                        window);
                }

                return false;
            }

            return true;
        }
    }

    public void RecordRestart(int maxRestarts, TimeSpan window)
    {
        lock (_gate)
        {
            var now = _clock.UtcNow;
            PruneExpiredUnsafe(window);
            _restartTimestamps.Enqueue(now);
            _lastRestartAt = now;

            _logger?.LogInformation(
                "Restart recorded. Count in window: {Count}/{Max}",
                _restartTimestamps.Count,
                maxRestarts);

            if (_restartTimestamps.Count >= maxRestarts)
            {
                _limitReached = true;
                _logger?.LogCritical(
                    "Restart limit reached after recording restart: {Count}/{Max} within {Window}.",
                    _restartTimestamps.Count,
                    maxRestarts,
                    window);
            }
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _restartTimestamps.Clear();
            _limitReached = false;
            _logger?.LogInformation("Restart counter reset.");
        }
    }

    public int GetCountInWindow(TimeSpan window)
    {
        lock (_gate)
        {
            PruneExpiredUnsafe(window);
            return _restartTimestamps.Count;
        }
    }

    private void PruneExpiredUnsafe(TimeSpan window)
    {
        var cutoff = _clock.UtcNow - window;
        while (_restartTimestamps.Count > 0 && _restartTimestamps.Peek() < cutoff)
        {
            _restartTimestamps.Dequeue();
        }

        // If the window has cleared enough slots, allow restarts again.
        // LimitReached stays true until explicit Reset OR window naturally drops below max —
        // we clear the flag when count drops so recovery after the window is possible.
        // Spec says: stop restarting when exceeded; manual reset clears. Natural window expiry
        // should also allow monitoring to resume once old restarts fall out.
        if (_limitReached && _restartTimestamps.Count == 0)
        {
            _limitReached = false;
            _logger?.LogInformation("Restart window expired; automatic restarts re-enabled.");
        }
    }
}
