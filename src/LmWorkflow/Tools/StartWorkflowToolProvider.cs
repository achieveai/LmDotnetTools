using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmMultiTurn.Collaboration;
using AchieveAi.LmDotnetTools.LmWorkflow.Collaboration;
using AchieveAi.LmDotnetTools.LmWorkflow.Ingest;
using AchieveAi.LmDotnetTools.LmWorkflow.Model;

namespace AchieveAi.LmDotnetTools.LmWorkflow.Tools;

/// <summary>
///     Exposes the agent-facing workflow launch tools over a <see cref="WorkflowManager"/>:
///     <c>StartWorkflowAgent</c> (delegate a bounded unit of work to an isolated agent running a
///     pre-authored workflow, sync or async), <c>GetWorkflows</c> (list the runs started here),
///     <c>CheckWorkflow</c> (non-blocking status), and <c>WaitWorkflow</c> (block until terminal or
///     timeout). These are the ONLY workflow tools a normal agent should ever see — the
///     authoring/mutation tools
///     (<c>GetWorkflow</c>/<c>SetCurrentNode</c>/<c>SetState</c>/<c>SetNotes</c>, and never
///     <c>SetWorkflow</c>) live exclusively inside the controller loop the manager spins up.
/// </summary>
public sealed class StartWorkflowToolProvider : IFunctionProvider
{
    /// <summary>The launch tool name.</summary>
    public const string StartWorkflowToolName = "StartWorkflowAgent";

    /// <summary>The run-discovery tool name.</summary>
    public const string GetWorkflowsToolName = "GetWorkflows";

    /// <summary>The non-blocking status tool name.</summary>
    public const string CheckWorkflowToolName = "CheckWorkflow";

    /// <summary>The blocking wait tool name.</summary>
    public const string WaitWorkflowToolName = "WaitWorkflow";

    /// <summary>Every tool name this provider exposes; a host keeps these out of sub-agent inheritance.</summary>
    public static readonly IReadOnlyList<string> ToolNames =
        [StartWorkflowToolName, GetWorkflowsToolName, CheckWorkflowToolName, WaitWorkflowToolName];

    /// <summary>
    ///     How many workflow ids an unknown-id error may list. Bounded so a long-lived conversation with
    ///     many runs cannot turn one mistyped id into a huge tool result; when the cap bites, the text
    ///     SAYS the list is partial rather than letting the model conclude its id does not exist.
    /// </summary>
    internal const int MaxListedWorkflowIds = 20;

    private static readonly JsonSerializerOptions ResultJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly WorkflowManager _manager;
    private readonly Func<string, string?>? _validatePreferredProvider;
    private readonly Func<AgentCollaborationSetup?>? _callerCollaboration;

    /// <summary>Creates the provider over <paramref name="manager"/>.</summary>
    /// <param name="manager">The workflow manager this tool family drives.</param>
    /// <param name="validatePreferredProvider">
    ///     Optional validator for a caller-supplied <c>provider</c> argument: returns an error string when the
    ///     provider is unknown/unavailable, or null when it is acceptable. Keeps this provider-agnostic — the
    ///     host injects a <c>ProviderRegistry</c>-backed check. Null skips validation (any non-null provider is
    ///     forwarded and the manager's profile factory decides).
    /// </param>
    /// <param name="callerCollaboration">
    ///     Resolves the LAUNCHING agent's collaboration handle, so the run's controller is admitted as a node
    ///     under whoever actually called the tool. Resolved per call rather than captured at construction
    ///     because the same provider instance is registered on a loop whose collaboration is bound later.
    ///     Null (or a null result) keeps the pre-collaboration behaviour.
    /// </param>
    public StartWorkflowToolProvider(
        WorkflowManager manager,
        Func<string, string?>? validatePreferredProvider = null,
        Func<AgentCollaborationSetup?>? callerCollaboration = null
    )
    {
        ArgumentNullException.ThrowIfNull(manager);
        _manager = manager;
        _validatePreferredProvider = validatePreferredProvider;
        _callerCollaboration = callerCollaboration;
    }

