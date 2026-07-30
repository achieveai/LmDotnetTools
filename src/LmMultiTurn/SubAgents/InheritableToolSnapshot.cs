using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Middleware;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;

/// <summary>
/// An immutable snapshot of the tools one agent exposes to <em>its</em> sub-agents — the pair of
/// contracts and their handlers that a <see cref="SubAgentManager"/> would hand down on a spawn.
/// </summary>
/// <remarks>
/// Used to make a nested-root loop (a WorkflowAgent controller) <em>transparent</em>: the controller
/// runs on its own isolated registry, but its delegate sub-agents should inherit the tools of the
/// first non-WorkflowAgent ancestor (the launching conversation). That ancestor's
/// <see cref="SubAgentManager.GetInheritableToolSnapshot"/> is captured here and threaded into the
/// controller's <see cref="SubAgentOptions.ExternalInheritableTools"/>, where
/// <c>MultiTurnAgentLoop</c> merges it into the snapshot the controller's own
/// <see cref="SubAgentManager"/> hands to delegates. The contracts are already filtered (they exclude
/// the ancestor's own <see cref="SubAgentOptions.NonInheritedToolNames"/>); <see cref="Handlers"/> may
/// contain more entries than <see cref="Contracts"/> (it is the full handler map, looked up by name).
/// </remarks>
public sealed record InheritableToolSnapshot(
    IReadOnlyList<FunctionContract> Contracts,
    IReadOnlyDictionary<string, ToolHandler> Handlers);
