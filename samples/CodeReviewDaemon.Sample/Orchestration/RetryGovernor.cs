namespace CodeReviewDaemon.Sample.Orchestration;

/// <summary>Whether a failed run should keep retrying (with backoff) or has been parked.</summary>
internal enum RetryDecision
{
    Retry,
    Parked,
}

/// <summary>
/// In-memory retry governance for review runs: attempt-counting, exponential backoff, and park-after-K.
/// State is deliberately NOT persisted — a daemon restart clears it so every backing-off/parked run is
/// retried fresh (operator intent: "restart = retry"). This is safe because ContextReady's clean-on-entry
/// self-heals the stuck cause on the first retry; a still-broken run simply re-parks within the new lifetime
/// after bounded backoff. Park is per run id, and a run id is the commit tuple, so a new commit is a new run
/// that starts fresh. See the design doc §5.
/// </summary>
internal sealed class RetryGovernor(
    int maxAttempts,
    TimeSpan backoffBase,
    TimeSpan backoffCap,
    Func<DateTimeOffset> clock,
    ILogger<RetryGovernor> logger)
{
    private sealed class State
    {
        public int Attempts;
        public DateTimeOffset NextEligibleAt;
        public bool Parked;
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, State> _states = new();

    /// <summary>True unless the run is currently backing off or has been parked.</summary>
    public bool ShouldAttempt(long runId)
    {
        if (!_states.TryGetValue(runId, out var state))
        {
            return true;
        }

        lock (state)
        {
            return !state.Parked && clock() >= state.NextEligibleAt;
        }
    }

    /// <summary>
    /// Records a failed attempt: increments the count and either schedules the next attempt after an
    /// exponential backoff, or — once <c>maxAttempts</c> is reached — parks the run and emits a greppable
    /// <c>PARKED</c> alert. A parked run is not retried again until a new commit (new run id) or a restart.
    /// </summary>
    public RetryDecision RecordFailure(long runId, string lastError)
    {
        var state = _states.GetOrAdd(runId, _ => new State());
        lock (state)
        {
            state.Attempts++;
            if (state.Attempts >= maxAttempts)
            {
                state.Parked = true;
                logger.LogError(
                    "review_run PARKED run {RunId} after {Attempts} attempts: {Error}",
                    runId, state.Attempts, lastError);
                return RetryDecision.Parked;
            }

            // Exponential backoff, clamped to the cap; the shift exponent is bounded to avoid overflow.
            var shift = Math.Min(state.Attempts - 1, 30);
            var delayTicks = Math.Min(backoffCap.Ticks, backoffBase.Ticks * (1L << shift));
            state.NextEligibleAt = clock() + TimeSpan.FromTicks(delayTicks);
            return RetryDecision.Retry;
        }
    }

    /// <summary>Clears a run's retry state after a successful attempt.</summary>
    public void RecordSuccess(long runId) => _states.TryRemove(runId, out _);
}
