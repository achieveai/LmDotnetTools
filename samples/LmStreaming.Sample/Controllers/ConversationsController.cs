using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Identity;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Utils;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Collaboration;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using AchieveAi.LmDotnetTools.LmMultiTurn.UsageAccounting;
using AchieveAi.LmDotnetTools.LmAgentInfra;
using AchieveAi.LmDotnetTools.LmAgentInfra.Agents;
using AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;
using LmStreaming.Sample.Identity;
using LmStreaming.Sample.Models;
using LmStreaming.Sample.Persistence;
using LmStreaming.Sample.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace LmStreaming.Sample.Controllers;

/// <summary>
/// Inbound S2S auth guard for <see cref="ConversationsController"/> (issue #153 M2). Implemented
/// directly as an attribute filter:
/// ASP.NET Core's default filter provider recognizes any controller/action attribute that implements
/// <see cref="IAsyncActionFilter"/> and runs it as-is — no <c>IFilterFactory</c>, no constructor DI,
/// no Program.cs registration required — so applying <c>[InboundS2SAuth]</c> on the controller is
/// entirely self-contained in this file.
/// <para>
/// The shared secret is read fresh on every request from <c>Auth:S2SInboundSecret</c> via
/// <see cref="HttpContext.RequestServices"/> — no caching, matching the "per-request constant-time
/// validation" design. Operators set it via the flat env var
/// <c>LMSTREAMING_S2S_INBOUND_SECRET</c>, which <c>Program.cs</c> bridges into
/// <c>Auth:S2SInboundSecret</c> at startup (the flat name does NOT bind to that section key through
/// the standard env-var provider on its own — only <c>Auth__S2SInboundSecret</c> would). When the
/// secret is unset/blank the guard is DISABLED: the keyless dev path, mirroring the sandbox gateway's
/// own <c>AUTH_ENFORCE=off</c> behavior. That state is logged as a single process-wide warning (not
/// per request) the first time it's observed.
/// </para>
/// <para>
/// SCOPE — the guard enforces only on <b>service-to-service requests</b>, identified by the presence
/// of the <see cref="HeaderName"/> (<c>X-S2S-Auth</c>) header or a caller-credential marker
/// (<see cref="SandboxCredential.AppIdHeader"/>, <c>X-Sbx-App-Id</c> — the header that triggers
/// per-caller credential passthrough). Those requests MUST present a matching <c>X-S2S-Auth</c>;
/// missing or mismatched → 401. A request carrying none of those markers is the interactive
/// same-origin browser path (the SPA calls these same <c>/api/conversations*</c> routes with plain
/// <c>fetch</c> and, correctly, no S2S secret) and is allowed through to run under the sample's own
/// gateway identity — so enabling the secret does NOT break the UI. This is deliberately a gate on
/// the credential-passthrough surface, not a blanket lock on the same-origin interactive API; see
/// <c>docs/deployment/AUTH_ENFORCE.md</c>. The comparison is constant-time
/// (<see cref="CryptographicOperations.FixedTimeEquals"/> over a SHA-256 digest of each side) — the
/// same shape as <c>AuthSharedSecret</c>, but deliberately NOT that instance: the S2S inbound secret
/// is a separate trust boundary from the gateway/webhook shared secret it guards.
/// Neither the configured secret nor the presented header value is ever logged or echoed in the response.
/// </para>
/// <para>
/// This doc used to cite a <c>decisions.md</c> file (items #1/#2, tag "BS3", and "the plan's Step
/// 10") for the design rationale above. No such file exists, and none ever did — it was a local
/// planning scratchpad kept during the #153 M2 session and never committed, so it is not
/// recoverable. The citations are dropped rather than left dangling; see #315.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class InboundS2SAuthAttribute : Attribute, IAsyncActionFilter, IOrderedFilter
{
    /// <summary>
    /// Configuration key the inbound shared secret is read from. Operators set the flat env var
    /// <c>LMSTREAMING_S2S_INBOUND_SECRET</c>, which <c>Program.cs</c> bridges into this key at startup
    /// (the flat env var does not bind here on its own — only <c>Auth__S2SInboundSecret</c> would,
    /// via the standard env-var provider's <c>__</c>→<c>:</c> section mapping).
    /// </summary>
    public const string SecretConfigKey = "Auth:S2SInboundSecret";

    /// <summary>Inbound header the caller must present the shared secret in.</summary>
    public const string HeaderName = "X-S2S-Auth";

    // 0 = not yet logged, 1 = logged. Process-wide (not per-request/per-instance): whether the
    // guard is disabled doesn't vary between requests, so this avoids log spam under load while
    // still surfacing the keyless dev path at least once.
    private static int s_disabledWarningLogged;

    /// <summary>
    /// Runs ahead of MVC's model-state validation filter so an unauthenticated S2S caller is refused.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="ApiControllerAttribute"/> installs that filter at <c>Order = -2000</c>, and an
    /// attribute filter that does not implement <see cref="IOrderedFilter"/> sits at <c>Order = 0</c>.
    /// This one did, so on every body-taking route it guards — <c>POST /api/conversations</c>,
    /// <c>POST</c>/<c>PUT /api/workspaces</c>, the file-browser writes, and now the chat-mode
    /// mutations — a caller presenting a forged S2S credential and a malformed body was answered
    /// <c>400</c> by validation and never reached the guard. A <c>400</c> is not a refusal: it
    /// confirms the route exists, discloses its request schema, all for a caller this filter exists
    /// to turn away.
    /// </para>
    /// <para>
    /// <c>-2100</c> is not a fresh number: it is exactly what
    /// <see cref="OperatorSecretAuthAttribute.Order"/> uses, for exactly this reason. It reads
    /// request headers and configuration only, never
    /// <see cref="ActionExecutingContext.ActionArguments"/>.
    /// </para>
    /// </remarks>
    public int Order => -2100;

    /// <inheritdoc />
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var httpContext = context.HttpContext;
        var configuration = httpContext.RequestServices.GetService<IConfiguration>();
        var secret = configuration?[SecretConfigKey];

        if (string.IsNullOrWhiteSpace(secret))
        {
            WarnGuardDisabledOnce(httpContext);
            await next().ConfigureAwait(false);
            return;
        }

        // Marker-gate: only S2S requests are subject to the secret. A same-origin browser request
        // (the SPA) carries neither the S2S header nor the caller-credential marker, so it passes
        // through unchanged — enabling the secret must not turn every existing UI operation into 401.
        if (!IsServiceToServiceRequest(httpContext.Request))
        {
            await next().ConfigureAwait(false);
            return;
        }

        var presented = httpContext.Request.Headers[HeaderName].ToString();
        if (!ConstantTimeEquals(secret, presented))
        {
            context.Result = new UnauthorizedObjectResult(new { error = "unauthorized", code = "s2s_auth_failed" });
            return;
        }

        await next().ConfigureAwait(false);
    }

    /// <summary>
    /// True when the request presents an S2S surface marker: the <see cref="HeaderName"/> secret
    /// header itself, or the <see cref="SandboxCredential.AppIdHeader"/> caller-credential header that
    /// asks the controller to forward a distinct identity to the gateway. Either marker means the
    /// caller is acting as a service (not the same-origin SPA), so the shared secret is required.
    /// </summary>
    public static bool IsServiceToServiceRequest(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return request.Headers.ContainsKey(HeaderName)
            || request.Headers.ContainsKey(SandboxCredential.AppIdHeader);
    }

    private static void WarnGuardDisabledOnce(HttpContext httpContext)
    {
        if (Interlocked.Exchange(ref s_disabledWarningLogged, 1) != 0)
        {
            return;
        }

        var logger = httpContext.RequestServices.GetService<ILogger<InboundS2SAuthAttribute>>();
        logger?.LogWarning(
            "{ConfigKey} is not configured; the S2S inbound-auth guard is DISABLED for headless "
                + "conversation endpoints (keyless dev path). Set {EnvVar} to enforce it.",
            SecretConfigKey,
            "LMSTREAMING_S2S_INBOUND_SECRET");
    }

    /// <summary>
    /// Constant-time comparison of <paramref name="presented"/> against <paramref name="expected"/>.
    /// Both sides are hashed to a fixed-width SHA-256 digest first, so the comparison neither throws
    /// on a length mismatch nor leaks the secret's length via an early-exit — mirrors
    /// <c>AuthSharedSecret</c>. Returns false when <paramref name="presented"/> is
    /// null/empty (the "missing header" case).
    /// </summary>
    /// <remarks>
    /// <c>internal</c> rather than private so the operator-secret guard on the tenant admin surface
    /// reuses this exact comparison. A second hand-written copy is how one of the two ends up
    /// comparing with <c>==</c> after a later edit.
    /// </remarks>
    internal static bool ConstantTimeEquals(string expected, string? presented)
    {
        if (string.IsNullOrEmpty(presented))
        {
            return false;
        }

        var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        var presentedHash = SHA256.HashData(Encoding.UTF8.GetBytes(presented));
        return CryptographicOperations.FixedTimeEquals(expectedHash, presentedHash);
    }
}

