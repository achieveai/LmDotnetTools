using System.Collections.Immutable;
using System.Text.Json.Serialization;
using AchieveAi.LmDotnetTools.LmCore.Messages;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.Collaboration;

/// <summary>
/// The lifecycle vocabulary the directory publishes, shared verbatim with the sub-agent observation
/// surface so the same agent is never described two different ways by two different tools.
/// </summary>
public static class AgentCollaborationStatuses
{
    /// <summary>Accepted, but not started yet.</summary>
    public const string Queued = "queued";

    /// <summary>Currently executing.</summary>
    public const string Running = "running";

    /// <summary>Finished its work successfully.</summary>
    public const string Completed = "completed";

    /// <summary>Finished by failing.</summary>
    public const string Error = "error";

    /// <summary>Stopped before finishing.</summary>
    public const string Stopped = "stopped";
}

/// <summary>
/// A read-only snapshot of one agent's directory entry: who it is, where it sits, and whether it is
/// still live.
/// </summary>
/// <remarks>
/// Deliberately a plain value with no capability on it. The endpoint that can actually reach an agent
/// is held privately by the directory, so handing a caller a snapshot never hands it the ability to
/// deliver to, or read from, the agent the snapshot describes.
/// </remarks>
public sealed record AgentDirectoryEntry
{
    /// <summary>Canonical, stable identifier. The only safe addressing key.</summary>
    public required string AgentId { get; init; }

    /// <summary>Collaboration this agent belongs to.</summary>
    public required string CollaborationId { get; init; }

    /// <summary>Human-facing name. May collide with another agent's, so it is not an addressing key.</summary>
    public required string Name { get; init; }

    /// <summary>The agent directly above this one, or null for the root.</summary>
    public string? ParentAgentId { get; init; }

    /// <summary>Every agent above this one, root first, excluding this agent.</summary>
    public ImmutableArray<string> AncestorAgentIds { get; init; } = [];

    /// <summary>What kind of node this is.</summary>
    public required AgentKind Kind { get; init; }

    /// <summary>Short statement of what this agent is for.</summary>
    public required string Role { get; init; }

    /// <summary>Longer guidance on when to contact this agent.</summary>
    public required string Description { get; init; }

    /// <summary>Template this agent was spawned from, when it came from one.</summary>
    public string? AgentType { get; init; }

    /// <summary>How many hierarchy levels lie between the root and this agent.</summary>
    public int StructuralDepth { get; init; }

    /// <summary>How much delegation budget has been spent reaching this agent.</summary>
    public int DelegationDepth { get; init; }

    /// <summary>
    /// Lifecycle status, using the same lowercase vocabulary the existing sub-agent observation
    /// surface already publishes (<c>queued</c>, <c>running</c>, <c>completed</c>, <c>error</c>,
    /// <c>stopped</c>).
    /// </summary>
    /// <remarks>
    /// Reusing those exact strings rather than introducing a parallel enum is what keeps a
    /// collaboration-wide listing and a per-manager <c>CheckAgents</c> from reporting the same agent
    /// two different ways.
    /// </remarks>
    public required string Status { get; init; }

    /// <summary>
    /// Whether this agent is still addressable. A retained entry stays visible for correlation after
    /// the agent stops, but nothing new can be delivered to it.
    /// </summary>
    public bool IsLive { get; init; } = true;

    /// <summary>Whether this agent has reached a terminal status.</summary>
    public bool IsTerminal =>
        Status
            is AgentCollaborationStatuses.Completed
                or AgentCollaborationStatuses.Error
                or AgentCollaborationStatuses.Stopped;
}

/// <summary>
/// The persisted, versioned projection of one hierarchy node.
/// </summary>
/// <remarks>
/// Separate from <see cref="AgentDirectoryEntry"/> because the in-memory snapshot is free to change
/// shape with the code, while anything written down has to be readable by a build that predates the
/// change. <see cref="SchemaVersion"/> is written explicitly so a future reader can tell an old row
/// from a corrupt one.
/// </remarks>
public sealed record CollaborationNodeRecord
{
    /// <summary>Schema version this build writes.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Schema version of this row.</summary>
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>Canonical identifier of the agent.</summary>
    [JsonPropertyName("agent_id")]
    public required string AgentId { get; init; }

    /// <summary>Collaboration the agent belongs to.</summary>
    [JsonPropertyName("collaboration_id")]
    public required string CollaborationId { get; init; }

