using System.Text.Json.Serialization;
using AchieveAi.LmDotnetTools.LmCore.Messages;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.Collaboration;

/// <summary>
/// One reply-bearing message that was still outstanding when the binding was captured.
/// </summary>
/// <remarks>
/// <para>
/// Identifiers, types and instants only — the same discipline the ledger itself keeps. A persisted
/// obligation exists so it can be <em>closed honestly</em> after a restart, never replayed: the target's
/// turn is gone, and ADR 0009 records that making the message fabric durable is the wrong answer. There
/// is deliberately nothing here that a delivery could be reconstructed from.
/// </para>
/// </remarks>
public sealed record OpenObligationRecord
{
    /// <summary>The message identifier the ledger minted at admission.</summary>
    [JsonPropertyName("message_id")]
    public required string MessageId { get; init; }

    /// <summary>Canonical identifier of the agent still waiting for an answer.</summary>
    [JsonPropertyName("from_agent_id")]
    public required string FromAgentId { get; init; }

    /// <summary>Canonical identifier of the agent that owed one.</summary>
    [JsonPropertyName("to_agent_id")]
    public required string ToAgentId { get; init; }

    /// <summary>Which reply-bearing kind this was.</summary>
    [JsonPropertyName("message_type")]
    public required AgentMessageType MessageType { get; init; }

    /// <summary>When the message was admitted.</summary>
    [JsonPropertyName("admitted_at")]
    public required DateTimeOffset AdmittedAt { get; init; }

    /// <summary>
    /// The board task this obligation was delegated against, when one was named.
    /// </summary>
    /// <remarks>
    /// Null on every row this build writes: the ledger records what a message means, not what board row
    /// prompted it. The member exists because a durable claim on the todo board is the other half of
    /// "a claim cannot point at a vanished actor", and the board's own restart reconciliation (#672)
    /// needs somewhere to record the link without a second persisted document.
    /// </remarks>
    [JsonPropertyName("task_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TaskId { get; init; }
}

/// <summary>
/// What one root conversation's collaboration looked like the last time it was captured: who was in
/// the directory, and what reply-bearing messages were still open between them.
/// </summary>
/// <remarks>
/// <para>
/// Scoped by <see cref="CollaborationId"/>, and that is load-bearing rather than decorative. Since #705
/// a sub-agent's id is an ordinal (<c>agent-1</c>, <c>agent-2</c>, …) minted per ROOT conversation, so
/// <em>every</em> conversation has an <c>agent-1</c>. An agent id alone therefore names an agent only
/// once you know which root it belongs to, and every lookup against these rows keys on the pair. The
/// same fact is why <see cref="CollaborationNodeRecord.AncestorAgentIds"/> is preserved verbatim: the
/// root is the first ancestor, and it is what turns a row back into the agent's transcript thread
/// (<c>SubAgentThreadIds.For</c>) without re-deriving any id shape here.
/// </para>
/// <para>
/// Bounded by construction: <see cref="AgentCollaborationOptions.MaxTotalAgents"/> caps the roster, and
/// only OPEN reply-bearing messages are recorded, so the document cannot grow with conversation length.
/// </para>
/// </remarks>
public sealed record AgentIdentityBindingSet
{
    /// <summary>Schema version this build writes.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Schema version of this document.</summary>
    [JsonPropertyName(CollaborationNodeRecord.SchemaVersionPropertyName)]
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>
    /// The collaboration — that is, the root conversation — these rows belong to. The scope half of
    /// every (scope, agent id) lookup.
    /// </summary>
    [JsonPropertyName("collaboration_id")]
    public required string CollaborationId { get; init; }

    /// <summary>Canonical identifier of the root agent that owns the collaboration.</summary>
    [JsonPropertyName("root_agent_id")]
    public required string RootAgentId { get; init; }

    /// <summary>When the capture was taken, so a stale write cannot regress a fresher one.</summary>
    [JsonPropertyName("captured_at_utc")]
    public required DateTimeOffset CapturedAtUtc { get; init; }

    /// <summary>Every directory node at capture time, ordered by canonical identifier.</summary>
    [JsonPropertyName("agents")]
    public IReadOnlyList<CollaborationNodeRecord> Agents { get; init; } = [];

    /// <summary>Every open reply-bearing message at capture time, oldest first.</summary>
    [JsonPropertyName("open_obligations")]
    public IReadOnlyList<OpenObligationRecord> OpenObligations { get; init; } = [];

    /// <summary>Whether there is nothing here worth persisting or reconciling.</summary>
    public bool IsEmpty => Agents.Count == 0 && OpenObligations.Count == 0;
}
