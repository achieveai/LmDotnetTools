using System.Text.Json.Serialization;
using AchieveAi.LmDotnetTools.LmAgentInfra.Auth;
using AchieveAi.LmDotnetTools.LmAgentInfra.Context;
using AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using LmStreaming.Sample.Services.Discovery;
using Microsoft.AspNetCore.Mvc;

namespace LmStreaming.Sample.Controllers;

/// <summary>
/// Known values of <see cref="ContextDiscoveryPayload.Kind"/>. Strings (not an enum) because
/// the gateway may add new kinds without breaking older app builds — unknown kinds are accepted
/// and logged as a no-op rather than rejected.
/// </summary>
internal static class ContextDiscoveryKinds
{
    public const string SubAgent = "subagent";
    public const string ContextFile = "context_file";
}

/// <summary>
/// Webhook endpoint the sandbox gateway calls when it discovers a new context file in the
/// workspace (sub-agent, skill, …). For <c>kind == "subagent"</c> payloads this resolves the
/// session, loads the markdown via <see cref="WorkspaceSubAgentLoader.LoadOneAsync"/>, and
/// registers the resulting template into the session's
/// <see cref="MutableSubAgentTemplateSource"/> so it surfaces in the Agent tool on the next turn.
/// Other kinds are still log-only.
/// </summary>
/// <remarks>
/// SECURITY: mirrors <see cref="AchieveAi.LmDotnetTools.LmAgentInfra.Controllers.AuthWebhookController"/>'s pattern: the gateway authenticates
/// itself with a shared secret in the <c>Authorization</c> header, compared in constant time
/// over fixed-width hashes. The controller NEVER logs the Authorization header value or the
/// shared secret — only the discovery payload's kind/name/path and the activation decision.
/// </remarks>
[ApiController]
[Route("api/discovery")]
public sealed class ContextDiscoveryController(
    AuthSharedSecret sharedSecret,
    SandboxSessionRegistry sessionRegistry,
    WorkspaceSubAgentLoader subAgentLoader,
    ContextDiscoveryInjector contextInjector,
    ContextDiscoveryDiagnostics diagnostics,
    ILogger<ContextDiscoveryController> logger) : ControllerBase
{
    /// <summary>
    /// Gateway callback for a batch of discovered context items. The gateway batches every newly
    /// discovered item for a session into one POST — a <see cref="ContextDiscoveryEnvelope"/>
    /// carrying the session id plus a <c>discoveries</c> array. Returns 200 on success (including
    /// best-effort no-ops), 401 if the shared secret doesn't match, 400 if the envelope is null or
    /// any item is malformed for its kind.
    /// </summary>
    [HttpPost("context_discovery")]
    public async Task<IActionResult> NotifyAsync(
        [FromBody] ContextDiscoveryEnvelope? body,
        CancellationToken cancellationToken)
    {
        if (!sharedSecret.Matches(Request.Headers.Authorization.ToString()))
        {
            // Do not reveal whether the header was missing, malformed, or simply wrong.
            logger.LogWarning("Rejected unauthorized context-discovery webhook call.");
            return Unauthorized();
        }

        if (body is null)
        {
            logger.LogWarning("Rejected context-discovery webhook with malformed payload.");
            return BadRequest();
        }

        // Project each batched item onto the per-item carrier the downstream handlers consume,
        // stamping the envelope-level session id onto each. Validate the WHOLE batch up front so a
        // malformed item is rejected at the boundary rather than partially applied.
        var items = body.Discoveries ?? [];
        var payloads = new List<ContextDiscoveryPayload>(items.Count);
        foreach (var item in items)
        {
            var payload = new ContextDiscoveryPayload
            {
                SessionId = body.SessionId,
                Kind = item.Kind,
                Name = item.Name,
                Description = item.Description,
                Path = item.Path,
                Content = item.Content,
                Truncated = item.Truncated,
            };

            if (!IsValid(payload))
            {
                logger.LogWarning(
                    "Rejected context-discovery webhook with malformed item (kind={Kind}, path={Path}).",
                    item.Kind,
                    item.Path);
                return BadRequest();
            }

            payloads.Add(payload);
        }

        // Dispatch the batch concurrently. Sub-agent activations are independent (first-wins
        // registration into a ConcurrentDictionary) and run in parallel. Context-file injections
        // must preserve their delivery order — the injected user-turn messages should reach the
        // model in a deterministic sequence (e.g. a nested CLAUDE.md after the root one), and the
        // agent input channel gives no ordering guarantee across concurrent writers — so they run
        // as one ordered chain. Both groups are awaited together.
        var pending = new List<Task>(payloads.Count);
        var contextFiles = new List<ContextDiscoveryPayload>();

        foreach (var payload in payloads)
        {
            logger.LogInformation(
                "ContextDiscovery: kind={Kind} name={Name} path={Path} description={Description} session={SessionId} truncated={Truncated}",
                payload.Kind,
                payload.Name,
                payload.Path,
                payload.Description,
                payload.SessionId,
                payload.Truncated);

            // Record the arrival for the diagnostics endpoint BEFORE the kind-specific handling
            // (which is best-effort and may no-op). This is what lets an operator confirm webhooks
            // are reaching the app at all — the count staying at zero is the signature of an
            // unreachable callback host OR a rejected (401) delivery. RecordReceived is a fast,
            // thread-safe, order-independent counter update.
            diagnostics.RecordReceived(payload.SessionId, payload.Kind, payload.Path ?? payload.Name);

            if (string.Equals(payload.Kind, ContextDiscoveryKinds.SubAgent, StringComparison.Ordinal))
            {
                pending.Add(TryActivateSubAgentAsync(payload, cancellationToken));
            }
            else if (string.Equals(payload.Kind, ContextDiscoveryKinds.ContextFile, StringComparison.Ordinal))
            {
                contextFiles.Add(payload);
            }
        }

        if (contextFiles.Count > 0)
        {
            pending.Add(InjectContextFilesInOrderAsync(contextFiles, cancellationToken));
        }

        await Task.WhenAll(pending).ConfigureAwait(false);

        return Ok();
    }

    /// <summary>
    /// Injects the batch's context files into the conversation in delivery order. Kept sequential
    /// on purpose: the injected user-turn messages must reach the model in a deterministic sequence
    /// (e.g. a nested CLAUDE.md after the root one), and concurrent writes to the agent input
    /// channel carry no ordering guarantee. Each injection is a fast non-blocking enqueue, so the
    /// chain adds no meaningful latency.
    /// </summary>
    private async Task InjectContextFilesInOrderAsync(
        IReadOnlyList<ContextDiscoveryPayload> contextFiles,
        CancellationToken cancellationToken)
    {
        foreach (var payload in contextFiles)
        {
            await contextInjector.InjectAsync(payload, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Kind-aware payload validation. Sub-agent deliveries require a name (the activation key);
    /// context-file deliveries require a path + content (the only thing the injector needs to
    /// build the next-turn message). Unknown kinds are accepted (logged + no-op) so the gateway
    /// can introduce new kinds without breaking older app builds.
    /// </summary>
    private static bool IsValid(ContextDiscoveryPayload body)
    {
        if (string.IsNullOrWhiteSpace(body.Kind))
        {
            return false;
        }

        if (string.Equals(body.Kind, ContextDiscoveryKinds.SubAgent, StringComparison.Ordinal))
        {
            return !string.IsNullOrWhiteSpace(body.Name);
        }

        if (string.Equals(body.Kind, ContextDiscoveryKinds.ContextFile, StringComparison.Ordinal))
        {
            return !string.IsNullOrWhiteSpace(body.Path) && body.Content is not null;
        }

        return true;
    }

    /// <summary>
    /// Best-effort activation: resolves the session + binding, loads the markdown, and registers
    /// the template. Every failure path is logged and swallowed — context discovery is an
    /// enrichment, never blocking, so the gateway always sees a 200 for an authenticated payload.
    /// </summary>
    private async Task TryActivateSubAgentAsync(
        ContextDiscoveryPayload body,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.SessionId))
        {
            logger.LogWarning(
                "ContextDiscovery subagent {Name}: payload missing session_id; cannot resolve session for activation.",
                body.Name);
            return;
        }

        if (!sessionRegistry.TryGetSessionById(body.SessionId, out var session) || session is null)
        {
            logger.LogInformation(
                "ContextDiscovery subagent {Name}: no session known for session_id {SessionId}; skipping activation.",
                body.Name,
                body.SessionId);
            return;
        }

        // Fan the discovery out to every conversation currently live on this session. Each
        // conversation owns its own catalog source (so a NEW conversation starting later does not
        // inherit another conversation's discoveries); a discovery that arrives while one or more
        // conversations are live activates into each of them.
        var bindings = sessionRegistry.GetSubAgentBindingsForSession(body.SessionId);
        if (bindings.Count == 0)
        {
            // No conversation has initialised the agent path for this session yet — built-ins/
            // discovered entries are loaded the first time a loop is created. Once one is,
            // subsequent discoveries land here and activate normally.
            logger.LogInformation(
                "ContextDiscovery subagent {Name}: session {SessionId} has no agent binding yet; skipping activation.",
                body.Name,
                body.SessionId);
            return;
        }

        SubAgentSessionRegistryItem item;
        try
        {
            item = new SubAgentSessionRegistryItem(
                Kind: body.Kind!,
                Name: body.Name!,
                Description: body.Description,
                Path: body.Path ?? string.Empty);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "ContextDiscovery subagent {Name}: failed to build discovery item.",
                body.Name);
            return;
        }

        // Load the markdown at most once and register the resulting template into every live
        // conversation's catalog. The loader is keyed by the session + path, not the conversation,
        // so a single read is reused; the per-conversation step is only the first-wins TryRegister.
        SubAgentTemplate? template = null;
        var loaded = false;

        foreach (var binding in bindings)
        {
            // Fast-path: if this conversation already has a template with this name (built-in seed
            // OR a previous discovery on this conversation), skip it entirely.
            if (binding.Source.Templates.ContainsKey(body.Name!))
            {
                continue;
            }

            if (!loaded)
            {
                loaded = true;
                try
                {
                    template = await subAgentLoader
                        .LoadOneAsync(session, item.ToDiscovered(), binding.AgentFactory, ct)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(
                        ex,
                        "ContextDiscovery subagent {Name}: loader threw while activating; skipping.",
                        body.Name);
                    return;
                }
            }

            if (template is null || string.IsNullOrWhiteSpace(template.Name))
            {
                // LoadOneAsync already logged the specific reason (path traversal, missing file,
                // malformed markdown, …); a non-null template with a blank Name would indicate a
                // mapping bug, so it's also dropped here rather than registered as garbage.
                return;
            }

            // Rebind the template to THIS conversation's agent factory. The template is loaded once
            // (with the first conversation's factory), but each conversation must spawn the
            // discovered sub-agent with its OWN provider — otherwise a sub-agent fanned into
            // conversation B would run on conversation A's provider.
            var conversationTemplate = template with { AgentFactory = binding.AgentFactory };

            if (binding.Source.TryRegister(conversationTemplate.Name!, conversationTemplate))
            {
                logger.LogInformation(
                    "ContextDiscovery subagent {Name}: activated for session {SessionId}.",
                    conversationTemplate.Name,
                    body.SessionId);
            }
            else
            {
                // First-wins collision with a built-in OR an earlier discovery on this conversation.
                logger.LogInformation(
                    "ContextDiscovery subagent {Name}: session {SessionId} already has a template "
                    + "with that name; keeping the first.",
                    template.Name,
                    body.SessionId);
            }
        }
    }
}

