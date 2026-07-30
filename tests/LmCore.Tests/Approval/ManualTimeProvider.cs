namespace AchieveAi.LmDotnetTools.LmCore.Tests.Approval;

/// <summary>
/// A clock the test drives by hand, so approval expiry can be exercised without sleeping.
/// </summary>
/// <remarks>
/// Timers created here fire on the thread that calls <see cref="Advance"/>, which is what makes an
/// expiry test deterministic: the wait either has or has not elapsed at the moment we assert, with
/// no window in which a real timer might not have run yet.
/// </remarks>
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _utcNow;

    public ManualTimeProvider(DateTimeOffset start) => _utcNow = start;

    public ManualTimeProvider()
        : this(new DateTimeOffset(2026, 7, 27, 12, 0, 0, TimeSpan.Zero)) { }

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _utcNow;
        }
    }

    public override ITimer CreateTimer(
        TimerCallback callback,
        object? state,
        TimeSpan dueTime,
        TimeSpan period
    )
    {
        ManualTimer timer;
        lock (_gate)
        {
            timer = new ManualTimer(this, callback, state, _utcNow, dueTime, period);
            _timers.Add(timer);
        }

        timer.FireIfDue(GetUtcNow());
        return timer;
    }

    /// <summary>Moves the clock forward and runs every timer that is now due.</summary>
    public void Advance(TimeSpan delta)
    {
        ManualTimer[] snapshot;
        DateTimeOffset now;
        lock (_gate)
        {
            _utcNow += delta;
            now = _utcNow;
            snapshot = [.. _timers];
        }

        foreach (var timer in snapshot)
        {
            timer.FireIfDue(now);
        }
    }

    private void Remove(ManualTimer timer)
    {
        lock (_gate)
        {
            _ = _timers.Remove(timer);
        }
    }

    private sealed class ManualTimer : ITimer
    {
        private readonly ManualTimeProvider _owner;
        private readonly TimerCallback _callback;
        private readonly object? _state;
        private readonly object _timerGate = new();
        private DateTimeOffset? _dueAt;
        private TimeSpan _period;
        private bool _disposed;

        public ManualTimer(
            ManualTimeProvider owner,
            TimerCallback callback,
            object? state,
            DateTimeOffset now,
            TimeSpan dueTime,
            TimeSpan period
        )
        {
            _owner = owner;
            _callback = callback;
            _state = state;
            _dueAt = dueTime == Timeout.InfiniteTimeSpan ? null : now + dueTime;
            _period = period;
        }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (_timerGate)
            {
                if (_disposed)
                {
                    return false;
                }

                _dueAt =
                    dueTime == Timeout.InfiniteTimeSpan ? null : _owner.GetUtcNow() + dueTime;
                _period = period;
            }

            FireIfDue(_owner.GetUtcNow());
            return true;
        }

        public void FireIfDue(DateTimeOffset now)
        {
            lock (_timerGate)
            {
                if (_disposed || _dueAt is not { } dueAt || now < dueAt)
                {
                    return;
                }

                _dueAt = _period == Timeout.InfiniteTimeSpan ? null : dueAt + _period;
            }

            _callback(_state);
        }

        public void Dispose()
        {
            lock (_timerGate)
            {
                _disposed = true;
            }

            _owner.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
