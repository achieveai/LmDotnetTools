using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Utils;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
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
    private static bool IsServiceToServiceRequest(HttpRequest request) =>
        request.Headers.ContainsKey(HeaderName)
        || request.Headers.ContainsKey(SandboxCredential.AppIdHeader);

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
    ILogger<ConversationsController> logger) : ControllerBase
{
    /// <summary>
    /// Warning returned from a mode/provider switch that recreated the agent while a <c>Wait</c> was
    /// armed. The switch succeeds; the pending timer/park is discarded with the old trigger runtime.
    /// </summary>
    private const string ArmedWaitDiscardedWarning =
        "A pending Wait was armed on this conversation; it was discarded when the agent was recreated for the switch.";

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
            // Sub-agent conversations use the reserved "subagent-{agentId}" thread-id convention and are
            // surfaced only through the sub-agent panel (GET .../subagents + /ws/subagent). They must not
            // leak into the primary conversation sidebar (nor be auto-selected on load).
            .Where(t => !t.ThreadId.StartsWith("subagent-", StringComparison.Ordinal))
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

    [HttpGet("{threadId}/messages")]
    public async Task<IActionResult> GetMessages(
        string threadId,
        CancellationToken ct = default)
    {
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
    /// stops, or otherwise mutates a sub-agent (WI #194). Returns 404 for an unknown thread, an
    /// empty array for a conversation whose agent has no sub-agent support, otherwise the
    /// <c>SubAgentManager.ListAgents()</c> snapshot projected to <see cref="SubAgentSummary"/>.
    /// </summary>
    [HttpGet("{threadId}/subagents")]
    public IActionResult ListSubAgents(string threadId)
    {
        if (!agentPool.TryGet(threadId, out var agent) || agent is null)
        {
            return NotFound(new { error = $"Conversation '{threadId}' not found.", code = "unknown_thread" });
        }

        if (agent is not MultiTurnAgentLoop loop || loop.SubAgentManager is null)
        {
            return Ok(Array.Empty<SubAgentSummary>());
        }

        var summaries = loop.SubAgentManager.ListAgents()
            .Select(s => new SubAgentSummary
            {
                AgentId = s.AgentId,
                Name = s.Name,
                Template = s.TemplateName,
                Task = s.Task,
                Status = s.Status.ToString().ToLowerInvariant(),
                ThreadId = s.ThreadId,
                LastActivityUtc = s.LastActivityUtc,
            })
            .ToArray();

        return Ok(summaries);
    }

    /// <summary>
    /// Queues a message onto a previously-provisioned thread. Non-blocking: returns as soon as the
    /// input is durably recorded as accepted, before it is necessarily drained into a run — callers
    /// poll <see cref="GetStatus"/> by the returned <c>inputId</c> to learn when/how it resolved.
    /// </summary>
    [HttpPost("{threadId}/messages")]
    public async Task<IActionResult> SendMessage(
        string threadId,
        [FromBody] SendMessageRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

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

        var inputId = Guid.NewGuid().ToString();
        var userMessage = new TextMessage { Role = Role.User, Text = request.Text };

        // A null return means the input channel is full — TrySendAsync guarantees no accepted-input
        // record survives in that case. A thrown exception (durable-store write failure) is left to
        // propagate to a 500, per the REST contract (no inputId returned either way).
        var receipt = await agent.TrySendAsync([userMessage], inputId: inputId, parentRunId: null, ct);
        if (receipt == null)
        {
            logger.LogWarning("SendMessage for thread {ThreadId} rejected: input queue full", threadId);
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new { error = "queue_full", code = "queue_full", threadId });
        }

        return Accepted(new SendMessageResponse { InputId = inputId, Queued = true });
    }

    /// <summary>
    /// Polls a run's resolved status by exactly one of <paramref name="runId"/> or
    /// <paramref name="inputId"/>. See <see cref="ConversationStatusResolver"/> for the 5-state
    /// resolution and the tool-only-run final-response convention.
    /// </summary>
    [HttpGet("{threadId}/status")]
    public async Task<IActionResult> GetStatus(
        string threadId,
        string? runId = null,
        string? inputId = null,
        CancellationToken ct = default)
    {
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

    [HttpPut("{threadId}/metadata")]
    public async Task<IActionResult> UpdateMetadata(
        string threadId,
        [FromBody] ConversationMetadataUpdate update,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(update);

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

    [HttpDelete("{threadId}")]
    public async Task<IActionResult> Delete(
        string threadId,
        CancellationToken ct = default)
    {
        await agentPool.RemoveAgentAsync(threadId);
        await store.DeleteThreadAsync(threadId, ct);
        return NoContent();
    }

    [HttpPost("{threadId}/mode")]
    public async Task<IActionResult> SwitchMode(
        string threadId,
        [FromBody] SwitchModeRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
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