/// <summary>
/// Internal helper that wraps the controller's payload fields in the shape
/// <see cref="WorkspaceSubAgentLoader.LoadOneAsync"/> expects, without leaking the controller's
/// public payload contract into the loader.
/// </summary>
internal readonly record struct SubAgentSessionRegistryItem(string Kind, string Name, string? Description, string Path)
{
    public SandboxSessionRegistry.DiscoveredItem ToDiscovered() =>
        new(Kind, Name, Description, Path);
}

/// <summary>
/// Gateway → app batch payload for a <c>context_discovery</c> webhook delivery. The gateway
/// batches every newly-discovered item for a session into ONE POST: a top-level envelope carrying
/// the session id plus a <c>discoveries</c> array of per-kind items (see SandboxedOstoolsMcpServer
/// <c>Docs/context-discovery.md</c> §Webhook payload). Field names are the gateway's wire contract
/// (snake_case), pinned via <see cref="JsonPropertyNameAttribute"/>. Unknown fields (e.g.
/// <c>event</c>, <c>app_id</c>) are tolerated by System.Text.Json so the gateway can evolve its
/// envelope without breaking older app builds.
/// </summary>
public sealed record ContextDiscoveryEnvelope
{
    [JsonPropertyName("event")]
    public string? Event { get; init; }

    [JsonPropertyName("session_id")]
    public string? SessionId { get; init; }

    [JsonPropertyName("app_id")]
    public string? AppId { get; init; }

    [JsonPropertyName("discoveries")]
    public List<ContextDiscoveryItem>? Discoveries { get; init; }
}

/// <summary>
/// One discovered item inside a <see cref="ContextDiscoveryEnvelope"/>, discriminated by
/// <see cref="Kind"/>. Only the fields the app acts on are bound here; the gateway's richer
/// per-kind fields (<c>frontmatter</c>/<c>prompt</c>/<c>directory</c>/<c>plugin</c>/…) are
/// tolerated and ignored.
/// </summary>
public sealed record ContextDiscoveryItem
{
    [JsonPropertyName("kind")]
    public string? Kind { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("path")]
    public string? Path { get; init; }

    /// <summary>Body of a discovered context file (CLAUDE.md / AGENTS.md). Present for
    /// <c>kind == "context_file"</c> items; other kinds resolve their content elsewhere.</summary>
    [JsonPropertyName("content")]
    public string? Content { get; init; }

    [JsonPropertyName("truncated")]
    public bool? Truncated { get; init; }
}
