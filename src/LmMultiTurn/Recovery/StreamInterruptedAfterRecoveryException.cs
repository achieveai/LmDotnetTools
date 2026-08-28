namespace AchieveAi.LmDotnetTools.LmMultiTurn.Recovery;

/// <summary>
///     Raised when a provider stream is interrupted a second time for the same logical input, after
///     the one automatic recovery that input is allowed has already been spent.
/// </summary>
/// <remarks>
///     The run loop's existing error path turns this into a failed run whose error message carries
///     <see cref="Classification" />, giving clients a stable token to branch on instead of having to
///     pattern-match transport wording that varies by provider and runtime.
/// </remarks>
internal sealed class StreamInterruptedAfterRecoveryException(Exception innerException)
    : Exception(
        $"{Classification}: the provider stream was interrupted again after its one automatic recovery attempt.",
        innerException
    )
{
    /// <summary>Stable, machine-readable classification token embedded in the message.</summary>
    public const string Classification = "stream_interrupted_after_recovery";
}
