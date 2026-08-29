namespace AchieveAi.LmDotnetTools.LmMultiTurn.Messages;

/// <summary>
/// Resume metadata for a turn whose provider stream was cut short by a recoverable transport
/// failure. Carried on <see cref="ResumeSentinel"/> so that the replacement turn knows it is a
/// recovery rather than a fresh request, and knows which kind of recovery it is.
/// </summary>
/// <param name="InterruptedRunId">The run whose turn was interrupted.</param>
/// <param name="InterruptedGenerationId">
/// The generation that was abandoned. The replacement turn always runs under a NEW generation, so
/// this value exists only to correlate telemetry and the client's discard control with the attempt
/// that failed.
/// </param>
/// <param name="HadCanonicalOutput">
/// Whether the interrupted attempt delivered content or ran an effect. This is the whole difference
/// between the two recovery modes: <see langword="false" /> means nothing survived and the original
/// request is simply reissued, while <see langword="true" /> means completed work was kept and the
/// provider must be told to continue from it rather than start over.
/// </param>
/// <remarks>
/// Internal: the loop is the only producer and the only consumer. The recovery budget deliberately
/// does NOT live here — it belongs to the logical input, which can outlive a single run when a turn
/// parks on a deferred tool call, so it is tracked across that boundary by the loop itself.
/// </remarks>
internal sealed record InterruptedTurnResume(
    string InterruptedRunId,
    string InterruptedGenerationId,
    bool HadCanonicalOutput
);
