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
internal sealed class RetryGovernor
{
    private readonly int _maxAttempts;
    private readonly TimeSpan _backoffBase;
    private readonly TimeSpan _backoffCap;
    private readonly Func<DateTimeOffset> _clock;
    private readonly ILogger<RetryGovernor> _logger;

    /// <summary>
    /// Fails fast on a misconfigured retry policy: attempts must be positive, the base delay non-negative, and
    /// the cap at least the base — otherwise the backoff/park math has no well-defined meaning. These come from
    /// operator config (<see cref="Configuration.CodeReviewDaemonOptions"/>), so a bad value is rejected at
    /// startup rather than silently producing a zero/negative or overflowing delay.
    /// </summary>
    public RetryGovernor(
        int maxAttempts,
        TimeSpan backoffBase,
        TimeSpan backoffCap,
        Func<DateTimeOffset> clock,
        ILogger<RetryGovernor> logger)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        if (maxAttempts < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxAttempts), maxAttempts, "must be >= 1.");
        }

        if (backoffBase < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(backoffBase), backoffBase, "must be non-negative.");
        }

        if (backoffCap < backoffBase)
        {
            throw new ArgumentOutOfRangeException(nameof(backoffCap), backoffCap, "must be >= backoffBase.");
        }

        _maxAttempts = maxAttempts;
        _backoffBase = backoffBase;
        _backoffCap = backoffCap;
        _clock = clock;
        _logger = logger;
    }

    private sealed class State
    {
        public long Seq;
        public int Attempts;
        public DateTimeOffset NextEligibleAt;
        public bool Parked;
    }

    // Retry state is cleared on ContextReady success, but a persistently-failing/parked or superseded run's
    // state would otherwise live for the whole daemon lifetime. Cap the tracked set and evict the oldest.
    private const int MaxTrackedRuns = 10_000;
    private long _seq;

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
            return !state.Parked && _clock() >= state.NextEligibleAt;
        }
    }

    /// <summary>
    /// Records a failed attempt: increments the count and either schedules the next attempt after an
    /// exponential backoff, or — once <c>maxAttempts</c> is reached — parks the run and emits a greppable
    /// <c>PARKED</c> alert. A parked run is not retried again until a new commit (new run id) or a restart.
    /// </summary>
    public RetryDecision RecordFailure(long runId, string lastError)
    {
        var state = _states.GetOrAdd(runId, _ => new State { Seq = System.Threading.Interlocked.Increment(ref _seq) });
        RetryDecision decision;
        lock (state)
        {
            state.Attempts++;
            if (state.Attempts >= _maxAttempts)
            {
                state.Parked = true;
                _logger.LogError(
                    "review_run PARKED run {RunId} after {Attempts} attempts: {Error}",
                    runId, state.Attempts, lastError);
                decision = RetryDecision.Parked;
            }
            else
            {
                // Exponential backoff, clamped to the cap. The exponent is bounded, and the base*2^shift product
                // is clamped to the cap WITHOUT overflowing the intermediate multiply: if base would exceed
                // cap/2^shift the product would exceed the cap anyway, so use the cap directly.
                var shift = Math.Min(state.Attempts - 1, 30);
                var multiplier = 1L << shift;
                var delayTicks = _backoffBase.Ticks > _backoffCap.Ticks / multiplier
                    ? _backoffCap.Ticks
                    : _backoffBase.Ticks * multiplier;
                state.NextEligibleAt = _clock() + TimeSpan.FromTicks(delayTicks);
                decision = RetryDecision.Retry;
            }
        }

        EvictOldestOverCapacity();
        return decision;
    }

    /// <summary>Clears a run's retry state after a successful attempt.</summary>
    public void RecordSuccess(long runId) => _states.TryRemove(runId, out _);

    /// <summary>
    /// Keeps the tracked set bounded by evicting the oldest entries once it exceeds <see cref="MaxTrackedRuns"/>.
    /// Safe: a re-polled evicted run simply starts fresh (<see cref="ShouldAttempt"/> returns true), and eviction
    /// only ever reaches the very oldest ids — long past their active retry/backoff window.
    /// </summary>
    private void EvictOldestOverCapacity()
    {
        var overflow = _states.Count - MaxTrackedRuns;
        if (overflow <= 0)
        {
            return;
        }

        foreach (var key in _states.OrderBy(kv => kv.Value.Seq).Take(overflow).Select(kv => kv.Key).ToArray())
        {
            _states.TryRemove(key, out _);
        }
    }
}
