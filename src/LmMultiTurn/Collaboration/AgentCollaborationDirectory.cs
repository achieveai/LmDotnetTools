using System.Collections.Concurrent;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.Collaboration;

/// <summary>
/// One target's bounded queue of message identifiers awaiting delivery.
/// </summary>
/// <remarks>
/// <para>
/// Identifiers only, never message state. The ledger is the single source of truth for what a message
/// is and what has happened to it; if the inbox duplicated any of that, the two would eventually
/// disagree and there would be no principled way to say which was right.
/// </para>
/// <para>
/// The bound is per target in total rather than per sender. One sender can therefore fill a target's
/// inbox, after which every sender — including that one — is refused explicitly. A bound plus a
/// visible refusal is enough to stop unbounded growth; fairness quotas wait for a workload that proves
/// it needs them.
/// </para>
/// </remarks>
public sealed class AgentInbox
{
    private readonly Queue<string> _pending = new();
    private readonly object _gate = new();

    /// <summary>Creates an inbox with a fixed depth.</summary>
    /// <param name="capacity">Largest number of undelivered messages this target may hold.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity"/> is below one.</exception>
    public AgentInbox(int capacity)
    {
        if (capacity < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(capacity),
                capacity,
                "A target inbox must hold at least one message."
            );
        }

        Capacity = capacity;
    }

    /// <summary>Largest number of undelivered messages this target may hold.</summary>
    public int Capacity { get; }

    /// <summary>How many messages are currently waiting.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _pending.Count;
            }
        }
    }

    /// <summary>Adds a message identifier if there is room.</summary>
    /// <returns>False when the inbox is full, which is a recoverable backpressure signal.</returns>
    /// <exception cref="ArgumentException"><paramref name="messageId"/> is blank.</exception>
    public bool TryEnqueue(string messageId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);

        lock (_gate)
        {
            if (_pending.Count >= Capacity)
            {
                return false;
            }

            _pending.Enqueue(messageId);
            return true;
        }
    }

    /// <summary>Removes and returns the oldest waiting identifier.</summary>
    /// <returns>False when nothing is waiting.</returns>
    public bool TryDequeue(out string messageId)
    {
        lock (_gate)
        {
            return _pending.TryDequeue(out messageId!);
        }
    }

    /// <summary>The waiting identifiers, oldest first, without removing any.</summary>
    public IReadOnlyList<string> Peek()
    {
        lock (_gate)
        {
            return [.. _pending];
        }
    }
}

/// <summary>Why an agent could not be admitted to, or resolved in, the directory.</summary>
public static class AgentDirectoryFailureCodes
{
    /// <summary>No agent matches the requested identifier or name.</summary>
    public const string NotFound = "not_found";

    /// <summary>Several agents share the requested name, so a canonical identifier is required.</summary>
    public const string AmbiguousName = "ambiguous_name";

    /// <summary>An agent with that canonical identifier is already registered.</summary>
    public const string DuplicateAgentId = "duplicate_agent_id";

    /// <summary>The registration belongs to a different collaboration.</summary>
    public const string CrossCollaboration = "cross_collaboration";

    /// <summary>The name is blank.</summary>
    public const string InvalidName = "invalid_name";

    /// <summary>The role or description is blank or outside its length bounds.</summary>
    public const string InvalidMetadata = "invalid_metadata";

    /// <summary>The declared parent is not registered.</summary>
    public const string UnknownParent = "unknown_parent";

    /// <summary>A depth is negative, or inconsistent with the declared ancestry.</summary>
    public const string InvalidDepth = "invalid_depth";

    /// <summary>Admitting the agent would exceed the configured delegation depth.</summary>
    public const string DepthLimit = "depth_limit";
}

/// <summary>The outcome of an attempt to admit an agent to the directory.</summary>
/// <param name="Entry">The admitted snapshot, or null when admission failed.</param>
/// <param name="FailureCode">
/// A code from <see cref="AgentDirectoryFailureCodes"/> when admission failed, otherwise null.
/// </param>
public readonly record struct AgentRegistrationResult(
    AgentDirectoryEntry? Entry,
    string? FailureCode = null
)
{
    /// <summary>Whether the agent was admitted.</summary>
    public bool Succeeded => Entry is not null;
}

