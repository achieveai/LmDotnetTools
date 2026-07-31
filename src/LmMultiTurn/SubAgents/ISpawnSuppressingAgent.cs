using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;

/// <summary>
/// Declared by agents that genuinely ENFORCE <see cref="UserInput.SuppressSubAgentSpawning"/> on the run
/// that consumes the input — the spawn tool is withheld from that run's contracts and its handler refuses.
/// <para>
/// Declaring this is a POSITIVE guarantee, and an agent that does not declare it is UNKNOWN rather than
/// safe: <see cref="IMultiTurnAgent.TrySendAsync(List{IMessage}, string?, string?, CancellationToken)"/>
/// has no suppression parameter, so an agent reached only through that overload silently runs with spawning
/// available. A host that must not let a turn fan out (e.g. a synthesis turn placed after a sub-agent
/// completion barrier) therefore has to REFUSE an agent that does not declare this interface, instead of
/// sending the flag into a path that would drop it.
/// </para>
/// </summary>
public interface ISpawnSuppressingAgent : IMultiTurnAgent
{
    /// <summary>
    /// Whether this instance will actually enforce the flag. The interface proves the type can CARRY
    /// suppression; this proves the instance KEEPS it — an implementation can satisfy the signature and
    /// still ignore the flag, and a host that gated on the type alone would have already queued an
    /// unsuppressed message by the time the receipt told it so. Check this BEFORE enqueuing; the response
    /// a caller is given must still come from <see cref="SendReceipt.SpawningSuppressed"/>, which is the
    /// per-input statement.
    /// </summary>
    bool EnforcesSpawnSuppression { get; }

    /// <summary>
    /// Non-blocking enqueue of a full <see cref="UserInput"/>, preserving
    /// <see cref="UserInput.SuppressSubAgentSpawning"/> through to the run that consumes it. Queue-full
    /// semantics match
    /// <see cref="IMultiTurnAgent.TrySendAsync(List{IMessage}, string?, string?, CancellationToken)"/>:
    /// <c>null</c> means the input was not accepted.
    /// </summary>
    ValueTask<SendReceipt?> TrySendAsync(UserInput input, CancellationToken ct = default);
}
