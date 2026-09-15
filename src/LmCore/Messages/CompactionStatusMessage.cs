using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.LmCore.Messages;

/// <summary>
///     A live-only frame for one compaction as it happens, manual or automatic: requested, running, then
///     applied, refused or failed. Implements <see cref="ITransientMessage" />: never buffered, added to
///     history, or persisted — the checkpoint row and <c>compaction.state</c> stay the record.
/// </summary>
/// <remarks>
///     Content-free apart from <see cref="Focus" />, which is the operator's own text. Field names are fixed
///     camelCase via <see cref="JsonPropertyNameAttribute" />, matching <c>context_pressure</c>.
/// </remarks>
public sealed record CompactionStatusMessage : IMessage, ITransientMessage
{
    /// <summary>The wire discriminator.</summary>
    public const string TypeDiscriminator = "compaction_status";

    /// <summary>Values of <see cref="Phase" />.</summary>
    public static class Phases
    {
        public const string Requested = "requested";
        public const string Running = "running";
        public const string Applied = "applied";
        public const string Refused = "refused";
        public const string Failed = "failed";
    }

    /// <summary>The thread being compacted.</summary>
    [JsonPropertyName("threadId")]
    public string? ThreadId { get; init; }

    /// <summary><c>root</c> or the sub-agent id.</summary>
    [JsonPropertyName("agentId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AgentId { get; init; }

    /// <summary>The manual request this frame belongs to; null for an automatic compaction.</summary>
    [JsonPropertyName("requestId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RequestId { get; init; }

    /// <summary><c>manual</c>, <c>preemptive</c> or <c>reactive</c>.</summary>
    [JsonPropertyName("trigger")]
    public required string Trigger { get; init; }

    /// <summary>One of <see cref="Phases" />.</summary>
    [JsonPropertyName("phase")]
    public required string Phase { get; init; }

    /// <summary>The typed reason for a refusal or failure.</summary>
    [JsonPropertyName("reason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Reason { get; init; }

    /// <summary>The checkpoint that was activated.</summary>
    [JsonPropertyName("checkpointId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CheckpointId { get; init; }

    /// <summary>The operator's focus for a manual compaction.</summary>
    [JsonPropertyName("focus")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Focus { get; init; }

    /// <summary>Not set: a compaction is not part of a generation's output.</summary>
    [JsonPropertyName("generationId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? GenerationId { get; init; }

    /// <summary>The role associated with this frame (assistant, matching other loop-emitted messages).</summary>
    [JsonPropertyName("role")]
    public Role Role { get; init; } = Role.Assistant;

    /// <summary>The name or identifier of the agent that produced this frame.</summary>
    [JsonPropertyName("fromAgent")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FromAgent { get; init; }

    /// <summary>Not carried on transient frames.</summary>
    [JsonIgnore]
    public ImmutableDictionary<string, object>? Metadata { get; init; }
}
