using AchieveAi.LmDotnetTools.LmMultiTurn.Collaboration;
using AchieveAi.LmDotnetTools.LmWorkflow.Runtime;
using AchieveAi.LmDotnetTools.Misc.Utils;

namespace LmStreaming.Sample.Services;

/// <summary>
///     The half of a conversation's tool wiring that belongs to the CONVERSATION rather than to the
///     mode or the model it is currently running under, kept as one instance for the conversation's
///     whole life — including across a mode or model switch the agent factory serves in place.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why these things and not others.</b> Everything here either IS conversation state (the
///         todo board, the workflow graph the model is editing, the collaboration directory) or is a
///         subscriber to it. The board's <c>OnChanged</c> multicast alone carries the live frame
///         publisher, the durable writer, the nudge service and the digest service; re-running that
///         wiring on every switch would add a second copy of each, so the third switch would publish
///         every board change four times and persist it four times. The MCP connections and the hosted
///         search session are deliberately NOT here either, but for the opposite reason: each is its own
///         keyed reusable resource (<c>mcp:sandbox:…</c>, <c>mcp:llmquery:…</c>, <c>hosted-search</c>)
///         that the factory re-registers on the next mode's registry, or carries dormant when that mode
///         hides it, so a child holding its handlers by reference keeps a live client across the switch.
///     </para>
///     <para>
///         <b>Lifetime.</b> Published to the agent pool under <see cref="ResourceKey"/> in both
///         <c>AgentCreationResult.OwnedResources</c> and <c>ReusableResources</c>: the first makes the
///         pool dispose it when the conversation's entry goes away, the second makes the pool offer it
///         back to the factory on the next switch instead of dropping it. Returning the same instance
///         is what keeps it alive; leaving it out is what ends it.
///     </para>
/// </remarks>
public sealed class ConversationToolScope : IAsyncDisposable
{
    /// <summary>The stable key this scope is published and looked up under.</summary>
    public const string ResourceKey = "conversation-scope";

    private readonly List<IAsyncDisposable> _owned = [];
    private bool _disposed;

    /// <summary>
    ///     The conversation's todo board — the single instance every task tool, every sub-agent and the
    ///     host's read path share.
    /// </summary>
    public required TaskManager Board { get; init; }

    /// <summary>
    ///     This conversation's collaboration directory and ledger, or <c>null</c> when the host has not
    ///     opted in for the mode it was created under.
    /// </summary>
    /// <remarks>
    ///     Resolved once, at creation, rather than per switch: the loop keeps its own handle across a
    ///     reconfigure, so a second one minted for the new mode would be a directory nothing reads.
    /// </remarks>
    public AgentCollaborationSetup? Collaboration { get; init; }

    /// <summary>
    ///     The live workflow-authoring graph, created the first time a mode selects the authoring tools
    ///     and kept afterwards — DORMANT under a mode that does not expose them, so switching back
    ///     restores the graph rather than an empty one.
    /// </summary>
    public WorkflowRuntime? AuthoringRuntime { get; set; }

    /// <summary>
    ///     Takes ownership of <paramref name="resource"/>, which is then disposed with this scope rather
    ///     than with the configuration that built it.
    /// </summary>
    /// <returns><paramref name="resource"/>, so a caller can own and keep using it in one expression.</returns>
    public T Own<T>(T resource)
        where T : IAsyncDisposable
    {
        ArgumentNullException.ThrowIfNull(resource);
        _owned.Add(resource);
        return resource;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Failures are swallowed per resource: this runs from the pool's entry teardown, where each of
    ///     these is a final flush (the board writer, the collaboration roster) and one refusing to close
    ///     must not stop the rest from being asked.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var resource in _owned)
        {
            try
            {
                await resource.DisposeAsync();
            }
            catch
            { /* a failed flush must not skip the remaining ones */
            }
        }

        _owned.Clear();
    }
}
