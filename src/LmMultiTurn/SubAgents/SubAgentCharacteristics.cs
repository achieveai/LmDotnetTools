using AchieveAi.LmDotnetTools.LmCore.Core;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;

/// <summary>
/// Resolved characteristics used to create a provider agent for a sub-agent spawn.
/// </summary>
public sealed record SubAgentCharacteristics(
    string? ModelId,
    ReasoningEffort? Effort)
{
    /// <summary>
    /// Whether the effective model came from an explicit template or per-spawn selection
    /// rather than parent-model inheritance.
    /// </summary>
    public bool IsModelExplicitlySelected { get; init; }
}
