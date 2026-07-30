using System.Text;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmCore.Models;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;

/// <summary>
/// Provides the Agent, SendMessage, and CheckAgent tool definitions for sub-agent
/// orchestration. Registered as an IFunctionProvider so these tools are included in
/// the parent agent's function registry alongside all other tools.
/// </summary>
public class SubAgentToolProvider : IFunctionProvider
{
    private readonly SubAgentManager _manager;
    private readonly MutableSubAgentTemplateSource _source;

    public SubAgentToolProvider(
        SubAgentManager manager,
        MutableSubAgentTemplateSource source)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(source);
        _manager = manager;
        _source = source;
    }

    public string ProviderName => "SubAgentTools";

    /// <summary>
    /// Low priority (high number) so parent tools take precedence.
    /// </summary>
    public int Priority => 100;

    public IEnumerable<FunctionDescriptor> GetFunctions()
    {
        yield return CreateAgentDescriptor();
        yield return CreateSendMessageDescriptor();
        yield return CreateCheckAgentDescriptor();
    }

    private FunctionDescriptor CreateAgentDescriptor()
    {
        // Snapshot the live templates view once per descriptor build so the catalog text
        // and the subagent_type enum list are consistent within this descriptor even when
        // TryRegister lands concurrently.
        var templates = _source.Templates;
        var typeList = string.Join(", ", templates.Keys);

        var contract = new FunctionContract
        {
            Name = "Agent",
            Description =
                "Delegate a task to a specialized sub-agent. By default this BLOCKS "
                + "until the sub-agent finishes and returns its final answer as the "
                + "tool result — use it when you need the answer before continuing.\n\n"
                + "Set run_in_background: true to spawn asynchronously instead: the tool "
                + "returns an agent id immediately, you poll progress with CheckAgent, "
                + "and the final result is also delivered back to you as a follow-up "
                + "message. Use background mode for long-running work you want to run "
                + "while you keep working, or to fan out several sub-agents at once.\n\n"
                + "Each sub-agent starts fresh and does NOT see your conversation history, "
                + "so make the prompt self-contained.\n\n"
                + "PREFER CONTINUING AN EXISTING SUB-AGENT: before spawning a NEW sub-agent, "
                + "check whether one you already spawned is still live and already has the "
                + "context for this work — if so, continue it with SendMessage instead of "
                + "spawning a fresh agent for the same or a follow-up task. Only spawn a new "
                + "agent when no suitable live agent exists, or when you deliberately want "
                + "independent/parallel work.\n\n"
                + BuildTemplateCatalog(templates),
            Parameters =
            [
                new FunctionParameterContract
                {
                    Name = "subagent_type",
                    Description =
                        $"Which sub-agent to spawn. One of: {typeList}.",
                    ParameterType = new JsonSchemaObject { Type = new("string") },
                    IsRequired = true,
                },
                new FunctionParameterContract
                {
                    Name = "prompt",
                    Description =
                        "The task or instruction for the sub-agent. Be specific and "
                        + "self-contained; the sub-agent does not share your context.",
                    ParameterType = new JsonSchemaObject { Type = new("string") },
                    IsRequired = true,
                },
                new FunctionParameterContract
                {
                    Name = "description",
                    Description =
                        "Optional short 3-5 word label for this delegation "
                        + "(used for telemetry/UI).",
                    ParameterType = new JsonSchemaObject { Type = new("string") },
                    IsRequired = false,
                },
                new FunctionParameterContract
                {
                    Name = "name",
                    Description =
                        "Recommended: a short, human-readable, unique handle for this "
                        + "sub-agent (e.g. 'auth-reviewer', 'db-migrator') so it is easy to "
                        + "identify in progress/telemetry and to address later via SendMessage. "
                        + "Optional — if omitted, a readable name is auto-derived from the "
                        + "subagent_type.",
                    ParameterType = new JsonSchemaObject { Type = new("string") },
                    IsRequired = false,
                },
                new FunctionParameterContract
                {
                    Name = "model",
                    Description = BuildModelOverrideDescription(_manager.AvailableModelIds),
                    ParameterType = new JsonSchemaObject { Type = new("string") },
                    IsRequired = false,
                },
                new FunctionParameterContract
                {
                    Name = "modelIntelligence",
                    Description =
                        "Optional model-intelligence tier (integer; ascending capability, 0 = cheapest) "
                        + "used to size this sub-agent's model when no explicit 'model' is given. The host "
                        + "resolves it to a concrete model, climbing to the nearest higher configured tier "
                        + "when the requested one is unmapped; omit it to keep the sub-agent's default "
                        + "(parent-inherited) model. An explicit 'model' always wins over this.",
                    ParameterType = new JsonSchemaObject { Type = new("integer") },
                    IsRequired = false,
                },
                new FunctionParameterContract
                {
                    Name = "run_in_background",
                    Description =
                        "When true, return immediately with an agent id instead of "
                        + "blocking for the result. Poll progress with CheckAgent.",
                    ParameterType = new JsonSchemaObject { Type = new("boolean") },
                    IsRequired = false,
                },
                new FunctionParameterContract
                {
                    Name = "add_tools",
                    Description =
                        "Comma-separated list of additional tool names to enable.",
                    ParameterType = new JsonSchemaObject { Type = new("string") },
                    IsRequired = false,
                },
                new FunctionParameterContract
                {
                    Name = "remove_tools",
                    Description =
                        "Comma-separated list of tool names to disable.",
                    ParameterType = new JsonSchemaObject { Type = new("string") },
                    IsRequired = false,
                },
            ],
        };

        return new FunctionDescriptor
        {
            Contract = contract,
            Handler = HandleAgentToolAsync,
            ProviderName = ProviderName,
        };
    }

    private FunctionDescriptor CreateSendMessageDescriptor()
    {
        var contract = new FunctionContract
        {
            Name = "SendMessage",
            Description =
                "Continue an existing sub-agent with a follow-up message — PREFER THIS over "
                + "spawning a new Agent when you are iterating on, correcting, or extending "
                + "work a still-live sub-agent already has the context for. Address it "
                + "by the id returned from Agent, or by the name you gave it when "
                + "spawning. By default BLOCKS until the continued run finishes and "
                + "returns its final answer; set run_in_background: true to return "
                + "immediately and poll with CheckAgent.",
            Parameters =
            [
                new FunctionParameterContract
                {
                    Name = "target",
                    Description =
                        "The sub-agent's id (from Agent) or the name you assigned it.",
                    ParameterType = new JsonSchemaObject { Type = new("string") },
                    IsRequired = true,
                },
                new FunctionParameterContract
                {
                    Name = "prompt",
                    Description = "The follow-up message or instruction to send.",
                    ParameterType = new JsonSchemaObject { Type = new("string") },
                    IsRequired = true,
                },
                new FunctionParameterContract
                {
                    Name = "run_in_background",
                    Description =
                        "When true, return immediately instead of blocking for the "
                        + "result. Poll progress with CheckAgent.",
                    ParameterType = new JsonSchemaObject { Type = new("boolean") },
                    IsRequired = false,
                },
            ],
        };

        return new FunctionDescriptor
        {
            Contract = contract,
            Handler = HandleSendMessageToolAsync,
            ProviderName = ProviderName,
        };
    }

    private FunctionDescriptor CreateCheckAgentDescriptor()
    {
        var contract = new FunctionContract
        {
            Name = "CheckAgent",
            Description =
                "Check the status and recent activity of a background sub-agent (one "
                + "spawned with run_in_background: true). Returns its status, recent "
                + "turns, and final result once completed. Synchronous Agent/SendMessage "
                + "calls already return the result directly and do not need CheckAgent.",
            Parameters =
            [
                new FunctionParameterContract
                {
                    Name = "agent_id",
                    Description =
                        "The id of the sub-agent to check (from Agent or SendMessage).",
                    ParameterType = new JsonSchemaObject { Type = new("string") },
                    IsRequired = true,
                },
            ],
        };

        return new FunctionDescriptor
        {
            Contract = contract,
            Handler = HandleCheckAgentToolAsync,
            ProviderName = ProviderName,
        };
    }

    /// <summary>
    /// Builds the <c>model</c> parameter description for the Agent tool. When the host surfaced the set of
    /// valid model ids (<see cref="SubAgentManager.AvailableModelIds"/>) the description lists them and
    /// spells out that this field is neither a <c>subagent_type</c> nor a capability tier — the two fields
    /// LLMs most often confuse it with — so the parent stops inventing ids or cross-filling from another
    /// argument. With no id list it keeps the generic wording (previous behavior). Pairs with the runtime
    /// <see cref="SubAgentOptions.ModelOverrideValidator"/>, which drops any id not in this set.
    /// </summary>
    private static string BuildModelOverrideDescription(IReadOnlyCollection<string>? availableModelIds)
    {
        // Usually OMIT this — a sub-agent inherits the right model from its template/the parent
        // automatically, which is almost always correct. Only set it to deliberately run this ONE
        // sub-agent on a different model.
        const string lead =
            "Optional model id override for this sub-agent. Usually OMIT this — the sub-agent inherits "
            + "the correct model automatically; set it only to deliberately run this one sub-agent on a "
            + "different model. This is a MODEL ID, not a subagent_type (that is the separate "
            + "'subagent_type' argument) and not a capability tier (use 'modelIntelligence' for that).";

        if (availableModelIds is { Count: > 0 })
        {
            return lead
                + " If set, it MUST be exactly one of: "
                + string.Join(", ", availableModelIds)
                + ". Any other value is ignored and the sub-agent keeps its inherited model.";
        }

        return lead + " Defaults to the template's configured model.";
    }

    /// <summary>
    /// Builds the per-template catalog embedded in the Agent tool description so the
    /// parent LLM can pick the right sub-agent type.
    /// </summary>
    private static string BuildTemplateCatalog(IReadOnlyDictionary<string, SubAgentTemplate> templates)
    {
        var sb = new StringBuilder();
        _ = sb.Append("Available sub-agent types (subagent_type):");

        foreach (var (key, template) in templates)
        {
            var description = string.IsNullOrWhiteSpace(template.Description)
                ? "(no description provided)"
                : template.Description.Trim();

            _ = sb.Append("\n- ").Append(key).Append(": ").Append(description);

            if (!string.IsNullOrWhiteSpace(template.WhenToUse))
            {
                _ = sb.Append("\n  When to use: ").Append(template.WhenToUse.Trim());
            }
        }

        return sb.ToString();
    }

    private async Task<ToolHandlerResult> HandleAgentToolAsync(
        string argsJson,
        ToolCallContext context,
        CancellationToken cancellationToken)
    {
        using var doc = JsonDocument.Parse(argsJson);
        var root = doc.RootElement;

        var prompt = GetOptionalString(root, "prompt")
            ?? throw new ArgumentException("The 'prompt' parameter is required.");
        var subagentType = GetOptionalString(root, "subagent_type")
            ?? throw new ArgumentException(
                "The 'subagent_type' parameter is required.");

        var name = GetOptionalString(root, "name");
        var model = GetOptionalString(root, "model");
        var modelIntelligence = GetOptionalInt(root, "modelIntelligence");
        if (_manager.SpawnModelSelectionResolver?.Invoke(name) is { } authoritativeSelection)
        {
            model = authoritativeSelection.Model;
            modelIntelligence = authoritativeSelection.ModelIntelligence;
        }

        var runInBackground = GetOptionalBool(root, "run_in_background") ?? false;

        // 'description' is intentionally accepted but not read here: it is a short
        // human-facing delegation label exposed for parity with Claude Code's Agent tool.
        // It has no server-side effect, so it is not threaded into SpawnAsync.
        var addTools = ParseCommaSeparated(GetOptionalString(root, "add_tools"));
        var removeTools = ParseCommaSeparated(GetOptionalString(root, "remove_tools"));

        // Self-correcting spawn-name gate (host-supplied, workflow-agnostic here). When the host correlates
        // spawn results by an EXACT name (a workflow controller), a name that matches no ready unit would run
        // and then be silently discarded — the caller must fix the NAME, not the spawn. Surface the correction
        // as a recoverable tool error (mirroring unknown_subagent_type) so the caller re-issues the exact name
        // instead of looping on a discarded duplicate. No gate (ordinary hosts) = pass through unchanged.
        if (_manager.SpawnNameGate?.Invoke(name) is { } rejection)
        {
            return ToolHandlerResult.FromError(rejection, "spawn_name_unmatched");
        }

        try
        {
            var result = await _manager.SpawnAsync(
                subagentType,
                prompt,
                name,
                model,
                runInBackground,
                addTools,
                removeTools,
                cancellationToken,
                modelIntelligence);

            return ToolHandlerResult.FromText(result);
        }
        catch (ArgumentException ex)
        {
            // An unknown or ambiguous subagent_type is a MODEL mistake (a bare/mis-prefixed name, or
            // one that matches several agents), not a host fault. Return the actionable message as a
            // recoverable tool result — listing the valid/suggested names — so the caller re-issues
            // with an exact name instead of silently collapsing to general-purpose.
            return ToolHandlerResult.FromError(ex.Message, "unknown_subagent_type");
        }
        catch (SubAgentExecutionException ex)
        {
            return ToolHandlerResult.FromError(ex.Message, "subagent_failed");
        }
    }

    private async Task<ToolHandlerResult> HandleSendMessageToolAsync(
        string argsJson,
        ToolCallContext context,
        CancellationToken cancellationToken)
    {
        using var doc = JsonDocument.Parse(argsJson);
        var root = doc.RootElement;

        var target = GetOptionalString(root, "target")
            ?? throw new ArgumentException("The 'target' parameter is required.");
        var prompt = GetOptionalString(root, "prompt")
            ?? throw new ArgumentException("The 'prompt' parameter is required.");
        var runInBackground = GetOptionalBool(root, "run_in_background") ?? false;

        try
        {
            var result = await _manager.SendMessageAsync(
                target, prompt, runInBackground, cancellationToken);

            return ToolHandlerResult.FromText(result);
        }
        catch (SubAgentExecutionException ex)
        {
            return ToolHandlerResult.FromError(ex.Message, "subagent_failed");
        }
    }

    private Task<ToolHandlerResult> HandleCheckAgentToolAsync(
        string argsJson,
        ToolCallContext context,
        CancellationToken cancellationToken)
    {
        using var doc = JsonDocument.Parse(argsJson);
        var root = doc.RootElement;

        var agentId = GetOptionalString(root, "agent_id")
            ?? throw new ArgumentException(
                "The 'agent_id' parameter is required.");

        // An unknown/stale/mistyped agent id is a MODEL mistake (e.g. polling with the wrong id), not a host
        // fault — return a helpful tool result listing the valid ids rather than throwing, which would surface
        // as an "Error executing tool call" and derail the loop.
        if (!_manager.TryPeek(agentId, out var status))
        {
            var known = _manager.KnownAgentIds();
            var hint = known.Count > 0
                ? $"No sub-agent with id '{agentId}'. Poll one of the ids the Agent tool returned: "
                    + $"{string.Join(", ", known)}."
                : $"No sub-agent with id '{agentId}'. No sub-agents are currently tracked — a synchronous Agent "
                    + "call returns its result inline (CheckAgent is unnecessary), and any background agents have completed.";
            return Task.FromResult<ToolHandlerResult>(ToolHandlerResult.FromError(hint, "unknown_agent"));
        }

        return Task.FromResult<ToolHandlerResult>(ToolHandlerResult.FromText(status));
    }

    private static string? GetOptionalString(
        JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var prop)
            && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;
    }

    private static bool? GetOptionalBool(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var prop))
        {
            return null;
        }

        return prop.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            // Some models emit booleans as strings ("true"/"false").
            JsonValueKind.String when bool.TryParse(prop.GetString(), out var parsed) => parsed,
            _ => null,
        };
    }

    private static int? GetOptionalInt(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var prop))
        {
            return null;
        }

        return prop.ValueKind switch
        {
            JsonValueKind.Number when prop.TryGetInt32(out var number) => number,
            // Some models emit integers as strings ("2").
            JsonValueKind.String when int.TryParse(prop.GetString(), out var parsed) => parsed,
            _ => null,
        };
    }

    private static string[]? ParseCommaSeparated(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? null
            : [.. value
            .Split(',', StringSplitOptions.RemoveEmptyEntries
                | StringSplitOptions.TrimEntries)];
    }
}
