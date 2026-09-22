using AchieveAi.LmDotnetTools.LmCore.Core;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;

/// <summary>
/// What a host's model-intelligence tier ladder resolved a tier to: the concrete model and the reasoning
/// effort configured for the tier it landed on (null when that tier names none).
/// </summary>
public sealed record SubAgentTierSelection(string ModelId, ReasoningEffort? Effort);
