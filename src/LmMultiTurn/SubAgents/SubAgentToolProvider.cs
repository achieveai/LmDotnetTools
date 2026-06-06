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
    private readonly IReadOnlyDictionary<string, SubAgentTemplate> _templates;

    public SubAgentToolProvider(
        SubAgentManager manager,
        IReadOnlyDictionary<string, SubAgentTemplate> templates)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(templates);
        _manager = manager;
        _templates = templates;
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
        var typeList = string.Join(", ", _templates.Keys);

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
                + "so make the prompt self-contained. Use SendMessage to continue an "
                + "existing sub-agent with a follow-up.\n\n"
                + BuildTemplateCatalog(),
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
                        "Optional handle to address this sub-agent later via "
                        + "SendMessage instead of using its generated id.",
                    ParameterType = new JsonSchemaObject { Type = new("string") },
                    IsRequired = false,
                },
                new FunctionParameterContract
                {
                    Name = "model",
                    Description =
                        "Optional model id override for this sub-agent "
                        + "(defaults to the template's configured model).",
                    ParameterType = new JsonSchemaObject { Type = new("string") },
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
                "Continue an existing sub-agent with a follow-up message. Address it "
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
    /// Builds the per-template catalog embedded in the Agent tool description so the
    /// parent LLM can pick the right sub-agent type.
    /// </summary>
    private string BuildTemplateCatalog()
    {
        var sb = new StringBuilder();
        _ = sb.Append("Available sub-agent types (subagent_type):");

        foreach (var (key, template) in _templates)
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
        var runInBackground = GetOptionalBool(root, "run_in_background") ?? false;

        // 'description' is intentionally accepted but not read here: it is a short
        // human-facing delegation label exposed for parity with Claude Code's Agent tool.
        // It has no server-side effect, so it is not threaded into SpawnAsync.
        var addTools = ParseCommaSeparated(GetOptionalString(root, "add_tools"));
        var removeTools = ParseCommaSeparated(GetOptionalString(root, "remove_tools"));

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
                cancellationToken);

            return ToolHandlerResult.FromText(result);
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

        return Task.FromResult<ToolHandlerResult>(
            ToolHandlerResult.FromText(_manager.Peek(agentId)));
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

    private static string[]? ParseCommaSeparated(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? null
            : [.. value
            .Split(',', StringSplitOptions.RemoveEmptyEntries
                | StringSplitOptions.TrimEntries)];
    }
}
