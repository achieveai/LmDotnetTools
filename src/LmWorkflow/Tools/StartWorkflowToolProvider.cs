using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmWorkflow.Ingest;
using AchieveAi.LmDotnetTools.LmWorkflow.Model;

namespace AchieveAi.LmDotnetTools.LmWorkflow.Tools;

/// <summary>
///     Exposes the agent-facing workflow launch tools over a <see cref="WorkflowManager"/>:
///     <c>StartWorkflow</c> (launch a pre-authored definition, sync or async), <c>CheckWorkflow</c>
///     (non-blocking status), and <c>WaitWorkflow</c> (block until terminal or timeout). These are the ONLY
///     workflow tools a normal agent should ever see — the authoring/mutation tools
///     (<c>GetWorkflow</c>/<c>SetCurrentNode</c>/<c>SetState</c>/<c>SetNotes</c>, and never
///     <c>SetWorkflow</c>) live exclusively inside the controller loop the manager spins up.
/// </summary>
public sealed class StartWorkflowToolProvider : IFunctionProvider
{
    /// <summary>The launch tool name.</summary>
    public const string StartWorkflowToolName = "StartWorkflow";

    /// <summary>The non-blocking status tool name.</summary>
    public const string CheckWorkflowToolName = "CheckWorkflow";

    /// <summary>The blocking wait tool name.</summary>
    public const string WaitWorkflowToolName = "WaitWorkflow";

    /// <summary>Every tool name this provider exposes; a host keeps these out of sub-agent inheritance.</summary>
    public static readonly IReadOnlyList<string> ToolNames =
        [StartWorkflowToolName, CheckWorkflowToolName, WaitWorkflowToolName];

    private static readonly JsonSerializerOptions ResultJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly WorkflowManager _manager;

    /// <summary>Creates the provider over <paramref name="manager"/>.</summary>
    public StartWorkflowToolProvider(WorkflowManager manager)
    {
        ArgumentNullException.ThrowIfNull(manager);
        _manager = manager;
    }

    /// <inheritdoc />
    public string ProviderName => "StartWorkflowTools";

    /// <summary>Low priority (high number) so parent tools take precedence on a name clash.</summary>
    public int Priority => 100;

    /// <inheritdoc />
    public IEnumerable<FunctionDescriptor> GetFunctions()
    {
        yield return CreateStartWorkflowDescriptor();
        yield return CreateCheckWorkflowDescriptor();
        yield return CreateWaitWorkflowDescriptor();
    }

    private FunctionDescriptor CreateStartWorkflowDescriptor()
    {
        var contract = new FunctionContract
        {
            Name = StartWorkflowToolName,
            Description =
                "Launch a fully pre-authored workflow definition as a bounded, delegated unit of work, run "
                + "by an isolated controller agent. By default (mode: sync) this BLOCKS until the workflow "
                + "reaches a terminal state and returns the terminal result. Set mode: async to return "
                + "immediately with {workflowId, status:\"started\"}; you'll receive a proactive notification "
                + "when it finishes, and can poll with CheckWorkflow or block with WaitWorkflow. You supply "
                + "the complete workflow graph — you cannot author or mutate it once running.",
            Parameters =
            [
                new FunctionParameterContract
                {
                    Name = "workflowId",
                    Description =
                        "An opaque, non-user-identifying handle for this workflow. Must be unique; a value "
                        + "already used is rejected.",
                    ParameterType = new JsonSchemaObject { Type = new("string") },
                    IsRequired = true,
                },
                new FunctionParameterContract
                {
                    Name = "workflow",
                    Description =
                        "The full, pre-authored workflow definition object (objective + a graph of nodes: "
                        + "one start, >=1 terminal, and the rest).",
                    ParameterType = new JsonSchemaObject { Type = new("object") },
                    IsRequired = true,
                },
                new FunctionParameterContract
                {
                    Name = "mode",
                    Description = "Either \"sync\" (default, blocks for the terminal result) or \"async\".",
                    ParameterType = new JsonSchemaObject { Type = new("string"), Enum = ["sync", "async"] },
                    IsRequired = false,
                },
            ],
        };

        return new FunctionDescriptor
        {
            Contract = contract,
            Handler = HandleStartWorkflowAsync,
            ProviderName = ProviderName,
        };
    }

