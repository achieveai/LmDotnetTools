using System.Collections.Immutable;
using AchieveAi.LmDotnetTools.LmCore.Agents;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;

/// <summary>
/// Provider agent and provider-specific request properties produced for a sub-agent spawn.
/// </summary>
public sealed record SubAgentProviderAgent(
    IStreamingAgent Agent,
    ImmutableDictionary<string, object?> ExtraProperties);
