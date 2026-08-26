using System.Security.Claims;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;

namespace AchieveAi.LmDotnetTools.LmAgentInfra.Lifecycle;

/// <summary>
/// What a caller sends to register a subscription.
/// <para>
/// A wire type distinct from <see cref="LifecycleSubscriptionRequest"/> rather than binding that one
/// directly: the domain type carries no <c>[JsonPropertyName]</c>, so binding it would silently make
/// the wire contract camelCase while every other lifecycle payload is snake_case. Keeping the two
/// apart also keeps a <c>required</c> property off the binding path, where <c>required</c> is not
/// enforced and reads as a guarantee that is not there.
/// </para>
/// <para>
/// Note again what is <b>absent</b>: no owner, no app id, no signing secret. The owner comes from the
/// authenticated principal and the secret is minted by the host, so neither is expressible here.
/// </para>
/// </summary>
public sealed record LifecycleSubscriptionRegistration
{
    /// <summary>Where deliveries are POSTed. Validated against the host's egress rules.</summary>
    [JsonPropertyName("callback_uri")]
    public string? CallbackUri { get; init; }

    /// <summary>Capabilities being asked for. Absent means none, and none is a valid subscription.</summary>
    [JsonPropertyName("capabilities")]
    public IReadOnlyList<string>? Capabilities { get; init; }

    /// <summary>Event types to receive, or absent for every type within the owner's own scope.</summary>
    [JsonPropertyName("event_types")]
    public IReadOnlyList<string>? EventTypes { get; init; }
}