/// <summary>The outcome of resolving a canonical identifier or a name to an agent.</summary>
/// <param name="Entry">The resolved snapshot, or null when resolution failed.</param>
/// <param name="FailureCode">
/// A code from <see cref="AgentDirectoryFailureCodes"/> when resolution failed, otherwise null.
/// </param>
public readonly record struct AgentResolution(
    AgentDirectoryEntry? Entry,
    string? FailureCode = null
)
{
    /// <summary>Whether an agent was resolved.</summary>
    public bool Succeeded => Entry is not null;
}

/// <summary>
/// Everything the collaboration knows about <em>where</em> an agent is: identity, aliases, ancestry,
/// depth, status, capacity leases, per-target inboxes, and the capability that reaches each agent.
/// </summary>
/// <remarks>
/// <para>
/// Push-maintained rather than discovered. Entries are written at the lifecycle boundaries that
/// already exist — admitted, status changed, left — so a lookup is a dictionary read rather than a
/// walk over live agent state owned by somebody else. That is what keeps this class off the critical
/// path of the machinery that was already hard to get right.
/// </para>
/// <para>
/// It is not the transport. It hands out snapshots freely and endpoints never; the endpoint stays
/// private so that resolving an agent and being able to reach it remain two separate privileges.
/// </para>
/// </remarks>
public sealed class AgentCollaborationDirectory
{
    private readonly ConcurrentDictionary<string, Registration> _byAgentId = new(
        StringComparer.Ordinal
    );

    // Name is not an identity. A name maps to one agent or, once two agents have claimed it, to
    // permanent ambiguity — never to "the most recent one", because silently retargeting an alias
    // would send a reply to a different agent than the one the sender was talking to.
    private readonly ConcurrentDictionary<string, NameBinding> _byName = new(
        StringComparer.Ordinal
    );

    private readonly AgentCollaborationOptions _options;

    /// <summary>Creates an empty directory for one collaboration.</summary>
    /// <param name="collaborationId">The collaboration this directory describes.</param>
    /// <param name="options">Root configuration supplying the depth, capacity, and inbox bounds.</param>
    /// <exception cref="ArgumentException"><paramref name="collaborationId"/> is blank.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    public AgentCollaborationDirectory(string collaborationId, AgentCollaborationOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collaborationId);
        ArgumentNullException.ThrowIfNull(options);

