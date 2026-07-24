using System.Text.Json;
using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmWorkflow.Tools;
using FluentAssertions;
using Xunit;
using static AchieveAi.LmDotnetTools.LmWorkflow.Tests.StartWorkflowTestHarness;

namespace AchieveAi.LmDotnetTools.LmWorkflow.Tests;

/// <summary>
///     Surface + error-mapping coverage for <see cref="StartWorkflowToolProvider"/>: the exact tool-name set
///     exposed to normal agents, the WaitWorkflow open-ended-timeout caveat in its description, and the
///     tool-error codes each manager failure maps to.
/// </summary>
public class StartWorkflowToolProviderTests
{
    private static WorkflowManager NewManager(
        Func<IStreamingAgent> controllerFactory,
        int maxConcurrentWorkflows = 8,
        TimeSpan? gateWaitTimeout = null
    ) =>
        new(
            controllerFactory,
            EmptyControllerOptions(),
            maxConcurrentWorkflows: maxConcurrentWorkflows,
            gateWaitTimeout: gateWaitTimeout
        );

    private static FunctionDescriptor Tool(StartWorkflowToolProvider provider, string name) =>
        provider.GetFunctions().Single(f => f.Contract.Name == name);

    private static async Task<ToolHandlerResult.Resolved> Invoke(FunctionDescriptor tool, string argsJson) =>
        (ToolHandlerResult.Resolved)await tool.Handler(argsJson, new ToolCallContext(), CancellationToken.None);

    private static string StartArgs(string workflowId, string definitionJson, string mode) =>
        new JsonObject
        {
            ["workflowId"] = workflowId,
            ["workflow"] = JsonNode.Parse(definitionJson),
            ["mode"] = mode,
        }.ToJsonString();

    [Fact]
    public void ExposesExactlyStartCheckWait()
    {
        var provider = new StartWorkflowToolProvider(NewManager(() => ScriptedController(DriveMinimalToTerminal).Object));

        provider
            .GetFunctions()
            .Select(f => f.Contract.Name)
            .Should()
            .BeEquivalentTo(["StartWorkflowAgent", "CheckWorkflow", "WaitWorkflow"]);
    }

    [Fact]
    public void WaitWorkflowDescription_DocumentsOpenEndedTimeoutTradeoff()
    {
        var provider = new StartWorkflowToolProvider(NewManager(() => ScriptedController(DriveMinimalToTerminal).Object));

        Tool(provider, "WaitWorkflow").Contract.Description.Should().Contain("open-ended");
    }

    [Fact]
    public void StartWorkflowAgent_WorkflowParam_AdvertisesTheFlatStepSchema()
    {
        var provider = new StartWorkflowToolProvider(NewManager(() => ScriptedController(DriveMinimalToTerminal).Object));

        var workflowSchema = Tool(provider, "StartWorkflowAgent")
            .Contract.Parameters!.Single(p => p.Name == "workflow")
            .ParameterType;

        // Regression: the param must advertise the flat step DSL (objective + steps[...] with kind-specific
        // fields), not a bare object and not the internal node union — otherwise the model guesses field
        // names and the strict translator rejects its graph (the workspace-agent authoring bug).
        workflowSchema.Properties.Should().NotBeNull();
        workflowSchema.Properties!.Should().ContainKeys("objective", "steps");

        var stepSchema = workflowSchema.Properties!["steps"].Items;
        stepSchema.Should().NotBeNull();
        stepSchema!.Properties!.Should().ContainKeys("id", "kind", "agent", "prompt", "next", "agents", "branches");
    }

    [Fact]
    public async Task StartWorkflow_AuthoredInTheFlatDsl_IsAcceptedAndTranslated()
    {
        var provider = new StartWorkflowToolProvider(NewManager(() => ScriptedController(DriveMinimalToTerminal).Object));
        const string dsl = """
            { "objective": "trivial", "steps": [
              { "id": "start", "kind": "start", "next": "done" },
              { "id": "done", "kind": "end" }
            ] }
            """;
        var args = new JsonObject
        {
            ["workflowId"] = "dsl-start",
            ["workflow"] = JsonNode.Parse(dsl),
            ["mode"] = "async",
        }.ToJsonString();

        var result = await Invoke(Tool(provider, "StartWorkflowAgent"), args);

        // The flat DSL translated + validated + started — the path that used to fail on a bare-object schema.
        result.Payload.IsError.Should().BeFalse();
        using var doc = JsonDocument.Parse(result.Payload.Text);
        doc.RootElement.GetProperty("status").GetString().Should().Be("started");
    }

