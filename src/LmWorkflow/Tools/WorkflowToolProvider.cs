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

    /// <summary>
    ///     Every workflow-state tool name this provider can expose. These are the authoring/mutation tools
    ///     that must stay confined to a workflow controller loop and never reach a normal agent — a host
    ///     asserting that invariant (or restricting a controller's sub-agent templates) keys on this list.
    /// </summary>
    public static readonly IReadOnlyList<string> AllToolNames =
        [SetWorkflowToolName, "GetWorkflow", "SetCurrentNode", "SetState", "SetNotes"];

    private readonly WorkflowRuntime _runtime;
    private readonly bool _includeSetWorkflow;

    /// <summary>Creates the provider over <paramref name="runtime"/>.</summary>
    /// <param name="runtime">The runtime the tools drive.</param>
    /// <param name="includeSetWorkflow">
    ///     When <c>true</c> (default) the provider exposes all five tools including <c>SetWorkflow</c>. When
    ///     <c>false</c> the <c>SetWorkflow</c> authoring tool is omitted — used for a controller that always
    ///     receives a pre-authored definition (e.g. via <c>StartWorkflow</c>) and so never needs to author or
    ///     replace one.
    /// </param>
    /// <remarks>
    ///     Internal: this provider is only wired inside the library (via <see cref="WorkflowSession"/>) so the
    ///     workflow-state tools stay confined to a controller loop and never reach a normal agent's registry.
    /// </remarks>
    internal WorkflowToolProvider(WorkflowRuntime runtime, bool includeSetWorkflow = true)
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
                [Param("definition", "The full workflow definition object.", DefinitionSchema(), required: true)],
                HandleSetWorkflowAsync
            );
        }

        yield return Descriptor(
            "GetWorkflow",
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
            "SetCurrentNode",
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
            "SetState",
            "Write a value into the mutable state channel at a 'state.' path.",
            [
                Param("path", "The destination path, e.g. state.analysis.", JsonSchemaObject.String(), required: true),
                Param("value", "The value to write.", ObjectSchema(), required: true),
                Param("mode", "The merge mode: set (default), append, or merge.", JsonSchemaObject.String(), required: false),
            ],
            HandleSetStateAsync
        );

        yield return Descriptor(
            "SetNotes",
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
                // Strict deserialize so a misspelled or invented field (e.g. 'tasks' for 'taskList',
                // 'agentType' for 'subagent_type') is rejected by name instead of silently dropped —
                // a silently-dropped task field used to yield a workflow that validated clean yet ran
                // nothing, giving the authoring LLM no signal to correct.
                definition = WorkflowJson.DeserializeStrict(definitionElement.GetRawText());
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
            return Text(
                WantsProse(projection) ? WorkflowProseRenderer.Render(state) : state.ToJsonString()
            );
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
    ///     A machine-readable schema for the <c>SetWorkflow</c> <c>definition</c> parameter. It advertises
    ///     the fields an authoring LLM most needs — <c>objective</c>, <c>nodes[]</c>, and within a
    ///     procedural node the <c>taskList[]</c> with <c>subagent_type</c> / <c>promptTemplate</c> — so the
    ///     model reads the field names off the tool schema instead of guessing them. The node/task shapes
    ///     vary by <c>type</c>, so their objects keep <c>additionalProperties</c> open; the strict authoring
    ///     deserializer (<see cref="WorkflowJson.DeserializeStrict"/>) is what actually rejects misspelled
    ///     fields, and this schema is the reference the model uses to get them right in the first place.
    /// </summary>
    private static JsonSchemaObject DefinitionSchema()
    {
        var task = JsonSchemaObject
            .Create("object")
            .WithDescription("An authored sub-agent task within a procedural node.")
            .WithProperty("id", JsonSchemaObject.String("Task id, unique within the node."), required: true)
            .WithProperty(
                "subagent_type",
                JsonSchemaObject.String(
                    "The sub-agent template to spawn (snake_case field name). "
                        + "Passed as the Agent tool's subagent_type. Required."
                ),
                required: true
            )
            .WithProperty(
                "promptTemplate",
                JsonSchemaObject.String("The prompt handed to the spawned agent. Required."),
                required: true
            )
            .WithProperty("label", JsonSchemaObject.String("Optional human-readable label."))
            .WithProperty(
                "writes",
                JsonSchemaObject
                    .Create("object")
                    .WithDescription("Optional: where to write the validated task output.")
                    .WithProperty(
                        "to",
                        JsonSchemaObject.String("Destination state path; must start with 'state.'."),
                        required: true
                    )
                    .WithProperty(
                        "mode",
                        new JsonSchemaObject
                        {
                            Type = new("string"),
                            Description = "Merge mode.",
                            Enum = ["set", "append", "merge"],
                        }
                    )
                    .AllowAdditionalProperties(true)
                    .Build()
            )
            .AllowAdditionalProperties(true)
            .Build();

        var node = JsonSchemaObject
            .Create("object")
            .WithDescription("A workflow node. The fields used depend on 'type'.")
            .WithProperty("id", JsonSchemaObject.String("Globally-unique node id."), required: true)
            .WithProperty(
                "type",
                new JsonSchemaObject
                {
                    Type = new("string"),
                    Description = "The node kind.",
                    Enum = ["start", "procedural", "conditional", "terminal"],
                },
                required: true
            )
            .WithProperty("title", JsonSchemaObject.String("Human-readable node title."), required: true)
            .WithProperty(
                "next",
                JsonSchemaObject.StringArray(
                    "Target node id(s). start: exactly one; procedural: at least one."
                )
            )
            .WithProperty(
                "taskList",
                JsonSchemaObject.Array(
                    task,
                    "procedural only: the authored sub-agent tasks this node runs. "
                        + "This is the field name — NOT 'tasks'."
                )
            )
            .AllowAdditionalProperties(true)
            .Build();

        return JsonSchemaObject
            .Create("object")
            .WithDescription(
                "A workflow definition: an objective plus a graph of nodes. See the worked example in the "
                    + "system prompt for the exact shape."
            )
            .WithProperty(
                "objective",
                JsonSchemaObject.String("The high-level objective the workflow pursues."),
                required: true
            )
            .WithProperty(
                "nodes",
                JsonSchemaObject.Array(node, "The workflow nodes: one start, >=1 terminal, and the rest."),
                required: true
            )
            .WithProperty(
                "schemaVersion",
                JsonSchemaObject.Integer("The workflow schema version (use 1).")
            )
            .AllowAdditionalProperties(true)
            .Build();
    }

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
