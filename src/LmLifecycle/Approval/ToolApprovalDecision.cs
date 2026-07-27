using System.Text.Json.Serialization;
using AchieveAi.LmDotnetTools.LmLifecycle.Serialization;

namespace AchieveAi.LmDotnetTools.LmLifecycle.Approval;

/// <summary>
/// An approver's answer to a <see cref="ToolApprovalRequest"/>.
/// </summary>
/// <remarks>
/// <para>
/// A decision is accepted only when it names a pending request, echoes that request's frozen
/// argument hash, arrives before the request expires, comes from a subscriber holding
/// <see cref="LifecycleCapabilities.ToolApprovalDecide"/>, and carries an outcome an approver is
/// permitted to submit. Anything else is refused with a stable code and the handler does not run.
/// </para>
/// <para>
/// The first valid decision wins. An identical retry yields the same result, so a network retry is
/// safe; a later decision that contradicts the first does not overturn it.
/// </para>
/// </remarks>
public sealed record ToolApprovalDecision
{
    /// <summary>The request being answered.</summary>
    [JsonPropertyName("request_id")]
    [JsonRequired]
    public string RequestId { get; set; } = string.Empty;

    /// <summary>
    /// The answer. Only <see cref="ToolApprovalOutcomes.Allowed"/> and
    /// <see cref="ToolApprovalOutcomes.Denied"/> may be submitted by an approver; see
    /// <see cref="ToolApprovalOutcomes.IsApproverSubmittable"/>.
    /// </summary>
    /// <remarks>
    /// Evaluated with <see cref="ToolApprovalOutcomes.IsAllowed"/>, which admits exactly one
    /// literal value. An unrecognized outcome does not allow execution — approval is the single
    /// place in this contract where an unknown value fails closed rather than being preserved and
    /// forwarded.
    /// </remarks>
    [JsonPropertyName("decision")]
    [JsonRequired]
    public string Decision { get; set; } = string.Empty;

    /// <summary>
    /// The argument hash the approver decided about, echoed from the request.
    /// </summary>
    /// <remarks>
    /// A mismatch means the approver decided about different bytes than the ones that would run, so
    /// the decision is refused rather than applied.
    /// </remarks>
    [JsonPropertyName("arguments_hash")]
    [JsonRequired]
    public string ArgumentsHash { get; set; } = string.Empty;

    /// <summary>A short human-readable rationale, free of sensitive material.</summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    /// <summary>When the approver decided, in UTC.</summary>
    [JsonPropertyName("decided_at")]
    [JsonConverter(typeof(CanonicalTimestampConverter))]
    public DateTimeOffset? DecidedAt { get; set; }

    /// <summary>
    /// Indicates whether this decision is well formed enough to be applied to
    /// <paramref name="request"/>.
    /// </summary>
    /// <param name="request">The request this decision claims to answer.</param>
    /// <returns>
    /// <see langword="true"/> when the request ids match, the argument hashes match ordinally, and
    /// the outcome is one an approver may submit.
    /// </returns>
    /// <remarks>
    /// This is a shape check, not an authorization check. Capability, expiry, and first-decision-
    /// wins are enforced by the host, which owns the state this type cannot see.
    /// </remarks>
    public bool Matches(ToolApprovalRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return string.Equals(RequestId, request.RequestId, StringComparison.Ordinal)
            && string.Equals(ArgumentsHash, request.ArgumentsHash, StringComparison.Ordinal)
            && ToolApprovalOutcomes.IsApproverSubmittable(Decision);
    }
}
