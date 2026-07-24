using System.Text.Json;
using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmWorkflow.Ingest;
using AchieveAi.LmDotnetTools.LmWorkflow.Model;
using AchieveAi.LmDotnetTools.LmWorkflow.Runtime;

namespace AchieveAi.LmDotnetTools.LmWorkflow.Tools;

/// <summary>
///     Exposes the controller-facing workflow tools over a <see cref="WorkflowRuntime"/>: the controller
///     LLM authors the workflow (<c>SetWorkflow</c>), reads the runtime including the ready-to-spawn
///     next-expected-action (<c>GetWorkflow</c>), routes between nodes (<c>SetCurrentNode</c>), and mutates
///     the state/notes channels (<c>SetState</c>/<c>SetNotes</c>).
/// </summary>
/// <remarks>
///     Registered with a lower <see cref="Priority"/> number than <c>SubAgentTools</c> (100) so that, on a
///     name clash, these workflow tools win. The actual sub-agent spawn is performed by the <c>Agent</c>
///     tool (from the sub-agent stack); the runtime observes those spawns out of band — see
///     <see cref="WorkflowRuntime.RegisterSpawn"/>.
/// </remarks>
public sealed class WorkflowToolProvider : IFunctionProvider
{
    /// <summary>The tool name the controller authors/replaces the definition with.</summary>
    public const string SetWorkflowToolName = "SetWorkflow";

    /// <summary>The tool name that reads the runtime + ready-to-spawn next action.</summary>
    public const string GetWorkflowToolName = "GetWorkflow";

    /// <summary>The tool name that routes between nodes / finalizes a terminal.</summary>
    public const string SetCurrentNodeToolName = "SetCurrentNode";

    /// <summary>The tool name that writes the mutable state channel.</summary>
    public const string SetStateToolName = "SetState";

    /// <summary>The tool name that records a scoped note.</summary>
    public const string SetNotesToolName = "SetNotes";

    /// <summary>The tool name that splices a new node into the graph.</summary>
    public const string AddNodeToolName = "AddNode";

    /// <summary>The tool name that removes a node from the graph.</summary>
    public const string RemoveNodeToolName = "RemoveNode";

    /// <summary>
    ///     Every workflow-state tool name this provider can expose. These are the authoring/mutation tools
    ///     that must stay confined to a workflow controller loop and never reach a normal agent — a host
    ///     asserting that invariant (or restricting a controller's sub-agent templates) keys on this list.
    ///     Derived from the same name constants <see cref="GetFunctions"/> uses so the two cannot drift.
    /// </summary>
    public static readonly IReadOnlyList<string> AllToolNames =
        [
            SetWorkflowToolName,
            GetWorkflowToolName,
            SetCurrentNodeToolName,
            SetStateToolName,
            SetNotesToolName,
            AddNodeToolName,
            RemoveNodeToolName,
        ];

    private readonly WorkflowRuntime _runtime;
    private readonly bool _includeSetWorkflow;

