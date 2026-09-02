namespace AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;

/// <summary>What a claimed call produced, recorded so its repeats do not redo the work.</summary>
/// <param name="Receipt">Exactly what the call returned.</param>
/// <param name="ErrorCode">
/// The code the call refused with, or null when it succeeded. A recorded outcome is not always a happy
/// one: a run that failed after the agent already ran must be replayed AS a failure, because handing its
/// repeat a plain result would tell the caller the retry it just made had worked.
/// </param>
/// <remarks>Reference type, so a null completion stays available as the "abandoned" marker.</remarks>
internal sealed record IdempotentOutcome(string Receipt, string? ErrorCode);

/// <summary>A result produced by an earlier call under the same key.</summary>
/// <param name="Outcome">Exactly what the first call returned, and how it ended.</param>
/// <param name="OriginalAt">When the first call claimed the key.</param>
internal readonly record struct IdempotentReplay(IdempotentOutcome Outcome, DateTimeOffset OriginalAt)
{
    /// <summary>What the first call returned.</summary>
    internal string Receipt => Outcome.Receipt;

    /// <summary>The code the first call refused with, or null when it succeeded.</summary>
    internal string? ErrorCode => Outcome.ErrorCode;
}

/// <summary>
/// One caller's exclusive right to do the work behind a key, and to record what it produced.
/// </summary>
/// <remarks>
/// Handed out by <see cref="IdempotencyLedger.ReserveAsync"/> and NEVER minted by a caller: holding one
/// is what proves nobody else is doing the same work. Every path that takes a claim must end in exactly
/// one <see cref="IdempotencyLedger.Complete"/> or <see cref="IdempotencyLedger.Abandon"/> — a claim
/// that is neither completed nor abandoned leaves every later caller of that key waiting forever.
/// </remarks>
internal sealed class IdempotencyClaim
{
    internal IdempotencyClaim(string key, DateTimeOffset claimedAt)
    {
        Key = key;
        ClaimedAt = claimedAt;
    }

    internal string Key { get; }

    internal DateTimeOffset ClaimedAt { get; }