/// <summary>
/// What the subscription endpoints return: the subscription, or the reason it was refused.
/// <para>
/// <see cref="SigningSecret"/> is populated on exactly two responses — the registration that minted
/// it and the rotation that replaced it — and is unreadable afterwards by any route, because a
/// control plane that can re-read a key turns one leaked subscription id into a leaked key.
/// </para>
/// </summary>
public sealed record LifecycleSubscriptionResponse
{
    /// <summary>The server-minted id later operations address this subscription by.</summary>
    [JsonPropertyName("subscription_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SubscriptionId { get; init; }

    /// <summary>The callback that was accepted, echoed so a caller can confirm what was stored.</summary>
    [JsonPropertyName("callback_uri")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CallbackUri { get; init; }

    /// <summary>The capabilities actually granted, which may be fewer than were asked for.</summary>
    [JsonPropertyName("capabilities")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Capabilities { get; init; }

    /// <summary>The event-type filter in force, empty meaning every type within the owner's scope.</summary>
    [JsonPropertyName("event_types")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? EventTypes { get; init; }

    /// <summary>When the subscription was registered.</summary>
    [JsonPropertyName("created_at")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? CreatedAt { get; init; }

    /// <summary>
    /// The plaintext signing secret. SECRET — returned once by registration and once by each
    /// rotation, and never again by anything.
    /// </summary>
    [JsonPropertyName("signing_secret")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SigningSecret { get; init; }

    /// <summary>Why the operation was refused.</summary>
    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Error { get; init; }
}

/// <summary>
/// The register / rotate-secret / revoke control plane for lifecycle subscriptions (ADR 0005).
/// </summary>
/// <remarks>
/// <para>
/// <b>This controller does not authenticate.</b> Exactly as with
/// <see cref="LifecycleApprovalController"/>, it reads the caller's identity from
/// <see cref="HttpContext.User"/> — established by whatever authentication the host wired in front of
/// it — and resolves an owner from that alone. No route, header, or body field names an owner, so a
/// caller cannot register into, rotate, or revoke another tenant's subscriptions. A host that wires no
/// authentication scheme gets an endpoint that refuses everything, which is the safe direction — and,
/// as the known gap recorded on <see cref="LifecycleApprovalController"/> notes, a heavier burden on
/// the integrator than it should be. Note that registration is excluded from that gap on principle:
/// it is the operation that <em>mints</em> the subscription secret, so a caller cannot present that
/// secret as proof of identity beforehand. Host authentication stays mandatory here no matter what
/// the existing-subscription routes (rotate, revoke, unregister) may later gain.
/// </para>
/// <para>
/// <b>There is no listing route, and no route that reads a subscription back.</b> ADR 0005 leaves the
/// control plane write-only on purpose: "show me my subscriptions" is one owner-resolution bug away
/// from "show me everyone's", and nothing in the design needs it. A caller keeps the id and the secret
/// it was handed, or it re-registers.
/// </para>
/// <para>
/// <b>Refusals collapse deliberately.</b> An unknown subscription and another owner's subscription
/// produce the same 404 with the same body, because the registry itself refuses to tell them apart —
/// anything finer would make the control plane an oracle for which ids are real.
/// </para>
/// <para>
/// The remote-approval store is optional because the two features are independently switchable: a
/// host may run delivery with approval off, in which case there is no store registered and nothing
/// pending to invalidate. Where both are on, revoking a subscription has to reach the approvals it
/// was an approver for, which is the one place these features touch.
/// </para>
/// <para>
/// The delivery pipeline is optional for the mirror-image reason: it is the runtime half of the
/// feature this controller configures, and a host may compose the registry against a pipeline of its
/// own. When it is present, revoking reaches into it so the revocation covers what is already in
/// motion as well as what would have been sent next.
/// </para>
/// </remarks>
[ApiController]
[Route("api/lifecycle/subscriptions")]
public sealed class LifecycleSubscriptionsController(
    ILifecycleSubscriptionRegistry subscriptions,
    ILifecycleOwnerResolver ownerResolver,
    LifecycleDeliveryOptions options,
    ILogger<LifecycleSubscriptionsController> logger,
    RemoteApprovalStore? approvals = null,
    LifecycleDeliveryPipeline? deliveries = null
) : ControllerBase
{
    /// <summary>The one body every unanswerable case returns, so none of them can be told apart.</summary>
    private static readonly LifecycleSubscriptionResponse NotFoundBody =
        new() { Error = "unknown subscription" };

    /// <summary>Registers a subscription and mints its signing secret.</summary>
    /// <param name="registration">The requested callback, capabilities, and event types.</param>
    /// <param name="cancellationToken">Cancels owner resolution.</param>
    /// <returns>
    /// 201 with the subscription and its secret; 400 when the callback, a capability, or an event
    /// type is refused; 403 when the caller is unauthenticated or resolves to no owner; 404 when
    /// delivery is switched off; 503 when the host is already at its subscription capacity.
    /// </returns>
    [HttpPost]
    public Task<IActionResult> Register(
        [FromBody] LifecycleSubscriptionRegistration registration,
        CancellationToken cancellationToken = default
    ) =>
        AuthorizedAsync(
            async (owner, appId) =>
            {
                if (registration is null)
                {
                    return BadRequest(
                        new LifecycleSubscriptionResponse { Error = "malformed registration" }
                    );
                }

                // Parsed permissively and handed on even when it fails: a string that is not a URL at
                // all arrives at the registry as null, which its egress policy already answers with
                // "must be an absolute URL". Rejecting it here instead would mean maintaining a second
                // copy of that message, and two copies are how the two answers drift apart.
                _ = Uri.TryCreate(registration.CallbackUri, UriKind.RelativeOrAbsolute, out var callback);

                var request = new LifecycleSubscriptionRequest
                {
                    // Null despite `required`, because the registry's egress policy is the single place
                    // that decides what a missing callback means.
                    CallbackUri = callback!,
                    Capabilities = registration.Capabilities ?? [],
                    EventTypes = registration.EventTypes ?? [],
                };

                var grant = subscriptions.Register(owner, appId, request);

                // 201 without a Location header: there is no route that reads a subscription back, so
                // pointing at one would advertise an endpoint that deliberately does not exist.
                return await Task.FromResult<IActionResult>(
                    StatusCode(StatusCodes.Status201Created, Describe(grant))
                );
            },
            cancellationToken
        );

    /// <summary>Mints a new signing secret, leaving the outgoing one valid for the rotation overlap.</summary>
    /// <param name="subscriptionId">The subscription to rotate.</param>
    /// <param name="cancellationToken">Cancels owner resolution.</param>
    /// <returns>200 with the new secret; 403 as above; 404 when the subscription is unknown or not the caller's.</returns>
    [HttpPost("{subscriptionId}/secret")]
    public Task<IActionResult> RotateSecret(
        string subscriptionId,
        CancellationToken cancellationToken = default
    ) =>
        AuthorizedAsync(
            (owner, _) =>
                Task.FromResult<IActionResult>(
                    Ok(Describe(subscriptions.Rotate(owner, subscriptionId)))
                ),
            cancellationToken
        );

    /// <summary>
    /// Ends the rotation overlap immediately, dropping the previous key. This is the compromise
    /// response, which is why it is separate from rotation rather than folded into it: rotating and
    /// revoking in one step would break every delivery already signed with the outgoing key.
    /// </summary>
    /// <param name="subscriptionId">The subscription whose previous key is dropped.</param>
    /// <param name="cancellationToken">Cancels owner resolution.</param>
    /// <returns>204; 403 as above; 404 when the subscription is unknown or not the caller's.</returns>
    [HttpDelete("{subscriptionId}/secret/previous")]
    public Task<IActionResult> RevokePreviousSecret(
        string subscriptionId,
        CancellationToken cancellationToken = default
    ) =>
        AuthorizedAsync(
            (owner, _) =>
            {
                subscriptions.RevokePreviousKey(owner, subscriptionId);
                return Task.FromResult<IActionResult>(NoContent());
            },
            cancellationToken
        );

    /// <summary>Removes a subscription, abandoning anything already queued for it.</summary>
    /// <param name="subscriptionId">The subscription to remove.</param>
    /// <param name="cancellationToken">Cancels owner resolution.</param>
    /// <returns>204; 403 as above; 404 when the subscription is unknown or not the caller's.</returns>
    /// <remarks>
    /// Removing an approver also denies whatever it was still being asked about. Approval is
    /// unanimous, so a revoked approver's pending requests can no longer be allowed by anyone — the
    /// outcome is already settled and only the timing is open. Denying them here makes the wait honest
    /// and frees the admission slots, instead of leaving calls blocked until they time out.
    /// </remarks>
    [HttpDelete("{subscriptionId}")]
    public Task<IActionResult> Unregister(
        string subscriptionId,
        CancellationToken cancellationToken = default
    ) =>
        AuthorizedAsync(
            (owner, _) =>
            {
                // Unregister first: a decision that arrives between the two calls is refused by the
                // controller's own approver lookup, whereas invalidating first would leave a window in
                // which the subscription is still live and could be asked about a fresh request.
                subscriptions.Unregister(owner, subscriptionId);

                // Then the two places the removed subscription still has state in motion. Delivery
                // before approvals, because a pending approval request is itself a queued delivery:
                // stopping the pipeline first means the deny below cannot race a copy of the request
                // still on its way to the approver that is being taken away.
                deliveries?.Abandon(owner, subscriptionId);
                approvals?.InvalidateForSubscription(owner, subscriptionId);
                return Task.FromResult<IActionResult>(NoContent());
            },
            cancellationToken
        );

    /// <summary>
    /// Runs <paramref name="operation"/> against the caller's server-resolved owner, or refuses.
    /// <para>
    /// Every action shares this rather than repeating the sequence, because the sequence <em>is</em>
    /// the authorization model — feature gate, authenticated principal, resolved owner — and an action
    /// added later that carried its own copy could omit a step without the omission being visible at
    /// the call site.
    /// </para>
    /// </summary>
    private async Task<IActionResult> AuthorizedAsync(
        Func<LifecycleOwnerKey, string, Task<IActionResult>> operation,
        CancellationToken cancellationToken
    )
    {
        // A disabled host answers as one with nothing to find rather than announcing a feature it is
        // not running. The host is expected to keep this controller out of its application parts
        // entirely when delivery is off (see AddLifecycleControlPlane); this is the second line.
        if (!options.Enabled)
        {
            return NotFound(NotFoundBody);
        }

        var appId = AuthenticatedAppId();
        if (appId is null)
        {
            // The overwhelmingly likely cause is a host that enabled delivery without wiring an
            // authentication scheme, and a bare 403 does not say so. Only whether a principal exists
            // at all is logged — no headers, no claim values.
            logger.LogWarning(
                "Refusing a lifecycle subscription operation: no authenticated caller (principal present: "
                    + "{PrincipalPresent}). Lifecycle delivery is enabled, so the host must wire an authentication "
                    + "scheme that populates HttpContext.User; webhook signature verification alone does not "
                    + "establish a principal.",
                User?.Identity is not null
            );
            return Denied("caller is not authenticated");
        }

        var owner = await ownerResolver
            .ResolveCallerAsync(appId, cancellationToken)
            .ConfigureAwait(false);
        if (owner is null)
        {
            logger.LogWarning(
                "Refusing a lifecycle subscription operation: app {AppId} resolves to no owner.",
                appId
            );
            return Denied("caller has no resolvable owner");
        }

        try
        {
            return await operation(owner, appId).ConfigureAwait(false);
        }
        catch (LifecycleSubscriptionRejectedException rejected)
        {
            return Refused(rejected);
        }
    }

    /// <summary>
    /// Maps a rejection onto a status. The registry has already decided what a caller may learn; this
    /// only chooses the code, and passes the message through unchanged so the two cannot disagree.
    /// </summary>
    private IActionResult Refused(LifecycleSubscriptionRejectedException rejected) =>
        rejected.Reason switch
        {
            // Identical to an unknown id, and identical to a disabled host: the registry refuses to
            // distinguish "not yours" from "not there", and a status code that distinguished them
            // would undo that.
            LifecycleSubscriptionRejection.NotAuthorized => NotFound(NotFoundBody),

            // Not the caller's fault and not permanent, so it is a 503 rather than a 4xx — a client
            // that reads this as "my request was wrong" would rewrite a request that was fine.
            LifecycleSubscriptionRejection.CapacityExceeded => StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new LifecycleSubscriptionResponse { Error = rejected.Message }
            ),

            // Everything else is something the caller sent: a callback the egress rules refuse, an
            // unknown capability or event type, or a wildcard.
            _ => BadRequest(new LifecycleSubscriptionResponse { Error = rejected.Message }),
        };

    /// <summary>
    /// Projects a grant onto the wire, secret included — this is the one moment the plaintext is
    /// visible, and both call sites are responses to the caller that caused it to be minted.
    /// </summary>
    private static LifecycleSubscriptionResponse Describe(LifecycleSubscriptionGrant grant) =>
        new()
        {
            SubscriptionId = grant.Subscription.SubscriptionId,
            CallbackUri = grant.Subscription.CallbackUri.ToString(),
            // Sorted because the underlying stores are hash sets, whose enumeration order varies
            // between runs. An unsorted list would give a caller a body that changes for no reason and
            // a test an intermittent failure for no cause.
            Capabilities = [.. grant.Subscription.Capabilities.Order(StringComparer.Ordinal)],
            EventTypes = [.. grant.Subscription.EventTypes.Order(StringComparer.Ordinal)],
            CreatedAt = grant.Subscription.CreatedAtUtc,
            SigningSecret = grant.Secret,
        };

    /// <summary>
    /// The authenticated app identity, or <c>null</c> when there is none. Taken from the authenticated
    /// principal only — a request header naming an app is not used, because this controller cannot
    /// tell whether anything upstream verified one.
    /// </summary>
    /// <remarks>
    /// <see cref="LifecycleAppIdentity.AppIdClaimType"/> is the EXCLUSIVE source, with no fallback to
    /// <c>ClaimTypes.NameIdentifier</c> or <c>Identity.Name</c>. Those are claims a signed-in human
    /// satisfies — a bearer handler maps the token's <c>sub</c> onto the name identifier — so reading
    /// them made any signed-in user an "app" here, able to register a callback and take a signing
    /// secret (#433). "Authenticated" is therefore no longer the operative rule at this endpoint;
    /// "carries the app-id claim" is.
    /// </remarks>
    private string? AuthenticatedAppId()
    {
        if (User?.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        var appId = User.FindFirstValue(LifecycleAppIdentity.AppIdClaimType);
        return string.IsNullOrWhiteSpace(appId) ? null : appId;
    }

    /// <summary>
    /// A 403 written directly rather than via <c>Forbid()</c>, which would delegate to an
    /// authentication scheme's challenge handler — machinery a host may not have registered, and which
    /// would turn an authorization refusal into a 500.
    /// </summary>
    private ObjectResult Denied(string error) =>
        StatusCode(
            StatusCodes.Status403Forbidden,
            new LifecycleSubscriptionResponse { Error = error }
        );
}
