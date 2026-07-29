using System.Text.Json.Serialization;
using AchieveAi.LmDotnetTools.LmLifecycle.Serialization;

namespace AchieveAi.LmDotnetTools.LmLifecycle.Approval;

/// <summary>
/// The question put to an approver: may this specific tool call, with these specific arguments,
/// run?
/// </summary>
/// <remarks>
/// <para>
/// The request describes one invocation, not a tool or a category. Approving it approves exactly
/// the bytes identified by <see cref="ArgumentsHash"/> and nothing else.
/// </para>
/// <para>
/// It reaches an approver only after the cheap local checks have already passed. A call that
/// provider-native or host policy refuses is blocked without a request ever being issued, so an
/// approver is never asked about something that was going to be refused anyway.
/// </para>
/// </remarks>
public sealed record ToolApprovalRequest
{
    /// <summary>
    /// Identity of this approval request. A decision names it, and a second decision naming the
    /// same request is resolved against the first rather than replacing it.
    /// </summary>
    [JsonPropertyName("request_id")]
    [JsonRequired]
    public string RequestId { get; set; } = string.Empty;

    /// <summary>
    /// The subscription this copy of the request was addressed to.
    /// </summary>
    /// <remarks>
    /// A request is fanned out to every approver frozen when the gate opened, and each copy names the
    /// approver it went to. The decision echoes it back, which is what lets the host tell <i>which</i>
    /// of the frozen approvers answered — and refuse an answer from one that was not asked. Absent
    /// only on the host's own internal copy, which is never sent anywhere.
    /// </remarks>
    [JsonPropertyName("subscription_id")]
    public string? SubscriptionId { get; set; }

    /// <summary>The thread the call belongs to.</summary>
    [JsonPropertyName("thread_id")]
    public string ThreadId { get; set; } = string.Empty;

    /// <summary>The run that requested the call.</summary>
    [JsonPropertyName("run_id")]
    public string RunId { get; set; } = string.Empty;

    /// <summary>The turn that requested the call.</summary>
    [JsonPropertyName("generation_id")]
    public string GenerationId { get; set; } = string.Empty;

    /// <summary>The tool call awaiting a decision.</summary>
    [JsonPropertyName("tool_call_id")]
    [JsonRequired]
    public string ToolCallId { get; set; } = string.Empty;

    /// <summary>The tool's registered name.</summary>
    [JsonPropertyName("tool_name")]
    public string ToolName { get; set; } = string.Empty;

    /// <summary>
    /// SHA-256 of the exact argument string that will execute, over its UTF-8 bytes, in lowercase
    /// hex.
    /// </summary>
    /// <remarks>
    /// Frozen when the gate opened. A decision must echo this value; a decision quoting a different
    /// hash is refused, because it decided about arguments other than the ones that will run.
    /// </remarks>
    [JsonPropertyName("arguments_hash")]
    [JsonRequired]
    public string ArgumentsHash { get; set; } = string.Empty;

    /// <summary>
    /// The argument string itself. Present only for approvers granted
    /// <see cref="LifecycleCapabilities.ContentFull"/>; otherwise omitted, leaving
    /// <see cref="ArgumentsHash"/> as the only description of what will run.
    /// </summary>
    [JsonPropertyName("arguments")]
    public string? Arguments { get; set; }

    /// <summary>
    /// When this request stops being answerable, in UTC.
    /// </summary>
    /// <remarks>
    /// Already reduced to the effective expiry — the earliest of the configured maximum wait, any
    /// provider or host deadline, and the lifetime of the run and turn that requested the call. A
    /// decision arriving after it is refused as late, and a pending approval can never outlive its
    /// run.
    /// </remarks>
    [JsonPropertyName("expires_at")]
    [JsonRequired]
    [JsonConverter(typeof(CanonicalTimestampConverter))]
    public DateTimeOffset ExpiresAt { get; set; }
}
