namespace AchieveAi.LmDotnetTools.LmMultiTurn.Collaboration;

/// <summary>
/// One agent's handle on the collaboration it belongs to: the shared root-owned
/// <see cref="AgentCollaborationBundle"/> plus this agent's own immutable place in it.
/// </summary>
/// <remarks>
/// <para>
/// This exists so that turning collaboration on is a <em>single</em> optional constructor argument on
/// every composition root that can build an agent, rather than three loose ones threaded
/// independently through a loop, a manager, a queued spawn, and a state record. Absence of the whole
/// object is the feature gate: a host that never supplies one keeps today's behaviour exactly.
/// </para>
/// <para>
/// The bundle is shared by reference all the way down the hierarchy — one directory, one ledger, one
/// set of bounds for the whole root conversation — while <see cref="Context"/> differs per agent.
/// </para>
/// </remarks>
public sealed class AgentCollaborationSetup
{
    /// <summary>Creates a handle onto an existing collaboration.</summary>
    /// <param name="bundle">The root-owned directory, ledger, and bounds.</param>
    /// <param name="context">This agent's immutable place in the hierarchy.</param>
    /// <param name="name">This agent's human-facing name.</param>
    /// <exception cref="ArgumentNullException"><paramref name="bundle"/> or <paramref name="context"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> is blank, or the context belongs to another collaboration.</exception>
    public AgentCollaborationSetup(AgentCollaborationBundle bundle, AgentCollaborationContext context, string name)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (!string.Equals(bundle.CollaborationId, context.CollaborationId, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The context belongs to a different collaboration than the bundle.",
                nameof(context)
            );
        }

        Bundle = bundle;
        Context = context;
        Name = name;
    }

    /// <summary>The root-owned collaboration state shared by every agent in the hierarchy.</summary>
    public AgentCollaborationBundle Bundle { get; }

    /// <summary>This agent's immutable place in the hierarchy.</summary>
    public AgentCollaborationContext Context { get; }

    /// <summary>This agent's human-facing name.</summary>
    public string Name { get; }

    /// <summary>The bounds the root configured.</summary>
    public AgentCollaborationOptions Options => Bundle.Options;

    /// <summary>The shared directory.</summary>
    public AgentCollaborationDirectory Directory => Bundle.Directory;

    /// <summary>This agent's canonical identifier.</summary>
    public string AgentId => Context.AgentId;

    /// <summary>
    /// Whether this agent may still spawn an ordinary sub-agent, i.e. whether one more delegation hop
    /// stays inside <see cref="AgentCollaborationOptions.MaxDelegationDepth"/>.
    /// </summary>
    public bool CanDelegate => Context.DelegationDepth < Options.MaxDelegationDepth;

    /// <summary>
    /// Starts a new collaboration rooted at one agent.
    /// </summary>
    /// <param name="options">The bounds for the whole collaboration. Validated here.</param>
    /// <param name="collaborationId">Identifier for the collaboration; generated when omitted.</param>
    /// <param name="agentId">Canonical identifier for the root agent; generated when omitted.</param>
    /// <param name="name">Human-facing name for the root agent.</param>
    /// <param name="timeProvider">Clock the ledger measures retention against.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A bound in <paramref name="options"/> is unusable.</exception>
    public static AgentCollaborationSetup CreateRoot(
        AgentCollaborationOptions options,
        string? collaborationId = null,
        string? agentId = null,
        string name = "root",
        TimeProvider? timeProvider = null
    )
    {
        ArgumentNullException.ThrowIfNull(options);

        var resolvedCollaborationId = string.IsNullOrWhiteSpace(collaborationId)
            ? "collab-" + Guid.NewGuid().ToString("N")
            : collaborationId;
        var resolvedAgentId = string.IsNullOrWhiteSpace(agentId)
            ? "agent-" + Guid.NewGuid().ToString("N")[..12]
            : agentId;

        var bundle = new AgentCollaborationBundle(resolvedCollaborationId, options, timeProvider);
        var context = AgentCollaborationContext.ForRoot(resolvedCollaborationId, resolvedAgentId);
        return new AgentCollaborationSetup(bundle, context, name);
    }

    /// <summary>
    /// Derives the handle a child agent gets: the same bundle, the child's own context and name.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="childContext"/> is null.</exception>
    public AgentCollaborationSetup ForChild(AgentCollaborationContext childContext, string childName)
    {
        return new AgentCollaborationSetup(Bundle, childContext, childName);
    }
}
