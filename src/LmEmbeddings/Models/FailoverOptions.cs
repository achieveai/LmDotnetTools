namespace AchieveAi.LmDotnetTools.LmEmbeddings.Models;

public class FailoverOptions
{
    /// <summary>
    /// Timeout for primary requests before triggering failover. Must be greater than TimeSpan.Zero.
    /// </summary>
    public TimeSpan PrimaryRequestTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Whether non-success HTTP responses (4xx/5xx) trigger failover. Default: true.
    /// </summary>
    public bool FailoverOnHttpError { get; init; } = true;

    /// <summary>
    /// Duration to stay on backup before probing primary. Must be greater than TimeSpan.Zero when specified.
    /// Null = stay on backup until manual reset via ResetToPrimary().
    /// </summary>
    public TimeSpan? RecoveryInterval { get; init; }
}
