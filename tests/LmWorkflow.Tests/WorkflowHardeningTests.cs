using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmWorkflow.Model;
using AchieveAi.LmDotnetTools.LmWorkflow.Runtime;
using AchieveAi.LmDotnetTools.LmWorkflow.Tools;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.LmWorkflow.Tests;

/// <summary>
///     Hardening tests at the runtime/tool-provider boundary: an un-writable task output is routed through the
///     failure policy instead of faulting the run (state-write guard), malformed tool-argument JSON returns a
///     structured <c>invalid_args</c> error instead of throwing (arg-parse guard), the SetState contract no
///     longer carries the dead <c>key</c> param, and the tool provider maps runtime errors to stable codes.
/// </summary>
public class WorkflowHardeningTests
{
    /// <summary>A <c>start → proc(merge write) → done</c> workflow whose task merges into <c>state.bag</c>.</summary>
    private const string MergeGuardWorkflow = """
        {
          "schemaVersion": 1,
          "objective": "merge guard",
          "state": {},
          "maxStepBudget": 50,
          "nodes": [
            { "id": "start", "type": "start", "title": "Start", "next": ["proc"] },
            {
              "id": "proc",
              "type": "procedural",
              "title": "Proc",
              "joinPolicy": { "mode": "all" },
              "onFailure": "fail",
              "taskList": [
                {
                  "id": "task",
                  "delegate": "agent",
                  "subagent_type": "x",
                  "promptTemplate": "do it",
                  "writes": { "to": "state.bag", "mode": "merge" },
                  "maxValidationRetries": 0
                }
              ],
              "next": ["done"]
            },
            { "id": "done", "type": "terminal", "title": "Done" },
            { "id": "fail", "type": "terminal", "title": "Fail" }
          ]
        }
        """;

    // ---- FIX: guard the task-output state write -------------------------------------------------

    [Fact]
    public void ObserveResult_UnwritableMergeOutput_FailsTaskWithoutFaulting_AndSurfacesOnFailure()
    {
        var runtime = new WorkflowRuntime();
        runtime.LoadDefinition(WorkflowJson.Deserialize(MergeGuardWorkflow));
        runtime.AdvanceTo("start", "proc", null);
        _ = runtime.ComposeNextExpectedAction();
        runtime.RegisterSpawn("tc", "proc:1:task");

        // The validated output is a JSON ARRAY, but a merge write requires an object, so StateWriter throws.
        // The runtime must route that through the failure policy rather than faulting the whole workflow.
        var observe = () => runtime.ObserveResult("tc", "[1, 2, 3]", isError: false);
        observe.Should().NotThrow();

        var projection = runtime.GetProjection(null);
        projection["tasks"]!["proc:1:task"]!.GetValue<string>().Should().Be("failed");
        runtime.Outputs["proc"]!["task"]!["_error"].Should().NotBeNull();
        projection["onFailure"]!.GetValue<string>().Should().Be("fail");
    }

    // ---- FIX: guard tool-arg JSON parse in all handlers ----------------------------------------

    [Fact]
    public async Task SetWorkflow_MalformedArgsJson_ReturnsInvalidArgs_NoThrow()
    {
        var result = await Invoke(Tool(new WorkflowRuntime(), "SetWorkflow"), "{not json");

        result.Payload.IsError.Should().BeTrue();
        result.Payload.ErrorCode.Should().Be("invalid_args");
    }

    [Fact]
    public async Task SetState_MalformedArgsJson_ReturnsInvalidArgs_NoThrow()
    {
        var result = await Invoke(Tool(new WorkflowRuntime(), "SetState"), "{not json");

        result.Payload.IsError.Should().BeTrue();
        result.Payload.ErrorCode.Should().Be("invalid_args");
    }

    // ---- FIX: drop the unused key param from SetState ------------------------------------------

    [Fact]
    public async Task SetState_SupportsSetAppendMerge_WithoutKeyParam()
    {
        var runtime = new WorkflowRuntime();
        var setState = Tool(runtime, "SetState");

        (await Invoke(setState, Args("state.x", JsonValue.Create(1), "set"))).Payload.IsError.Should()
            .BeFalse();
        (await Invoke(setState, Args("state.arr", JsonValue.Create(2), "append"))).Payload.IsError
            .Should()
            .BeFalse();
        (await Invoke(setState, Args("state.obj", JsonNode.Parse("""{ "a": 1 }"""), "merge")))
            .Payload.IsError.Should()
            .BeFalse();

        runtime.State["x"]!.GetValue<int>().Should().Be(1);
        runtime.State["arr"]!.AsArray()[0]!.GetValue<int>().Should().Be(2);
        runtime.State["obj"]!["a"]!.GetValue<int>().Should().Be(1);

        static string Args(string path, JsonNode? value, string mode) =>
            new JsonObject
            {
                ["path"] = path,
                ["value"] = value,
                ["mode"] = mode,
            }.ToJsonString();
    }

    // ---- FIX: tool-provider error mapping ------------------------------------------------------

    [Fact]
    public async Task SetWorkflow_InvalidDefinition_ReturnsInvalidWorkflow()
    {
        // Schema-valid JSON but an invalid workflow (no terminal node) → invalid_workflow.
        var args = new JsonObject
        {
            ["definition"] = JsonNode.Parse(
                """
                { "schemaVersion": 1, "objective": "x",
                  "nodes": [ { "id": "s", "type": "start", "title": "S", "next": ["s"] } ] }
                """
            ),
        };

        var result = await Invoke(Tool(new WorkflowRuntime(), "SetWorkflow"), args.ToJsonString());

        result.Payload.IsError.Should().BeTrue();
        result.Payload.ErrorCode.Should().Be("invalid_workflow");
    }

    [Fact]
    public async Task SetCurrentNode_UndeclaredTransition_ReturnsInvalidTransition()
    {
        var runtime = new WorkflowRuntime();
        runtime.LoadDefinition(WorkflowJson.Deserialize(WorkflowFixtures.MinimalValid));

        var args = new JsonObject { ["nextNodeId"] = "ghost" };
        var result = await Invoke(Tool(runtime, "SetCurrentNode"), args.ToJsonString());

        result.Payload.IsError.Should().BeTrue();
        result.Payload.ErrorCode.Should().Be("invalid_transition");
    }

    [Fact]
    public async Task SetState_NonStatePath_ReturnsInvalidStateWrite()
    {
        var args = new JsonObject { ["path"] = "outputs.x", ["value"] = JsonValue.Create(1) };
        var result = await Invoke(Tool(new WorkflowRuntime(), "SetState"), args.ToJsonString());

        result.Payload.IsError.Should().BeTrue();
        result.Payload.ErrorCode.Should().Be("invalid_state_write");
    }

    private static FunctionDescriptor Tool(WorkflowRuntime runtime, string name) =>
        new WorkflowToolProvider(runtime).GetFunctions().Single(f => f.Contract.Name == name);

    private static async Task<ToolHandlerResult.Resolved> Invoke(
        FunctionDescriptor tool,
        string argsJson
    ) =>
        (ToolHandlerResult.Resolved)
            await tool.Handler(argsJson, new ToolCallContext(), CancellationToken.None);
}