[ApiController]
[Route("api/conversations")]
[InboundS2SAuth]
public class ConversationsController(
    IConversationStore store,
    MultiTurnAgentPool agentPool,
    IChatModeStore modeStore,
    IWorkspaceStore workspaceStore,
    ProviderRegistry providerRegistry,
    ConversationStatusResolver statusResolver,
    TimeProvider timeProvider,
    WorkflowRunRegistry workflowRunRegistry,
    ConversationAuthorizer authorizer,
    ILogger<ConversationsController> logger,
    ILogger<AgentHierarchyService> hierarchyLogger,
    SubAgentScanCoverageCache scanCoverageCache,
    ConversationDescendantScanner descendantScanner) : ControllerBase
{
    /// <summary>
    /// Warning returned from a mode/provider switch that recreated the agent while a <c>Wait</c> was
    /// armed. The switch succeeds; the pending timer/park is discarded with the old trigger runtime.
    /// </summary>
    private const string ArmedWaitDiscardedWarning =
        "A pending Wait was armed on this conversation; it was discarded when the agent was recreated for the switch.";

    /// <summary>Reason code for a raw agent-owned read that must go through the transcript route.</summary>
    internal const string AgentOwnedThreadReadCode = "use_transcript_route";

    /// <summary>Reason code for an attempt to write into a thread an agent owns.</summary>
    internal const string AgentOwnedThreadWriteCode = "agent_owned_thread";

    /// <summary>
    /// The hierarchy/transcript reader shared with the in-agent <c>GetAgentTranscript</c> tool (#244).
    /// Composed from this controller's own dependencies rather than injected as itself — what matters is
    /// that HTTP and the tool resolve every access decision through the same code. It holds no state of
    /// its own, but <c>scanCoverageCache</c> IS a shared singleton: this controller instance (like
    /// <see cref="AgentHierarchyService"/> itself) is rebuilt fresh on every request, so the cache is the
    /// one thing that lets a repeated poll remember a persisted child roster this same code already
    /// scanned for on an earlier request instead of rescanning it every time.
    /// </summary>
    private readonly AgentHierarchyService _hierarchy =
        new(agentPool, workflowRunRegistry, store, hierarchyLogger, scanCoverageCache);

    /// <summary>
    /// Error surfaced when a mode/provider switch is HARD-blocked (issue #246) because the
    /// conversation has an unanswered <c>AskUserQuestion</c> parked. Unlike an armed <c>Wait</c>
    /// (warn-only — the switch proceeds and the client is merely told what was discarded), recreating
    /// the agent here would silently orphan a question the human hasn't answered yet, with no
    /// surviving deferred call for the client to resolve against. The switch must be rejected, not
    /// merely warned about.
    /// </summary>
    private const string PendingAskUserQuestionBlockedMessage =
        "Cannot switch mode/provider while an AskUserQuestion is awaiting the user's answer.";

    /// <summary>
    /// Reserves a new conversation thread and locks its workspace/provider/mode as metadata, without
    /// starting a live agent/sandbox session. Enables a headless caller to provision a conversation
    /// ahead of the first message, so the server (not the caller) mints the thread id.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Provision(
        [FromBody] ProvisionConversationRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var workspace = await workspaceStore.GetAsync(request.WorkspaceId, ct);
        if (workspace == null)
        {
            return NotFound(new { error = $"Workspace '{request.WorkspaceId}' not found." });
        }

        var mode = await modeStore.GetModeAsync(request.ModeId, ct);
        if (mode == null)
        {
            return NotFound(new { error = $"Mode '{request.ModeId}' not found." });
        }

        if (!providerRegistry.IsAvailable(request.ProviderId))
        {
            var reason = providerRegistry.IsKnown(request.ProviderId)
                ? $"Provider '{request.ProviderId}' is currently unavailable."
                : $"Provider '{request.ProviderId}' is not a known provider.";
            logger.LogWarning(
                "Provision rejected: provider {ProviderId} unavailable ({Reason})",
                request.ProviderId,
                reason);
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new
                {
                    error = "provider_unavailable",
                    code = "provider_unavailable",
                    providerId = request.ProviderId,
                    detail = reason,
                });
        }

        var now = DateTimeOffset.UtcNow;

        // The epoch-millisecond segment is load-bearing, not decoration: it is the ONLY record of
        // when a conversation was created. ThreadMetadata has no CreatedAt column, and LastUpdated
        // is bumped on every completed run, so ConversationListOptions.CreationTimestampOf reads
        // creation order back out of this id. Minting a bare "thread-{guid:N}" here - as this did
        // when server-side provisioning landed - makes that parse fail and silently degrades the
        // Created sort to LastUpdated for every conversation the app creates, which is the mutable
        // key Created exists to avoid.
        var threadId = $"thread-{now.ToUnixTimeMilliseconds()}-{Guid.NewGuid():N}";

        await store.UpdateMetadataAsync(
            threadId,
            existing =>
            {
                var propertiesBuilder = existing?.Properties?.ToBuilder()
                    ?? ImmutableDictionary.CreateBuilder<string, object>();

                propertiesBuilder[MultiTurnAgentPool.ProviderPropertyKey] = request.ProviderId;
                propertiesBuilder[MultiTurnAgentPool.WorkspacePropertyKey] = request.WorkspaceId;
                propertiesBuilder[MultiTurnAgentPool.ModePropertyKey] = request.ModeId;

                if (!string.IsNullOrWhiteSpace(request.SystemPromptAppendix))
                {
                    propertiesBuilder[SystemPromptAugmenter.AppendixPropertyKey] =
                        request.SystemPromptAppendix;
                }

                if (!string.IsNullOrWhiteSpace(request.SubAgentModelId))
                {
                    propertiesBuilder[ConversationSubAgentModel.PropertyKey] = request.SubAgentModelId;
                }

                if (!string.IsNullOrWhiteSpace(request.AuthWebhookUrl))
                {
                    propertiesBuilder["sample.authWebhookUrl"] = request.AuthWebhookUrl;
                    propertiesBuilder["sample.authWebhookProviderId"] = request.ProviderId;
                    propertiesBuilder["sample.authWebhookRegisteredAt"] = now.ToUnixTimeMilliseconds();
                }

                // Ownership is stamped HERE, at creation, not by a later repair (spec 8.3, and
                // this is what closes #162). The startup repair exists for rows written before
                // identity did; a row this build creates must never need it.
                return authorizer.StampOwnership(new ThreadMetadata
                {
                    ThreadId = threadId,
                    CurrentRunId = existing?.CurrentRunId,
                    LatestRunId = existing?.LatestRunId,
                    LastUpdated = now.ToUnixTimeMilliseconds(),
                    SessionMappings = existing?.SessionMappings,
                    Properties = propertiesBuilder.ToImmutable(),
                    TenantId = existing?.TenantId,
                    OwnerUserId = existing?.OwnerUserId,
                    OwnerAppId = existing?.OwnerAppId,
                    Visibility = existing?.Visibility,
                });
            },
            ct);

        return Ok(new ProvisionConversationResponse { ThreadId = threadId });
    }

    /// <summary>
    /// One page of the conversation sidebar.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Listing is a FILTER, not a loop (spec 7.5): BOTH narrowings are pushed into the store so the
    /// page is trimmed by the query. This comment used to say that about the scope alone while the
    /// very next statement filtered agent-owned threads out of the already-trimmed page, and that
    /// was the bug. <c>LastUpdated</c> is bumped on every completed run and background sub-agent and
    /// workflow runs are constant, so <c>subagent-*</c>/<c>workflow-*</c> rows crowd the front of a
    /// last-updated ordering: on a live deployment of 302 threads, 256 of them agent-owned, a
    /// top-50 page arrived holding 45 agent-owned rows and the sidebar rendered five real
    /// conversations. Everything older was simply gone, and nothing anywhere said a page had been
    /// trimmed.
    /// </para>
    /// <para>
    /// The exclusion travels as <see cref="ConversationListOptions"/> rather than being folded into
    /// the scope, because the two answer different questions - what this SURFACE is asking for
    /// versus what this CALLER is allowed to see. That type documents the separation.
    /// </para>
    /// <para>
    /// An unrecognised <paramref name="sort"/>, a negative <paramref name="offset"/> or an
    /// out-of-range <paramref name="limit"/> is a 400, never a silent fall back to the default: a
    /// silently ignored sort parameter is indistinguishable from a working one, so a client that
    /// misspells it would see a plausible list forever and never learn its ordering was never
    /// applied.
    /// </para>
    /// </remarks>
    /// <param name="limit">Page size, 1..100. Defaults to the client's page size.</param>
    /// <param name="offset">Rows to skip. Must not be negative.</param>
    /// <param name="sort"><c>lastUsed</c> (default) or <c>created</c>, case-insensitive.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet]
    public async Task<IActionResult> List(
        int limit = 30,
        int offset = 0,
        string? sort = null,
        CancellationToken ct = default)
    {
        if (limit is < MinListLimit or > MaxListLimit)
        {
            return BadRequest(new
            {
                error = $"limit must be between {MinListLimit} and {MaxListLimit}.",
                code = "invalid_limit",
            });
        }

        if (offset < 0)
        {
            return BadRequest(new { error = "offset must not be negative.", code = "invalid_offset" });
        }

        if (!TryParseSortOrder(sort, out var sortOrder))
        {
            return BadRequest(new
            {
                error = $"Unknown sort '{sort}'. Expected 'lastUsed' or 'created'.",
                code = "invalid_sort",
            });
        }

        // Sub-agent and workflow-controller conversations use the sample's reserved agent-owned
        // thread-id space and are surfaced only through the sub-agent panel (GET .../subagents +
        // /ws/subagent). They must not leak into the primary conversation sidebar (nor be
        // auto-selected on load) - and the STORE, not this method, is where that is decided, so the
        // page comes back full of rows the sidebar can actually show.
        var options = new ConversationListOptions
        {
            ExcludedThreadIdPrefixes = SubAgentSummary.AgentOwnedThreadIdPrefixes,
            SortOrder = sortOrder,
        };

        var scope = await authorizer.CreateListScopeAsync(ct);
        var threads = scope is null
            ? await store.ListThreadsAsync(limit, offset, options, ct)
            : await store.ListThreadsAsync(scope, limit, offset, options, ct);

        // NOTE: there is deliberately no .Where(!IsAgentOwnedThreadId) here. That post-filter is
        // what `options` above replaces, and reinstating it would not be redundant - it would be
        // the bug again. A page is a contract about COUNT: dropping rows after the store has
        // already applied limit/offset returns short pages, and because agent-owned rows crowd the
        // front of a last-updated ordering it returned a nearly EMPTY one. Exclude in the store,
        // where the whole candidate set is still in hand, or not at all.

        // Materialized here rather than projected lazily, as ListShares does at the end of its own
        // projection. ToWireVisibility throws on a visibility it has no name for, and a lazy
        // enumerable would defer that throw to response serialization - truncating a 200 mid-body
        // rather than failing as a clean 500. Nothing produces that today; this decides how it fails
        // if a fourth member is ever added without a wire name.
        var result = new List<ConversationSummary>(threads.Count);

        foreach (var t in threads)
        {
            // #482/#487. A LOOP, unlike the scope above, and unavoidably so: the listing filter
            // answers "may this viewer SEE the row", one question for the whole page, while canShare
            // answers "may this viewer SHARE this row", which the rights table decides per row from
            // the row's own owner and visibility (an owner may share a private conversation and not a
            // published one). The row loaded for the listing is the same ThreadMetadata the point
            // read would load, so this costs no extra store round trip.
            //
            // Through the authorizer rather than by comparing OwnerUserId here. Re-deriving it would
            // put a second, drifting copy of spec 7.4.1 in a controller, and the copy would be wrong
            // immediately: an owner may not re-share a tenant-published conversation, and a tenant
            // admin - who can see every row on this page - may not share any of them.
            //
            // Through the CAPABILITY seam (#487), not AuthorizeAsync: this is a display-time probe,
            // not an access attempt. So it writes NO attempt-grade audit record - a page load used to
            // emit one Security/Warning deny per row an admin could not share, noise an operator
            // could not tell from real refused attempts - and it consults the grant batch the scope
            // already resolved rather than re-querying grants once per row.
            var canShare = await authorizer.MayShareForListingAsync(t, scope?.GrantedThreadIds, ct);

            result.Add(new ConversationSummary
            {
                ThreadId = t.ThreadId,
                Title = t.Properties?.TryGetValue("title", out var titleObj) == true
                    ? titleObj?.ToString() ?? "New Conversation"
                    : "New Conversation",
                Preview = t.Properties?.TryGetValue("preview", out var previewObj) == true
                    ? previewObj?.ToString()
                    : null,
                LastUpdated = t.LastUpdated,
                Provider = t.Properties?.TryGetValue(MultiTurnAgentPool.ProviderPropertyKey, out var providerObj) == true
                    ? providerObj?.ToString()
                    : null,
                Workspace = t.Properties?.TryGetValue(MultiTurnAgentPool.WorkspacePropertyKey, out var workspaceObj) == true
                    ? workspaceObj?.ToString()
                    : null,
                Mode = t.Properties?.TryGetValue(MultiTurnAgentPool.ModePropertyKey, out var modeObj) == true
                    ? modeObj?.ToString()
                    : null,
                // Read straight off the row rather than out of Properties: visibility is a
                // first-class stamped field (spec 8.3), and it is what the share control reflects.
                Visibility = ConversationSummary.ToWireVisibility(t.Visibility),
                CanShare = canShare,
            });
        }

        return Ok(result);
    }

    /// <summary>Smallest page the sidebar may ask for. Zero would be a page that can never fill.</summary>
    private const int MinListLimit = 1;

    /// <summary>
    /// Largest page the sidebar may ask for. A bound, not a preference: without one a caller can
    /// make every listing read the whole store, and the file store has no offset index to soften it.
    /// </summary>
    private const int MaxListLimit = 100;

    /// <summary>
    /// Resolves the <c>sort</c> query parameter, rejecting anything it does not recognise.
    /// </summary>
    /// <remarks>
    /// Absent (or blank) means <see cref="ConversationSortOrder.LastUsed"/>, which is the ordering
    /// every caller got before the parameter existed. Anything else that is not one of the two known
    /// spellings returns false so the route can answer 400 - falling back to the default there would
    /// serve a correct-looking list in the wrong order, with no way for the client to tell.
    /// </remarks>
    /// <param name="sort">The raw query value.</param>
    /// <param name="sortOrder">
    /// The resolved order; <see cref="ConversationSortOrder.LastUsed"/> when this returns false.
    /// </param>
    private static bool TryParseSortOrder(string? sort, out ConversationSortOrder sortOrder)
    {
        sortOrder = ConversationSortOrder.LastUsed;

        if (string.IsNullOrWhiteSpace(sort))
        {
            return true;
        }

        if (string.Equals(sort, "lastUsed", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(sort, "created", StringComparison.OrdinalIgnoreCase))
        {
            sortOrder = ConversationSortOrder.Created;
            return true;
        }

        return false;
    }

    private static readonly JsonSerializerOptions NormalizeOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false,
        Converters = { new IMessageJsonConverter() },
    };

    /// <summary>
    /// Returns a conversation's persisted transcript for its own client.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An agent-owned thread (<c>subagent-*</c>/<c>workflow-*</c>) is some agent's transcript, and #244
    /// decides who may read one. So this raw route answers agent-owned threads ONLY for a caller that
    /// presents no agent or service identity at all — the conversation's own browser client, which is
    /// how the sub-agent tab loads its history and why that path is unchanged. A caller that names a
    /// <paramref name="viewer"/>, or that presents an S2S/caller-credential header, is a machine caller
    /// and is sent to <see cref="GetAgentTranscript"/>, which applies the transcript policy and excludes
    /// reasoning. Without that, an agent could simply read the raw route instead of the checked one and
    /// the policy would decide nothing.
    /// </para>
    /// <para>
    /// This is a boundary, not authentication: the sample's HTTP API is unauthenticated, so a caller
    /// that presents no identity is TAKEN to be the human's client. What the guard removes is the
    /// ability to hold an agent identity and still bypass the check that identity is subject to.
    /// Ordinary (root) conversations are untouched — they keep the legacy read, reasoning included.
    /// </para>
    /// </remarks>
    [HttpGet("{threadId}/messages")]
    public async Task<IActionResult> GetMessages(
        string threadId,
        string? viewer = null,
        CancellationToken ct = default)
    {
        if (RefuseMachineCaller(threadId, AgentOwnedThreadReadCode, viewer) is { } refusal)
        {
            return refusal;
        }

        if (await AuthorizeAsync(threadId, AccessAction.Read, ct) is { } denied)
        {
            return denied;
        }

        var messages = await store.LoadMessagesAsync(threadId, ct);

        // Normalize messageJson to ensure consistent discriminators
        // (e.g., legacy "server_tool_use" → "tool_call" with execution_target).
        var normalized = messages
            .Select(m =>
            {
                try
                {
                    var msg = JsonSerializer.Deserialize<IMessage>(m.MessageJson, NormalizeOptions);
                    if (msg == null)
                    {
                        return m;
                    }

                    // Fix legacy "{}{"query":"..."}" args from the content_block_start bug.
                    msg = FixLegacyDoubledArgs(msg);

                    var newJson = JsonSerializer.Serialize(msg, msg.GetType(), NormalizeOptions);
                    return m with { MessageJson = newJson };
                }
                catch
                {
                    return m;
                }
            })
            .ToList();

        return Ok(normalized);
    }

    /// <summary>
    /// Refuses a machine caller that addressed an agent-owned thread on a raw, unchecked route, or null
    /// when the request may proceed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One rule, applied by every raw route that can read an agent's words or change an agent's thread,
    /// so the surface cannot be widened one action at a time: a caller that names a
    /// <paramref name="viewer"/> or presents a service/caller-credential header is not the conversation's
    /// browser, and an agent-owned thread is some agent's — reachable only through
    /// <see cref="GetAgentTranscript"/>, which applies the #244 policy. Ordinary root conversations are
    /// never affected, and a caller presenting no identity keeps the exact legacy behaviour, so the
    /// interactive client is untouched.
    /// </para>
    /// <para>
    /// The body carries a content-free reason code and nothing else — a refusal must not disclose the
    /// name, task, or existence of what it refuses.
    /// </para>
    /// </remarks>
    /// <param name="threadId">The thread the request addressed.</param>
    /// <param name="code">
    /// <see cref="AgentOwnedThreadReadCode"/> when the route would disclose an agent's content,
    /// <see cref="AgentOwnedThreadWriteCode"/> when it would change an agent's thread.
    /// </param>
    /// <param name="viewer">The agent the caller reads as, when the route accepts one.</param>
    private ObjectResult? RefuseMachineCaller(string threadId, string code, string? viewer = null) =>
        SubAgentSummary.IsAgentOwnedThreadId(threadId) && IsMachineCaller(viewer)
            ? StatusCode(StatusCodes.Status403Forbidden, new { error = "forbidden", code })
            : null;

    /// <summary>
    /// Whether this request identifies its caller as something other than the conversation's own
    /// browser client: it names the agent it reads as, or it carries a service/caller-credential
    /// header. <c>HttpContext</c> is null when an action is invoked directly (a unit test constructing
    /// the controller without an MVC pipeline), which is treated as "no header" rather than dereferenced.
    /// </summary>
    private bool IsMachineCaller(string? viewer) =>
        viewer is not null
        || (HttpContext?.Request is { } request && InboundS2SAuthAttribute.IsServiceToServiceRequest(request));

    /// <summary>
    /// Returns the persisted conversation-wide token usage &amp; cost aggregate (#196): totals plus the
    /// per-model breakdown, including usage from sub-agents and workflow descendants. A client that
    /// re-opens a conversation reads this to show real usage that survives reload; headless clients use
    /// it to retrieve spend without a live stream. Returns 404 when no usage has been recorded yet.
    /// </summary>
    [HttpGet("{threadId}/usage")]
    public async Task<IActionResult> GetUsage(string threadId, CancellationToken ct = default)
    {
        if (await AuthorizeAsync(threadId, AccessAction.Read, ct) is { } denied)
        {
            return denied;
        }

        var usage = await ConversationUsageProjection.LoadAsync(store, threadId, ct);
        return usage is null ? NotFound() : Ok(usage);
    }

    /// <summary>
    /// Reports whether a conversation currently has an in-flight run. A client returning to a
    /// conversation (switch-back or refresh) calls this after loading persisted history; when
    /// <see cref="ConversationRunState.IsInProgress"/> is true it re-opens the WebSocket to resume
    /// the live stream (the pooled agent keeps running after the client disconnects). The signal is
    /// in-memory run state, not persisted metadata, so it reflects the actual live run.
    /// </summary>
    [HttpGet("{threadId}/run-state")]
    public async Task<IActionResult> GetRunState(string threadId, CancellationToken ct = default)
    {
        // Whether a conversation is streaming right now is a fact ABOUT that conversation, so it is
        // gated like any other read. Left open it is a liveness oracle: poll another tenant's ids
        // and learn which of them are real and busy.
        if (await AuthorizeAsync(threadId, AccessAction.Read, ct) is { } denied)
        {
            return denied;
        }

        var runState = agentPool.GetRunStateInfo(threadId);
        return Ok(new ConversationRunState
        {
            ThreadId = threadId,
            IsInProgress = runState.IsInProgress,
            CurrentRunId = runState.CurrentRunId,
        });
    }

    /// <summary>
    /// Read-only presentation listing of the sub-agents a conversation's parent agent has spawned.
    /// The Vue client polls this to render a conversation's children; it never spawns, sends to,
    /// stops, or otherwise mutates a sub-agent (WI #194).
    /// <para>
    /// The roster is the UNION of the live <c>SubAgentManager.ListAgents()</c> snapshot and the
    /// children reconstructed from persisted metadata (<see cref="SubAgentProvenance"/>), keyed by
    /// agent id with the live entry winning — it carries real lifecycle status and the manager is the
    /// authority while it exists. The persisted half is what makes a shared link to a FINISHED
    /// conversation work: the manager's parent→child mapping is in-memory only, so once the run ends
    /// and the parent leaves the agent pool (or the host restarts) the children's transcripts are
    /// still on disk but nothing else says whose children they are. It also covers a parent the pool
    /// re-created on demand, whose fresh manager has an empty registry.
    /// </para>
    /// Returns 404 only when the thread is unknown everywhere — not in the pool, no persisted
    /// metadata, and no persisted children.
    /// <para>
    /// <paramref name="recursive"/> switches to an entirely different, VERSIONED contract
    /// (<see cref="SubAgentTreeResponse"/>): the full persisted descendant graph reachable from
    /// <paramref name="threadId"/> (children, grandchildren, ...), not just direct children, and not
    /// unioned with live state (no nested live Agent delegation exists to union with — see
    /// <see cref="BuildDescendantTreeAsync"/>). The plain array shape above is unchanged when this
    /// is omitted/false, so existing callers of the flat endpoint are unaffected.
    /// </para>
    /// </summary>
    [HttpGet("{threadId}/subagents")]
    public async Task<IActionResult> ListSubAgents(
        string threadId,
        bool recursive = false,
        string? viewer = null,
        CancellationToken ct = default)
    {
        if (await AuthorizeAsync(threadId, AccessAction.Read, ct) is { } denied)
        {
            return denied;
        }

        if (recursive)
        {
            return await BuildDescendantTreeAsync(threadId, ct);
        }

        var (rows, isKnown, _) = await _hierarchy.BuildAsync(threadId, viewer, ct);
        return isKnown
            ? Ok(rows.ToArray())
            : UnknownThread(threadId);
    }

    /// <summary>
    /// Returns one agent's transcript to a reader that the collaboration's transcript policy allows to
    /// see it (#244). Reasoning is never included: an agent's private deliberation is the one part of a
    /// transcript that was addressed to nobody.
    /// </summary>
    /// <remarks>
    /// The decision is <see cref="AgentHierarchyService.ReadTranscriptAsync"/>'s — the same call the
    /// in-agent <c>GetAgentTranscript</c> tool makes — so the HTTP surface cannot be a way around the
    /// policy the tool enforces. A denial returns the content-free
    /// <see cref="TranscriptAccessReasons"/> code and nothing else: the response must not disclose
    /// whether the agent exists, what it is called, or what it is doing. The "no hierarchy at all"
    /// outcomes answer the shared <see cref="AgentTranscriptReasons"/> codes, so the route and the tool
    /// report one vocabulary.
    /// </remarks>
    [HttpGet("{threadId}/agents/{agentId}/transcript")]
    public async Task<IActionResult> GetAgentTranscript(
        string threadId,
        string agentId,
        string? viewer = null,
        CancellationToken ct = default)
    {
        // The root conversation is checked FIRST, before the #244 transcript policy: that policy
        // decides which agent inside a hierarchy may read a sibling's words, and answering it at all
        // presumes the caller is entitled to the hierarchy. Reversed, a caller from another tenant
        // would learn the shape of this one's agent tree from the refusal codes alone.
        if (await AuthorizeAsync(threadId, AccessAction.Read, ct) is { } denied)
        {
            return denied;
        }

        var result = await _hierarchy.ReadTranscriptAsync(threadId, agentId, viewer, ct);
        return result.Outcome switch
        {
            AgentTranscriptOutcome.UnknownThread => UnknownThread(threadId),
            AgentTranscriptOutcome.CollaborationUnavailable =>
                NotFound(new
                {
                    error = "Agent collaboration is not enabled.",
                    code = AgentTranscriptReasons.CollaborationUnavailable,
                }),
            AgentTranscriptOutcome.Denied =>
                StatusCode(StatusCodes.Status403Forbidden, new { error = "forbidden", code = result.DenialCode }),
            _ => Ok(result.Messages),
        };
    }

    /// <summary>
    /// True when <paramref name="threadId"/> is known either as a live pooled agent
    /// (<paramref name="agent"/>, already resolved by the caller via <c>agentPool.TryGet</c>) or as
    /// persisted metadata. Shared by the flat and recursive branches of <see cref="ListSubAgents"/>
    /// so both answer the same "does this conversation exist at all" question the same way.
    /// </summary>
    private async Task<bool> IsKnownThreadAsync(string threadId, IMultiTurnAgent? agent, CancellationToken ct) =>
        agent is not null || await store.LoadMetadataAsync(threadId, ct) is not null;

    /// <summary>
    /// Builds the versioned recursive descendant graph (schema v1) for <paramref name="rootThreadId"/>:
    /// one bounded store scan, one in-memory parent→children index built from it, then a visited-set
    /// BFS from the root — all of which now lives in <see cref="ConversationDescendantScanner"/>, which
    /// the transcript writer shares (issue #251). Deliberately persisted-only — no live
    /// <c>SubAgentManager</c> union — because no current spawn path creates a depth-&gt;1 tree anyway
    /// (nested live Agent delegation stays disabled); this reader exists to answer "what does the
    /// persisted graph say", which is exactly what a restarted host or a finished run still has. The
    /// root itself is never emitted as a node, only its descendants.
    /// </summary>
    /// <remarks>
    /// Calls the UNCACHED <see cref="ConversationDescendantScanner.ScanAsync"/>, not the cached
    /// <c>GetOrScanAsync</c>: this endpoint observes no agent activity of its own, so it has nothing to
    /// refresh a cached graph with, and serving a remembered answer here would silently change the
    /// route's contract from "what the store says now" to "what it said at some earlier poll".
    /// </remarks>
    private async Task<IActionResult> BuildDescendantTreeAsync(string rootThreadId, CancellationToken ct)
    {
        var ordered = await descendantScanner.ScanAsync(rootThreadId, ct);

        if (ordered.Count == 0)
        {
            agentPool.TryGet(rootThreadId, out var agent);
            if (!await IsKnownThreadAsync(rootThreadId, agent, ct))
            {
                return UnknownThread(rootThreadId);
            }
        }

        return Ok(new SubAgentTreeResponse(SchemaVersion: 1, Nodes: ordered));
    }

    [HttpGet("capabilities")]
    public IActionResult GetCapabilities() =>
        Ok(new ConversationCapabilitiesResponse
        {
            SchemaVersion = 1,
            MessageIdempotency = store is IInputAcceptanceStore,
            SpawnSuppression = true,
        });

    /// <summary>
    /// Queues a message onto a previously-provisioned thread. Non-blocking: returns as soon as the
    /// input is durably recorded as accepted, before it is necessarily drained into a run — callers
    /// poll <see cref="GetStatus"/> by the returned <c>inputId</c> to learn when/how it resolved.
    /// </summary>
    /// <remarks>
    /// Agent-owned threads (<c>subagent-*</c>/<c>workflow-*</c>) are refused outright. Nothing may speak
    /// into an agent's thread through this route: doing so would put words in another agent's transcript
    /// and, because the pool creates an agent for whatever thread id it is handed, would spin up a
    /// top-level agent bound to that transcript. The client never posts here either — a focused
    /// sub-agent's input goes over <c>/ws/subagent</c> to the manager that owns it.
    /// </remarks>
    [HttpPost("{threadId}/messages")]
    public async Task<IActionResult> SendMessage(
        string threadId,
        [FromBody] SendMessageRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (SubAgentSummary.IsAgentOwnedThreadId(threadId))
        {
            return StatusCode(
                StatusCodes.Status403Forbidden,
                new { error = "forbidden", code = AgentOwnedThreadWriteCode });
        }

        // A key that cannot be turned into a durable, unambiguous id is refused rather than read as
        // "absent": absent means "no protection asked for", but a caller that SENT a key is asking for a
        // guarantee, and treating its unusable key as absent would hand it an acknowledged-but-unprotected
        // send — the one outcome the acknowledgement exists to rule out.
        if (request.IdempotencyKey is not null && !IsUsableIdempotencyKey(request.IdempotencyKey))
        {
            return BadRequest(new
            {
                error = "invalid_idempotency_key",
                code = "invalid_idempotency_key",
                detail =
                    "IdempotencyKey, when supplied, must be a non-blank identifier of at most "
                    + $"{MaxIdempotencyKeyLength} characters and must not contain control characters.",
                threadId,
            });
        }

        var metadata = await store.LoadMetadataAsync(threadId, ct);

        // Authorize BEFORE the null-metadata 404: the authorizer runs its equalising grant lookup for a
        // never-minted thread exactly as for a forbidden cross-tenant one, so short-circuiting the missing
        // case here would make a missing thread cost zero look-ups and a forbidden one cost one - a
        // work-shape existence oracle (#389) even with byte-identical 404 bodies.
        if (Refuse(threadId, await authorizer.AuthorizeAsync(threadId, metadata, AccessAction.Write, ct))
            is { } denied)
        {
            return denied;
        }

        // Reachable only with enforcement OFF (an allowed decision over null metadata): still unknown.
        if (metadata == null)
        {
            return UnknownThread(threadId);
        }

        var persistedModeId =
            metadata.Properties?.TryGetValue(MultiTurnAgentPool.ModePropertyKey, out var modeObj) == true
                ? modeObj?.ToString()
                : null;
        var mode =
            await modeStore.GetModeAsync(persistedModeId ?? SystemChatModes.DefaultModeId, ct)
            ?? await modeStore.GetModeAsync(SystemChatModes.DefaultModeId, ct);
        if (mode == null)
        {
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                new { error = "Could not resolve the conversation's mode.", threadId });
        }

        // HttpContext is null when an action is invoked directly (outside the MVC pipeline, e.g. a
        // unit test constructing the controller without wiring ControllerContext) — treat that the
        // same as "no caller credential" rather than dereferencing a null Request.
        var callerCredential = TryBuildCallerCredential(HttpContext?.Request?.Headers);

        IMultiTurnAgent agent;
        try
        {
            // AFTER the authorization above, never before - see the helper's remarks. Inside the try
            // because it can now refuse a cross-app handoff, and that refusal is the same
            // caller_credential_conflict the pool raises a few lines below.
            await ReleaseAgentBoundToAnotherUserAsync(threadId, "SendMessage", callerCredential);

            _ = agentPool.GetOrCreateAgent(
                threadId,
                mode,
                requestedProviderId: null,
                requestResponseDumpFileName: null,
                callerCredential: callerCredential,
                ownerUserId: CallerUserId);

            // A pooled agent can outlive the sandbox session it was bound to — a workspace
            // plugin-selection change replaces the session underneath it. GetOrCreateAgent returns
            // whatever is pooled, session-liveness unexamined, so dispatching straight off it would
            // send this turn into a destroyed session. The WebSocket setup path solves this by
            // DISCARDING the GetOrCreateAgent result and taking the agent off the refresh instead
            // (ChatWebSocketManager, connection setup); REST/S2S is the same one-shot situation and
            // gets the same treatment, so both entry points agree on what "current" means.
            //
            // callerCredential must be threaded through even though the WebSocket call omits it:
            // that path never passes a credential to GetOrCreateAgent, so its entries hold none and
            // the default null matches. Here the entry is created WITH this caller's credential, so
            // omitting it would compare a non-null app id against null and raise a bogus
            // SandboxCredentialConflictException against the very caller that owns the thread.
            var refresh = await agentPool.EnsureCurrentAgentAsync(
                threadId,
                callerCredential,
                ct,
                ownerUserId: CallerUserId);
            if (refresh.Status == MultiTurnAgentPool.AgentRefreshStatus.RefreshDeferred)
            {
                // RefreshDeferred means the pooled entry has an ACTIVE run, so the refresh could not
                // swap it: EnsureCurrentAgentAsync hands back that same old agent, still bound to the
                // superseded session. Dispatching on it would queue this turn into a session the
                // migration's retirement grace is about to destroy. The WebSocket path can tell an
                // already-connected client to stand by (it emits this same
                // "sandbox_session_refresh_deferred" name); REST is one-shot, so it answers with the
                // 503 its sibling transient failures above use. Retrying after the active run ends
                // takes the normal refresh path.
                logger.LogWarning(
                    "SendMessage for thread {ThreadId} deferred: sandbox session refresh is blocked by an active run",
                    threadId);
                return StatusCode(
                    StatusCodes.Status503ServiceUnavailable,
                    new
                    {
                        error = "sandbox_session_refresh_deferred",
                        code = "sandbox_session_refresh_deferred",
                        detail = "The conversation's sandbox session is being replaced and its current run must finish first. Retry shortly.",
                        threadId
                    });
            }

            agent = refresh.Agent;
        }
        catch (ProviderUnavailableException ex)
        {
            logger.LogWarning(ex, "SendMessage for thread {ThreadId} failed: provider {ProviderId} unavailable", threadId, ex.ProviderId);
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new { error = "provider_unavailable", code = "provider_unavailable", providerId = ex.ProviderId, detail = ex.Message, threadId });
        }
        catch (SandboxSessionUnavailableException ex)
        {
            logger.LogWarning(ex, "SendMessage for thread {ThreadId} failed: sandbox unavailable (gateway status {StatusCode})", threadId, ex.StatusCode);
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new { error = "sandbox_unavailable", code = "sandbox_unavailable", detail = ex.Message, threadId });
        }
        catch (SandboxCredentialConflictException ex)
        {
            return CallerCredentialConflict(threadId, "SendMessage", ex);
        }
        catch (PrincipalConflictException ex)
        {
            return PrincipalConflict(threadId, "SendMessage", ex);
        }

        var userMessage = new TextMessage { Role = Role.User, Text = request.Text };

        // Per-turn spawn suppression is a GUARANTEE, so it fails closed BEFORE anything is queued: the agent
        // must both declare ISpawnSuppressingAgent and report that it enforces the flag. Declaring the
        // interface alone only proves the input can carry the flag — an implementation can satisfy the
        // signature and ignore it, and by the time an unconfirmed receipt said so the message would already
        // be in the run's channel with nothing suppressed.
        if (request.SuppressSubAgentSpawning && agent is not ISpawnSuppressingAgent { EnforcesSpawnSuppression: true })
        {
            logger.LogWarning(
                "SendMessage for thread {ThreadId} rejected: agent {AgentType} cannot enforce per-turn sub-agent spawn suppression",
                threadId,
                agent.GetType().Name);
            return BadRequest(new
            {
                error = "spawn_suppression_unsupported",
                code = "spawn_suppression_unsupported",
                detail =
                    "This conversation's agent cannot suppress sub-agent spawning for a single turn, so the "
                    + "requested guarantee cannot be made.",
                threadId,
            });
        }

        // An idempotent send is identified by the caller's key TOGETHER WITH the options that change what the
        // turn does, and the resulting id is ADMITTED durably before anything is queued — so a repeat can be
        // answered from the record of what this host actually granted. That is the recovery a caller needs
        // when it never saw the first response (socket reset, or a process that died between acceptance and
        // the answer): it gets the input the host already took, instead of queueing a second minutes-long,
        // sub-agent-fanning turn onto the same conversation.
        var acceptances = store as IInputAcceptanceStore;
        if (request.IdempotencyKey is not null && acceptances is null)
        {
            // Refused BEFORE the enqueue, because the alternative is queueing the turn and then telling the
            // caller its key was not honored — by which point the duplicate this endpoint exists to prevent
            // has already been created. Without an admission store there is nowhere to record the grant, so
            // the guarantee cannot be made at all.
            logger.LogWarning(
                "SendMessage for thread {ThreadId} rejected: store {StoreType} cannot durably admit an "
                    + "input id, so an idempotency key cannot be honored",
                threadId,
                store.GetType().Name);
            return BadRequest(new
            {
                error = "idempotency_unsupported",
                code = "idempotency_unsupported",
                detail =
                    "This host cannot durably record accepted inputs, so it cannot promise that a repeated "
                    + "IdempotencyKey will not queue a second turn.",
                threadId,
            });
        }

        var idempotent = request.IdempotencyKey is not null;

        // The admission describes what this send is asking to be granted. Every response below — fresh,
        // reconciled, or downgraded — is a projection of one of these, so the answer a caller gets first and
        // the answer it gets on a retry are shaped by the same fact and cannot drift apart.
        var admission = new InputAcceptance(
            threadId,
            idempotent
                ? DeriveIdempotentInputId(request.IdempotencyKey!, request.SuppressSubAgentSpawning)
                : ServerMintedInputIdPrefix + Guid.NewGuid().ToString("N"),
            timeProvider.GetUtcNow(),
            InputAcceptanceState.Pending,
            SpawningSuppressed: request.SuppressSubAgentSpawning,
            IdempotencyHonored: idempotent,
            ReservationId: Guid.NewGuid());

        if (idempotent
            && await TryReconcileAdmissionAsync(acceptances!, admission, ct) is { } reconciled)
        {
            return reconciled;
        }

        // A null return means the input channel is full — TrySendAsync guarantees no accepted-input
        // record survives in that case. A thrown exception (durable-store write failure) is left to
        // propagate to a 500, per the REST contract (no inputId returned either way). Either way the
        // admission taken above has to go back, or the id stays claimed by work that never ran and every
        // later retry of it reconciles to a turn that does not exist.
        // No pool ledger call here. The agent records its own accept from the place the receipt id is
        // minted (#434), and since #442 the pool refuses to pool an agent that cannot - so a second,
        // synchronous record on this one transport would be a duplicate of a fact the agent already
        // reports, kept in a list of call sites that has to stay complete. That list is what #434
        // removed the need for. The rollbacks below are still needed for the ADMISSION, which is this
        // controller's own and which nothing else withdraws.
        SendReceipt? receipt;
        try
        {
            receipt = agent is ISpawnSuppressingAgent suppressing
                ? await suppressing.TrySendAsync(
                    new UserInput(
                        [userMessage],
                        admission.InputId,
                        ParentRunId: null,
                        SuppressSubAgentSpawning: request.SuppressSubAgentSpawning),
                    ct)
                : await agent.TrySendAsync([userMessage], inputId: admission.InputId, parentRunId: null, ct);
        }
        catch (InputAcceptanceRefusedException ex)
        {
            // The conversation's agent was replaced (a mode or provider switch, or a handoff) while
            // this send was reporting its accept, so the agent resolved above is no longer the one the
            // pool holds. Nothing was queued and nothing recorded. Answered as a retryable 503 rather
            // than the 500 a bare throw would produce: the caller's request was well-formed, the
            // deployment is healthy, and repeating it resolves a fresh agent and succeeds.
            logger.LogWarning(
                ex,
                "SendMessage for thread {ThreadId} raced an agent replacement; nothing was queued",
                threadId);
            if (idempotent)
            {
                await ReleaseAdmissionAsync(acceptances!, admission);
            }

            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new { error = "agent_replaced", code = "agent_replaced", threadId });
        }
        catch
        {
            // The send threw, so nothing is queued. The agent's own rescind withdraws its accept from
            // the pool's ledger; what this has to undo is the admission.
            if (idempotent)
            {
                await ReleaseAdmissionAsync(acceptances!, admission);
            }

            throw;
        }

        if (receipt == null)
        {
            // Queue full: the input was refused. TrySendAsync has already rescinded the accept it
            // reported, so the only thing left to undo here is the admission.
            logger.LogWarning("SendMessage for thread {ThreadId} rejected: input queue full", threadId);
            if (idempotent)
            {
                await ReleaseAdmissionAsync(acceptances!, admission);
            }

            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new { error = "queue_full", code = "queue_full", threadId });
        }

        // The capability check got the request this far; the RECEIPT is what says this particular input will
        // actually be enforced. An agent that claims the capability but does not stamp the receipt cannot
        // make this host advertise a guarantee — and the negative is RECORDED, not just returned, so a retry
        // that arrives after the turn has been drained still reads "not suppressed" instead of being told by
        // a rebuilt-from-the-request answer that the guarantee held.
        var guaranteeKept = !request.SuppressSubAgentSpawning || receipt.SpawningSuppressed;
        if (!guaranteeKept)
        {
            logger.LogWarning(
                "SendMessage for thread {ThreadId}: agent {AgentType} accepted the input but did not confirm "
                    + "sub-agent spawn suppression; the response will not claim the guarantee",
                threadId,
                agent.GetType().Name);
        }

        var granted = admission with
        {
            State = guaranteeKept ? InputAcceptanceState.Enforced : InputAcceptanceState.Unenforced,
            SpawningSuppressed = receipt.SpawningSuppressed,
        };

        if (idempotent && !await acceptances!.TryRecordOutcomeAsync(granted, ct))
        {
            logger.LogError(
                "Could not record the outcome of input {InputId} on thread {ThreadId}; the turn is queued but "
                    + "a repeat of this idempotency key may not reconcile to it",
                granted.InputId,
                threadId);
        }

        return AcceptedAdmission(granted, queued: true);
    }

    /// <summary>
    /// Takes the admission for this send, and returns the response to send INSTEAD of queueing when it turns
    /// out the input was already taken.
    /// <para>
    /// Two distinct "already taken" cases, in the order they can be decided. The reservation settles the
    /// normal one entirely in the store, with no read-then-write window: N simultaneous sends of one key all
    /// attempt it and exactly one is told it won, while every loser is handed the winner's RECORD — which
    /// outlives the drain, so this stays exact long after the turn has started or finished.
    /// </para>
    /// <para>
    /// Winning still isn't proof the input is new: an input admitted by an EARLIER build of this host (or one
    /// whose record was released after its turn was already queued) has no record to lose against. So the
    /// winner confirms against the thread's live accepted/run state before enqueueing, and if the id is
    /// already in flight there it gives the admission back rather than starting a second turn. That answer
    /// claims no suppression: there is no record to back a guarantee with, and inventing one from the request
    /// is exactly the re-derivation this endpoint must not do.
    /// </para>
    /// <para>
    /// Losing is not proof either — only <see cref="InputAcceptanceState.Enforced"/> and
    /// <see cref="InputAcceptanceState.Unenforced"/> are OUTCOMES. A record left
    /// <see cref="InputAcceptanceState.Pending"/> is an undertaking still in progress, and it is exactly what
    /// a host that died between admitting an input and queueing it leaves behind. Answering a retry from one
    /// of those returns "accepted, not queued" for a turn that does not exist and never will — and since the
    /// id is derived from the caller's key, the SAME answer comes back for every later retry, wedging that
    /// key for the whole of the caller's deadline. So a Pending record is resolved rather than reported:
    /// against the thread's live state first, then, only once the input is provably not live and the record
    /// is too old to belong to a send still on its way to the queue, by re-taking it.
    /// </para>
    /// </summary>
    /// <returns>The reconciled response, or null when this caller may enqueue.</returns>
    private async Task<IActionResult?> TryReconcileAdmissionAsync(
        IInputAcceptanceStore acceptances,
        InputAcceptance admission,
        CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            if (await acceptances.TryReserveAcceptanceAsync(admission, ct) is not { } granted)
            {
                return await ConfirmAdmissionAgainstLiveStateAsync(acceptances, admission, ct);
            }

            if (granted.State is not InputAcceptanceState.Pending)
            {
                logger.LogInformation(
                    "SendMessage for thread {ThreadId} reconciled to already-admitted input {InputId} "
                        + "({State}); nothing was queued",
                    admission.ThreadId,
                    admission.InputId,
                    granted.State);

                return AcceptedAdmission(granted, queued: false);
            }

            // Live work beats an unsettled record every time: the admitting host got as far as queueing, it
            // just never got to write the outcome. Re-taking the id here would start the turn a second time.
            if (await statusResolver.ResolveByInputIdAsync(admission.ThreadId, admission.InputId, ct) is not null)
            {
                logger.LogInformation(
                    "SendMessage for thread {ThreadId} reconciled to in-flight input {InputId} whose "
                        + "admission is still unsettled; nothing was queued",
                    admission.ThreadId,
                    admission.InputId);

                return AcceptedAdmission(granted, queued: false);
            }

            // Every healthy send looks abandoned for the instant between taking its admission and queueing
            // its input. Only a record that has outlived that handoff may be re-taken.
            if (timeProvider.GetUtcNow() - granted.AcceptedAt < UnsettledAdmissionGrace)
            {
                logger.LogInformation(
                    "SendMessage for thread {ThreadId} reconciled to just-admitted input {InputId} that has "
                        + "not reached the queue yet; nothing was queued",
                    admission.ThreadId,
                    admission.InputId);

                return AcceptedAdmission(granted, queued: false);
            }

            // Retract it under the ABANDONED reservation's own token. That is a compare-and-set on the
            // record, so of several retries deciding this at once exactly one can succeed — the rest find
            // the id already gone or already re-taken and reconcile to whatever now holds it, which is how
            // recovery avoids becoming the duplicate it exists to prevent.
            if (attempt > UnsettledAdmissionRecoveryAttempts
                || !await acceptances.TryReleaseAcceptanceAsync(
                    admission.ThreadId,
                    admission.InputId,
                    granted.ReservationId,
                    ct))
            {
                logger.LogInformation(
                    "SendMessage for thread {ThreadId} left the unsettled admission for input {InputId} to "
                        + "the caller that is already recovering it; nothing was queued",
                    admission.ThreadId,
                    admission.InputId);

                return AcceptedAdmission(granted, queued: false);
            }

            logger.LogWarning(
                "Input {InputId} on thread {ThreadId} was admitted at {AcceptedAt} and never queued or "
                    + "settled, so this send re-took it rather than leaving the key wedged",
                admission.InputId,
                admission.ThreadId,
                granted.AcceptedAt);
        }
    }

    /// <summary>
    /// The check a caller that WON the reservation still owes: an input admitted by an earlier build of this
    /// host is in flight with no record to lose against, so the thread's live state is what settles it.
    /// </summary>
    /// <returns>The reconciled response, or null when this caller may enqueue.</returns>
    private async Task<IActionResult?> ConfirmAdmissionAgainstLiveStateAsync(
        IInputAcceptanceStore acceptances,
        InputAcceptance admission,
        CancellationToken ct)
    {
        if (await statusResolver.ResolveByInputIdAsync(admission.ThreadId, admission.InputId, ct) is null)
        {
            return null;
        }

        await ReleaseAdmissionAsync(acceptances, admission);
        logger.LogInformation(
            "SendMessage for thread {ThreadId} reconciled to in-flight input {InputId} that carries no "
                + "admission record; nothing was queued and no suppression is claimed",
            admission.ThreadId,
            admission.InputId);

        return AcceptedAdmission(
            admission with { State = InputAcceptanceState.Unenforced, SpawningSuppressed = false },
            queued: false);
    }

    /// <summary>
    /// How long an admission may sit unsettled before a retry may conclude its owner is never coming back.
    /// It only has to cover the gap between taking the admission and the input reaching the queue — an
    /// in-process channel write, plus whatever a loaded host adds — so it is generous against that and still
    /// far shorter than any caller's deadline, which is the point: a wedged key must recover on its own long
    /// before the work it names is given up on.
    /// </summary>
    private static readonly TimeSpan UnsettledAdmissionGrace = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How many times one send will re-take an abandoned admission before answering from whatever holds the
    /// id. Each pass costs another caller its own attempt, so this bounds a pathological ping-pong; failing
    /// to recover leaves the key as it was, which is a delay rather than a duplicate.
    /// </summary>
    private const int UnsettledAdmissionRecoveryAttempts = 3;

    /// <summary>
    /// The one place a <see cref="SendMessageResponse"/> is shaped, so a fresh send and a repeat of the same
    /// key can only ever answer with the same projection of the same record.
    /// </summary>
    private IActionResult AcceptedAdmission(InputAcceptance acceptance, bool queued) =>
        Accepted(new SendMessageResponse
        {
            InputId = acceptance.InputId,
            Queued = queued,

            // Only an ENFORCED record proves the guarantee: it is the state an agent's receipt put the
            // record into. Pending carries what a host undertook when it admitted the input, and relaying
            // that would confirm a guarantee out of the request that asked for it; Unenforced is a refusal
            // and already carries false.
            SpawningSuppressed =
                acceptance.State is InputAcceptanceState.Enforced && acceptance.SpawningSuppressed,
            IdempotencyKeyHonored = acceptance.IdempotencyHonored,
        });

    /// <summary>
    /// Gives back an admission whose send did not survive to become queued work. Best-effort and never
    /// cancellable: the request's token is typically already cancelled on this path, and an admission left
    /// behind is worse than the extra work — it would make every later retry of that key reconcile to a turn
    /// that never ran. The reservation token means this can only ever retract THIS request's admission.
    /// </summary>
    private async Task ReleaseAdmissionAsync(IInputAcceptanceStore acceptances, InputAcceptance admission)
    {
        try
        {
            if (!await acceptances.TryReleaseAcceptanceAsync(
                    admission.ThreadId,
                    admission.InputId,
                    admission.ReservationId,
                    CancellationToken.None))
            {
                logger.LogWarning(
                    "Admission for input {InputId} on thread {ThreadId} was not retracted because it is no "
                        + "longer this request's to retract",
                    admission.InputId,
                    admission.ThreadId);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Could not retract the admission for input {InputId} on thread {ThreadId}; a repeat of this "
                    + "idempotency key will reconcile to a turn that was never queued",
                admission.InputId,
                admission.ThreadId);
        }
    }

    /// <summary>Longest caller-supplied idempotency key this host will turn into a durable input id.</summary>
    private const int MaxIdempotencyKeyLength = 200;

    /// <summary>
    /// Namespace for an id this HOST minted because no key was supplied. Distinct from
    /// <see cref="IdempotentInputIdPrefix"/> so a server-minted id can never be produced by any caller key.
    /// </summary>
    private const string ServerMintedInputIdPrefix = "srv:";

    /// <summary>Namespace for an id derived from a caller's idempotency key.</summary>
    private const string IdempotentInputIdPrefix = "idem:";

    /// <summary>
    /// A key must be storable and unambiguous as part of an input id. Control characters are rejected
    /// because they survive JSON but not logs, ids and file/db round-trips intact; the length cap bounds
    /// what a caller can push into every accepted-input record and run row.
    /// </summary>
    private static bool IsUsableIdempotencyKey(string idempotencyKey) =>
        !string.IsNullOrWhiteSpace(idempotencyKey)
        && idempotencyKey.Length <= MaxIdempotencyKeyLength
        && !idempotencyKey.Any(char.IsControl);

    /// <summary>
    /// Derives the durable input id an idempotent send is recorded under. The options that change what the
    /// turn DOES are folded in, so a repeat carrying different options is a different operation instead of
    /// silently resolving to the earlier, differently-behaving input.
    /// <para>
    /// The mapping is injective by construction: both variable parts sit at FIXED positions — a one-character
    /// suppression flag immediately after the namespace, then the key as the entire remainder. A suffix
    /// instead of a prefix would not be, because a key may itself end in whatever marker was chosen, letting
    /// two different (key, flag) pairs derive the same id and dedupe against each other.
    /// </para>
    /// </summary>
    private static string DeriveIdempotentInputId(string idempotencyKey, bool suppressSubAgentSpawning) =>
        $"{IdempotentInputIdPrefix}{(suppressSubAgentSpawning ? '1' : '0')}:{idempotencyKey}";

    /// <summary>
    /// Polls a run's resolved status by exactly one of <paramref name="runId"/> or
    /// <paramref name="inputId"/>. See <see cref="ConversationStatusResolver"/> for the 5-state
    /// resolution and the tool-only-run final-response convention.
    /// </summary>
    /// <remarks>
    /// The response carries the run's final answer TEXT, so on an agent-owned thread this route can
    /// disclose exactly what <see cref="GetAgentTranscript"/> is there to gate. It is therefore closed
    /// to machine callers on those threads (see <see cref="RefuseMachineCaller"/>).
    /// </remarks>
    [HttpGet("{threadId}/status")]
    public async Task<IActionResult> GetStatus(
        string threadId,
        string? runId = null,
        string? inputId = null,
        CancellationToken ct = default)
    {
        if (RefuseMachineCaller(threadId, AgentOwnedThreadReadCode) is { } refusal)
        {
            return refusal;
        }

        if (string.IsNullOrEmpty(runId) == string.IsNullOrEmpty(inputId))
        {
            return BadRequest(new { error = "Exactly one of 'runId' or 'inputId' must be provided." });
        }

        var metadata = await store.LoadMetadataAsync(threadId, ct);

        // Authorize BEFORE the null-metadata 404: the authorizer runs its equalising grant lookup for a
        // never-minted thread exactly as for a forbidden cross-tenant one, so short-circuiting the missing
        // case here would make a missing thread cost zero look-ups and a forbidden one cost one - a
        // work-shape existence oracle (#389) even with byte-identical 404 bodies.
        if (Refuse(threadId, await authorizer.AuthorizeAsync(threadId, metadata, AccessAction.Read, ct))
            is { } denied)
        {
            return denied;
        }

        // Reachable only with enforcement OFF (an allowed decision over null metadata): still unknown.
        if (metadata == null)
        {
            return UnknownThread(threadId);
        }

        var result = runId != null
            ? await statusResolver.ResolveByRunIdAsync(threadId, runId, ct)
            : await statusResolver.ResolveByInputIdAsync(threadId, inputId!, ct);

        if (result == null)
        {
            var idKind = runId != null ? "runId" : "inputId";
            var idValue = runId ?? inputId;
            return NotFound(new { error = $"Unknown {idKind} '{idValue}' for thread '{threadId}'.", code = $"unknown_{idKind}" });
        }

        return Ok(new ConversationStatusResponse
        {
            ThreadId = result.ThreadId,
            RunId = result.RunId,
            Status = result.Status.ToString(),
            Response = result.Response,
        });
    }

    /// <summary>
    /// Renames/re-previews a conversation. Closed to machine callers on an agent-owned thread: a
    /// hierarchy row's title is the hierarchy's to state, not another agent's.
    /// </summary>
    [HttpPut("{threadId}/metadata")]
    public async Task<IActionResult> UpdateMetadata(
        string threadId,
        [FromBody] ConversationMetadataUpdate update,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(update);

        if (RefuseMachineCaller(threadId, AgentOwnedThreadWriteCode) is { } refusal)
        {
            return refusal;
        }

        if (await AuthorizeAsync(threadId, AccessAction.Write, ct) is { } denied)
        {
            return denied;
        }

        // Atomic read-modify-write: a title/preview edit races with the pool's binding persistence
        // (provider/workspace/mode written when the agent is created for the first message). Doing a
        // separate LoadMetadata + SaveMetadata here would drop whichever write lost the interleave —
        // exactly the lost-update that stripped the persisted provider. UpdateMetadataAsync serializes
        // the whole cycle so both survive.
        await store.UpdateMetadataAsync(
            threadId,
            existing =>
            {
                var propertiesBuilder = existing?.Properties?.ToBuilder()
                    ?? ImmutableDictionary.CreateBuilder<string, object>();

                if (update.Title != null)
                {
                    propertiesBuilder["title"] = update.Title;
                }

                if (update.Preview != null)
                {
                    propertiesBuilder["preview"] = update.Preview;
                }

                // Ownership is CARRIED, not recomputed. This projection rebuilds the whole row
                // from `existing`, so leaving the four owner columns off would silently unstamp the
                // conversation on every rename - and an unstamped row is one nobody can read.
                return new ThreadMetadata
                {
                    ThreadId = threadId,
                    CurrentRunId = existing?.CurrentRunId,
                    LatestRunId = existing?.LatestRunId,
                    LastUpdated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    SessionMappings = existing?.SessionMappings,
                    Properties = propertiesBuilder.ToImmutable(),
                    TenantId = existing?.TenantId,
                    OwnerUserId = existing?.OwnerUserId,
                    OwnerAppId = existing?.OwnerAppId,
                    Visibility = existing?.Visibility,
                };
            },
            ct);

        return Ok();
    }

    /// <summary>
    /// Deletes a conversation and evicts its agent. Closed to machine callers on an agent-owned thread:
    /// destroying a sibling's transcript — and its live agent with it — is the most damaging thing this
    /// controller can be asked to do by id alone.
    /// </summary>
    [HttpDelete("{threadId}")]
    public async Task<IActionResult> Delete(
        string threadId,
        CancellationToken ct = default)
    {
        if (RefuseMachineCaller(threadId, AgentOwnedThreadWriteCode) is { } refusal)
        {
            return refusal;
        }

        if (await AuthorizeAsync(threadId, AccessAction.Delete, ct) is { } denied)
        {
            return denied;
        }

        await agentPool.RemoveAgentAsync(threadId);
        await store.DeleteThreadAsync(threadId, ct);

        // Owner-keyed invalidation (see SubAgentScanCoverageCache's remarks) already covers every
        // mode/provider/restart reset automatically, but a deleted thread id CAN be reused by a caller
        // (ids are not guaranteed server-minted for every path), and a fresh conversation on that id
        // would start "cold" under the same shared NoLiveManager owner the deleted conversation's cold
        // entry was also recorded under — a coincidental owner match that would otherwise resurrect the
        // deleted conversation's stale recovered rows. Forget it explicitly so a reused id always rescans.
        scanCoverageCache.Forget(threadId);

        return NoContent();
    }

    [HttpPost("{threadId}/mode")]
    public async Task<IActionResult> SwitchMode(
        string threadId,
        [FromBody] SwitchModeRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (RefuseMachineCaller(threadId, AgentOwnedThreadWriteCode) is { } refusal)
        {
            return refusal;
        }

        if (await AuthorizeAsync(threadId, AccessAction.Write, ct) is { } denied)
        {
            return denied;
        }

        var mode = await modeStore.GetModeAsync(request.ModeId, ct);
        if (mode == null)
        {
            return NotFound(new { error = $"Mode '{request.ModeId}' not found." });
        }

        var runState = agentPool.GetRunStateInfo(threadId);
        if (runState.IsInProgress)
        {
            logger.LogWarning(
                "Blocked mode switch for thread {ThreadId} to mode {ModeId} because a run is in progress. CurrentRunId={CurrentRunId}, AgentIsRunning={AgentIsRunning}, RunTaskCompleted={RunTaskCompleted}, IsStale={IsStale}",
                threadId,
                request.ModeId,
                runState.CurrentRunId,
                runState.AgentIsRunning,
                runState.RunTaskCompleted,
                runState.IsStale);
            return Conflict(
                new
                {
                    error = "Cannot switch mode while response is streaming.",
                    code = "mode_switch_while_streaming",
                    threadId,
                });
        }

        // HARD block (issue #246): an unanswered AskUserQuestion must not be silently orphaned by a
        // recreate. Checked BEFORE the (warn-only, unchanged) armed-Wait capture below, mirroring how
        // the IsInProgress conflict above is checked first among the hard blocks.
        if (await agentPool.HasPendingAskUserQuestionAsync(threadId, ct))
        {
            logger.LogWarning(
                "Blocked mode switch for thread {ThreadId} to mode {ModeId} because an AskUserQuestion is awaiting an answer.",
                threadId,
                request.ModeId);
            return Conflict(
                new
                {
                    error = PendingAskUserQuestionBlockedMessage,
                    code = "mode_switch_blocked_by_pending_ask_user_question",
                    threadId,
                });
        }

        // A mode switch recreates the agent, which tears down its trigger runtime. If a Wait is armed
        // (the run is parked on a timer, not streaming — so it passed the IsInProgress guard above), the
        // switch is still allowed but the pending wait is discarded; capture that up front so the
        // response can warn the caller. Checked before recreate, since recreate drops the old agent.
        var hadArmedWait = await agentPool.HasArmedWaitAsync(threadId, ct);

        // Switching into a sandbox-backed mode (e.g. Workspace Agent) eagerly creates the sandbox
        // session. A gateway rejection or an unreachable gateway must answer a clean 503 — not crash
        // the request with an unhandled 500 (which, in Development, also leaks a stack-trace page).
        var callerCredential = TryBuildCallerCredential(HttpContext?.Request?.Headers);

        try
        {
            // AFTER the authorization above, never before - see the helper's remarks. Inside the try
            // so its cross-app refusal lands on the same caller_credential_conflict catch below.
            await ReleaseAgentBoundToAnotherUserAsync(threadId, "Mode switch", callerCredential);

            _ = await agentPool.RecreateAgentWithModeAsync(
                threadId,
                mode,
                callerCredential,
                CallerUserId);
        }
        catch (SandboxCredentialConflictException ex)
        {
            return CallerCredentialConflict(threadId, "Mode switch", ex);
        }
        catch (PrincipalConflictException ex)
        {
            return PrincipalConflict(threadId, "Mode switch", ex);
        }
        catch (SandboxSessionUnavailableException ex)
        {
            logger.LogWarning(
                ex,
                "Mode switch to {ModeId} for thread {ThreadId} failed: sandbox unavailable (gateway status {StatusCode})",
                request.ModeId,
                threadId,
                ex.StatusCode);
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new { error = "sandbox_unavailable", code = "sandbox_unavailable", detail = ex.Message, threadId });
        }
        catch (ProviderUnavailableException ex)
        {
            logger.LogWarning(
                ex,
                "Mode switch to {ModeId} for thread {ThreadId} failed: provider {ProviderId} unavailable",
                request.ModeId,
                threadId,
                ex.ProviderId);
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new { error = "provider_unavailable", code = "provider_unavailable", providerId = ex.ProviderId, detail = ex.Message, threadId });
        }

        return Ok(new SwitchModeResponse
        {
            ModeId = mode.Id,
            ModeName = mode.Name,
            Warning = hadArmedWait ? ArmedWaitDiscardedWarning : null,
        });
    }

    /// <summary>
    /// Switches a conversation's provider. Mirrors <see cref="SwitchMode"/>: the provider is mutable
    /// while the conversation is idle (its run has completed) and locked only while a run streams.
    /// The thread's current mode and persisted workspace are preserved. An unavailable/unknown target
    /// provider answers a clean 503 rather than evicting the working agent.
    /// </summary>
    [HttpPost("{threadId}/provider")]
    public async Task<IActionResult> SwitchProvider(
        string threadId,
        [FromBody] SwitchProviderRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (RefuseMachineCaller(threadId, AgentOwnedThreadWriteCode) is { } refusal)
        {
            return refusal;
        }

        if (await AuthorizeAsync(threadId, AccessAction.Write, ct) is { } denied)
        {
            return denied;
        }

        var runState = agentPool.GetRunStateInfo(threadId);
        if (runState.IsInProgress)
        {
            logger.LogWarning(
                "Blocked provider switch for thread {ThreadId} to provider {ProviderId} because a run is in progress. CurrentRunId={CurrentRunId}, AgentIsRunning={AgentIsRunning}, RunTaskCompleted={RunTaskCompleted}, IsStale={IsStale}",
                threadId,
                request.ProviderId,
                runState.CurrentRunId,
                runState.AgentIsRunning,
                runState.RunTaskCompleted,
                runState.IsStale);
            return Conflict(
                new
                {
                    error = "Cannot switch provider while response is streaming.",
                    code = "provider_switch_while_streaming",
                    threadId,
                });
        }

        // Preserve the thread's current mode across the provider swap. Prefer the live agent's mode;
        // fall back to the persisted mode id (then the system default) if the agent was evicted.
        var currentMode = agentPool.GetAgentMode(threadId);
        if (currentMode == null)
        {
            var metadata = await store.LoadMetadataAsync(threadId, ct);
            var persistedModeId =
                metadata?.Properties?.TryGetValue(MultiTurnAgentPool.ModePropertyKey, out var modeObj) == true
                    ? modeObj?.ToString()
                    : null;
            var chatMode =
                await modeStore.GetModeAsync(persistedModeId ?? SystemChatModes.DefaultModeId, ct)
                ?? await modeStore.GetModeAsync(SystemChatModes.DefaultModeId, ct);
            if (chatMode != null)
            {
                currentMode = chatMode; // implicit ChatMode -> AgentProfile (non-null)
            }
        }

        if (currentMode == null)
        {
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                new { error = "Could not resolve the conversation's current mode.", threadId });
        }

        // HARD block (issue #246): mirrors SwitchMode — an unanswered AskUserQuestion must not be
        // silently orphaned by a provider recreate. Checked before the warn-only armed-Wait capture.
        if (await agentPool.HasPendingAskUserQuestionAsync(threadId, ct))
        {
            logger.LogWarning(
                "Blocked provider switch for thread {ThreadId} to provider {ProviderId} because an AskUserQuestion is awaiting an answer.",
                threadId,
                request.ProviderId);
            return Conflict(
                new
                {
                    error = PendingAskUserQuestionBlockedMessage,
                    code = "provider_switch_blocked_by_pending_ask_user_question",
                    threadId,
                });
        }

        // See SwitchMode: a provider swap recreates the agent and discards any armed Wait. Capture it
        // before recreate so the response can warn the caller that a pending park-and-wake was dropped.
        var hadArmedWait = await agentPool.HasArmedWaitAsync(threadId, ct);

        // Switching to a sandbox-backed provider eagerly reprovisions; a gateway rejection or an
        // unavailable/unknown provider must answer a clean 503, not crash the request with a 500.
        var callerCredential = TryBuildCallerCredential(HttpContext?.Request?.Headers);

        try
        {
            // AFTER the authorization above, never before - see the helper's remarks. Inside the try
            // so its cross-app refusal lands on the same caller_credential_conflict catch below.
            await ReleaseAgentBoundToAnotherUserAsync(threadId, "Provider switch", callerCredential);

            _ = await agentPool.RecreateAgentWithProviderAsync(
                threadId,
                request.ProviderId,
                currentMode,
                callerCredential,
                CallerUserId);
        }
        catch (SandboxCredentialConflictException ex)
        {
            return CallerCredentialConflict(threadId, "Provider switch", ex);
        }
        catch (PrincipalConflictException ex)
        {
            return PrincipalConflict(threadId, "Provider switch", ex);
        }
        catch (ProviderUnavailableException ex)
        {
            logger.LogWarning(
                ex,
                "Provider switch to {ProviderId} for thread {ThreadId} failed: provider unavailable",
                request.ProviderId,
                threadId);
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new { error = "provider_unavailable", code = "provider_unavailable", providerId = ex.ProviderId, detail = ex.Message, threadId });
        }
        catch (SandboxSessionUnavailableException ex)
        {
            logger.LogWarning(
                ex,
                "Provider switch to {ProviderId} for thread {ThreadId} failed: sandbox unavailable (gateway status {StatusCode})",
                request.ProviderId,
                threadId,
                ex.StatusCode);
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new { error = "sandbox_unavailable", code = "sandbox_unavailable", detail = ex.Message, threadId });
        }

        return Ok(new SwitchProviderResponse
        {
            ProviderId = request.ProviderId,
            Warning = hadArmedWait ? ArmedWaitDiscardedWarning : null,
        });
    }


    /// <summary>
    /// The end user this request is attributed to, or null for an app-only or unauthenticated
    /// caller. Handed to <see cref="MultiTurnAgentPool"/> as the principal half of its freeze.
    /// </summary>
    private string? CallerUserId => authorizer.Current?.EffectiveUserId;

    /// <summary>
    /// Loads the conversation's row and decides one action on it, returning the refusal to send or
    /// null when the request may proceed.
    /// </summary>
    /// <remarks>
    /// The row is loaded only while enforcement is on. With it off the decision is
    /// <c>enforcement_disabled</c> for every input, so a read here would be work done purely to be
    /// discarded on the pre-rollout path every existing test runs under.
    /// </remarks>
    /// <param name="threadId">The conversation being addressed.</param>
    /// <param name="action">The action being attempted.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task<IActionResult?> AuthorizeAsync(
        string threadId,
        AccessAction action,
        CancellationToken ct)
    {
        if (!authorizer.IsEnforced)
        {
            return null;
        }

        var metadata = await store.LoadMetadataAsync(threadId, ct);
        return Refuse(threadId, await authorizer.AuthorizeAsync(threadId, metadata, action, ct));
    }

    /// <summary>
    /// The byte-identical 404 every route in this controller returns for a thread id it will not
    /// admit exists - a never-minted id, a refused cross-tenant read, an authorized-but-missing row,
    /// all of it. This is the existence-hiding convention (see <see cref="Refuse"/>'s remarks), not
    /// "row missing": the body, code, and phrasing must stay identical across every call site so none
    /// of them becomes distinguishable from the others and reopens the oracle the 404 exists to close.
    /// Do not vary the body per call site.
    /// </summary>
    /// <param name="threadId">The conversation id to report as not found.</param>
    private ObjectResult UnknownThread(string threadId) =>
        NotFound(UnknownThreadRefusal.Body(threadId));

    /// <summary>
    /// Turns one access decision into the response that carries it, or null when it allowed.
    /// </summary>
    /// <remarks>
    /// The 404 body is BYTE-IDENTICAL to the one a genuinely unknown thread produces, down to the
    /// <c>unknown_thread</c> code and the interpolated id. Any difference - a distinct code, a
    /// different phrasing, even a different field order - would turn the route back into the
    /// existence oracle the 404 is there to close.
    /// </remarks>
    /// <param name="threadId">The conversation being addressed.</param>
    /// <param name="result">The decision.</param>
    private IActionResult? Refuse(string threadId, ConversationAccessResult result)
    {
        if (result.Allowed)
        {
            return null;
        }

        if (result.HidesExistence)
        {
            logger.LogWarning(
                "Conversation {ThreadId} refused as unknown for the current principal: {Reason}",
                threadId,
                result.Reason);
            return UnknownThread(threadId);
        }

        if (string.Equals(result.Reason, ConversationAuthorizer.UnauthenticatedReason, StringComparison.Ordinal))
        {
            return StatusCode(
                StatusCodes.Status401Unauthorized,
                new { error = "unauthorized", code = result.Reason });
        }

        logger.LogWarning(
            "Conversation {ThreadId} refused: {Reason}",
            threadId,
            result.Reason);

        return StatusCode(
            StatusCodes.Status403Forbidden,
            new { error = "forbidden", code = result.Reason, threadId });
    }

    /// <summary>
    /// The app-id half of the pool's freeze (Cross-Actor Resume Matrix, #153), answered on every
    /// route that can hit it so the three of them cannot drift apart.
    /// </summary>
    /// <remarks>
    /// The body names no identity at all. <c>ex.Message</c> interpolates the app id the thread is
    /// frozen to, which the refused caller is not entitled to: an editor grantee reaching this
    /// through the UI would learn which service minted the conversation, and that is a fact about
    /// another actor, not about their own request. Both ids stay in the structured log line, and the
    /// WebSocket sibling suppresses the same field. The message itself is unchanged - it still
    /// carries app ids and never an app key - because logs read it and clients no longer do.
    /// </remarks>
    /// <param name="threadId">The conversation being addressed.</param>
    /// <param name="operation">Which route hit the conflict, for the log line.</param>
    /// <param name="ex">The conflict.</param>
    private ConflictObjectResult CallerCredentialConflict(
        string threadId,
        string operation,
        SandboxCredentialConflictException ex)
    {
        logger.LogWarning(
            "{Operation} for thread {ThreadId} rejected: caller credential conflict (existing app id {ExistingAppId}, requested app id {RequestedAppId})",
            operation,
            threadId,
            ex.ExistingAppId ?? "(none)",
            ex.RequestedAppId ?? "(none)");

        return Conflict(new
        {
            error = "caller_credential_conflict",
            code = "caller_credential_conflict",
            detail = "This conversation belongs to a different caller identity and cannot be continued here.",
            threadId,
        });
    }

    /// <summary>
    /// The principal half of the pool's freeze, answered exactly like its app-id sibling: a
    /// <c>409</c> naming what conflicted, never a <c>403</c>. The caller may well be authorized -
    /// what it cannot have is this conversation's LIVE agent, which is bound to another person.
    /// </summary>
    /// <param name="threadId">The conversation being addressed.</param>
    /// <param name="operation">Which route hit the conflict, for the log line.</param>
    /// <param name="ex">The conflict.</param>
    private ConflictObjectResult PrincipalConflict(
        string threadId,
        string operation,
        PrincipalConflictException ex)
    {
        logger.LogWarning(
            "{Operation} for thread {ThreadId} rejected: principal conflict (existing user {ExistingUserId}, requested user {RequestedUserId})",
            operation,
            threadId,
            ex.ExistingUserId ?? "(none)",
            ex.RequestedUserId ?? "(none)");

        return Conflict(new
        {
            error = "principal_conflict",
            code = "principal_conflict",

            // Word for word what the WebSocket sibling sends, and for the same reason: ex.Message
            // interpolates BOTH stable user ids, and this caller has not been authorized to learn who
            // else uses the conversation. The ids stay in the structured log line above, where the
            // operator reading them already has the tenant. Two transports answering one condition
            // must not disagree about that - suppressing on one and disclosing on the other only
            // tells an attacker which door to use.
            detail = "This conversation's agent is in use by a different user and cannot be continued here.",
            threadId,
        });
    }

    /// <summary>
    /// Releases a pooled agent frozen to a DIFFERENT user than this request's caller, so a caller the
    /// policy has already allowed gets an agent of their own instead of colliding with the owner's
    /// (#376).
    /// </summary>
    /// <remarks>
    /// <para>
    /// MUST be called only AFTER <see cref="AuthorizeAsync"/> has allowed the action. It is not a
    /// guard and decides nothing about access; it acts on the pool for a caller who is already
    /// entitled to write. Called before the decision it would be worse than the bug it fixes: any
    /// tenant member could evict a stranger's live agent - and its sandbox - by id alone, learning
    /// nothing from their own 404 while the owner pays for it.
    /// </para>
    /// <para>
    /// The sandbox answer, recorded in <c>docs/deployment/AUTH_ENFORCE.md</c>: a grantee DOES inherit
    /// the owner's sandbox, and this remark previously claimed the opposite. Releasing clears the pool
    /// entry only, which never destroys the gateway session behind it; the recreate resolves the same
    /// workspace id back out of persisted metadata, and the session cache is keyed
    /// <c>(workspaceId, appId)</c>, and an interactive UI caller presents no credential of their own,
    /// so the registry resolves <c>credential ?? _defaultCredential</c> and keys on the CONFIGURED
    /// DEFAULT app id - the same one for everyone signed in, and never null. Both users therefore key
    /// the same entry and get the same live session - same id, same host path. So this
    /// costs ZERO sandbox provisions (it is a cache hit) plus the pooled agent's in-memory-only state,
    /// and sharing a conversation today shares its filesystem. Whether that should be so is an open
    /// product decision tracked in #417; this releases the agent, never the sandbox.
    /// </para>
    /// <para>
    /// The reads below are ONE look at one entry, and the release re-validates that same entry under
    /// the pool's per-thread lock before it removes anything (#418). Three things follow, and none of
    /// them is best-effort any more. A run in progress is left in place, and so is an input that has
    /// been accepted and not yet started - both count as work in hand, so a turn a sender already holds
    /// a receipt for is not discarded by a handoff. An entry that was REPLACED between the look and the
    /// release is left alone rather than disposed, because the decision made here no longer describes
    /// it. And the owner and the app id come from one entry, so the cross-app compare can no longer be
    /// decided against a thread state that never existed - a vanished entry now reports absence rather
    /// than a null app id indistinguishable from "never frozen".
    /// </para>
    /// <para>
    /// What is still not guaranteed, stated so the paragraph above is not read as more: the pool
    /// answers what it DID, and this method acts on that answer, but a handoff refused as busy is
    /// refused for as long as work keeps arriving on that thread. The accepted-input marker is also
    /// bounded (<c>MultiTurnAgentPool.AcceptedInputGrace</c>): an agent that accepts an input and then
    /// wedges stops pinning the entry once the grace elapses, because refusing every future handoff for
    /// that conversation forever is a worse failure than the one being prevented.
    /// </para>
    /// <para>
    /// The app-id freeze (<see cref="SandboxCredentialConflictException"/>) is NOT released alongside
    /// this, and enforcing that is this method's job rather than the pool's. It is the boundary between
    /// SERVICES, not between people: an app-only S2S caller has no <c>EffectiveUserId</c> to hold a
    /// grant with, so there is no authorization verdict here that could stand in for one, and the
    /// cross-actor resume matrix (#153) pins that refusal on purpose.
    /// </para>
    /// <para>
    /// Saying so is not enough, because the release itself is what drops it: <c>RemoveAgentAsync</c>
    /// takes the whole entry, including the <c>CallerCredential</c> the app-id compare reads, so the
    /// recreate that follows finds nothing to compare against and re-freezes the conversation to
    /// whatever app the NEW caller presents. So the freeze is read HERE, before the removal makes it
    /// unreadable, and a mismatch raises the same exception the pool would have raised - the routes map
    /// it to the same <c>409 caller_credential_conflict</c> either way. The gap was invisible to the
    /// #153 matrix because an app-only caller returns above without ever reaching the removal; it takes
    /// a caller with BOTH a user id and a different app id - a grantee signing in through the UI to a
    /// conversation an S2S app minted - to reach it.
    /// </para>
    /// </remarks>
    /// <param name="threadId">The conversation whose pooled agent may need releasing.</param>
    /// <param name="operation">Which route is releasing, for the log line.</param>
    /// <param name="callerCredential">
    /// This request's sandbox credential, or <c>null</c> for an interactive UI caller. Compared against
    /// the app id the thread is frozen to so the release cannot launder a cross-actor takeover.
    /// </param>
    /// <exception cref="SandboxCredentialConflictException">
    /// Thrown when this caller's app id differs from the one the thread's agent was created under.
    /// </exception>
    private async Task ReleaseAgentBoundToAnotherUserAsync(
        string threadId,
        string operation,
        SandboxCredential? callerCredential)
    {
        var callerUserId = CallerUserId;
        if (callerUserId is null)
        {
            // An app-only caller (or enforcement off) carries no user to compare, and the pool's
            // principal guard short-circuits on a null on either side. Nothing to release.
            return;
        }

        // ONE look, and every fact this method decides on comes out of it (#418). Absence is its own
        // answer here: previously the owner and the app id were two unlocked lookups that both
        // answered null for a thread with no entry, so a vanished entry read as "frozen to no app".
        if (!agentPool.TryGetHandoffState(threadId, out var handoff))
        {
            return;
        }

        var boundTo = handoff.OwnerUserId;
        if (boundTo is null || string.Equals(boundTo, callerUserId, StringComparison.Ordinal))
        {
            return;
        }

        // Before anything is torn down, and before the busy exit below: the app-id refusal is
        // unconditional, so it must not depend on whether someone happens to be streaming.
        var frozenAppId = handoff.CallerAppId;
        var requestedAppId = callerCredential?.AppId;
        if (!string.Equals(frozenAppId, requestedAppId, StringComparison.Ordinal))
        {
            logger.LogWarning(
                "{Operation} for thread {ThreadId} refused a handoff across app identities: frozen to {ExistingAppId}, requested {RequestedAppId}",
                operation,
                threadId,
                frozenAppId ?? "(none)",
                requestedAppId ?? "(none)");
            throw new SandboxCredentialConflictException(threadId, frozenAppId, requestedAppId);
        }

        // The pool decides and acts in one step. Answering on this outcome rather than on the read
        // above is the point: a run that started in between, or an entry that was replaced in
        // between, is reported as such instead of being torn down anyway.
        var outcome = await agentPool.TryReleaseIdleAgentAsync(threadId, handoff);
        switch (outcome)
        {
            case MultiTurnAgentPool.AgentReleaseOutcome.Released:
                logger.LogInformation(
                    "{Operation} for thread {ThreadId} released the agent bound to another user so an authorized caller gets their own",
                    operation,
                    threadId);
                break;

            case MultiTurnAgentPool.AgentReleaseOutcome.Busy:
                logger.LogInformation(
                    "{Operation} for thread {ThreadId} left the agent bound to another user in place: it has work in hand",
                    operation,
                    threadId);
                break;

            case MultiTurnAgentPool.AgentReleaseOutcome.NotPooled:
            case MultiTurnAgentPool.AgentReleaseOutcome.Replaced:
            default:
                // Either way the entry this method reasoned about is gone, and the next caller through
                // gets its own look at whatever is there now.
                logger.LogInformation(
                    "{Operation} for thread {ThreadId} released nothing: the entry it read was {Outcome}",
                    operation,
                    threadId,
                    outcome);
                break;
        }
    }

    /// <summary>Lists the grants on a conversation. Reading the roster is a read of the resource.</summary>
    /// <param name="threadId">The conversation.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("{threadId}/shares")]
    public async Task<IActionResult> ListShares(string threadId, CancellationToken ct = default)
    {
        if (await AuthorizeAsync(threadId, AccessAction.Read, ct) is { } denied)
        {
            return denied;
        }

        var tenantId = authorizer.Current?.TenantId;
        if (tenantId is null)
        {
            return Ok(Array.Empty<ConversationShareResponse>());
        }

        var grants = await authorizer.Grants
            .ListGrantsForResourceAsync(tenantId, ConversationAuthorizer.ConversationRef(threadId), ct);

        return Ok(grants.Select(g => new ConversationShareResponse
        {
            ThreadId = threadId,
            SubjectId = g.SubjectId,
            Role = g.Role == GrantRole.Editor ? "editor" : "viewer",
            GrantedBy = g.GrantedBy,
            GrantedAtUnixMs = g.GrantedAt.ToUnixTimeMilliseconds(),
            ExpiresAtUnixMs = g.ExpiresAt?.ToUnixTimeMilliseconds(),
        }).ToArray());
    }

    /// <summary>
    /// Shares a conversation with one named person (spec 8.4). Idempotent: re-sharing with a
    /// different role replaces the grant rather than adding a second one.
    /// </summary>
    /// <remarks>
    /// <c>Share</c> is its own action, not a flavour of <c>Write</c>: by the rights table of 7.4.1
    /// a grantee - including an <c>editor</c> - may not re-share, and a tenant admin may not share
    /// on the owner's behalf. Routing this through <c>Write</c> would hand both of them the right.
    /// </remarks>
    /// <param name="threadId">The conversation.</param>
    /// <param name="request">Who to share with, and as what.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpPost("{threadId}/shares")]
    public async Task<IActionResult> AddShare(
        string threadId,
        [FromBody] ConversationShareRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (ParseGrantRole(request.Role) is not { } role)
        {
            return BadRequest(new
            {
                error = "invalid_role",
                code = "invalid_role",
                detail = "Role must be 'viewer' or 'editor'.",
                threadId,
            });
        }

        if (string.IsNullOrWhiteSpace(request.SubjectId))
        {
            return BadRequest(new
            {
                error = "invalid_subject",
                code = "invalid_subject",
                detail = "SubjectId must be the '{tid}:{oid}' pair of the person to share with.",
                threadId,
            });
        }

        var metadata = await store.LoadMetadataAsync(threadId, ct);
        if (Refuse(threadId, await authorizer.AuthorizeAsync(threadId, metadata, AccessAction.Share, ct))
            is { } denied)
        {
            return denied;
        }

        // Reachable only while enforcement is off, where the whole model is inert; a grant written
        // with no principal would name nobody as its grantor.
        if (authorizer.Current is not { } principal || metadata is null)
        {
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new
                {
                    error = "sharing_unavailable",
                    code = "sharing_unavailable",
                    detail = "Sharing requires Identity:Enforce and an authenticated principal.",
                    threadId,
                });
        }

        var now = authorizer.Clock.GetUtcNow();

        await authorizer.Grants.GrantAsync(
            new ResourceGrant
            {
                TenantId = principal.TenantId,
                Resource = ConversationAuthorizer.ConversationRef(threadId),
                SubjectId = request.SubjectId,
                Role = role,
                GrantedBy = principal.EffectiveUserId ?? principal.Actor.Id,
                GrantedAt = now,
                ExpiresAt = request.ExpiresAtUnixMs is { } expires
                    ? DateTimeOffset.FromUnixTimeMilliseconds(expires)
                    : null,
            },
            ct);

        await SetVisibilityAsync(threadId, Visibility.Shared, ct);

        logger.LogInformation(
            "Conversation {ThreadId} shared with {SubjectId} as {Role}.",
            threadId,
            request.SubjectId,
            role);

        return Ok(new ConversationShareResponse
        {
            ThreadId = threadId,
            SubjectId = request.SubjectId,
            Role = role == GrantRole.Editor ? "editor" : "viewer",
            GrantedBy = principal.EffectiveUserId ?? principal.Actor.Id,
            GrantedAtUnixMs = now.ToUnixTimeMilliseconds(),
            ExpiresAtUnixMs = request.ExpiresAtUnixMs,
        });
    }

    /// <summary>Revokes one person's grant on a conversation.</summary>
    /// <remarks>
    /// Answers <c>204</c> whether or not a row was removed. A revoke is a statement about the end
    /// state, and a <c>404</c> for "there was no grant" would let anyone entitled to revoke
    /// enumerate who a conversation is shared with without reading the roster.
    /// </remarks>
    /// <param name="threadId">The conversation.</param>
    /// <param name="subjectId">The person whose grant is removed.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpDelete("{threadId}/shares/{subjectId}")]
    public async Task<IActionResult> RemoveShare(
        string threadId,
        string subjectId,
        CancellationToken ct = default)
    {
        if (await AuthorizeAsync(threadId, AccessAction.Share, ct) is { } denied)
        {
            return denied;
        }

        if (authorizer.Current is not { } principal)
        {
            return NoContent();
        }

        var resource = ConversationAuthorizer.ConversationRef(threadId);
        _ = await authorizer.Grants.RevokeAsync(principal.TenantId, resource, subjectId, ct);

        // Visibility is STORED, not derived (spec 8.3), so the transition back to Private has to be
        // made by whoever removed the last grant - otherwise `Shared` outlives the sharing, and a
        // conversation reads as shared with nobody forever.
        if (!await authorizer.Grants.HasAnyGrantAsync(
                principal.TenantId,
                resource,
                authorizer.Clock.GetUtcNow(),
                ct))
        {
            await SetVisibilityAsync(threadId, Visibility.Private, ct);
        }

        return NoContent();
    }

    /// <summary>Parses the wire role. An unrecognised value is refused, never defaulted.</summary>
    private static GrantRole? ParseGrantRole(string? role) => role switch
    {
        "viewer" => GrantRole.Viewer,
        "editor" => GrantRole.Editor,
        _ => null,
    };

    /// <summary>
    /// Moves a conversation between <see cref="Visibility.Private"/> and
    /// <see cref="Visibility.Shared"/> through the store's atomic read-modify-write, so the change
    /// cannot clobber a concurrent title edit or binding write.
    /// </summary>
    private Task SetVisibilityAsync(string threadId, Visibility visibility, CancellationToken ct) =>
        store.UpdateMetadataAsync(
            threadId,
            existing => new ThreadMetadata
            {
                ThreadId = threadId,
                CurrentRunId = existing?.CurrentRunId,
                LatestRunId = existing?.LatestRunId,
                LastUpdated = existing?.LastUpdated ?? timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
                SessionMappings = existing?.SessionMappings,
                Properties = existing?.Properties,
                TenantId = existing?.TenantId,
                OwnerUserId = existing?.OwnerUserId,
                OwnerAppId = existing?.OwnerAppId,
                Visibility = visibility,
            },
            ct);

    /// <summary>
    /// Reads the caller-forwarded <c>X-Sbx-App-Id</c> / <c>X-Sbx-App-Key</c> headers (the same names
    /// <c>Program.cs</c>'s <c>AddSandboxAuthHeaders</c> writes for the sample's own outbound gateway
    /// calls) and, when an app id is present, builds a <see cref="SandboxCredential"/> to pass through
    /// as the pool's <c>callerCredential</c>. This is a per-request VALUE only — it is never persisted
    /// to <see cref="ThreadMetadata.Properties"/> or logged; the pool freezes it against the thread's
    /// first-writer app id and re-validates it on every subsequent call (issue #153 M2).
    /// <para>
    /// Deliberately does not call <see cref="SandboxCredential.ValidateKeyOrThrow"/>: a malformed
    /// caller-forwarded key isn't this controller's concern to reject — the gateway itself validates
    /// the key on the actual sandbox call, so a bad key surfaces there instead of as an unrelated 500
    /// here.
    /// </para>
    /// <para>
    /// Absent app id means "no caller credential" (the plain interactive-UI default), matching
    /// <see cref="SandboxCredentialConflictException"/>'s null-app-id convention. An app id with no
    /// key is still forwarded — key presence/shape is the gateway's concern, not this guard's.
    /// <paramref name="headers"/> itself may be <c>null</c> (e.g. an action invoked directly without
    /// an <c>HttpContext</c>), which is likewise treated as "no caller credential".
    /// </para>
    /// </summary>
    private static SandboxCredential? TryBuildCallerCredential(IHeaderDictionary? headers)
    {
        if (headers == null)
        {
            return null;
        }

        var appId = headers[SandboxCredential.AppIdHeader].ToString();
        if (string.IsNullOrEmpty(appId))
        {
            return null;
        }

        var appKey = headers[SandboxCredential.AppKeyHeader].ToString();
        return new SandboxCredential(appId, appKey);
    }

    /// <summary>
    /// Fixes legacy persisted messages where content_block_start leaked "{}" into FunctionArgs,
    /// producing invalid JSON like {}{"query":"..."}.
    /// </summary>
    private static IMessage FixLegacyDoubledArgs(IMessage msg)
    {
        return msg switch
        {
            ToolCallMessage tc when NeedsArgsFix(tc.FunctionArgs) =>
                tc with { FunctionArgs = StripLeadingEmptyObject(tc.FunctionArgs!) },
            _ => msg,
        };
    }

    private static bool NeedsArgsFix(string? args)
    {
        return args is not null && args.StartsWith("{}{", StringComparison.Ordinal);
    }

    private static string StripLeadingEmptyObject(string args)
    {
        return args[2..]; // Remove leading "{}"
    }
}