    /// <inheritdoc />
    public string ProviderName => "StartWorkflowTools";

    /// <summary>Low priority (high number) so parent tools take precedence on a name clash.</summary>
    public int Priority => 100;

    /// <inheritdoc />
    public IEnumerable<FunctionDescriptor> GetFunctions()
    {
        yield return CreateStartWorkflowDescriptor();
        yield return CreateGetWorkflowsDescriptor();
        yield return CreateCheckWorkflowDescriptor();
        yield return CreateWaitWorkflowDescriptor();
    }

    private FunctionDescriptor CreateStartWorkflowDescriptor()
    {
        var contract = new FunctionContract
        {
            Name = StartWorkflowToolName,
            Description =
                "Run a COMPLETE, already-authored workflow graph as an isolated background job. This is the "
                + "hand-off step you take AFTER the graph is assembled and validated: if you have the "
                + "authoring tools (SetWorkflow/GetWorkflow/AddNode/RemoveNode/SetCurrentNode/SetState), "
                + "build and check the graph with those first, then pass the finished object here to run it "
                + "independently of this conversation. Prefer mode: async — it returns immediately with "
                + "{workflowId, status:\"started\"}, you get a proactive notification when it finishes, and "
                + "you can poll anytime with CheckWorkflow or block for the result with WaitWorkflow. A returned "
                + "status of 'started' is SUCCESS: do NOT launch a second/replacement workflow for the same "
                + "objective while it is active. Keep the workflowId and use CheckWorkflow/WaitWorkflow until it "
                + "is terminal; only start another workflow when the first failed terminally and a genuinely "
                + "different graph is required. "
                + "(mode: sync instead BLOCKS this turn until the workflow reaches a terminal state and "
                + "returns its result.) It runs on its own controller loop, so pass the graph complete; to "
                + "change it afterward, start a new run with an updated graph.",
            Parameters =
            [
                new FunctionParameterContract
                {
                    Name = "workflowId",
                    Description =
                        "An opaque, non-user-identifying handle for this workflow. Must be unique; a value "
                        + "already used is rejected. It is the id you pass to GetWorkflows/CheckWorkflow/"
                        + "WaitWorkflow to follow this StartWorkflowAgent run — a workflow id, not an "
                        + "agent_id, and unrelated to the ids the Agent tool hands out.",
                    ParameterType = new JsonSchemaObject { Type = new("string") },
                    IsRequired = true,
                },
                new FunctionParameterContract
                {
                    Name = "workflow",
                    Description =
                        "The complete workflow to run, in the flat step DSL: an 'objective' plus a list of "
                        + "'steps', each with an 'id', a 'kind' (start/agent/parallel/branch/end), and its "
                        + "kind-specific fields. Author and validate it before starting; follow the provided "
                        + "schema. Put concurrency in the GRAPH: when the work is several independent checks, "
                        + "model them as ONE 'parallel' step with an 'agents' lane per check — never as one "
                        + "'agent' step whose prompt tells the sub-agent to dispatch/spawn/delegate to other "
                        + "agents (a step's sub-agent cannot spawn sub-agents, so that collapses to a single "
                        + "agent doing shallow work). Gather shared context once and pass it to each lane via "
                        + "saveAs/{{state.<saveAs>}}.",
                    ParameterType = SimpleWorkflowSchema.Workflow(),
                    IsRequired = true,
                },
                new FunctionParameterContract
                {
                    Name = "mode",
                    Description = "Either \"sync\" (default, blocks for the terminal result) or \"async\".",
                    ParameterType = new JsonSchemaObject { Type = new("string"), Enum = ["sync", "async"] },
                    IsRequired = false,
                },
                new FunctionParameterContract
                {
                    Name = "provider",
                    Description =
                        "Optional preferred provider id to run this workflow's controller AND its delegate "
                        + "sub-agents on. Omit to use this conversation's provider.",
                    ParameterType = new JsonSchemaObject { Type = new("string") },
                    IsRequired = false,
                },
                new FunctionParameterContract
                {
                    Name = "model",
                    Description =
                        "Optional model id for the workflow controller loop. Omit to use the configured "
                        + "controller model.",
                    ParameterType = new JsonSchemaObject { Type = new("string") },
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

    /// <summary>
    ///     Lists the runs started through this manager. Exists for the recovery case Check/Wait cannot
    ///     serve: the agent no longer has the workflowId (a restart, a context compaction, a relayed
    ///     hand-off), and without a way to rediscover it the only "progress" left is launching a
    ///     duplicate run of work that is already in flight.
    /// </summary>
    private FunctionDescriptor CreateGetWorkflowsDescriptor()
    {
        var contract = new FunctionContract
        {
            Name = GetWorkflowsToolName,
            Description =
                "List the workflows started with StartWorkflowAgent in this conversation, with each one's "
                + "workflowId, objective, status, and current node. Use it when you no longer have a "
                + "workflowId — to recover it and resume with CheckWorkflow/WaitWorkflow — and BEFORE "
                + "starting a workflow for an objective that may already be running, so you do not launch a "
                + "duplicate. Returns immediately; the ids listed are workflow ids, never agent ids.",
            Parameters = [],
        };

        return new FunctionDescriptor
        {
            Contract = contract,
            Handler = HandleGetWorkflowsAsync,
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
                + "terminal — the final result) of a workflow started with StartWorkflowAgent, WITHOUT "
                + "blocking. Works in either mode and remains available after completion.",
            Parameters =
            [
                new FunctionParameterContract
                {
                    Name = "workflowId",
                    Description =
                        "The workflowId you supplied to StartWorkflowAgent (GetWorkflows lists them if you "
                        + "lost it). This is a workflow id, not an agent_id — ids from the Agent tool do not "
                        + "resolve here.",
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
                "Block until a workflow started with StartWorkflowAgent reaches a terminal state, or until "
                + "the optional timeout elapses, then return the final result (or a timeout signal). A timeout is "
                + "non-destructive — the workflow keeps running and can be waited on again. NOTE: unlike the "
                + "Agent tool's turn-bounded wait, this timeout is open-ended, so a long wait suspends this "
                + "turn's tool dispatch for its full duration; prefer a bounded timeout, or async + "
                + "CheckWorkflow, for long-running workflows. A returned status of \"timeout\" or \"running\" "
                + "means the workflow has NOT finished yet — call WaitWorkflow or CheckWorkflow again to "
                + "observe completion.",
            Parameters =
            [
                new FunctionParameterContract
                {
                    Name = "workflowId",
                    Description =
                        "The workflowId you supplied to StartWorkflowAgent (GetWorkflows lists them if you "
                        + "lost it). This is a workflow id, not an agent_id — ids from the Agent tool do not "
                        + "resolve here.",
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
            return argsError;
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
                // The model authors in the flat SimpleWorkflow DSL (advertised on the tool schema); a legacy
                // internal-shaped {"nodes":[...]} definition is still accepted (FromToolArgument).
                definition = SimpleWorkflowTranslator.FromToolArgument(workflowElement);
            }
            catch (WorkflowValidationException ex)
            {
                return ToolHandlerResult.FromError(
                    "The workflow definition is invalid: " + string.Join("; ", ex.Errors),
                    "invalid_workflow"
                );
            }
            catch (JsonException ex)
            {
                return ToolHandlerResult.FromError(
                    $"The workflow definition is not valid JSON: {ex.Message}",
                    "invalid_workflow"
                );
            }

            var mode = ParseMode(GetOptionalString(root, "mode"));

            var provider = GetOptionalString(root, "provider");
            var model = GetOptionalString(root, "model");

            // Validate a caller-supplied provider before starting (keeps a bad id from silently falling back).
            if (!string.IsNullOrWhiteSpace(provider) && _validatePreferredProvider is not null)
            {
                var providerError = _validatePreferredProvider(provider);
                if (!string.IsNullOrEmpty(providerError))
                {
                    return ToolHandlerResult.FromError(providerError, "invalid_provider");
                }
            }

            try
            {
                var result = await _manager
                    .StartAsync(
                        workflowId,
                        definition,
                        mode,
                        cancellationToken,
                        context.ToolCallId,
                        preferredProvider: provider,
                        preferredModel: model,
                        callerCollaboration: _callerCollaboration?.Invoke()
                    )
                    .ConfigureAwait(false);
                return ToolHandlerResult.FromText(Serialize(result));
            }
            catch (WorkflowCollaborationException ex)
            {
                // A refused admission (nested launch, agent cap, directory rejection) is a normal, recoverable
                // tool outcome carrying the collaboration's own stable code — never a thrown tool failure.
                return ToolHandlerResult.FromError(ex.Message, ex.FailureCode);
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

    private Task<ToolHandlerResult> HandleGetWorkflowsAsync(
        string argsJson,
        ToolCallContext context,
        CancellationToken cancellationToken
    )
    {
        // Sorted so repeated calls read identically instead of shuffling with dictionary order; no cap,
        // because this IS the listing (the bounded hint below is the error-path affordance).
        var workflows = _manager
            .ListRuns()
            .OrderBy(r => r.WorkflowId, StringComparer.Ordinal)
            .Select(r => new
            {
                r.WorkflowId,
                r.Objective,
                r.Status,
                r.CurrentNodeId,
                r.StartedUtc,
                r.LastActivityUtc,
            });

        return Task.FromResult<ToolHandlerResult>(
            ToolHandlerResult.FromText(JsonSerializer.Serialize(new { Workflows = workflows }, ResultJson))
        );
    }

    private Task<ToolHandlerResult> HandleCheckWorkflowAsync(
        string argsJson,
        ToolCallContext context,
        CancellationToken cancellationToken
    )
    {
        if (!TryParseArgs(argsJson, out var doc, out var argsError))
        {
            return Task.FromResult<ToolHandlerResult>(argsError);
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
                    ToolHandlerResult.FromError(DescribeUnknownWorkflow(ex), "unknown_workflow")
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
            return argsError;
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
                return ToolHandlerResult.FromError(DescribeUnknownWorkflow(ex), "unknown_workflow");
            }
        }
    }

    /// <summary>
    ///     Turns "unknown workflow" into a recoverable instruction: the ids that WOULD have worked, plus
    ///     the namespace mistake worth naming (an agent_id passed where a workflowId belongs).
    /// </summary>
    /// <remarks>
    ///     Sorted ordinally so repeated failures read identically, and capped at
    ///     <see cref="MaxListedWorkflowIds"/> with an explicit "showing N of M" — a silently truncated
    ///     list would invite the model to conclude its run does not exist and start a duplicate.
    /// </remarks>
    private string DescribeUnknownWorkflow(UnknownWorkflowException ex)
    {
        var ids = _manager.ListRuns()
            .Select(r => r.WorkflowId)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();

        if (ids.Length == 0)
        {
            return $"{ex.Message} No workflows have been started in this conversation — start one with "
                + "StartWorkflowAgent. Note that a workflowId is not an agent_id: ids from the Agent tool "
                + "do not resolve here.";
        }

        var suffix = ids.Length > MaxListedWorkflowIds
            ? $" (showing {MaxListedWorkflowIds} of {ids.Length})"
            : string.Empty;

        return $"{ex.Message} Use one of the workflow ids you supplied to StartWorkflowAgent: "
            + $"{string.Join(", ", ids.Take(MaxListedWorkflowIds))}{suffix}. GetWorkflows lists them all. "
            + "Note that a workflowId is not an agent_id: ids from the Agent tool do not resolve here.";
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
    private static bool TryParseArgs(
        string argsJson,
        [NotNullWhen(true)] out JsonDocument? doc,
        [NotNullWhen(false)] out ToolHandlerResult? error
    )
    {
        try
        {
            doc = JsonDocument.Parse(argsJson);
            error = null;
            return true;
        }
        catch (JsonException ex)
        {
            doc = null;
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