    /// <summary>Creates the provider over <paramref name="runtime"/>.</summary>
    /// <param name="runtime">The runtime the tools drive.</param>
    /// <param name="includeSetWorkflow">
    ///     When <c>true</c> (default) the provider exposes all five tools including <c>SetWorkflow</c>. When
    ///     <c>false</c> the <c>SetWorkflow</c> authoring tool is omitted — used for a controller that always
    ///     receives a pre-authored definition (e.g. via <c>StartWorkflowAgent</c>) and so never needs to author or
    ///     replace one.
    /// </param>
    /// <remarks>
    ///     Normally wired inside the library (via <see cref="WorkflowSession"/>), which keeps the
    ///     workflow-state tools confined to an isolated controller loop. A host may also register this
    ///     provider directly against a <see cref="WorkflowRuntime.CreateNew"/> runtime so a normal agent can
    ///     author/drive a workflow inline on its own conversation loop; that host should also set
    ///     <c>SubAgentOptions.NonInheritedToolNames</c> to include <see cref="AllToolNames"/> so a spawned
    ///     sub-agent can't mutate the same runtime out from under the parent.
    /// </remarks>
    public WorkflowToolProvider(WorkflowRuntime runtime, bool includeSetWorkflow = true)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        _runtime = runtime;
        _includeSetWorkflow = includeSetWorkflow;
    }

    /// <inheritdoc />
    public string ProviderName => "WorkflowTools";

    /// <inheritdoc />
    public int Priority => 50;

    /// <inheritdoc />
    public IEnumerable<FunctionDescriptor> GetFunctions()
    {
        if (_includeSetWorkflow)
        {
            yield return Descriptor(
                SetWorkflowToolName,
                "Author (or replace) the workflow definition and position the controller at the start node.",
                [Param("definition", "The workflow to author, in the flat step DSL (objective + steps).", SimpleWorkflowSchema.Workflow(), required: true)],
                HandleSetWorkflowAsync
            );

            yield return Descriptor(
                AddNodeToolName,
                "Splice a new node into the graph without re-authoring the whole definition. Supply "
                    + "'previousNodeId' to append the new node after an existing start/procedural node, "
                    + "'nextNodeId' to wire the new node's own outgoing edge, or both. At least one is "
                    + "required so the new node is reachable.",
                [
                    Param("node", "The step to add, in the flat step DSL (id, kind, and kind-specific fields).", SimpleWorkflowSchema.Step(), required: true),
                    Param(
                        "previousNodeId",
                        "An existing start/procedural node to append the new node after.",
                        JsonSchemaObject.String(),
                        required: false
                    ),
                    Param(
                        "nextNodeId",
                        "An existing node the new node's own outgoing edge should target.",
                        JsonSchemaObject.String(),
                        required: false
                    ),
                ],
                HandleAddNodeAsync
            );

            yield return Descriptor(
                RemoveNodeToolName,
                "Neuter a node into a no-op pass-through. The node keeps its id and every inbound edge "
                    + "(nothing dangles): a procedural node drops its task list and failure/visit-cap edges "
                    + "(so it does no work and its loop cycle becomes 0) and just advances along its existing "
                    + "next; a conditional 'defaults to true', collapsing to its FIRST branch's target and "
                    + "dropping the others. Fails for the start node, a terminal node, the node the controller "
                    + "is currently on, or when dropping an edge would orphan a node reachable only through it "
                    + "(edit those via SetWorkflow first).",
                [Param("nodeId", "The node id to neuter into a no-op.", JsonSchemaObject.String(), required: true)],
                HandleRemoveNodeAsync
            );
        }

        yield return Descriptor(
            GetWorkflowToolName,
            "Read the current workflow state. The result always includes the ready-to-spawn "
                + "nextExpectedAction unit(s) for the active node.",
            [
                Param(
                    "projection",
                    "Optional projection selector; mention state/outputs/notes (or all) to include channels, "
                        + "or prose/text for a human-readable summary instead of JSON.",
                    JsonSchemaObject.String(),
                    required: false
                ),
            ],
            HandleGetWorkflowAsync
        );

        yield return Descriptor(
            SetCurrentNodeToolName,
            "Advance the controller from the completed node to the next node along a declared edge. "
                + "Supply a result object when advancing into a terminal to finalize the workflow.",
            [
                Param(
                    "completedNodeId",
                    "The node just completed (optional, informational).",
                    JsonSchemaObject.String(),
                    required: false
                ),
                Param("nextNodeId", "The node to transition to.", JsonSchemaObject.String(), required: true),
                Param("result", "The final result object when entering a terminal node.", ObjectSchema(), required: false),
            ],
            HandleSetCurrentNodeAsync
        );

        yield return Descriptor(
            SetStateToolName,
            "Write a value into the mutable state channel at a 'state.' path.",
            [
                Param("path", "The destination path, e.g. state.analysis.", JsonSchemaObject.String(), required: true),
                Param("value", "The value to write.", ObjectSchema(), required: true),
                Param("mode", "The merge mode: set (default), append, or merge.", JsonSchemaObject.String(), required: false),
            ],
            HandleSetStateAsync
        );

        yield return Descriptor(
            SetNotesToolName,
            "Record a scoped note for later reference.",
            [
                Param("scope", "The note scope.", JsonSchemaObject.String(), required: true),
                Param("key", "The note key.", JsonSchemaObject.String(), required: true),
                Param("value", "The note text.", JsonSchemaObject.String(), required: true),
            ],
            HandleSetNotesAsync
        );
    }

    private Task<ToolHandlerResult> HandleSetWorkflowAsync(
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
            if (
                !doc.RootElement.TryGetProperty("definition", out var definitionElement)
                || definitionElement.ValueKind != JsonValueKind.Object
            )
            {
                return Error("The 'definition' object parameter is required.", "invalid_args");
            }

            WorkflowDefinition definition;
            try
            {
                // The model authors in the flat SimpleWorkflow DSL (advertised on the tool schema); a legacy
                // internal-shaped {"nodes":[...]} definition is still accepted (FromToolArgument). A misspelled
                // DSL/internal field surfaces as a batched invalid_workflow error rather than being silently
                // dropped, so the authoring LLM gets a signal to correct.
                definition = SimpleWorkflowTranslator.FromToolArgument(definitionElement);
            }
            catch (WorkflowValidationException ex)
            {
                return Error(string.Join("; ", ex.Errors), "invalid_workflow");
            }
            catch (JsonException ex)
            {
                return Error(
                    $"The workflow definition is not valid JSON: {ex.Message}",
                    "invalid_workflow"
                );
            }

            try
            {
                _runtime.LoadDefinition(definition);
            }
            catch (WorkflowValidationException ex)
            {
                return Error(string.Join("; ", ex.Errors), "invalid_workflow");
            }

            return Text(_runtime.GetProjection(null).ToJsonString());
        }
    }

    private Task<ToolHandlerResult> HandleAddNodeAsync(
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
            var root = doc.RootElement;

            if (
                !root.TryGetProperty("node", out var nodeElement)
                || nodeElement.ValueKind != JsonValueKind.Object
            )
            {
                return Error("The 'node' object parameter is required.", "invalid_args");
            }

            WorkflowNode node;
            try
            {
                // A flat DSL step ({"kind": ...}) is translated; a legacy internal node ({"type": ...}) is
                // still accepted (NodeFromToolArgument).
                node = SimpleWorkflowTranslator.NodeFromToolArgument(nodeElement);
            }
            catch (WorkflowValidationException ex)
            {
                return Error(string.Join("; ", ex.Errors), "invalid_workflow");
            }
            catch (JsonException ex)
            {
                return Error($"The 'node' object is not a valid node: {ex.Message}", "invalid_workflow");
            }

            var previousNodeId = OptionalString(root, "previousNodeId");
            var nextNodeId = OptionalString(root, "nextNodeId");

            try
            {
                _runtime.AddNode(node, previousNodeId, nextNodeId);
            }
            catch (InvalidOperationException ex)
            {
                return Error(ex.Message, "invalid_transition");
            }
            catch (WorkflowValidationException ex)
            {
                return Error(string.Join("; ", ex.Errors), "invalid_workflow");
            }

            return Text(_runtime.GetProjection(null).ToJsonString());
        }
    }

    private Task<ToolHandlerResult> HandleRemoveNodeAsync(
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
            var root = doc.RootElement;

            var nodeId = OptionalString(root, "nodeId");
            if (string.IsNullOrEmpty(nodeId))
            {
                return Error("The 'nodeId' parameter is required.", "invalid_args");
            }

            try
            {
                _runtime.RemoveNode(nodeId);
            }
            catch (InvalidOperationException ex)
            {
                return Error(ex.Message, "invalid_transition");
            }
            catch (WorkflowValidationException ex)
            {
                return Error(string.Join("; ", ex.Errors), "invalid_workflow");
            }

            return Text(_runtime.GetProjection(null).ToJsonString());
        }
    }

    private Task<ToolHandlerResult> HandleGetWorkflowAsync(
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
            var projection = OptionalString(doc.RootElement, "projection");
            var state = _runtime.GetProjection(projection);

            // A prose/text projection asks for the human-readable rendering; everything else stays JSON.
            if (WantsProse(projection))
            {
                return Text(WorkflowProseRenderer.Render(state));
            }

            // Read the graph back in the SAME flat DSL shape it was authored in, so the model inspects and
            // edits its workflow without a write-DSL / read-internal mismatch.
            if (_runtime.Definition is { } definition)
            {
                state["workflow"] = JsonSerializer.SerializeToNode(
                    SimpleWorkflowTranslator.FromDefinition(definition),
                    SimpleWorkflow.OutputJsonOptions
                );
            }

            return Text(state.ToJsonString());
        }
    }

    /// <summary>Whether the controller asked for the prose rendering via a <c>prose</c>/<c>text</c> projection.</summary>
    private static bool WantsProse(string? projection) =>
        projection is not null
        && (
            projection.Contains("prose", StringComparison.OrdinalIgnoreCase)
            || projection.Contains("text", StringComparison.OrdinalIgnoreCase)
        );

    private Task<ToolHandlerResult> HandleSetCurrentNodeAsync(
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
            var root = doc.RootElement;

            var nextNodeId = OptionalString(root, "nextNodeId");
            if (string.IsNullOrEmpty(nextNodeId))
            {
                return Error("The 'nextNodeId' parameter is required.", "invalid_args");
            }

            var completedNodeId = OptionalString(root, "completedNodeId");
            var result =
                root.TryGetProperty("result", out var resultElement)
                && resultElement.ValueKind != JsonValueKind.Null
                    ? JsonNode.Parse(resultElement.GetRawText())
                    : null;

            try
            {
                _runtime.AdvanceTo(completedNodeId, nextNodeId, result);
            }
            catch (InvalidOperationException ex)
            {
                return Error(ex.Message, "invalid_transition");
            }

            return _runtime.IsComplete
                ? Text("Workflow complete. " + _runtime.GetProjection("all").ToJsonString())
                : Text(_runtime.GetProjection(null).ToJsonString());
        }
    }

    private Task<ToolHandlerResult> HandleSetStateAsync(
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
            var root = doc.RootElement;

            var path = OptionalString(root, "path");
            if (string.IsNullOrEmpty(path))
            {
                return Error("The 'path' parameter is required.", "invalid_args");
            }

            var value =
                root.TryGetProperty("value", out var valueElement)
                    ? JsonNode.Parse(valueElement.GetRawText())
                    : null;
            var mode = OptionalString(root, "mode");

            try
            {
                _runtime.SetState(path, value, mode);
            }
            catch (Exception ex)
                when (ex is ArgumentException or NotSupportedException or InvalidOperationException)
            {
                return Error(ex.Message, "invalid_state_write");
            }

            return Text($"State updated at '{path}'.");
        }
    }

    private Task<ToolHandlerResult> HandleSetNotesAsync(
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
            var root = doc.RootElement;

            var scope = OptionalString(root, "scope");
            var key = OptionalString(root, "key");
            if (string.IsNullOrEmpty(scope) || string.IsNullOrEmpty(key))
            {
                return Error("The 'scope' and 'key' parameters are required.", "invalid_args");
            }

            _runtime.SetNotes(scope, key, OptionalString(root, "value") ?? string.Empty);
            return Text($"Note '{scope}.{key}' recorded.");
        }
    }

    private static FunctionDescriptor Descriptor(
        string name,
        string description,
        IEnumerable<FunctionParameterContract> parameters,
        ToolHandler handler
    ) =>
        new()
        {
            Contract = new FunctionContract
            {
                Name = name,
                Description = description,
                Parameters = [.. parameters],
            },
            Handler = handler,
            ProviderName = "WorkflowTools",
        };

    private static FunctionParameterContract Param(
        string name,
        string description,
        JsonSchemaObject type,
        bool required
    ) =>
        new()
        {
            Name = name,
            Description = description,
            ParameterType = type,
            IsRequired = required,
        };

    private static JsonSchemaObject ObjectSchema() => new() { Type = new("object") };

    /// <summary>
    ///     Parses a handler's raw JSON arguments, returning a structured <c>invalid_args</c> error result
    ///     instead of letting a malformed-args <see cref="JsonException"/> escape to the executor (where it
    ///     would surface as a generic, unstructured tool failure).
    /// </summary>
    private static bool TryParseArgs(
        string argsJson,
        out JsonDocument doc,
        out ToolHandlerResult? error
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
            doc = null!;
            error = ToolHandlerResult.FromError(
                $"Tool arguments are not valid JSON: {ex.Message}",
                "invalid_args"
            );
            return false;
        }
    }

    private static string? OptionalString(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var property)
        && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static Task<ToolHandlerResult> Text(string text) =>
        Task.FromResult<ToolHandlerResult>(ToolHandlerResult.FromText(text));

    private static Task<ToolHandlerResult> Error(string text, string errorCode) =>
        Task.FromResult<ToolHandlerResult>(ToolHandlerResult.FromError(text, errorCode));
}
