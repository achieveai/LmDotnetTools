using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;

namespace AchieveAi.LmDotnetTools.LmMultiTurn;

/// <summary>Positive capability to enforce a tool-free correction input, including replayed calls.</summary>
public interface IActionToolSuppressingAgent : ISpawnSuppressingAgent
{
    bool EnforcesActionToolSuppression { get; }
}
