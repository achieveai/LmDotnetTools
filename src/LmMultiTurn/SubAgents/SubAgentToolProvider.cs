using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmCore.Models;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;

/// <summary>
/// Provides Agent and CheckAgent tool definitions for sub-agent orchestration.
/// Registered as an IFunctionProvider so these tools are included in the
/// parent agent's function registry alongside all other tools.
/// </summary>
public class SubAgentToolProvider : IFunctionProvider
{
    private readonly SubAgentManager _manager;
    private readonly IReadOnlyList<string> _templateNames;

    public SubAgentToolProvider(
        SubAgentManager manager,
        IReadOnlyList<string> templateNames)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(templateNames);
        _manager = manager;
        _templateNames = templateNames;
    }

    public string ProviderName => "SubAgentTools";

    /// <summary>
    /// Low priority (high number) so parent tools take precedence.
    /// </summary>
    public int Priority => 100;

    public IEnumerable<FunctionDescriptor> GetFunctions()
    {
        yield return CreateAgentDescriptor();
        yield return CreateCheckAgentDescriptor();
    }

    private FunctionDescriptor CreateAgentDescriptor()
    {
        var templateList = string.Join(", ", _templateNames);

        var contract = new FunctionContract
        {
            Name = "Agent",
            Description =
                "Spawn a new sub-agent from a named template to work on a task, " +
                "or send a new message to an existing sub-agent by ID. " +
                "Returns immediately with the agent ID.",
            Parameters =
            [
                new FunctionParameterContract
                {
                    Name = "template_name",
                    Description =
                        "Name of the sub-agent template. Available: " +
                        $"{templateList}. Required for new agents.",
                    ParameterType = new JsonSchemaObject
                    {
                        Type = new("string"),
                    },
                    IsRequired = false,
                },
                new FunctionParameterContract
                {
                    Name = "task",
                    Description =
                        "The task description or message to send to " +
                        "the sub-agent.",
                    ParameterType = new JsonSchemaObject
                    {
                        Type = new("string"),
                    },
                    IsRequired = true,
                },
                new FunctionParameterContract
                {
                    Name = "agent_id",
                    Description =
                        "If provided, resumes or sends to an existing " +
                        "sub-agent instead of spawning new.",
                    ParameterType = new JsonSchemaObject
                    {
                        Type = new("string"),
                    },
                    IsRequired = false,
                },
                new FunctionParameterContract
                {
                    Name = "add_tools",
                    Description =
                        "Comma-separated list of additional tool names " +
                        "to enable.",
                    ParameterType = new JsonSchemaObject
                    {
                        Type = new("string"),
                    },
                    IsRequired = false,
                },
                new FunctionParameterContract
                {
                    Name = "remove_tools",
                    Description =
                        "Comma-separated list of tool names to disable.",
                    ParameterType = new JsonSchemaObject
                    {
                        Type = new("string"),
                    },
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

    private FunctionDescriptor CreateCheckAgentDescriptor()
    {
        var contract = new FunctionContract
        {
            Name = "CheckAgent",
            Description =
                "Check the status and recent activity of a sub-agent.",
            Parameters =
            [
                new FunctionParameterContract
                {
                    Name = "agent_id",
                    Description = "The ID of the sub-agent to check.",
                    ParameterType = new JsonSchemaObject
                    {
                        Type = new("string"),
                    },
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

    private async Task<ToolHandlerResult> HandleAgentToolAsync(
        string argsJson,
        ToolCallContext context,
        CancellationToken cancellationToken)
    {
        using var doc = JsonDocument.Parse(argsJson);
        var root = doc.RootElement;

        var agentId = GetOptionalString(root, "agent_id");
        var task = GetOptionalString(root, "task")
            ?? throw new ArgumentException(
                "The 'task' parameter is required.");

        if (agentId != null)
        {
            // Resume or send to existing agent
            return new ToolHandlerResult.Resolved(new ToolCallResult(null, await _manager.ResumeAsync(agentId, task)));
        }

        // Spawn new agent
        var templateName = GetOptionalString(root, "template_name")
            ?? throw new ArgumentException(
                "The 'template_name' parameter is required " +
                "when spawning a new agent.");

        var addTools = ParseCommaSeparated(
            GetOptionalString(root, "add_tools"));
        var removeTools = ParseCommaSeparated(
            GetOptionalString(root, "remove_tools"));

        return new ToolHandlerResult.Resolved(
            new ToolCallResult(null, await _manager.SpawnAsync(templateName, task, addTools, removeTools)));
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
            new ToolHandlerResult.Resolved(new ToolCallResult(null, _manager.Peek(agentId))));
    }

    private static string? GetOptionalString(
        JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var prop)
            && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;
    }

    private static string[]? ParseCommaSeparated(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value
            .Split(',', StringSplitOptions.RemoveEmptyEntries
                | StringSplitOptions.TrimEntries)
            .ToArray();
    }
}
