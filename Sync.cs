using System.Collections.Concurrent;

// Remembers which calendar feeds recently failed, across the whole session.
static class CalendarThrottle
{
    public static readonly ConcurrentDictionary<string, DateTime> Failures =
        new(StringComparer.OrdinalIgnoreCase);
}

// Pushes game progress to NYT in the background, latest-wins. The game thread calls
// Queue(...) with a push closure that has already snapshotted the current state; the
// pump always runs the most recently queued push and keeps going until caught up —
// so the final state syncs within a couple seconds even if the player goes idle or
// closes without a clean exit (no dependence on redraws). Not tied to any one game.
class BackgroundSyncer
{
    Func<Task<bool>>? _pending;
    bool _running;
    readonly object _lock = new();
    public volatile bool Failed;

    public void Queue(Func<Task<bool>> push)
    {
        lock (_lock)
        {
            _pending = push;          // latest wins; supersedes any not-yet-started push
            if (_running) return;
            _running = true;
        }
        _ = Task.Run(PumpAsync);
    }

    async Task PumpAsync()
    {
        while (true)
        {
            Func<Task<bool>>? job;
            lock (_lock)
            {
                job = _pending;
                _pending = null;
                if (job == null) { _running = false; return; }
            }
            try { Failed = !await job(); }
            catch (Exception ex) { AppLog.Debug("BackgroundSyncer", ex); Failed = true; }
        }
    }
}