    [Fact]
    public async Task StartWorkflow_DslMissingAgentFields_ReturnsInvalidWorkflow()
    {
        var provider = new StartWorkflowToolProvider(NewManager(() => ScriptedController(DriveMinimalToTerminal).Object));
        const string dsl = """
            { "objective": "x", "steps": [
              { "id": "start", "kind": "start", "next": "a" },
              { "id": "a", "kind": "agent", "next": "done" },
              { "id": "done", "kind": "end" }
            ] }
            """;
        var args = new JsonObject
        {
            ["workflowId"] = "dsl-bad",
            ["workflow"] = JsonNode.Parse(dsl),
            ["mode"] = "sync",
        }.ToJsonString();

        var result = await Invoke(Tool(provider, "StartWorkflowAgent"), args);

        result.Payload.IsError.Should().BeTrue();
        result.Payload.ErrorCode.Should().Be("invalid_workflow");
    }

    [Fact]
    public async Task StartWorkflow_InvalidDefinition_MapsToInvalidWorkflow()
    {
        var provider = new StartWorkflowToolProvider(NewManager(() => ScriptedController(DriveMinimalToTerminal).Object));

        var result = await Invoke(Tool(provider, "StartWorkflowAgent"), StartArgs("x", InvalidNoTerminal, "sync"));

        result.Payload.IsError.Should().BeTrue();
        result.Payload.ErrorCode.Should().Be("invalid_workflow");
    }

    [Fact]
    public async Task StartWorkflow_DuplicateId_MapsToDuplicateWorkflow()
    {
        var provider = new StartWorkflowToolProvider(NewManager(() => ScriptedController(DriveMinimalToTerminal).Object));
        var start = Tool(provider, "StartWorkflowAgent");

        var first = await Invoke(start, StartArgs("dup", WorkflowFixtures.MinimalValid, "sync"));
        first.Payload.IsError.Should().BeFalse();

        var second = await Invoke(start, StartArgs("dup", WorkflowFixtures.MinimalValid, "async"));
        second.Payload.IsError.Should().BeTrue();
        second.Payload.ErrorCode.Should().Be("duplicate_workflow");
    }

    [Fact]
    public async Task CheckWorkflow_UnknownId_MapsToUnknownWorkflow()
    {
        var provider = new StartWorkflowToolProvider(NewManager(() => ScriptedController(DriveMinimalToTerminal).Object));

        var result = await Invoke(
            Tool(provider, "CheckWorkflow"),
            new JsonObject { ["workflowId"] = "nope" }.ToJsonString()
        );

        result.Payload.IsError.Should().BeTrue();
        result.Payload.ErrorCode.Should().Be("unknown_workflow");
    }

    [Fact]
    public async Task StartWorkflow_AtCapacity_MapsToWorkflowCapacity()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new StartWorkflowToolProvider(
            NewManager(() => GatedController(gate).Object, maxConcurrentWorkflows: 1, gateWaitTimeout: TimeSpan.FromMilliseconds(200))
        );
        var start = Tool(provider, "StartWorkflowAgent");

        // Hold the only slot with a gated async workflow.
        var first = await Invoke(start, StartArgs("cap-1", WorkflowFixtures.MinimalValid, "async"));
        first.Payload.IsError.Should().BeFalse();

        var second = await Invoke(start, StartArgs("cap-2", WorkflowFixtures.MinimalValid, "async"));
        second.Payload.IsError.Should().BeTrue();
        second.Payload.ErrorCode.Should().Be("workflow_capacity");

