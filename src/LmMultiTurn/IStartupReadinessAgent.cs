namespace AchieveAi.LmDotnetTools.LmMultiTurn;

/// <summary>
/// Optional startup signal for hosts that must know a background agent has finished
/// recovery and startup hooks before making it available to callers.
/// Callers must start <c>RunAsync</c> before waiting. The synchronous agent pool does
/// not currently await this signal when it publishes an agent.
/// </summary>
public interface IStartupReadinessAgent
{
    /// <summary>
    /// Waits for the current <c>RunAsync</c> attempt to reach its input loop.
    /// A failed or cancelled startup fails this wait. This does not guarantee
    /// that the loop remains healthy after startup.
    /// </summary>
    Task WaitUntilReadyAsync(CancellationToken ct = default);
}
