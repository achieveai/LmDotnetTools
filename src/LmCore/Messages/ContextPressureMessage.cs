using System.Collections.Immutable;
using System.Text.Json.Serialization;
using AchieveAi.LmDotnetTools.LmCore.Models;

namespace AchieveAi.LmDotnetTools.LmCore.Messages;

/// <summary>
///     A live-only frame carrying one agent loop's latest <see cref="ContextObservation" /> (#681; spec 679
///     §7.2): how full the model's window is, measured or estimated, for the thread it was taken on.
///     Published after each observation is written, per agent thread. Implements
///     <see cref="ITransientMessage" />: never buffered, added to history, or persisted — a reconnecting
///     client restores the authoritative figure from <c>GET /api/conversations/{id}/context</c>, and live
///     frames only update that snapshot, never replace it (the usage-frame rule).
/// </summary>
/// <remarks>
///     Content-free by construction: counts, ratios, ids and statuses only. Field names are fixed camelCase
///     via <see cref="JsonPropertyNameAttribute" /> so the wire shape is stable regardless of the
///     serializer's naming policy.
/// </remarks>
public sealed record ContextPressureMessage : IMessage, ITransientMessage
{
    /// <summary>The wire discriminator.</summary>
    public const string TypeDiscriminator = "context_pressure";

    /// <summary>The thread the observation was taken on.</summary>
    [JsonPropertyName("threadId")]
    public string? ThreadId { get; init; }

    /// <summary><c>root</c> or the sub-agent id.</summary>
    [JsonPropertyName("agentId")]
    public required string AgentId { get; init; }

    /// <summary>The run the generation ran in.</summary>
    [JsonPropertyName("runId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RunId { get; init; }

    /// <summary>The generation observed.</summary>
    [JsonPropertyName("generationId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? GenerationId { get; init; }

    /// <summary>Loop-local generation counter.</summary>
    [JsonPropertyName("generationOrdinal")]
    public long GenerationOrdinal { get; init; }

    /// <summary>When the observation was taken.</summary>
    [JsonPropertyName("observedAtUtc")]
    public DateTimeOffset ObservedAtUtc { get; init; }

    /// <summary>The model the request was sent to.</summary>
    [JsonPropertyName("effectiveModelId")]
    public required string EffectiveModelId { get; init; }

    /// <summary>Pre-send estimate of the request's input tokens.</summary>
    [JsonPropertyName("estimatedInputTokens")]
    public long EstimatedInputTokens { get; init; }

    /// <summary>Post-response measurement, when the provider reported usage.</summary>
    [JsonPropertyName("measuredInputTokens")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? MeasuredInputTokens { get; init; }

    /// <summary>How the size number was obtained: Measured, Estimated, Unavailable.</summary>
    [JsonPropertyName("provenance")]
    public string Provenance { get; init; } = nameof(MeasurementProvenance.Unavailable);

    /// <summary>The model's context window, when known.</summary>
    [JsonPropertyName("windowTokens")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? WindowTokens { get; init; }

    /// <summary>Tokens held back for output.</summary>
    [JsonPropertyName("reserveTokens")]
    public long ReserveTokens { get; init; }

    /// <summary>Fraction of the usable window the request occupies, or null when the window is unknown.</summary>
    [JsonPropertyName("utilization")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Utilization { get; init; }

    /// <summary>The checkpoint the view was built on, when one was active.</summary>
    [JsonPropertyName("activeCheckpointId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ActiveCheckpointId { get; init; }

    /// <summary>How many rows the execution view contained.</summary>
    [JsonPropertyName("rowsInView")]
    public long RowsInView { get; init; }

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

    /// <summary>Projects an observation into the frame. Every field is content-free.</summary>
    public static ContextPressureMessage FromObservation(ContextObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);

        return new ContextPressureMessage
        {
            ThreadId = observation.ThreadId,
            AgentId = observation.AgentId,
            RunId = observation.RunId,
            GenerationId = observation.GenerationId,
            GenerationOrdinal = observation.GenerationOrdinal,
            ObservedAtUtc = observation.ObservedAtUtc,
            EffectiveModelId = observation.EffectiveModelId,
            EstimatedInputTokens = observation.EstimatedInputTokens,
            MeasuredInputTokens = observation.MeasuredInputTokens,
            Provenance = observation.Provenance.ToString(),
            WindowTokens = observation.WindowTokens,
            ReserveTokens = observation.ReserveTokens,
            Utilization = observation.Utilization,
            ActiveCheckpointId = observation.ActiveCheckpointId,
            RowsInView = observation.RowsInView,
        };
    }
}
