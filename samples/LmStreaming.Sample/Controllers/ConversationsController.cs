using System.Collections.Immutable;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Utils;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using AchieveAi.LmDotnetTools.LmAgentInfra;
using AchieveAi.LmDotnetTools.LmAgentInfra.Agents;
using AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;
using LmStreaming.Sample.Models;
using LmStreaming.Sample.Persistence;
using LmStreaming.Sample.Services;
using Microsoft.AspNetCore.Mvc;

namespace LmStreaming.Sample.Controllers;

[ApiController]
[Route("api/conversations")]
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
        var result = threads.Select(t => new ConversationSummary
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

        IMultiTurnAgent agent;
        try
        {
            agent = agentPool.GetOrCreateAgent(threadId, mode, requestedProviderId: null, requestResponseDumpFileName: null);
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

        // Switching into a sandbox-backed mode (e.g. Workspace Agent) eagerly creates the sandbox
        // session. A gateway rejection or an unreachable gateway must answer a clean 503 — not crash
        // the request with an unhandled 500 (which, in Development, also leaks a stack-trace page).
        try
        {
            _ = await agentPool.RecreateAgentWithModeAsync(threadId, mode);
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

        return Ok(new { modeId = mode.Id, modeName = mode.Name });
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

        // Switching to a sandbox-backed provider eagerly reprovisions; a gateway rejection or an
        // unavailable/unknown provider must answer a clean 503, not crash the request with a 500.
        try
        {
            _ = await agentPool.RecreateAgentWithProviderAsync(threadId, request.ProviderId, currentMode);
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

        return Ok(new { providerId = request.ProviderId });
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