    /// <summary>Human-facing name of the agent.</summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>The agent directly above this one, or null for the root.</summary>
    [JsonPropertyName("parent_agent_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ParentAgentId { get; init; }

    /// <summary>Every agent above this one, root first.</summary>
    [JsonPropertyName("ancestor_agent_ids")]
    public IReadOnlyList<string> AncestorAgentIds { get; init; } = [];

    /// <summary>What kind of node this is.</summary>
    [JsonPropertyName("kind")]
    public required AgentKind Kind { get; init; }

    /// <summary>Short statement of what the agent is for.</summary>
    [JsonPropertyName("role")]
    public required string Role { get; init; }

    /// <summary>Longer guidance on when to contact the agent.</summary>
    [JsonPropertyName("description")]
    public required string Description { get; init; }

    /// <summary>Template the agent was spawned from, when it came from one.</summary>
    [JsonPropertyName("agent_type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AgentType { get; init; }

    /// <summary>How many hierarchy levels lie between the root and the agent.</summary>
    [JsonPropertyName("structural_depth")]
    public int StructuralDepth { get; init; }

    /// <summary>How much delegation budget has been spent reaching the agent.</summary>
    [JsonPropertyName("delegation_depth")]
    public int DelegationDepth { get; init; }

    /// <summary>Lifecycle status at the time the row was written.</summary>
    [JsonPropertyName("status")]
    public required string Status { get; init; }

    /// <summary>Projects a live snapshot into its persisted form.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="entry"/> is null.</exception>
    public static CollaborationNodeRecord FromEntry(AgentDirectoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return new CollaborationNodeRecord
        {
            AgentId = entry.AgentId,
            CollaborationId = entry.CollaborationId,
            Name = entry.Name,
            ParentAgentId = entry.ParentAgentId,
            AncestorAgentIds = [.. entry.AncestorAgentIds],
            Kind = entry.Kind,
            Role = entry.Role,
            Description = entry.Description,
            AgentType = entry.AgentType,
            StructuralDepth = entry.StructuralDepth,
            DelegationDepth = entry.DelegationDepth,
            Status = entry.Status,
        };
    }

    /// <summary>
    /// Rehydrates a snapshot from a persisted row. The result is never marked live: a row written
    /// before a restart describes an agent that is no longer running, and treating it as reachable is
    /// how a caller would end up addressing a dead endpoint.
    /// </summary>
    public AgentDirectoryEntry ToEntry()
    {
        return new AgentDirectoryEntry
        {
            AgentId = AgentId,
            CollaborationId = CollaborationId,
            Name = Name,
            ParentAgentId = ParentAgentId,
            AncestorAgentIds = [.. AncestorAgentIds],
            Kind = Kind,
            Role = Role,
            Description = Description,
            AgentType = AgentType,
            StructuralDepth = StructuralDepth,
            DelegationDepth = DelegationDepth,
            Status = Status,
            IsLive = false,
        };
    }
}

/// <summary>How an attempt to hand a message to a target ended.</summary>
public enum AgentDeliveryDisposition
{
    /// <summary>The target's owner accepted the message into the target's own input path.</summary>
    Delivered,

    /// <summary>
    /// The target's owner refused, recoverably — it is busy, full, or not currently deliverable. The
    /// sender may retry.
    /// </summary>
    Refused,

    /// <summary>
    /// Delivery failed terminally. The message will not arrive and must not be retried under the same
    /// identifier.
    /// </summary>
    Failed,
}

/// <summary>The outcome of one delivery attempt.</summary>
/// <param name="Disposition">How the attempt ended.</param>
/// <param name="ReasonCode">
/// Short, content-free code explaining a refusal or failure. Codes are safe to log; the message body
/// never is.
/// </param>
public readonly record struct AgentDeliveryOutcome(AgentDeliveryDisposition Disposition, string? ReasonCode = null)
{
    /// <summary>Whether the target's owner accepted the message.</summary>
    public bool IsDelivered => Disposition == AgentDeliveryDisposition.Delivered;
}

/// <summary>
/// The narrow write capability the directory holds for one agent: hand this message to that agent.
/// </summary>
/// <remarks>
/// Implemented by whatever already owns the target — a loop, a manager — so delivery still happens
/// inside the target's own lifecycle rules rather than through a parallel transport. Split from
/// <see cref="IAgentReadEndpoint"/> precisely so holding the ability to read an agent never confers
/// the ability to inject into it.
/// </remarks>
public interface IAgentWriteEndpoint
{
    /// <summary>Hands one already-admitted message to the target.</summary>
    /// <param name="message">The message to deliver. Its identity and correlation are already trusted.</param>
    /// <param name="cancellationToken">Cancels the attempt.</param>
    ValueTask<AgentDeliveryOutcome> DeliverAsync(AgentMessage message, CancellationToken cancellationToken = default);
}

/// <summary>
/// The narrow read capability the directory holds for one agent: what is it doing, and what has it
/// said.
/// </summary>
/// <remarks>
/// Every call through this interface is gated by the transcript policy before it is reached. The
/// interface itself carries no authorization, so it must never be handed to a caller directly.
/// </remarks>
public interface IAgentReadEndpoint
{
    /// <summary>Current lifecycle status, in the same lowercase vocabulary the directory publishes.</summary>
    ValueTask<string> GetStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>The agent's conversation so far, as the projection its owner already produces.</summary>
    ValueTask<IReadOnlyList<IMessage>> GetTranscriptAsync(CancellationToken cancellationToken = default);
}
