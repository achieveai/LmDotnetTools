using System.Security.Claims;
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
/// The capability check is repeated here rather than trusted from registration time, because
/// <see cref="LifecycleCapabilities.ToolApprovalDecide"/> can be revoked while an approval is
/// pending, and the moment that matters is the moment the decision lands.
/// </para>
/// <para>
/// <b>404 is a single answer, not several.</b> Unknown id, expired request, another owner's request,
/// an id from a previous process, and a host with remote approval switched off all return the same
/// status and the same body, so the endpoint cannot be used to discover which request ids exist.
/// Only a caller who already holds a valid, matching request id learns anything at all.
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
    /// match the pending request; the hash is what ties the answer to the exact arguments that will
    /// run.</param>
    /// <param name="cancellationToken">Cancels owner resolution.</param>
    /// <returns>
    /// 200 with the outcome that stands; 400 for a malformed body; 403 when the caller is
    /// unauthenticated, has no resolvable owner, or may not decide approvals; 404 when there is no
    /// answerable request — including on a host where remote approval is switched off; 409 when the
    /// decision does not match the request, or contradicts one already recorded.
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

        var appId = AuthenticatedAppId();
        if (appId is null)
        {
            // Diagnosable on purpose: the overwhelmingly likely cause is a host that enabled remote
            // approval without wiring an authentication scheme, and the 403 alone does not say so.
            // No headers, no body, no claim values — whether a principal exists at all is the one
            // bit that distinguishes "nothing wired" from "wired but this caller is anonymous".
            logger.LogWarning(
                "Rejecting an approval decision: no authenticated caller (principal present: {PrincipalPresent}). "
                    + "Remote approval is enabled, so the host must wire an authentication scheme that populates "
                    + "HttpContext.User; webhook signature verification alone does not establish a principal.",
                User?.Identity is not null
            );
            return Denied("caller is not authenticated");
        }

        var owner = await ownerResolver
            .ResolveCallerAsync(appId, cancellationToken)
            .ConfigureAwait(false);
        if (owner is null)
        {
            logger.LogWarning("Rejecting an approval decision: app {AppId} resolves to no owner.", appId);
            return Denied("caller has no resolvable owner");
        }

        if (!subscriptions.ForOwner(owner).Any(s => s.HasCapability(LifecycleCapabilities.ToolApprovalDecide)))
        {
            logger.LogWarning(
                "Rejecting an approval decision: owner {Owner} holds no subscription with {Capability}.",
                owner.Value,
                LifecycleCapabilities.ToolApprovalDecide
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
    /// </remarks>
    private string? AuthenticatedAppId()
    {
        if (User?.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        var appId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.Identity.Name;
        return string.IsNullOrWhiteSpace(appId) ? null : appId;
    }

    /// <summary>
    /// A 403 written directly rather than via <c>Forbid()</c>, which would delegate to an
    /// authentication scheme's challenge handler — machinery a host may not have registered, and
    /// which would turn an authorization refusal into a 500.
    /// </summary>
    private ObjectResult Denied(string error) =>
        StatusCode(StatusCodes.Status403Forbidden, new ToolApprovalDecisionResponse { Error = error });
}
