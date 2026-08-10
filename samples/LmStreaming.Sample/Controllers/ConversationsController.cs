using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
using LmStreaming.Sample.Models;
using LmStreaming.Sample.Persistence;
using LmStreaming.Sample.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace LmStreaming.Sample.Controllers;

/// <summary>
/// Inbound S2S auth guard for <see cref="ConversationsController"/> (issue #153 M2; see
/// <c>decisions.md</c> #1/#2 and the plan's Step 10). Implemented directly as an attribute filter:
/// ASP.NET Core's default filter provider recognizes any controller/action attribute that implements
/// <see cref="IAsyncActionFilter"/> and runs it as-is — no <c>IFilterFactory</c>, no constructor DI,
/// no Program.cs registration required — so applying <c>[InboundS2SAuth]</c> on the controller is
/// entirely self-contained in this file.
/// <para>
/// The shared secret is read fresh on every request from <c>Auth:S2SInboundSecret</c> via
/// <see cref="HttpContext.RequestServices"/> — no caching, matching the decision log's "per-request
/// constant-time validation" (BS3). Operators set it via the flat env var
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
/// is a separate trust boundary from the gateway/webhook shared secret it guards (decisions.md #1).
/// Neither the configured secret nor the presented header value is ever logged or echoed in the response.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class InboundS2SAuthAttribute : Attribute, IAsyncActionFilter
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
    private static bool ConstantTimeEquals(string expected, string? presented)
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

        var threadId = $"thread-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;

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

                return new ThreadMetadata
                {
                    ThreadId = threadId,
                    CurrentRunId = existing?.CurrentRunId,
                    LatestRunId = existing?.LatestRunId,
                    LastUpdated = now.ToUnixTimeMilliseconds(),
                    SessionMappings = existing?.SessionMappings,
                    Properties = propertiesBuilder.ToImmutable(),
                };
            },
            ct);

        return Ok(new ProvisionConversationResponse { ThreadId = threadId });
    }

    [HttpGet]
    public async Task<IActionResult> List(
        int limit = 50,
        int offset = 0,
        CancellationToken ct = default)
    {
        var threads = await store.ListThreadsAsync(limit, offset, ct);
        var result = threads
            // Sub-agent and workflow-controller conversations use the sample's reserved agent-owned
            // thread-id space and are surfaced only through the sub-agent panel (GET .../subagents +
            // /ws/subagent). They must not leak into the primary conversation sidebar (nor be
            // auto-selected on load).
            .Where(t => !SubAgentSummary.IsAgentOwnedThreadId(t.ThreadId))
            .Select(t => new ConversationSummary
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
            });
        return Ok(result);
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
    public IActionResult GetRunState(string threadId)
    {
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
        if (recursive)
        {
            return await BuildDescendantTreeAsync(threadId, ct);
        }

        var (rows, isKnown, _) = await _hierarchy.BuildAsync(threadId, viewer, ct);
        return isKnown
            ? Ok(rows.ToArray())
            : NotFound(new { error = $"Conversation '{threadId}' not found.", code = "unknown_thread" });
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
        var result = await _hierarchy.ReadTranscriptAsync(threadId, agentId, viewer, ct);
        return result.Outcome switch
        {
            AgentTranscriptOutcome.UnknownThread =>
                NotFound(new
                {
                    error = $"Conversation '{threadId}' not found.",
                    code = AgentTranscriptReasons.UnknownThread,
                }),
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
                return NotFound(new { error = $"Conversation '{rootThreadId}' not found.", code = "unknown_thread" });
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
        if (metadata == null)
        {
            return NotFound(new { error = $"Conversation '{threadId}' not found.", code = "unknown_thread" });
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
            agent = agentPool.GetOrCreateAgent(
                threadId,
                mode,
                requestedProviderId: null,
                requestResponseDumpFileName: null,
                callerCredential: callerCredential);
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
            // Cross-actor mismatch (Cross-Actor Resume Matrix, issue #153): the thread is bound to a
            // different caller identity than the one on this request. The exception message carries
            // only app ids, never app keys, so it's safe to surface via ex.Message.
            logger.LogWarning(
                "SendMessage for thread {ThreadId} rejected: caller credential conflict (existing app id {ExistingAppId}, requested app id {RequestedAppId})",
                threadId,
                ex.ExistingAppId ?? "(none)",
                ex.RequestedAppId ?? "(none)");
            return Conflict(
                new { error = "caller_credential_conflict", code = "caller_credential_conflict", detail = ex.Message, threadId });
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
        catch when (idempotent)
        {
            await ReleaseAdmissionAsync(acceptances!, admission);
            throw;
        }

        if (receipt == null)
        {
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
        if (metadata == null)
        {
            return NotFound(new { error = $"Conversation '{threadId}' not found.", code = "unknown_thread" });
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

                return new ThreadMetadata
                {
                    ThreadId = threadId,
                    CurrentRunId = existing?.CurrentRunId,
                    LatestRunId = existing?.LatestRunId,
                    LastUpdated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    SessionMappings = existing?.SessionMappings,
                    Properties = propertiesBuilder.ToImmutable(),
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
            _ = await agentPool.RecreateAgentWithModeAsync(threadId, mode, callerCredential);
        }
        catch (SandboxCredentialConflictException ex)
        {
            // Same cross-actor rejection SendMessage enforces (issue #153): a caller may not switch
            // the mode of a conversation bound to a different app identity. Message carries only app
            // ids, never keys.
            logger.LogWarning(
                "Mode switch for thread {ThreadId} rejected: caller credential conflict (existing app id {ExistingAppId}, requested app id {RequestedAppId})",
                threadId,
                ex.ExistingAppId ?? "(none)",
                ex.RequestedAppId ?? "(none)");
            return Conflict(
                new { error = "caller_credential_conflict", code = "caller_credential_conflict", detail = ex.Message, threadId });
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
            _ = await agentPool.RecreateAgentWithProviderAsync(threadId, request.ProviderId, currentMode, callerCredential);
        }
        catch (SandboxCredentialConflictException ex)
        {
            // Same cross-actor rejection SendMessage enforces (issue #153): a caller may not switch
            // the provider of a conversation bound to a different app identity. Message carries only
            // app ids, never keys.
            logger.LogWarning(
                "Provider switch for thread {ThreadId} rejected: caller credential conflict (existing app id {ExistingAppId}, requested app id {RequestedAppId})",
                threadId,
                ex.ExistingAppId ?? "(none)",
                ex.RequestedAppId ?? "(none)");
            return Conflict(
                new { error = "caller_credential_conflict", code = "caller_credential_conflict", detail = ex.Message, threadId });
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
