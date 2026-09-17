namespace AchieveAi.LmDotnetTools.LmMultiTurn.Lifecycle;

/// <summary>
/// Opt-in configuration for the experimental elapsed-time notice. When handed to
/// <see cref="MultiTurnAgentLoop"/>, the loop appends a persisted, never-published user-role message
/// at a turn boundary once at least <see cref="Interval"/> of wall-clock time has passed since the run
/// started or since the previous notice, telling the model how long it has been working on the current
/// request. Null on the loop means the feature is absent and the request is built exactly as before.
/// </summary>
/// <remarks>
/// Experimental: the notice costs a few tokens per interval and its effect on model behaviour is
/// unmeasured. Everything about it lives in this record, <see cref="ElapsedTimeNotice"/> and one
/// constructor parameter, so it can be removed cleanly if it does not earn its keep.
/// </remarks>
public sealed record ElapsedTimeNoticeOptions
{
    private readonly TimeSpan _interval = TimeSpan.FromMinutes(1);

    /// <summary>Minimum wall-clock time between notices. Default one minute; must be positive.</summary>
    public TimeSpan Interval
    {
        get => _interval;
        init
        {
            if (value <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(Interval), value, "Interval must be positive.");
            }

            _interval = value;
        }
    }

    /// <summary>Clock the elapsed time is measured on. Null uses <see cref="TimeProvider.System"/>.</summary>
    public TimeProvider? Clock { get; init; }
}