    private FunctionDescriptor CreateCheckWorkflowDescriptor()
    {
        var contract = new FunctionContract
        {
            Name = CheckWorkflowToolName,
            Description =
                "Check the current status and state snapshot (current node, outputs, notes, and — once "
                + "terminal — the final result) of a workflow started with StartWorkflow, WITHOUT blocking. "
                + "Works in either mode and remains available after completion.",
            Parameters =
            [
                new FunctionParameterContract
                {
                    Name = "workflowId",
                    Description = "The workflowId returned/used when the workflow was started.",
                    ParameterType = new JsonSchemaObject { Type = new("string") },
                    IsRequired = true,
                },
            ],
        };

        return new FunctionDescriptor
        {
            Contract = contract,
            Handler = HandleCheckWorkflowAsync,
            ProviderName = ProviderName,
        };
    }

    private FunctionDescriptor CreateWaitWorkflowDescriptor()
    {
        var contract = new FunctionContract
        {
            Name = WaitWorkflowToolName,
            Description =
                "Block until a workflow started with StartWorkflow reaches a terminal state, or until the "
                + "optional timeout elapses, then return the final result (or a timeout signal). A timeout is "
                + "non-destructive — the workflow keeps running and can be waited on again. NOTE: unlike the "
                + "Agent tool's turn-bounded wait, this timeout is open-ended, so a long wait suspends this "
                + "turn's tool dispatch for its full duration; prefer a bounded timeout, or async + "
                + "CheckWorkflow, for long-running workflows.",
            Parameters =
            [
                new FunctionParameterContract
                {
                    Name = "workflowId",
                    Description = "The workflowId returned/used when the workflow was started.",
                    ParameterType = new JsonSchemaObject { Type = new("string") },
                    IsRequired = true,
                },
                new FunctionParameterContract
                {
                    Name = "timeout",
                    Description =
                        "Optional maximum seconds to block before returning a timeout signal. Omit to wait "
                        + "for completion (bounded only by this turn's cancellation).",
                    ParameterType = new JsonSchemaObject { Type = new("number") },
                    IsRequired = false,
                },
            ],
        };

        return new FunctionDescriptor
        {
            Contract = contract,
            Handler = HandleWaitWorkflowAsync,
            ProviderName = ProviderName,
        };
    }

    private async Task<ToolHandlerResult> HandleStartWorkflowAsync(
        string argsJson,
        ToolCallContext context,
        CancellationToken cancellationToken
    )
    {
        if (!TryParseArgs(argsJson, out var doc, out var argsError))
        {
            return argsError!;
        }

        using (doc)
        {
            var root = doc.RootElement;

            var workflowId = GetOptionalString(root, "workflowId");
            if (string.IsNullOrWhiteSpace(workflowId))
            {
                return ToolHandlerResult.FromError("The 'workflowId' parameter is required.", "invalid_args");
            }

            if (
                !root.TryGetProperty("workflow", out var workflowElement)
                || workflowElement.ValueKind != JsonValueKind.Object
            )
            {
                return ToolHandlerResult.FromError(
                    "The 'workflow' object parameter is required.",
                    "invalid_args"
                );
            }

            WorkflowDefinition definition;
            try
            {
                // Strict deserialize so a misspelled/invented field is rejected by name rather than silently
                // dropped — matching SetWorkflow's authoring path.
                definition = WorkflowJson.DeserializeStrict(workflowElement.GetRawText());
            }
            catch (JsonException ex)
            {
                return ToolHandlerResult.FromError(
                    $"The workflow definition is not valid JSON: {ex.Message}",
                    "invalid_workflow"
                );
            }

            var mode = ParseMode(GetOptionalString(root, "mode"));

            try
            {
                var result = await _manager
                    .StartAsync(workflowId, definition, mode, cancellationToken, context.ToolCallId)
                    .ConfigureAwait(false);
                return ToolHandlerResult.FromText(Serialize(result));
            }
            catch (WorkflowValidationException ex)
            {
                return ToolHandlerResult.FromError(
                    "The workflow definition is invalid: " + string.Join("; ", ex.Errors),
                    "invalid_workflow"
                );
            }
            catch (DuplicateWorkflowException ex)
            {
                return ToolHandlerResult.FromError(ex.Message, "duplicate_workflow");
            }
            catch (WorkflowCapacityException ex)
            {
                return ToolHandlerResult.FromError(ex.Message, "workflow_capacity");
            }
        }
    }

