using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.LmLifecycle.Payloads;

/// <summary>
/// Why a run started, and what started it.
/// </summary>
/// <remarks>
/// A run always has a cause. The delayed-result path in particular carries the real tool call that
/// produced the result rather than a fabricated user message, so a consumer replaying history never
/// feeds the model a turn the user did not take. See ADR 0004.
/// </remarks>
public sealed record LifecycleRunCause
{
    /// <summary>
    /// What kind of thing caused the run. See <see cref="LifecycleRunCauseKinds"/>. Open
    /// vocabulary.
    /// </summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = LifecycleRunCauseKinds.UserInput;

    /// <summary>
    /// The tool call whose result caused this run, for
    /// <see cref="LifecycleRunCauseKinds.ToolResult"/> and
    /// <see cref="LifecycleRunCauseKinds.SubAgentSpawn"/>. Absent otherwise.
    /// </summary>
    [JsonPropertyName("tool_call_id")]
    public string? ToolCallId { get; set; }
}

/// <summary>
/// Payload for <see cref="LifecycleEventTypes.RunStarted"/>.
/// </summary>
public sealed record RunStartedPayload
{
    /// <summary>The run that started.</summary>
    [JsonPropertyName("run_id")]
    public string RunId { get; set; } = string.Empty;

    /// <summary>The first turn's generation id.</summary>
    [JsonPropertyName("generation_id")]
    public string GenerationId { get; set; } = string.Empty;

    /// <summary>What caused the run.</summary>
    [JsonPropertyName("cause")]
    public LifecycleRunCause Cause { get; set; } = new();

    /// <summary>
    /// Whether the caller asked the run to inherit provider-side context from its parent.
    /// </summary>
    /// <remarks>
    /// This describes provider-context inheritance and nothing about lineage. A delayed-result
    /// child run is a continuation, not a fork, so it reports <see langword="false"/> while still
    /// carrying a parent run id in its correlation.
    /// </remarks>
    [JsonPropertyName("was_forked")]
    public bool WasForked { get; set; }

    /// <summary>
    /// Which agent implementation is running. See <see cref="LifecycleAgentKinds"/>. Open
    /// vocabulary.
    /// </summary>
    [JsonPropertyName("agent_kind")]
    public string AgentKind { get; set; } = string.Empty;

    /// <summary>The model the run was configured with, when the host knows it.</summary>
    [JsonPropertyName("model_id")]
    public string? ModelId { get; set; }
}
