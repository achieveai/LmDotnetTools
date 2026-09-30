using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;

namespace AchieveAi.LmDotnetTools.LmMultiTurn;

/// <summary>What an <see cref="IInputTap"/> decided about one input.</summary>
public enum InputTapDecision
{
    /// <summary>Queue the input on this agent as usual.</summary>
    Queue,

    /// <summary>
    /// The tap took the input. It is not queued on this agent and no run will consume it here; the
    /// caller still gets a receipt, because from its side the input was delivered.
    /// </summary>
    Taken,
}

/// <summary>
/// Sees every input offered to an agent through its send methods before it is queued, and may take it
/// instead. This is the seam a host uses to route or mirror inputs between agents that present
/// themselves as one: a planner that must see the notifications its executor's sub-agents raise, an
/// executor that is shown what its planner was told.
/// </summary>
/// <remarks>
/// The tap runs before the durable accepted-input record and before
/// <see cref="IInputAcceptanceObserver"/>, so a taken input leaves no trace on the agent it was
/// offered to. A throwing tap fails the send with nothing queued. Inputs the loop mints for itself
/// (resume sentinels, wake-ups, trigger fires) never pass through it.
/// </remarks>
public interface IInputTap
{
    /// <summary>Decides what happens to <paramref name="input"/>.</summary>
    ValueTask<InputTapDecision> OnInputAsync(UserInput input, CancellationToken cancellationToken);
}
