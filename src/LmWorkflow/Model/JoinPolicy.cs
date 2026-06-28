namespace AchieveAi.LmDotnetTools.LmWorkflow.Model;

/// <summary>
///     Controls how a <see cref="ProceduralNode"/> joins the results of its task list. V1 supports
///     <see cref="JoinMode.All"/> and <see cref="JoinMode.Any"/> only.
/// </summary>
public sealed record JoinPolicy
{
    /// <summary>The join strategy. Defaults to <see cref="JoinMode.All"/>.</summary>
    public JoinMode Mode { get; init; } = JoinMode.All;

    /// <summary>
    ///     The threshold fraction for <see cref="JoinMode.Quorum"/>. Unused in V1 because quorum joins
    ///     are rejected by the validator.
    /// </summary>
    public double? Threshold { get; init; }
}