        CollaborationId = collaborationId;
        _options = options;
        Capacity = new AgentCapacityLimiter(options.MaxTotalAgents);
    }

    /// <summary>The collaboration this directory describes.</summary>
    public string CollaborationId { get; }

    /// <summary>The root-wide agent budget.</summary>
    public AgentCapacityLimiter Capacity { get; }

    /// <summary>How many agents are registered, live or retained.</summary>
    public int Count => _byAgentId.Count;

    /// <summary>
    /// Takes a root-wide capacity permit for an agent, without waiting.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="TryRegister"/> on purpose: the permit must be taken before any
    /// per-manager gate or queue, which is strictly earlier than the point at which the agent has an
    /// identity to register. A queued agent therefore already counts against the cap.
    /// </remarks>
    /// <returns>A lease, or null when the collaboration is at capacity.</returns>
    public AgentCapacityLease? TryAcquireCapacity(string agentId)
    {
        return Capacity.TryAcquire(agentId);
    }

    /// <summary>
    /// Admits an agent, or refuses it entirely.
    /// </summary>
    /// <remarks>
    /// Admission is all-or-nothing. Every field is validated before anything is written, so a rejected
    /// registration leaves no half-registered agent that could be resolved, addressed, or counted.
    /// </remarks>
    /// <param name="context">The agent's immutable place in the hierarchy.</param>
    /// <param name="name">Human-facing name.</param>
    /// <param name="status">Initial lifecycle status.</param>
    /// <param name="writeEndpoint">The capability that delivers to this agent.</param>
    /// <param name="readEndpoint">The capability that reads this agent's status and transcript.</param>
    /// <param name="agentType">Template the agent was spawned from, when it came from one.</param>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is null.</exception>
    public AgentRegistrationResult TryRegister(
        AgentCollaborationContext context,
        string name,
        string status,
        IAgentWriteEndpoint? writeEndpoint = null,
        IAgentReadEndpoint? readEndpoint = null,
        string? agentType = null
    )
    {
        ArgumentNullException.ThrowIfNull(context);

        var failure = ValidateRegistration(context, name, status);
        if (failure is not null)
        {
            return new AgentRegistrationResult(null, failure);
        }

        // The root has no role or description to show — nothing has to choose whether to contact it —
        // so it is described structurally rather than being forced to invent metadata.
        var entry = new AgentDirectoryEntry
        {
            AgentId = context.AgentId,
            CollaborationId = context.CollaborationId,
            Name = name,
            ParentAgentId = context.ParentAgentId,
            AncestorAgentIds = context.AncestorAgentIds,
            Kind = context.Kind,
            Role = context.Role ?? context.Kind.ToString(),
            Description = context.Description ?? context.Kind.ToString(),
            AgentType = agentType,
            StructuralDepth = context.StructuralDepth,
            DelegationDepth = context.DelegationDepth,
            Status = status,
            IsLive = true,
        };

        var registration = new Registration(
            entry,
            writeEndpoint,
            readEndpoint,
            new AgentInbox(_options.MaxInboxMessages)
        );

        if (!_byAgentId.TryAdd(entry.AgentId, registration))
        {
            return new AgentRegistrationResult(null, AgentDirectoryFailureCodes.DuplicateAgentId);
        }

        BindName(name, entry.AgentId);
        return new AgentRegistrationResult(entry);
    }

    /// <summary>Updates an agent's lifecycle status, leaving its identity untouched.</summary>
    /// <returns>False when no such agent is registered.</returns>
    public bool TryUpdateStatus(string agentId, string status)
    {
        if (string.IsNullOrWhiteSpace(agentId) || string.IsNullOrWhiteSpace(status))
        {
            return false;
        }

        return TryMutate(agentId, entry => entry with { Status = status });
    }

    /// <summary>
    /// Marks an agent as no longer addressable while keeping it visible.
    /// </summary>
    /// <remarks>
    /// A stopped agent's entry has to outlive it: a sender holding an open Question needs to learn that
    /// its target is gone, and an entry that vanished would be indistinguishable from one that never
    /// existed.
    /// </remarks>
    /// <returns>False when no such agent is registered.</returns>
    public bool TryMarkRetained(string agentId)
    {
        return TryMutate(agentId, entry => entry with { IsLive = false });
    }

    /// <summary>Looks an agent up by canonical identifier.</summary>
    public AgentDirectoryEntry? FindById(string agentId)
    {
        return string.IsNullOrWhiteSpace(agentId) ? null
            : _byAgentId.TryGetValue(agentId, out var registration) ? registration.Entry
            : null;
    }

    /// <summary>
    /// Resolves a canonical identifier or a name to one agent.
    /// </summary>
    /// <remarks>
    /// Identifiers win over names, so an agent named after another agent's identifier cannot shadow it.
    /// A name claimed by more than one agent resolves to nothing at all rather than to a guess.
    /// </remarks>
    public AgentResolution Resolve(string target)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return new AgentResolution(null, AgentDirectoryFailureCodes.NotFound);
        }

        if (_byAgentId.TryGetValue(target, out var byId))
        {
            return new AgentResolution(byId.Entry);
        }

        if (!_byName.TryGetValue(target, out var binding))
        {
            return new AgentResolution(null, AgentDirectoryFailureCodes.NotFound);
        }

        if (binding.IsAmbiguous)
        {
            return new AgentResolution(null, AgentDirectoryFailureCodes.AmbiguousName);
        }

        return _byAgentId.TryGetValue(binding.AgentId, out var byName)
            ? new AgentResolution(byName.Entry)
            : new AgentResolution(null, AgentDirectoryFailureCodes.NotFound);
    }

    /// <summary>Every registered agent, ordered by canonical identifier so listings are stable.</summary>
    public IReadOnlyList<AgentDirectoryEntry> Snapshot()
    {
        return
        [
            .. _byAgentId
                .Values.Select(registration => registration.Entry)
                .OrderBy(entry => entry.AgentId, StringComparer.Ordinal),
        ];
    }

    /// <summary>The bounded queue of message identifiers awaiting delivery to an agent.</summary>
    public AgentInbox? GetInbox(string agentId)
    {
        return string.IsNullOrWhiteSpace(agentId) ? null
            : _byAgentId.TryGetValue(agentId, out var registration) ? registration.Inbox
            : null;
    }

    /// <summary>
    /// The capability that delivers to an agent. Internal, because handing a caller an endpoint would
    /// hand it the ability to bypass admission, correlation, and inbox bounds.
    /// </summary>
    internal IAgentWriteEndpoint? GetWriteEndpoint(string agentId)
    {
        return _byAgentId.TryGetValue(agentId, out var registration)
            ? registration.WriteEndpoint
            : null;
    }

    /// <summary>
    /// The capability that reads an agent. Internal, because it carries no authorization of its own —
    /// the transcript policy must be consulted first.
    /// </summary>
    internal IAgentReadEndpoint? GetReadEndpoint(string agentId)
    {
        return _byAgentId.TryGetValue(agentId, out var registration)
            ? registration.ReadEndpoint
            : null;
    }

    private string? ValidateRegistration(
        AgentCollaborationContext context,
        string name,
        string status
    )
    {
        if (!string.Equals(context.CollaborationId, CollaborationId, StringComparison.Ordinal))
        {
            return AgentDirectoryFailureCodes.CrossCollaboration;
        }

        if (string.IsNullOrWhiteSpace(context.AgentId))
        {
            return AgentDirectoryFailureCodes.NotFound;
        }

        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(status))
        {
            return AgentDirectoryFailureCodes.InvalidName;
        }

        // A root is described structurally, so it is the one node exempt from supplying metadata.
        if (
            context.Kind != AgentKind.Root
            && !AgentCollaborationContext.IsMetadataValid(context.Role, context.Description)
        )
        {
            return AgentDirectoryFailureCodes.InvalidMetadata;
        }

        if (context.StructuralDepth < 0 || context.DelegationDepth < 0)
        {
            return AgentDirectoryFailureCodes.InvalidDepth;
        }

        if (context.AncestorAgentIds.Length != context.StructuralDepth)
        {
            return AgentDirectoryFailureCodes.InvalidDepth;
        }

        if (context.DelegationDepth > _options.MaxDelegationDepth)
        {
            return AgentDirectoryFailureCodes.DepthLimit;
        }

        if (context.ParentAgentId is not null && !_byAgentId.ContainsKey(context.ParentAgentId))
        {
            return AgentDirectoryFailureCodes.UnknownParent;
        }

        return _byAgentId.ContainsKey(context.AgentId)
            ? AgentDirectoryFailureCodes.DuplicateAgentId
            : null;
    }

    private void BindName(string name, string agentId)
    {
        _ = _byName.AddOrUpdate(
            name,
            _ => new NameBinding(agentId, IsAmbiguous: false),
            // Latching, not toggling: once two agents have answered to a name, the name stays
            // unusable even if one of them leaves, because a sender cannot know which one it meant.
            (_, existing) =>
                string.Equals(existing.AgentId, agentId, StringComparison.Ordinal)
                    ? existing
                    : existing with
                    {
                        IsAmbiguous = true,
                    }
        );
    }

    private bool TryMutate(string agentId, Func<AgentDirectoryEntry, AgentDirectoryEntry> mutate)
    {
        if (string.IsNullOrWhiteSpace(agentId))
        {
            return false;
        }

        while (_byAgentId.TryGetValue(agentId, out var current))
        {
            var updated = current with { Entry = mutate(current.Entry) };
            if (_byAgentId.TryUpdate(agentId, updated, current))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// What the directory actually stores: the public snapshot plus the two capabilities and the inbox
    /// that must not travel with it.
    /// </summary>
    private sealed record Registration(
        AgentDirectoryEntry Entry,
        IAgentWriteEndpoint? WriteEndpoint,
        IAgentReadEndpoint? ReadEndpoint,
        AgentInbox Inbox
    );

    private readonly record struct NameBinding(string AgentId, bool IsAmbiguous);
}
