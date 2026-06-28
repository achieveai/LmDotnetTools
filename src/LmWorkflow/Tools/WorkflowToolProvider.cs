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
    private readonly WorkflowRuntime _runtime;

    /// <summary>Creates the provider over <paramref name="runtime"/>.</summary>
    public WorkflowToolProvider(WorkflowRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        _runtime = runtime;
    }

    /// <inheritdoc />
    public string ProviderName => "WorkflowTools";

    /// <inheritdoc />
    public int Priority => 50;

    /// <inheritdoc />
    public IEnumerable<FunctionDescriptor> GetFunctions()
    {
        yield return Descriptor(
            "SetWorkflow",
            "Author (or replace) the workflow definition and position the controller at the start node.",
            [Param("definition", "The full workflow definition object.", ObjectSchema(), required: true)],
            HandleSetWorkflowAsync
        );

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
                Param("key", "Optional key for keyed merges.", JsonSchemaObject.String(), required: false),
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
        using var doc = JsonDocument.Parse(argsJson);
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
            definition = WorkflowJson.Deserialize(definitionElement.GetRawText());
        }
        catch (JsonException ex)
        {
            return Error($"The workflow definition is not valid JSON: {ex.Message}", "invalid_workflow");
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

    private Task<ToolHandlerResult> HandleGetWorkflowAsync(
        string argsJson,
        ToolCallContext context,
        CancellationToken cancellationToken
    )
    {
        using var doc = JsonDocument.Parse(argsJson);
        var projection = OptionalString(doc.RootElement, "projection");
        var state = _runtime.GetProjection(projection);

        // A prose/text projection asks for the human-readable rendering; everything else stays JSON.
        return Text(
            WantsProse(projection) ? WorkflowProseRenderer.Render(state) : state.ToJsonString()
        );
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
        using var doc = JsonDocument.Parse(argsJson);
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
            ? Text(
                "Workflow complete. " + _runtime.GetProjection("all").ToJsonString()
            )
            : Text(_runtime.GetProjection(null).ToJsonString());
    }

    private Task<ToolHandlerResult> HandleSetStateAsync(
        string argsJson,
        ToolCallContext context,
        CancellationToken cancellationToken
    )
    {
        using var doc = JsonDocument.Parse(argsJson);
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
        var key = OptionalString(root, "key");

        try
        {
            _runtime.SetState(path, value, mode, key);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or InvalidOperationException)
        {
            return Error(ex.Message, "invalid_state_write");
        }

        return Text($"State updated at '{path}'.");
    }

    private Task<ToolHandlerResult> HandleSetNotesAsync(
        string argsJson,
        ToolCallContext context,
        CancellationToken cancellationToken
    )
    {
        using var doc = JsonDocument.Parse(argsJson);
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