    private Task<ToolHandlerResult> HandleCheckWorkflowAsync(
        string argsJson,
        ToolCallContext context,
        CancellationToken cancellationToken
    )
    {
        if (!TryParseArgs(argsJson, out var doc, out var argsError))
        {
            return Task.FromResult<ToolHandlerResult>(argsError!);
        }

        using (doc)
        {
            var workflowId = GetOptionalString(doc.RootElement, "workflowId");
            if (string.IsNullOrWhiteSpace(workflowId))
            {
                return Task.FromResult<ToolHandlerResult>(
                    ToolHandlerResult.FromError("The 'workflowId' parameter is required.", "invalid_args")
                );
            }

            try
            {
                return Task.FromResult<ToolHandlerResult>(
                    ToolHandlerResult.FromText(Serialize(_manager.Check(workflowId)))
                );
            }
            catch (UnknownWorkflowException ex)
            {
                return Task.FromResult<ToolHandlerResult>(
                    ToolHandlerResult.FromError(ex.Message, "unknown_workflow")
                );
            }
        }
    }

    private async Task<ToolHandlerResult> HandleWaitWorkflowAsync(
        string argsJson,
        ToolCallContext context,
        CancellationToken cancellationToken
    )
    {
        if (!TryParseArgs(argsJson, out var doc, out var argsError))
        {
            return argsError!;
        }

        using (doc)
        {
            var root = doc.RootElement;

            var workflowId = GetOptionalString(root, "workflowId");
            if (string.IsNullOrWhiteSpace(workflowId))
            {
                return ToolHandlerResult.FromError("The 'workflowId' parameter is required.", "invalid_args");
            }

            // Distinguish an omitted timeout (→ wait until completion) from a present-but-invalid one
            // (negative / non-numeric / NaN / infinity), which must be rejected rather than silently
            // collapsing to an unbounded wait.
            if (!TryReadTimeout(root, "timeout", out var timeout, out var timeoutError))
            {
                return ToolHandlerResult.FromError(timeoutError!, "invalid_args");
            }

            try
            {
                var result = await _manager
                    .WaitAsync(workflowId, timeout, cancellationToken)
                    .ConfigureAwait(false);
                return ToolHandlerResult.FromText(Serialize(result));
            }
            catch (UnknownWorkflowException ex)
            {
                return ToolHandlerResult.FromError(ex.Message, "unknown_workflow");
            }
        }
    }

    private static string Serialize(WorkflowRunResult result) => JsonSerializer.Serialize(result, ResultJson);

    private static WorkflowStartMode ParseMode(string? mode) =>
        string.Equals(mode, "async", StringComparison.OrdinalIgnoreCase)
            ? WorkflowStartMode.Async
            : WorkflowStartMode.Sync;

    private static string? GetOptionalString(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;

    /// <summary>
    ///     Parses the handler args, returning a structured <c>invalid_args</c> error instead of letting a
    ///     malformed-JSON <see cref="JsonException"/> escape to the executor as a generic tool failure.
    /// </summary>
    private static bool TryParseArgs(string argsJson, out JsonDocument doc, out ToolHandlerResult? error)
    {
        try
        {
            doc = JsonDocument.Parse(argsJson);
            error = null;
            return true;
        }
        catch (JsonException ex)
        {
            doc = null!;
            error = ToolHandlerResult.FromError(
                $"Tool arguments are not valid JSON: {ex.Message}",
                "invalid_args"
            );
            return false;
        }
    }

    /// <summary>
    ///     Reads an optional <c>timeout</c> (seconds). An OMITTED property yields <c>null</c> (wait until
    ///     completion) and returns <c>true</c>. A PRESENT-but-invalid value (negative, NaN/infinity, or a
    ///     non-numeric string) returns <c>false</c> with an error, so an invalid input is rejected rather than
    ///     silently collapsing to an unbounded wait. Valid values are clamped to <c>Task.WaitAsync</c>'s range.
    /// </summary>
    private static bool TryReadTimeout(
        JsonElement root,
        string propertyName,
        out TimeSpan? timeout,
        out string? error
    )
    {
        timeout = null;
        error = null;

        if (!root.TryGetProperty(propertyName, out var prop))
        {
            return true; // omitted → no timeout
        }

        double? seconds = prop.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.Number when prop.TryGetDouble(out var value) => value,
            // Some models emit numbers as strings.
            JsonValueKind.String when double.TryParse(prop.GetString(), out var value) => value,
            _ => double.NaN, // present but not a usable number
        };

        if (seconds is null)
        {
            return true; // explicit null → no timeout
        }

        if (double.IsNaN(seconds.Value) || double.IsInfinity(seconds.Value) || seconds.Value < 0)
        {
            error = "The 'timeout' parameter must be a non-negative number of seconds.";
            return false;
        }

        // Clamp to the range Task.WaitAsync accepts (~24.8 days), which is effectively "wait until completion".
        var ms = Math.Min(seconds.Value * 1000d, int.MaxValue);
        timeout = TimeSpan.FromMilliseconds(ms);
        return true;
    }
}
