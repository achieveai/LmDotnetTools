namespace AchieveAi.LmDotnetTools.LmAgentInfra.Tests;

/// <summary>
/// A clock — and a set of timers — the test drives by hand, so a rotation overlap, a retry backoff,
/// or an attempt timeout can be expired without sleeping.
/// </summary>
/// <remarks>
/// Timers matter as much as <see cref="GetUtcNow"/> here: a <c>Task.Delay</c> or a
/// <c>CancellationTokenSource</c> deadline taken against a clock-only provider falls back to
/// <see cref="TimeProvider.System"/>'s timers and becomes a genuine wall-clock wait, which is
/// exactly the sleep these suites are written to avoid. Both are virtual, so the only thing that
/// moves time is <see cref="Advance"/>.
/// </remarks>
internal sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private readonly Lock _sync = new();
    private readonly List<FakeTimer> _timers = [];
    private readonly Gate _timerGate = new();

    private DateTimeOffset _utcNow = start;

    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow()
    {
        lock (_sync)
        {
            return _utcNow;
        }
    }

    /// <inheritdoc />
    public override ITimer CreateTimer(
        TimerCallback callback,
        object? state,
        TimeSpan dueTime,
        TimeSpan period
    )
    {
        var timer = new FakeTimer(this, callback, state);
        lock (_sync)
        {
            _timers.Add(timer);
        }

        _ = timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>
    /// Moves the clock, firing every timer that comes due on the way. Callbacks run outside the lock
    /// so a continuation is free to read the clock or schedule another timer.
    /// </summary>
    internal void Advance(TimeSpan delta)
    {
        var target = GetUtcNow() + delta;
        while (true)
        {
            FakeTimer due;
            lock (_sync)
            {
                var next = _timers
                    .Where(timer => timer.DueAt is { } dueAt && dueAt <= target)
                    .OrderBy(timer => timer.DueAt!.Value)
                    .FirstOrDefault();

                if (next is null)
                {
                    _utcNow = target;
                    return;
                }

                _utcNow = next.DueAt!.Value;
                next.DueAt =
                    next.Period > TimeSpan.Zero && next.Period != Timeout.InfiniteTimeSpan
                        ? _utcNow + next.Period
                        : null;
                due = next;
            }

            due.Fire();
        }
    }

    /// <summary>
    /// Completes once a timer is pending whose due time falls inside the given band. The band is how
    /// a test names <em>which</em> timer it means: a retry backoff and an attempt timeout are both
    /// pending at once and are told apart only by how far out they are.
    /// </summary>
    internal Task WaitForTimerAsync(TimeSpan earliestDueIn, TimeSpan latestDueIn) =>
        _timerGate.WaitAsync(() =>
        {
            lock (_sync)
            {
                return _timers.Any(timer =>
                    timer.DueAt is { } dueAt
                    && dueAt - _utcNow >= earliestDueIn
                    && dueAt - _utcNow <= latestDueIn
                );
            }
        });

    private bool Schedule(FakeTimer timer, TimeSpan dueTime, TimeSpan period)
    {
        lock (_sync)
        {
            timer.Period = period;
            timer.DueAt = dueTime == Timeout.InfiniteTimeSpan ? null : _utcNow + dueTime;
        }

        _timerGate.Signal();
        return true;
    }

    private void Remove(FakeTimer timer)
    {
        lock (_sync)
        {
            timer.DueAt = null;
            _ = _timers.Remove(timer);
        }
    }

    private sealed class FakeTimer(ManualTimeProvider owner, TimerCallback callback, object? state)
        : ITimer
    {
        internal DateTimeOffset? DueAt { get; set; }

        internal TimeSpan Period { get; set; } = Timeout.InfiniteTimeSpan;

        internal void Fire() => callback(state);

        public bool Change(TimeSpan dueTime, TimeSpan period) =>
            owner.Schedule(this, dueTime, period);

        public void Dispose() => owner.Remove(this);

        public ValueTask DisposeAsync()
        {
            owner.Remove(this);
            return ValueTask.CompletedTask;
        }
    }
}
