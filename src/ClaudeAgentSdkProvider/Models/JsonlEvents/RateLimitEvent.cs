using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Models.JsonlEvents;

/// <summary>
///     Rate-limit advisory emitted by the Claude Code CLI (2.x) before each turn —
///     reports the current quota status and any active overage policy. The legacy 0.1.x
///     bundle didn't emit these; treat their absence as not significant.
/// </summary>
/// <remarks>
///     Example payload:
///     <code>
///     {
///       "type": "rate_limit_event",
///       "rate_limit_info": {
///         "status": "allowed",
///         "resetsAt": 1777410000,
///         "rateLimitType": "five_hour",
///         "overageStatus": "rejected",
///         "overageDisabledReason": "org_level_disabled",
///         "isUsingOverage": false
///       },
///       "uuid": "...",
///       "session_id": "..."
///     }
///     </code>
///     We surface this through the parser so callers can log or react (e.g. back off when
///     <c>status != "allowed"</c>) — historically the parser warned "Unhandled JSONL event
///     type" on every turn the CLI emitted one, which polluted logs.
/// </remarks>
public record RateLimitEvent : JsonlEventBase
{
    [JsonPropertyName("rate_limit_info")]
    public RateLimitInfo? RateLimitInfo { get; init; }

    [JsonPropertyName("uuid")]
    public string? Uuid { get; init; }

    [JsonPropertyName("session_id")]
    public string? SessionId { get; init; }
}

/// <summary>
///     Rate-limit details. <see cref="Status"/> is the actionable signal — anything
///     other than <c>allowed</c> means the current request was throttled or denied.
/// </summary>
public record RateLimitInfo
{
    /// <summary>
    ///     One of: <c>allowed</c>, <c>warning</c>, <c>throttled</c>, <c>denied</c>
    ///     (exact set depends on CLI version; treat <c>allowed</c> as the only OK value).
    /// </summary>
    [JsonPropertyName("status")]
    public string? Status { get; init; }

    /// <summary>
    ///     Unix epoch seconds when the current rate-limit window resets.
    /// </summary>
    [JsonPropertyName("resetsAt")]
    public long? ResetsAt { get; init; }

    /// <summary>
    ///     The rate-limit window name (e.g. <c>five_hour</c>, <c>weekly</c>).
    /// </summary>
    [JsonPropertyName("rateLimitType")]
    public string? RateLimitType { get; init; }

    /// <summary>
    ///     Whether overage credits are available for the current account
    ///     (<c>allowed</c>, <c>rejected</c>, etc.).
    /// </summary>
    [JsonPropertyName("overageStatus")]
    public string? OverageStatus { get; init; }

    /// <summary>
    ///     Reason overage is disabled (e.g. <c>org_level_disabled</c>), present only when
    ///     <see cref="OverageStatus"/> is non-allowing.
    /// </summary>
    [JsonPropertyName("overageDisabledReason")]
    public string? OverageDisabledReason { get; init; }

    /// <summary>
    ///     True when the current request consumed overage credits rather than the
    ///     standard quota.
    /// </summary>
    [JsonPropertyName("isUsingOverage")]
    public bool? IsUsingOverage { get; init; }
}
