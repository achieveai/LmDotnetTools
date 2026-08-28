namespace AchieveAi.LmDotnetTools.LmStreaming.Sample.Triggers;

/// <summary>
/// Counts consecutive failures of a polling loop and decides when the loop should say so.
/// </summary>
/// <remarks>
/// <para>
/// A monitoring trigger must survive a transient IO error, so its poll loop catches and continues.
/// The cost of that is silence: an ACL change, an unmounted volume, or a deleted file makes the
/// trigger structurally unable to ever observe anything, and it goes on polling as if healthy until
/// the wait's TTL expires. The wait then reports a timeout, which reads as "nothing matched" —
/// exactly the wrong conclusion (#161).
/// </para>
/// <para>
/// Counting distinguishes the two: one failed tick is a rotation race, a few hundred in a row is a
/// broken deployment. This exists as its own type so that decision is testable without driving a
/// real filesystem fault, and so the loop keeps reading as a loop.
/// </para>
/// </remarks>
/// <param name="warnAfter">Consecutive failures before the first warning. Must be positive.</param>
/// <param name="repeatEvery">
/// Further consecutive failures between repeat warnings, so a fault lasting hours stays visible
/// without writing a line per poll tick. Must be positive.
/// </param>
internal sealed class PollFaultStreak(int warnAfter, int repeatEvery)
{
    private readonly int _warnAfter =
        warnAfter > 0 ? warnAfter : throw new ArgumentOutOfRangeException(nameof(warnAfter));

    private readonly int _repeatEvery =
        repeatEvery > 0 ? repeatEvery : throw new ArgumentOutOfRangeException(nameof(repeatEvery));

    private int _consecutive;
    private bool _warned;

    /// <summary>Consecutive failures recorded since the last success.</summary>
    internal int Consecutive => _consecutive;

    /// <summary>
    /// Records one failed poll. Returns true when the caller should emit a warning: once on
    /// reaching the threshold, then every <c>repeatEvery</c> failures after it.
    /// </summary>
    internal bool RecordFailure()
    {
        _consecutive++;

        if (_consecutive < _warnAfter)
        {
            return false;
        }

        if (_consecutive == _warnAfter)
        {
            _warned = true;
            return true;
        }

        return (_consecutive - _warnAfter) % _repeatEvery == 0;
    }

    /// <summary>
    /// Records one healthy poll. Returns true only when a warning was previously emitted, so the
    /// loop can report recovery — a warning with no matching recovery line leaves an operator
    /// unable to tell a fault that healed from one that is still live.
    /// </summary>
    internal bool RecordSuccess()
    {
        _consecutive = 0;
        if (!_warned)
        {
            return false;
        }

        _warned = false;
        return true;
    }
}