        gate.SetResult();
    }

    [Fact]
    public async Task StartWorkflow_Sync_ReturnsCompletedJson()
    {
        var provider = new StartWorkflowToolProvider(NewManager(() => ScriptedController(DriveMinimalToTerminal).Object));

        var result = await Invoke(Tool(provider, "StartWorkflowAgent"), StartArgs("ok", WorkflowFixtures.MinimalValid, "sync"));

        result.Payload.IsError.Should().BeFalse();
        using var doc = JsonDocument.Parse(result.Payload.Text);
        doc.RootElement.GetProperty("status").GetString().Should().Be("completed");
        doc.RootElement.GetProperty("workflowId").GetString().Should().Be("ok");
    }

    [Fact]
    public async Task CheckWorkflow_RunningWorkflow_ReturnsRunningJson()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new StartWorkflowToolProvider(NewManager(() => GatedController(gate).Object));
        var start = Tool(provider, "StartWorkflowAgent");
        var check = Tool(provider, "CheckWorkflow");

        _ = await Invoke(start, StartArgs("chk", WorkflowFixtures.MinimalValid, "async"));

        var result = await Invoke(check, new JsonObject { ["workflowId"] = "chk" }.ToJsonString());
        result.Payload.IsError.Should().BeFalse();
        using (var doc = JsonDocument.Parse(result.Payload.Text))
        {
            doc.RootElement.GetProperty("status").GetString().Should().Be("running");
        }

        gate.SetResult();
    }

    [Fact]
    public async Task WaitWorkflow_UnknownId_MapsToUnknownWorkflow()
    {
        var provider = new StartWorkflowToolProvider(NewManager(() => ScriptedController(DriveMinimalToTerminal).Object));

        var result = await Invoke(
            Tool(provider, "WaitWorkflow"),
            new JsonObject { ["workflowId"] = "nope", ["timeout"] = 1 }.ToJsonString()
        );

        result.Payload.IsError.Should().BeTrue();
        result.Payload.ErrorCode.Should().Be("unknown_workflow");
    }

    [Fact]
    public async Task WaitWorkflow_MissingWorkflowId_ReturnsInvalidArgs()
    {
        var provider = new StartWorkflowToolProvider(NewManager(() => ScriptedController(DriveMinimalToTerminal).Object));

        var result = await Invoke(Tool(provider, "WaitWorkflow"), "{}");

        result.Payload.IsError.Should().BeTrue();
        result.Payload.ErrorCode.Should().Be("invalid_args");
    }

    [Theory]
    [InlineData("5")] // number-as-string (some models emit this)
    [InlineData(5000000)] // ~57 days → clamped, must not throw
    public async Task WaitWorkflow_ParsesAndClampsTimeout_OnCompletedWorkflow(object timeout)
    {
        // The workflow is already terminal, so WaitWorkflow returns immediately regardless of the timeout —
        // this exercises TryReadTimeout's string/clamp paths through the actual handler.
        var provider = new StartWorkflowToolProvider(NewManager(() => ScriptedController(DriveMinimalToTerminal).Object));
        var start = Tool(provider, "StartWorkflowAgent");
        var wait = Tool(provider, "WaitWorkflow");

        _ = await Invoke(start, StartArgs("done", WorkflowFixtures.MinimalValid, "sync"));

        var args = new JsonObject
        {
            ["workflowId"] = "done",
            ["timeout"] = timeout is string s ? JsonValue.Create(s) : JsonValue.Create((int)timeout),
        };
        var result = await Invoke(wait, args.ToJsonString());

        result.Payload.IsError.Should().BeFalse();
        using var doc = JsonDocument.Parse(result.Payload.Text);
        doc.RootElement.GetProperty("status").GetString().Should().Be("completed");
    }

    [Theory]
    [InlineData("-5")] // negative number-as-string
    [InlineData(-5)] // negative number
    [InlineData("abc")] // non-numeric string
    public async Task WaitWorkflow_PresentButInvalidTimeout_ReturnsInvalidArgs(object timeout)
    {
        // A present-but-invalid timeout must be rejected, not silently collapsed to an unbounded wait.
        var provider = new StartWorkflowToolProvider(NewManager(() => ScriptedController(DriveMinimalToTerminal).Object));
        var start = Tool(provider, "StartWorkflowAgent");
        var wait = Tool(provider, "WaitWorkflow");

        _ = await Invoke(start, StartArgs("inv", WorkflowFixtures.MinimalValid, "sync"));

        var args = new JsonObject
        {
            ["workflowId"] = "inv",
            ["timeout"] = timeout is string s ? JsonValue.Create(s) : JsonValue.Create((int)timeout),
        };
        var result = await Invoke(wait, args.ToJsonString());

        result.Payload.IsError.Should().BeTrue();
        result.Payload.ErrorCode.Should().Be("invalid_args");
    }

    [Theory]
    [InlineData("StartWorkflowAgent")]
    [InlineData("CheckWorkflow")]
    [InlineData("WaitWorkflow")]
    public async Task Handlers_MalformedJson_ReturnInvalidArgs(string toolName)
    {
        var provider = new StartWorkflowToolProvider(NewManager(() => ScriptedController(DriveMinimalToTerminal).Object));

        var result = await Invoke(Tool(provider, toolName), "{not valid json");

        result.Payload.IsError.Should().BeTrue();
        result.Payload.ErrorCode.Should().Be("invalid_args");
    }
}
