using System.Text.Json;
using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmWorkflow.Model;
using AchieveAi.LmDotnetTools.LmWorkflow.Prompts;
using AchieveAi.LmDotnetTools.LmWorkflow.Runtime;
using AchieveAi.LmDotnetTools.LmWorkflow.Tools;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.LmWorkflow.Tests;

/// <summary>
///     Ergonomics tests that lock in the three fixes that make workflows authorable by an LLM working
///     only from the tool schema + system prompt:
///     <list type="number">
///         <item>SetWorkflow's <c>definition</c> parameter advertises a real nested schema (nodes and,
///             crucially, a procedural node's <c>taskList</c>), not a bare <c>{"type":"object"}</c>.</item>
///         <item>A misspelled task-list field (e.g. <c>tasks</c> instead of <c>taskList</c>) is rejected
///             LOUDLY, naming the offending field, rather than silently deserializing to an empty node
///             that validates clean and walks with zero tasks.</item>
///         <item>The worked example embedded in <see cref="ControllerSystemPrompt"/> is real: it
///             deserializes, validates, and actually registers a spawnable task.</item>
///     </list>
///     Strict authoring deserialization must still accept every valid fixture — including the derived
///     get-only <c>type</c> discriminator and polymorphic node shapes.
/// </summary>
public class WorkflowAuthoringErgonomicsTests
{
    // ---- Fix A: SetWorkflow advertises a usable flat-step schema -------------------------------

    [Fact]
    public void SetWorkflow_DefinitionParameter_ExposesTheFlatStepSchema()
    {
        var definition = SetWorkflowDefinitionSchema();

        // The definition is an object with named properties (not an opaque bag).
        JsonSchemaObject.GetJsonPrimaryType(definition).Should().Be("object");
        definition.Properties.Should().NotBeNull("the LLM needs a machine-readable field list");
        definition.Properties!.Should().ContainKey("objective");
        definition.Properties.Should().ContainKey("steps");

        // Drill into steps[] — the flat, uniform step shape (id/kind + kind-specific fields like
        // agent/prompt), which replaced the internal polymorphic node schema the model kept guessing wrong.
        var stepSchema = definition.Properties!["steps"].Items;
        stepSchema.Should().NotBeNull("steps must advertise an item schema");
        stepSchema!.Properties.Should().NotBeNull();
        stepSchema.Properties!.Should().ContainKeys("id", "kind", "agent", "prompt");
    }

    // ---- Fix C: a misspelled task field is rejected loudly, naming the field -------------------

    [Fact]
    public async Task SetWorkflow_MisspelledTaskListField_IsRejected_NamingTheField()
    {
        // 'tasks' instead of the real 'taskList'. Before the fix this silently produced a procedural
        // node with TaskList == null, validated clean, and returned success with zero tasks.
        const string misspelled = """
            {
              "schemaVersion": 1,
              "objective": "one agent task",
              "nodes": [
                { "id": "s", "type": "start", "title": "Start", "next": ["p"] },
                {
                  "id": "p", "type": "procedural", "title": "Work", "next": ["t"],
                  "tasks": [
                    { "id": "a", "subagent_type": "general-purpose", "promptTemplate": "Do the thing." }
                  ]
                },
                { "id": "t", "type": "terminal", "title": "Done" }
              ]
            }
            """;

        var args = new JsonObject { ["definition"] = JsonNode.Parse(misspelled) };
        var result = await Invoke(Tool(new WorkflowRuntime(), "SetWorkflow"), args.ToJsonString());

        result.Payload.IsError.Should().BeTrue("a wrong field name must not silently succeed");
        result.Payload.ErrorCode.Should().Be("invalid_workflow");
        result
            .Payload.Text.Should()
            .Contain("tasks", "the error must name the offending field so the model can correct it");
    }

