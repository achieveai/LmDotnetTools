namespace AchieveAi.LmDotnetTools.LmMultiTurn.Audit;

/// <summary>
/// Accepts immutable source records captured at multi-turn model boundaries. Implementations must
/// support concurrent <see cref="RecordAsync"/> calls from provider-response and tool-result paths.
/// </summary>
public interface IMultiTurnAuditSink
{
    /// <summary>
    /// Durably accepts one exact source record. This method may be called concurrently for different
    /// records; implementations must synchronize any mutable state they own.
    /// </summary>
    ValueTask RecordAsync(ModelTurnAuditRecord record, CancellationToken cancellationToken = default);
}