    internal TaskCompletionSource<IdempotentOutcome?> Completion { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>
/// Makes a repeated tool call return the first call's result instead of doing the work twice.
/// </summary>
/// <remarks>
/// <para>
/// A duplicate spawn or a duplicate message is not a wasted call — it is a second agent, or a second
/// obligation somebody now owes an answer on, and neither can be taken back once it exists. The model
/// cannot avoid duplicates on its own: the case that produces them is precisely the one where it does
/// not know whether the first call landed.
/// </para>
/// <para>
/// The claim is taken BEFORE the work starts and released only when it ends, so a second caller that
/// arrives while the first is still running waits for it rather than racing it. Checking a
/// "have I seen this?" map afterwards would close the retry window and leave the concurrent one wide
/// open — and two identical tool calls in ONE model turn are the concurrent case.
/// </para>
/// <para>
/// What decides whether a key is remembered is not success but whether the call CREATED anything. A
/// refusal that happened before there was an agent or an obligation releases the key: it left nothing to
/// protect from a repeat, and remembering it would turn a transient failure into a permanent one. A run
/// that failed after the agent had already run is remembered, and remembered as the failure it was —
/// releasing that one would let the repeat start a second agent, which is exactly what the key was
/// given to prevent.
/// </para>
/// <para>
/// Memory-only and bounded by count, not by time. A key's usefulness is exhausted by the calls that
/// come after it rather than by the clock: a session that makes thousands of calls has long since
/// stopped being able to retry its oldest one, while a session that makes three can retry any of them
/// an hour later.
/// </para>
/// </remarks>
internal sealed class IdempotencyLedger
{
    /// <summary>How many keys are remembered before the oldest is forgotten.</summary>
    internal const int MaxRemembered = 64;

    private readonly Dictionary<string, IdempotencyClaim> _claims = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();
    private readonly object _gate = new();
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates an empty ledger.</summary>
    /// <param name="timeProvider">Clock stamping the original call time. Defaults to the system clock.</param>
    internal IdempotencyLedger(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Claims a key for this caller, or waits for whoever holds it and reports what they produced.
    /// </summary>
    /// <param name="toolName">The tool being called. Keys are scoped to it, so the same key used on two
    /// different tools means two different things — which is what a model naming a key after its task
    /// would expect.</param>
    /// <param name="key">The caller-supplied key.</param>
    /// <param name="cancellationToken">Cancels only the wait for another caller's in-flight work.</param>
    /// <returns>
    /// A claim when this caller must do the work, or a replay of the result an earlier call produced.
    /// </returns>
    internal async ValueTask<(IdempotencyClaim? Claim, IdempotentReplay? Replay)> ReserveAsync(
        string toolName,
        string key,
        CancellationToken cancellationToken = default
    )
    {
        // Separated by a character no tool name and no model-supplied key can contain, so no pair of
        // (tool, key) can spell the same composite as another. Written as the ESCAPE and never as a raw
        // byte: a literal NUL in the source makes git treat this whole file as binary — no diff, no
        // review, and greps that should find this line return nothing.
        var composite = $"{toolName}\0{key}";

        while (true)
        {
            IdempotencyClaim holder;
            lock (_gate)
            {
                if (!_claims.TryGetValue(composite, out var existing))
                {
                    var claim = new IdempotencyClaim(composite, _timeProvider.GetUtcNow());
                    _claims[composite] = claim;
                    _order.Enqueue(composite);
                    Evict();
                    return (claim, null);
                }

                if (IsAbandoned(existing))
                {
                    // Taken over in place. Re-enqueueing the key would put it in the eviction order
                    // twice, and the second copy would evict a LIVE claim of the same key later —
                    // handing two concurrent callers the same key and undoing the whole guarantee.
                    var retry = new IdempotencyClaim(composite, _timeProvider.GetUtcNow());
                    _claims[composite] = retry;
                    return (retry, null);
                }

                holder = existing;
            }

            // Outside the lock: the holder's work is a whole spawn or send, and blocking every other
            // key's caller behind it would make this ledger a global tool-call serializer.
            var outcome = await holder.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (outcome is not null)
            {
                return (null, new IdempotentReplay(outcome, holder.ClaimedAt));
            }

            // The holder failed and released the key, so there is nothing to replay. Loop round and
            // try to claim it — this caller then does the work the failed one did not.
        }
    }

    /// <summary>Records what the claimed work produced, so later callers replay it instead of repeating it.</summary>
    /// <param name="claim">The claim taken for this work.</param>
    /// <param name="receipt">What the work returned.</param>
    /// <param name="errorCode">
    /// The code the work refused with, or null when it succeeded. Passed rather than defaulted so that
    /// recording a failed outcome is a deliberate act at the call site: the caller is the only party that
    /// knows whether the failure happened before or after something irreversible was created.
    /// </param>
    internal void Complete(IdempotencyClaim claim, string receipt, string? errorCode)
    {
        ArgumentNullException.ThrowIfNull(claim);

        // Set through the claim the caller holds rather than through a fresh lookup: eviction may
        // already have forgotten the key, and a waiter holding this same instance must still be
        // released. Nothing else can complete it, so the result is the first caller's either way.
        _ = claim.Completion.TrySetResult(new IdempotentOutcome(receipt, errorCode));
    }

    /// <summary>Releases a claim whose work produced nothing, leaving the key free to be tried again.</summary>
    /// <remarks>
    /// The entry stays in the map, completed with null. It is the marker the next caller takes over,
    /// and leaving it there is what keeps each key in the eviction order exactly once.
    /// </remarks>
    internal void Abandon(IdempotencyClaim claim)
    {
        ArgumentNullException.ThrowIfNull(claim);

        _ = claim.Completion.TrySetResult(null);
    }

    private static bool IsAbandoned(IdempotencyClaim claim) =>
        claim.Completion.Task is { IsCompletedSuccessfully: true, Result: null };

    /// <summary>
    /// Forgets the oldest keys once the bound is passed.
    /// </summary>
    /// <remarks>
    /// An in-flight claim can be evicted, and that is deliberate: eviction only removes the ability to
    /// replay, never the ability to finish. The evicted holder still completes, and its waiters still
    /// get its result, because both hold the claim itself rather than a lookup.
    /// </remarks>
    private void Evict()
    {
        while (_order.Count > MaxRemembered)
        {
            _ = _claims.Remove(_order.Dequeue());
        }
    }
}
