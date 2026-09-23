namespace AchieveAi.LmDotnetTools.LmCore.Agents;

/// <summary>
///     Who is running right now, when a tool that several agents share needs to tell them apart.
/// </summary>
/// <param name="AgentId">Canonical identifier — <c>agent-7</c> — stable for the life of the agent.</param>
/// <param name="DisplayName">The human-facing name, when the host knows one. Never an identity key.</param>
public sealed record AgentActor(string AgentId, string? DisplayName = null);

/// <summary>
///     The ambient acting sub-agent for the current async flow, or <c>null</c> when the flow belongs to
///     the conversation's root agent.
/// </summary>
/// <remarks>
///     <para>
///         Exists because a conversation's shared stateful tools — the todo board above all — are ONE
///         instance that every agent in the hierarchy calls. The tool methods are the model-facing
///         surface and cannot carry a host-supplied caller argument, so a destructive tool that must be
///         root-only has no other way to know who asked. Bug 19: a sub-agent called
///         <c>bulk-initialize(clearExisting: true)</c> on the shared board and dropped hours of the
///         root's work.
///     </para>
///     <para>
///         Lives in LmCore rather than beside either party because the two parties cannot see each
///         other: the board is in <c>Misc</c> (a published package with a narrow dependency set) and
///         the sub-agent manager is in <c>LmMultiTurn</c>, and neither project references the other.
///         LmCore is the one assembly both already depend on.
///     </para>
///     <para>
///         <b>Null means root, deliberately.</b> Only a sub-agent run opens a scope, so nothing has to
///         be wired for the root case and a host that never opts in behaves exactly as it did before.
///         The cost of that choice is the failure direction: a sub-agent path that forgets to open a
///         scope reads as the root and is permitted, rather than a root path being refused.
///     </para>
///     <para>
///         An <see cref="AsyncLocal{T}" />, which is what makes this work at all: a sub-agent's whole
///         run is one async flow started by <c>SubAgentManager</c>, and the value set before that flow
///         begins is captured into every continuation under it — including the tool call — without
///         travelling as a parameter through code that has no reason to know about it. It is per-flow,
///         not per-thread, so concurrent sub-agent runs on shared pool threads never see each other's
///         actor.
///     </para>
/// </remarks>
public static class AgentActorScope
{
    private static readonly AsyncLocal<AgentActor?> Ambient = new();

    /// <summary>The agent acting on the current async flow, or null when that is the root agent.</summary>
    public static AgentActor? Current => Ambient.Value;

    /// <summary>
    ///     Marks the current async flow, and everything started under it, as acting for
    ///     <paramref name="agentId" />. Dispose restores the previous actor.
    /// </summary>
    /// <remarks>
    ///     Restores the PREVIOUS value rather than clearing to null, so a nested scope (a sub-agent that
    ///     spawns a sub-agent) hands control back to its parent rather than to the root.
    /// </remarks>
    public static IDisposable Begin(string agentId, string? displayName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);

        var previous = Ambient.Value;
        Ambient.Value = new AgentActor(agentId, displayName);
        return new Scope(previous);
    }

    private sealed class Scope(AgentActor? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Ambient.Value = previous;
        }
    }
}
