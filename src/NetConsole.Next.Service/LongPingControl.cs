namespace NetConsole.Next.Service;

// Pause requests drain the current probe before PAUSED is acknowledged.
// The original wall-clock window keeps running, so paused tasks remain bounded.
internal sealed class LongPingControl
{
    private readonly object _gate = new();
    private bool _pauseRequested;
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool SetPaused(bool paused, Action<string> stateChanged)
    {
        lock (_gate)
        {
            if (_pauseRequested == paused) return false;
            _pauseRequested = paused;
            stateChanged(paused ? "PAUSING" : "RUNNING");
            var previous = _changed;
            _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            previous.TrySetResult();
            return true;
        }
    }

    public async Task<bool> WaitToProbeAsync(Func<TimeSpan> remaining, Action requireCurrentTarget,
        Action<string> stateChanged, CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            requireCurrentTarget();
            var budget = remaining();
            if (budget <= TimeSpan.Zero) return false;
            Task changed;
            lock (_gate)
            {
                if (!_pauseRequested) return true;
                stateChanged("PAUSED");
                changed = _changed.Task;
            }
            // Revalidate authority even when nobody resumes or stops the task.
            await Task.WhenAny(changed, Task.Delay(TimeSpan.FromMilliseconds(Math.Min(100, budget.TotalMilliseconds)), token));
        }
    }
}
