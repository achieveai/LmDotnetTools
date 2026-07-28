namespace AchieveAi.LmDotnetTools.LmAgentInfra.Lifecycle;

/// <summary>
/// How one delivery attempt ended, reduced to the only four answers the retry policy can act on.
/// <para>
/// The transport decides this, not the pipeline. Status codes, socket errors, and TLS failures are
/// facts about HTTP; keeping their interpretation inside the sender lets a non-HTTP sender — a
/// queue, an in-process test double — classify its own failure modes instead of inventing status
/// codes it does not have.
/// </para>
/// </summary>
public enum LifecycleDeliveryOutcome
{
    /// <summary>The subscriber accepted the delivery. Nothing further is attempted.</summary>
    Succeeded,

    /// <summary>
    /// The failure may not recur: a timeout, a network fault, or a status the endpoint uses to say
    /// "not now" (408, 429, 5xx). Retried until the attempt cap or the delivery deadline binds.
    /// </summary>
    Retryable,

    /// <summary>
    /// The failure will recur: the subscriber rejected the request itself (any other 4xx). Retrying
    /// a rejected request just multiplies it, so the delivery is abandoned after one attempt.
    /// </summary>
    Permanent,

    /// <summary>
    /// The endpoint is gone (HTTP 410) and is stating that it will not come back. This is the one
    /// outcome that acts on the subscription rather than the delivery: it quarantines the
    /// subscriber, because continuing to POST to an endpoint that has explicitly retired is
    /// indistinguishable from abuse from the far side.
    /// </summary>
    Gone,
}

/// <summary>
/// The result of a single delivery attempt.
/// <para>
/// <see cref="Reason"/> is a short, fixed token — not a message built from the response. ADR 0005
/// requires that diagnostics carry opaque identifiers only, so nothing here may be derived from a
/// response body, a header value, or a callback URL.
/// </para>
/// </summary>
public sealed record LifecycleDeliveryResult
{
    private LifecycleDeliveryResult(
        LifecycleDeliveryOutcome outcome,
        string reason,
        int? statusCode,
        TimeSpan? retryAfter
    )
    {
        Outcome = outcome;
        Reason = reason;
        StatusCode = statusCode;
        RetryAfter = retryAfter;
    }

    /// <summary>What the pipeline should do next.</summary>
    public LifecycleDeliveryOutcome Outcome { get; }

    /// <summary>
    /// A low-cardinality token for logs and metrics (for example <c>http_status</c>,
    /// <c>transport</c>, <c>attempt_timeout</c>). Never response text.
    /// </summary>
    public string Reason { get; }

    /// <summary>The HTTP status code when the attempt reached a responding endpoint.</summary>
    public int? StatusCode { get; }

    /// <summary>
    /// The subscriber's requested wait, when it sent one. A <em>hint</em>: the pipeline clamps it to
    /// <see cref="LifecycleDeliveryOptions.MaxRetryAfter"/> before honoring it.
    /// </summary>
    public TimeSpan? RetryAfter { get; }

    /// <summary>The subscriber accepted the delivery.</summary>
    /// <param name="statusCode">The accepting status code.</param>
    public static LifecycleDeliveryResult Succeeded(int statusCode) =>
        new(LifecycleDeliveryOutcome.Succeeded, "accepted", statusCode, null);

    /// <summary>The attempt failed in a way that may not recur.</summary>
    /// <param name="reason">A low-cardinality token. Never response text.</param>
    /// <param name="statusCode">The status code, when there was a response.</param>
    /// <param name="retryAfter">The subscriber's requested wait, when it sent one.</param>
    public static LifecycleDeliveryResult Retryable(
        string reason,
        int? statusCode = null,
        TimeSpan? retryAfter = null
    ) => new(LifecycleDeliveryOutcome.Retryable, reason, statusCode, retryAfter);

    /// <summary>The subscriber rejected the request; retrying would only repeat it.</summary>
    /// <param name="reason">A low-cardinality token. Never response text.</param>
    /// <param name="statusCode">The rejecting status code, when there was a response.</param>
    public static LifecycleDeliveryResult Permanent(string reason, int? statusCode = null) =>
        new(LifecycleDeliveryOutcome.Permanent, reason, statusCode, null);

    /// <summary>The endpoint has retired itself and the subscription should be quarantined.</summary>
    /// <param name="statusCode">The status code that said so.</param>
    public static LifecycleDeliveryResult Gone(int statusCode) =>
        new(LifecycleDeliveryOutcome.Gone, "endpoint_gone", statusCode, null);
}

/// <summary>
/// Sends one already-serialized delivery to one subscriber.
/// <para>
/// The body arrives as bytes rather than as an object, and the delivery id arrives alongside it,
/// because a retry must re-send the <em>same</em> bytes under the <em>same</em> id: the receiver's
/// replay cache is keyed on that id, and re-serializing or re-identifying a retry would make an
/// ordinary network hiccup look like a second event.
/// </para>
/// </summary>
public interface ILifecycleDeliverySender
{
    /// <summary>
    /// Performs a single attempt. Implementations classify their own failures and return rather than
    /// throw, except for <see cref="OperationCanceledException"/>, which the pipeline needs in order
    /// to tell an attempt timeout apart from a shutdown.
    /// </summary>
    /// <param name="subscription">The destination and the key its deliveries are signed with.</param>
    /// <param name="deliveryId">
    /// The delivery's identity, minted once and reused across every attempt.
    /// </param>
    /// <param name="body">The exact bytes to send, unchanged between attempts.</param>
    /// <param name="cancellationToken">Cancels this attempt.</param>
    /// <returns>How the attempt ended.</returns>
    Task<LifecycleDeliveryResult> SendAsync(
        LifecycleSubscription subscription,
        string deliveryId,
        ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken
    );
}
