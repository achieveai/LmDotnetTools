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
            .BeEquivalentTo(["StartWorkflow", "CheckWorkflow", "WaitWorkflow"]);
    }

    [Fact]
    public void WaitWorkflowDescription_DocumentsOpenEndedTimeoutTradeoff()
    {
        var provider = new StartWorkflowToolProvider(NewManager(() => ScriptedController(DriveMinimalToTerminal).Object));

        Tool(provider, "WaitWorkflow").Contract.Description.Should().Contain("open-ended");
    }

    [Fact]
    public async Task StartWorkflow_InvalidDefinition_MapsToInvalidWorkflow()
    {
        var provider = new StartWorkflowToolProvider(NewManager(() => ScriptedController(DriveMinimalToTerminal).Object));

        var result = await Invoke(Tool(provider, "StartWorkflow"), StartArgs("x", InvalidNoTerminal, "sync"));

        result.Payload.IsError.Should().BeTrue();
        result.Payload.ErrorCode.Should().Be("invalid_workflow");
    }

    [Fact]
    public async Task StartWorkflow_DuplicateId_MapsToDuplicateWorkflow()
    {
        var provider = new StartWorkflowToolProvider(NewManager(() => ScriptedController(DriveMinimalToTerminal).Object));
        var start = Tool(provider, "StartWorkflow");

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
        var start = Tool(provider, "StartWorkflow");

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

        var result = await Invoke(Tool(provider, "StartWorkflow"), StartArgs("ok", WorkflowFixtures.MinimalValid, "sync"));

        result.Payload.IsError.Should().BeFalse();
        using var doc = JsonDocument.Parse(result.Payload.Text);
        doc.RootElement.GetProperty("status").GetString().Should().Be("completed");
        doc.RootElement.GetProperty("workflowId").GetString().Should().Be("ok");
    }
}
