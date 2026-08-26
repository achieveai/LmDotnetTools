using System.Text.Json.Serialization;
using AchieveAi.LmDotnetTools.LmLifecycle;
using AchieveAi.LmDotnetTools.LmLifecycle.Approval;
using Microsoft.AspNetCore.Mvc;

namespace AchieveAi.LmDotnetTools.LmAgentInfra.Lifecycle;

/// <summary>
/// What the decision endpoint returns. Carries an outcome or an error and nothing else — never the
/// arguments, the tool name, or any subscription detail, because a decision endpoint that echoes
/// what it was asked about becomes a way to read state rather than change it.
/// </summary>
public sealed record ToolApprovalDecisionResponse
{
    /// <summary>The request the outcome belongs to, echoed only on success.</summary>
    [JsonPropertyName("request_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RequestId { get; init; }

    /// <summary>The decision that stands — which, on a conflict, is the <em>first</em> one.</summary>
    [JsonPropertyName("outcome")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Outcome { get; init; }

    /// <summary>Why the submission was refused. Deliberately coarse; see the controller's remarks.</summary>
    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Error { get; init; }
}

/// <summary>
/// The endpoint an approver submits a <see cref="ToolApprovalDecision"/> to.
/// </summary>
/// <remarks>
/// <para>
/// <b>This controller does not authenticate.</b> It reads the caller's identity from
/// <see cref="HttpContext.User"/> — established by whatever authentication the host wired in front
/// of it — and hands that identity to <see cref="ILifecycleOwnerResolver.ResolveCallerAsync"/>. It
/// never reads an owner or an app id from the body: a payload field naming the owner would let any
/// authenticated caller decide any other tenant's approvals, and no amount of validating such a
/// field fixes that.
/// </para>
/// <para>
/// <b>The host must therefore wire an authentication scheme that populates
/// <see cref="HttpContext.User"/>, and this endpoint is unusable until it does.</b> Webhook
/// signature verification alone does <em>not</em> satisfy the requirement: a signature proves a body
/// arrived unmodified from someone holding the key, but the verification path establishes no
/// principal, so <see cref="HttpContext.User"/> stays unauthenticated and every decision is refused
/// with 403. That is the safe direction, and it is also a silent one — a host that enables
/// <see cref="RemoteApprovalOptions.Enabled"/> and wires only signature verification has an approval
/// endpoint that rejects entirely legitimate decisions and a 403 that does not say why. Missing
/// identity is logged at warning for exactly that reason: on an enabled approval endpoint it is a
/// misconfiguration rather than routine traffic.
/// </para>
/// <para>
/// <b>Known gap, accepted for now: an integrator who wants nothing more than "the holder of
/// subscription X may decide X's approvals" must stand up a whole separate authentication scheme to
/// say it.</b> The refusal above is correct — a controller cannot tell a verified header from a
/// merely typed one, so trusting one would be trusting every deployment to have wired verification —
/// but the burden it creates is larger than the problem it solves for the common case. There is a
/// latent capability here: a subscriber does hold a host-minted secret bound to one subscription and
/// one owner. It is only a capability. Today that secret is used in one direction — the host signs
/// outbound deliveries with it (<see cref="HttpLifecycleDeliverySender"/>), the subscriber verifies
/// them — and no subscriber-to-host signing convention exists, so nothing a caller sends to this
/// endpoint carries a signature for anyone to check.
/// </para>
/// <para>
/// Closing the gap therefore means defining that inbound protocol first, not merely adding an
/// <c>AuthenticationHandler</c>. Three prerequisites, none of them satisfied today, and all of them
/// changes to contracts this type does not own:
/// <list type="number">
/// <item>a subscriber-to-host signing convention, which does not exist;</item>
/// <item>a way to resolve a subscription id to its secret and owner <em>before</em> a principal
/// exists, which <see cref="ILifecycleSubscriptionRegistry"/> deliberately forbids — every lookup on
/// it is owner-scoped precisely so that a caller cannot reach another tenant's subscription by id;</item>
/// <item>a signed payload that names its own direction and route domain. The current HMAC binds only
/// <c>{timestamp}.{deliveryId}.{body}</c>, so reusing it unchanged for caller authentication would let
/// a captured outbound delivery signature be replayed as an inbound credential.</item>
/// </list>
/// That is a design decision spanning the registry and the wire format, so it belongs in a superseding
/// ADR rather than in this comment. Until it is made, the host-supplied scheme documented above is the
/// only supported path, and this endpoint stays fail-closed without it.
/// </para>
/// <para>
/// The capability check is repeated here rather than trusted from registration time, because
/// <see cref="LifecycleCapabilities.ToolApprovalDecide"/> can be revoked while an approval is
/// pending, and the moment that matters is the moment the decision lands. It is checked against the
/// <em>exact</em> subscription the decision names — not against "some capable subscription under
/// this owner" — so a second approval-capable subscriber cannot answer in place of the one the gate
/// actually asked.
/// </para>
/// <para>
/// <b>404 is a single answer, not several.</b> Unknown id, expired request, another owner's request,
/// a request the caller was not asked to approve, an id from a previous process, and a host with
/// remote approval switched off all return the same status and the same body, so the endpoint cannot
/// be used to discover which request ids exist. Only a caller who already holds a valid, matching
/// request id it was actually asked about learns anything at all.
/// </para>
/// </remarks>
[ApiController]
[Route("api/lifecycle/approvals")]
public sealed class LifecycleApprovalController(
    RemoteApprovalStore store,
    ILifecycleOwnerResolver ownerResolver,
    ILifecycleSubscriptionRegistry subscriptions,
    RemoteApprovalOptions options,
    ILogger<LifecycleApprovalController> logger
) : ControllerBase
{
    /// <summary>The one body returned for every not-found class, so the four are indistinguishable.</summary>
    private static readonly ToolApprovalDecisionResponse NotFoundBody =
        new() { Error = "unknown approval request" };

    /// <summary>Submits one approver's decision about one pending tool call.</summary>
    /// <param name="decision">The decision. Its <c>request_id</c> and <c>arguments_hash</c> must both
    /// match the pending request, and its <c>subscription_id</c> must name the caller's own
    /// subscription among the approvers frozen when the gate opened; the hash is what ties the answer
    /// to the exact arguments that will run.</param>
    /// <param name="cancellationToken">Cancels owner resolution.</param>
    /// <returns>
    /// 200 with the outcome that stands; 202 when an allow was recorded and the request is still
    /// waiting on another frozen approver; 400 for a malformed body; 403 when the caller is
    /// unauthenticated, has no resolvable owner, or does not own an approval-capable subscription;
    /// 404 when there is no answerable request — including a request the caller was not asked about,
    /// and a host where remote approval is switched off; 409 when the decision does not match the
    /// request, or contradicts one already recorded.
    /// </returns>
    [HttpPost("decisions")]
    public async Task<IActionResult> Decide(
        [FromBody] ToolApprovalDecision decision,
        CancellationToken cancellationToken = default
    )
    {
        // Checked first, and answered as a not-found rather than as its own status. The host is
        // expected to keep this controller out of its application parts entirely when the feature is
        // off; if one is wired without the other, a disabled host must look like a host with nothing
        // pending rather than announce a feature it is not running.
        if (!options.Enabled)
        {
            return NotFound(NotFoundBody);
        }

        if (decision is null)
        {
            return BadRequest(new ToolApprovalDecisionResponse { Error = "malformed decision" });
        }

        // Diagnosable on purpose, and as TWO conditions rather than one: "nothing wired" and "wired,
        // but this caller is not an app" are different faults with different fixes. No headers, no
        // body, no claim values in either log.
        var resolved = AuthenticatedAppId();
        if (resolved.AppId is not { } appId)
        {
            if (resolved.Refusal == LifecycleAppIdentity.AppIdRefusal.Unauthenticated)
            {
                logger.LogWarning(
                    "Rejecting an approval decision: no authenticated caller (principal present: {PrincipalPresent}). "
                        + "Remote approval is enabled, so the host must wire an authentication scheme that populates "
                        + "HttpContext.User; webhook signature verification alone does not establish a principal.",
                    User?.Identity is not null
                );
                return Denied("caller is not authenticated");
            }

            // Authentication WORKED, so saying otherwise misdirects whoever reads this. The real causes
            // are a signed-in person reaching an app-only control plane, or a host that populates
            // HttpContext.User itself and never stamped the app-id claim.
            logger.LogWarning(
                "Rejecting an approval decision: the caller is authenticated but carries no {ClaimType} claim, "
                    + "so it does not name an application. A host that populates HttpContext.User itself must "
                    + "stamp this claim once it has established the caller is an app; a signed-in person is "
                    + "not one.",
                LifecycleAppIdentity.AppIdClaimType
            );
            return Denied("caller does not name an application");
        }

        var owner = await ownerResolver
            .ResolveCallerAsync(appId, cancellationToken)
            .ConfigureAwait(false);
        if (owner is null)
        {
            logger.LogWarning("Rejecting an approval decision: app {AppId} resolves to no owner.", appId);
            return Denied("caller has no resolvable owner");
        }

        // The decision names which of the frozen approvers is answering. That subscription must exist
        // under this owner, must still be the one the authenticated caller registered, and must still
        // hold the capability. Resolving the named subscription rather than asking whether the owner
        // has any capable one is the difference between "an approver answered" and "the approver that
        // was asked answered" — and the store then refuses it again if it was not among the frozen set.
        var approver = subscriptions
            .ForOwner(owner)
            .FirstOrDefault(s =>
                string.Equals(s.SubscriptionId, decision.SubscriptionId, StringComparison.Ordinal)
            );

        // Comparing the app id as well as the owner is deliberate: two apps can resolve to one owner,
        // and a subscription is an approver identity, not a tenancy-wide permission. A subscriber
        // whose app identity has changed re-registers, which is also how it would regain delivery.
        if (
            approver is null
            || !string.Equals(approver.OwnerAppId, appId, StringComparison.Ordinal)
            || !approver.HasCapability(LifecycleCapabilities.ToolApprovalDecide)
        )
        {
            logger.LogWarning(
                "Rejecting an approval decision: subscription {SubscriptionId} is not an approval-capable subscription "
                    + "registered by app {AppId} under owner {Owner}.",
                decision.SubscriptionId,
                appId,
                owner.Value
            );
            return Denied("caller may not decide tool approvals");
        }

        var settlement = store.Settle(owner, decision);
        return settlement.Status switch
        {
            RemoteApprovalSettleStatus.Accepted or RemoteApprovalSettleStatus.AlreadyDecided => Ok(
                new ToolApprovalDecisionResponse
                {
                    RequestId = decision.RequestId,
                    Outcome = settlement.Outcome,
                }
            ),

            // Counted, and the call is still blocked: every frozen approver has to allow. No outcome
            // is echoed because there is none yet, and reporting the caller's own answer back would
            // read like the request had been settled. StatusCode rather than Accepted() to name the
            // response type unambiguously.
            RemoteApprovalSettleStatus.Recorded => StatusCode(
                StatusCodes.Status202Accepted,
                new ToolApprovalDecisionResponse { RequestId = decision.RequestId }
            ),

            // The submitted decision described something other than the pending call — most often a
            // different arguments hash, which means it answered about arguments that will not run.
            RemoteApprovalSettleStatus.Mismatched => Conflict(
                new ToolApprovalDecisionResponse { Error = "decision does not match the request" }
            ),

            // The first decision stands and is returned, so the approver learns the real state
            // rather than being left to assume its own answer took effect.
            RemoteApprovalSettleStatus.Contradicted => Conflict(
                new ToolApprovalDecisionResponse
                {
                    RequestId = decision.RequestId,
                    Outcome = settlement.Outcome,
                    Error = "the request was already decided",
                }
            ),

            _ => NotFound(NotFoundBody),
        };
    }

    /// <summary>
    /// The authenticated app identity, or <c>null</c> when there is none.
    /// </summary>
    /// <remarks>
    /// Taken from the authenticated principal only. A request header such as the gateway's
    /// <c>X-Sbx-App-Id</c> is not used here: this controller cannot tell whether such a header was
    /// verified by anything upstream, and trusting an unverified one would let any caller name any
    /// owner — precisely what <see cref="ILifecycleOwnerResolver.ResolveCallerAsync"/> warns against.
    /// <para>
    /// <see cref="LifecycleAppIdentity.AppIdClaimType"/> is the EXCLUSIVE source, with no fallback to
    /// <c>ClaimTypes.NameIdentifier</c> or <c>Identity.Name</c>. A bearer handler maps a human token's
    /// <c>sub</c> onto the name identifier, so reading it let any signed-in user settle another app's
    /// approvals as if they owned them (#433). "Authenticated" is therefore no longer the operative
    /// rule at this endpoint; "carries the app-id claim" is.
    /// </para>
    /// </remarks>
    private LifecycleAppIdentity.AppIdResolution AuthenticatedAppId() =>
        LifecycleAppIdentity.ResolveAppId(User);

    /// <summary>
    /// A 403 written directly rather than via <c>Forbid()</c>, which would delegate to an
    /// authentication scheme's challenge handler — machinery a host may not have registered, and
    /// which would turn an authorization refusal into a 500.
    /// </summary>
    private ObjectResult Denied(string error) =>
        StatusCode(StatusCodes.Status403Forbidden, new ToolApprovalDecisionResponse { Error = error });
}
