namespace AchieveAi.LmDotnetTools.LmMultiTurn.Recovery;

/// <summary>
///     How one provider turn ended, together with what that turn produced.
/// </summary>
/// <param name="Attempt">The observed state of the attempt, whether or not it finished.</param>
/// <param name="RetryableInterruption">
///     The transport failure that cut the attempt short when it is one automatic recovery may act on,
///     otherwise <see langword="null" />. A failure that is <i>not</i> retryable is never represented
///     here — it propagates, exactly as before, and fails the run.
/// </param>
internal sealed record TurnExecutionResult(
    TurnAttemptState Attempt,
    Exception? RetryableInterruption = null)
{
    /// <summary>The provider stream ran to completion.</summary>
    public bool CompletedNormally => RetryableInterruption is null;

    /// <summary>The turn emitted at least one locally executed tool call, so another turn is owed.</summary>
    public bool HasToolCalls => Attempt.HasToolCalls;
}
