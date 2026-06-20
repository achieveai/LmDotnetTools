using System.Text.Json.Serialization;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using LmStreaming.Sample.Services;
using LmStreaming.Sample.Services.Auth;
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
/// SECURITY: mirrors <see cref="AuthWebhookController"/>'s pattern: the gateway authenticates
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
    ILogger<ContextDiscoveryController> logger) : ControllerBase
{
    /// <summary>
    /// Gateway callback for one discovered context item. Returns 200 on success (including
    /// best-effort no-ops), 401 if the shared secret doesn't match, 400 if the payload is
    /// malformed.
    /// </summary>
    [HttpPost("context_discovery")]
    public async Task<IActionResult> NotifyAsync(
        [FromBody] ContextDiscoveryPayload? body,
        CancellationToken cancellationToken)
    {
        if (!sharedSecret.Matches(Request.Headers.Authorization.ToString()))
        {
            // Do not reveal whether the header was missing, malformed, or simply wrong.
            logger.LogWarning("Rejected unauthorized context-discovery webhook call.");
            return Unauthorized();
        }

        if (body is null || !IsValid(body))
        {
            logger.LogWarning("Rejected context-discovery webhook with malformed payload.");
            return BadRequest();
        }

        logger.LogInformation(
            "ContextDiscovery: kind={Kind} name={Name} path={Path} description={Description} session={SessionId} truncated={Truncated}",
            body.Kind,
            body.Name,
            body.Path,
            body.Description,
            body.SessionId,
            body.Truncated);

        if (string.Equals(body.Kind, ContextDiscoveryKinds.SubAgent, StringComparison.Ordinal))
        {
            await TryActivateSubAgentAsync(body, cancellationToken).ConfigureAwait(false);
        }
        else if (string.Equals(body.Kind, ContextDiscoveryKinds.ContextFile, StringComparison.Ordinal))
        {
            await contextInjector.InjectAsync(body, cancellationToken).ConfigureAwait(false);
        }

        return Ok();
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
/// Gateway → app payload for a single discovered context item. Field names are the gateway's
/// wire contract (snake_case), pinned via <see cref="JsonPropertyNameAttribute"/> so they bind
/// regardless of the app's JSON naming defaults. Unknown fields are tolerated by System.Text.Json
/// by default so the gateway can add new fields without breaking older app builds.
/// </summary>
public sealed record ContextDiscoveryPayload
{
    [JsonPropertyName("session_id")]
    public string? SessionId { get; init; }

    [JsonPropertyName("kind")]
    public string? Kind { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("path")]
    public string? Path { get; init; }

    /// <summary>
    /// Body of a discovered context file (CLAUDE.md / AGENTS.md). Sent by the gateway only for
    /// <c>kind == "context_file"</c> deliveries; the sub-agent path resolves the markdown by
    /// reading it from the workspace host directory instead and ignores this field.
    /// </summary>
    [JsonPropertyName("content")]
    public string? Content { get; init; }

    /// <summary>
    /// Set by the gateway when <see cref="Content"/> was truncated to fit a delivery size cap.
    /// The injector surfaces a tag in the injected message so the model knows it isn't seeing
    /// the full file. Optional + defaults to false when absent.
    /// </summary>
    [JsonPropertyName("truncated")]
    public bool? Truncated { get; init; }
}