    [Fact]
    public async Task SetWorkflow_MisspelledSubagentField_IsRejected_NamingTheField()
    {
        // 'agentType' — the classic guess born of the snake_case subagent_type exception.
        const string misspelled = """
            {
              "schemaVersion": 1,
              "objective": "one agent task",
              "nodes": [
                { "id": "s", "type": "start", "title": "Start", "next": ["p"] },
                {
                  "id": "p", "type": "procedural", "title": "Work", "next": ["t"],
                  "taskList": [
                    { "id": "a", "agentType": "general-purpose", "promptTemplate": "Do the thing." }
                  ]
                },
                { "id": "t", "type": "terminal", "title": "Done" }
              ]
            }
            """;

        var args = new JsonObject { ["definition"] = JsonNode.Parse(misspelled) };
        var result = await Invoke(Tool(new WorkflowRuntime(), "SetWorkflow"), args.ToJsonString());

        result.Payload.IsError.Should().BeTrue();
        result.Payload.ErrorCode.Should().Be("invalid_workflow");
        result.Payload.Text.Should().Contain("agentType");
    }

    // ---- Fix B: the prompt's worked example is real and drives a task --------------------------

    [Fact]
    public async Task ControllerPrompt_EmbedsTheWorkedExample_Verbatim()
    {
        // The example the model is shown must be the same string the tests prove works — no drift.
        ControllerSystemPrompt.Default.Should().Contain(WorkflowExamples.MinimalProcedural);
        ControllerSystemPrompt.Default.Should().Contain("taskList");
        ControllerSystemPrompt.Default.Should().Contain("subagent_type");
        ControllerSystemPrompt.Default.Should().Contain("promptTemplate");

        await Task.CompletedTask;
    }

    [Fact]
    public async Task WorkedExample_AuthorsCleanly_AndRegistersASpawnableTask()
    {
        var runtime = new WorkflowRuntime();
        var args = new JsonObject
        {
            ["definition"] = JsonNode.Parse(WorkflowExamples.MinimalProcedural),
        };

        var result = await Invoke(Tool(runtime, "SetWorkflow"), args.ToJsonString());
        result.Payload.IsError.Should().BeFalse(because: result.Payload.Text);

        // Drive to the procedural node and confirm the runtime actually surfaces a spawn unit —
        // i.e. the task list parsed, which was the whole point.
        runtime.AdvanceTo("start", "work", null);
        var units = runtime.ComposeNextExpectedAction();
        units.Should().NotBeEmpty("the authored task must be surfaced as a ready-to-spawn unit");
    }

    // ---- Strict authoring deserialize still accepts every valid shape --------------------------

    [Fact]
    public void StrictDeserialize_AcceptsAllValidFixtures_IncludingPolymorphicNodesAndTypeDiscriminator()
    {
        // The 'type' discriminator is a derived get-only property, not a settable member; strict
        // unmapped-member handling must not choke on it, nor on any node/task field in the rich fixture.
        var act = () =>
        {
            _ = WorkflowJson.DeserializeStrict(WorkflowFixtures.ValidWorkflow);
            _ = WorkflowJson.DeserializeStrict(WorkflowFixtures.MinimalValid);
        };

        act.Should().NotThrow();
    }

    [Fact]
    public void StrictDeserialize_RejectsUnknownTaskField_WithJsonException()
    {
        const string json = """
            {
              "schemaVersion": 1, "objective": "x",
              "nodes": [
                { "id": "s", "type": "start", "title": "S", "next": ["p"] },
                {
                  "id": "p", "type": "procedural", "title": "P", "next": ["t"],
                  "taskList": [ { "id": "a", "subagent_type": "x", "promptTemplate": "y", "bogusField": 1 } ]
                },
                { "id": "t", "type": "terminal", "title": "T" }
              ]
            }
            """;

        var act = () => WorkflowJson.DeserializeStrict(json);
        act.Should().Throw<JsonException>().WithMessage("*bogusField*");
    }

    private static JsonSchemaObject SetWorkflowDefinitionSchema() =>
        new WorkflowToolProvider(new WorkflowRuntime())
            .GetFunctions()
            .Single(f => f.Contract.Name == "SetWorkflow")
            .Contract.Parameters!.Single(p => p.Name == "definition")
            .ParameterType;

    private static FunctionDescriptor Tool(WorkflowRuntime runtime, string name) =>
        new WorkflowToolProvider(runtime).GetFunctions().Single(f => f.Contract.Name == name);

    private static async Task<ToolHandlerResult.Resolved> Invoke(
        FunctionDescriptor tool,
        string argsJson
    ) =>
        (ToolHandlerResult.Resolved)
            await tool.Handler(argsJson, new ToolCallContext(), CancellationToken.None);
}
