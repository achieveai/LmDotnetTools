using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using AchieveAi.LmDotnetTools.LmWorkflow.Runtime;
using AchieveAi.LmDotnetTools.LmWorkflow.Tools;
using FluentAssertions;
using Xunit;
using static AchieveAi.LmDotnetTools.LmWorkflow.Tests.StartWorkflowTestHarness;

namespace AchieveAi.LmDotnetTools.LmWorkflow.Tests;

/// <summary>
///     The tool-restriction invariant: a StartWorkflowAgent-launched controller loop exposes exactly the
///     restricted workflow-state + Agent-family tools (never <c>SetWorkflow</c>, never a library <c>Read</c>),
///     and a controller sub-agent template set that could leak the workflow tools is rejected at construction.
///     (The complementary "controller Agent must not spawn background sub-agents" rail is enforced by
///     <c>WorkflowRuntime.ObserveSpawnResult</c> and covered by <see cref="BackgroundCorrelationTests"/>, which
///     this controller inherits unchanged via <see cref="WorkflowSession"/>.)
/// </summary>
public class WorkflowControllerToolRestrictionTests
{
    [Fact]
    public async Task ControllerLoop_ExposesExactlyRestrictedToolSet_NoSetWorkflow()
    {
        var controller = ScriptedController(DriveMinimalToTerminal);

        await using var handle = await WorkflowSession.StartAsync(
            objective: "drive",
            inputs: null,
            definition: MinimalDefinition(),
            subAgentOptions: EmptyControllerOptions(),
            controllerAgent: controller.Object,
            threadId: "wf-restrict-thread",
            includeAuthoringTool: false
        );

        handle
            .Loop.RegisteredToolNames.Should()
            .BeEquivalentTo(
                [
                    "GetWorkflow",
                    "SetCurrentNode",
                    "SetState",
                    "SetNotes",
                    "Agent",
                    "SendMessage",
                    "CheckAgent",
                ]
            );
    }

    [Fact]
    public void Manager_RejectsControllerTemplate_WithNullEnabledTools()
    {
        var options = new SubAgentOptions
        {
            Templates = new Dictionary<string, SubAgentTemplate>
            {
                ["leaky"] = new SubAgentTemplate
                {
                    SystemPrompt = "leaky",
                    AgentFactory = () => ScriptedController(DriveMinimalToTerminal).Object,
                    EnabledTools = null, // inherit-all → would inherit the controller's workflow tools.
                },
            },
        };

        var act = () => WorkflowManager.AssertRestrictedControllerTemplates(options);
        act.Should().Throw<ArgumentException>().WithMessage("*EnabledTools*");
    }

    [Fact]
    public void Manager_RejectsControllerTemplate_ThatEnablesAWorkflowTool()
    {
        var options = new SubAgentOptions
        {
            Templates = new Dictionary<string, SubAgentTemplate>
            {
                ["leaky"] = new SubAgentTemplate
                {
                    SystemPrompt = "leaky",
                    AgentFactory = () => ScriptedController(DriveMinimalToTerminal).Object,
                    EnabledTools = ["Read", "SetState"], // SetState is a controller-only workflow tool.
                },
            },
        };

        var act = () => WorkflowManager.AssertRestrictedControllerTemplates(options);
        act.Should().Throw<ArgumentException>().WithMessage("*SetState*");
    }

    [Fact]
    public void WorkflowToolProvider_OmitsSetWorkflow_WhenAuthoringDisabled()
    {
        var runtime = new WorkflowRuntime();

        new WorkflowToolProvider(runtime, includeSetWorkflow: false)
            .GetFunctions()
            .Select(f => f.Contract.Name)
            .Should()
            .BeEquivalentTo(["GetWorkflow", "SetCurrentNode", "SetState", "SetNotes"]);

        // The default surface is unchanged for other callers (e.g. the authoring/test path).
        new WorkflowToolProvider(runtime)
            .GetFunctions()
            .Select(f => f.Contract.Name)
            .Should()
            .Contain("SetWorkflow");
    }
}
