namespace AchieveAi.LmDotnetTools.Sandbox.Tests;

/// <summary>
/// A <see cref="TimeProvider"/> for <see cref="SandboxClient.TransportClock"/> that never waits in real time. Timers
/// due in under 10 s (poll back-off delays) fire at once on the thread pool; longer ones (transport budgets, a
/// preparation's <c>MaxWait</c>) fire only when a test calls <see cref="Fire"/> with their exact due time.
/// </summary>
internal sealed class ScriptedClock : TimeProvider
{
    private static readonly TimeSpan S_immediateBelow = TimeSpan.FromSeconds(10);
    private readonly List<ScriptedTimer> _timers = [];

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ScriptedTimer(callback, state, dueTime);
        if (dueTime != Timeout.InfiniteTimeSpan && dueTime < S_immediateBelow)
        {
            _ = ThreadPool.UnsafeQueueUserWorkItem(static t => t.Fire(), timer, preferLocal: false);
            return timer;
        }

        lock (_timers)
        {
            _timers.Add(timer);
        }

        return timer;
    }

    /// <summary>Fires every live timer created with <paramref name="dueTime"/>; returns how many fired.</summary>
    public int Fire(TimeSpan dueTime)
    {
        List<ScriptedTimer> due;
        lock (_timers)
        {
            due = [.. _timers.Where(timer => timer.DueTime == dueTime && !timer.IsDone)];
        }

        return due.Count(timer => timer.Fire());
    }

    private sealed class ScriptedTimer(TimerCallback callback, object? state, TimeSpan dueTime) : ITimer
    {
        private int _done;

        public TimeSpan DueTime { get; private set; } = dueTime;

        public bool IsDone => Volatile.Read(ref _done) != 0;

        public bool Fire()
        {
            if (Interlocked.Exchange(ref _done, 1) != 0)
            {
                return false;
            }

            callback(state);
            return true;
        }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            DueTime = dueTime;
            return !IsDone;
        }

        public void Dispose() => Interlocked.Exchange(ref _done, 1);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
