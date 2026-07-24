using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
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
///     and controller sub-agent options that fail to exclude the workflow tools from delegate inheritance
///     (via <c>NonInheritedToolNames</c>) are rejected at construction. (The complementary "controller Agent
///     must not spawn background sub-agents" rail is enforced by <c>WorkflowRuntime.ObserveSpawnResult</c> and
///     covered by <see cref="BackgroundCorrelationTests"/>, which this controller inherits unchanged via
///     <see cref="WorkflowSession"/>.)
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
    public async Task ControllerLoop_DelegatesInheritExternalTools_ButNeverTheWorkflowTools()
    {
        // Transparency (Rules 1 & 2): the launching conversation's inheritable tools flow onto the
        // controller run via SubAgentOptions.ExternalInheritableTools, and the controller's delegate
        // sub-agents inherit them — while the controller's own workflow-state tools stay excluded.
        var external = new InheritableToolSnapshot(
            [new FunctionContract { Name = "Foo", Description = "Foo", Parameters = [] }],
            new Dictionary<string, ToolHandler>(StringComparer.Ordinal)
            {
                ["Foo"] = (_, _, _) => Task.FromResult<ToolHandlerResult>(ToolHandlerResult.FromText("ok")),
            });

        var options = new SubAgentOptions
        {
            Templates = new Dictionary<string, SubAgentTemplate>
            {
                ["general-purpose"] = new SubAgentTemplate
                {
                    SystemPrompt = "gp",
                    AgentFactory = () => ScriptedController(DriveMinimalToTerminal).Object,
                    EnabledTools = null, // transparent delegate.
                },
            },
            NonInheritedToolNames =
            [
                .. WorkflowToolProvider.AllToolNames,
                .. StartWorkflowToolProvider.ToolNames,
            ],
            ExternalInheritableTools = external,
        };

        await using var handle = await WorkflowSession.StartAsync(
            objective: "drive",
            inputs: null,
            definition: MinimalDefinition(),
            subAgentOptions: options,
            controllerAgent: ScriptedController(DriveMinimalToTerminal).Object,
            threadId: "wf-transparency-thread",
            includeAuthoringTool: false
        );

        var snapshot = handle.Loop.SubAgentManager!.GetInheritableToolSnapshot();

        // Delegates inherit the launching conversation's "Foo"...
        snapshot.Contracts.Select(c => c.Name).Should().Contain("Foo");
        // ...but NEVER the controller's own workflow-state tools.
        snapshot.Contracts.Select(c => c.Name)
            .Should().NotContain(["GetWorkflow", "SetCurrentNode", "SetState", "SetNotes"]);
    }

    [Fact]
    public void Manager_RejectsControllerOptions_MissingStructuralExclusion()
    {
        // Under transparency, an inherit-all (EnabledTools = null) delegate template is FINE — the
        // controller's workflow-state/launch tools are kept out of the delegate snapshot structurally,
        // via NonInheritedToolNames. Options that DON'T exclude them are the misconfiguration now.
        var options = new SubAgentOptions
        {
            Templates = new Dictionary<string, SubAgentTemplate>
            {
                ["transparent"] = new SubAgentTemplate
                {
                    SystemPrompt = "transparent",
                    AgentFactory = () => ScriptedController(DriveMinimalToTerminal).Object,
                    EnabledTools = null, // inherit-all — now legal, given the structural exclusion below.
                },
            },
            // NonInheritedToolNames intentionally omitted → the workflow tools would leak into delegates.
        };

        var act = () => WorkflowManager.AssertRestrictedControllerTemplates(options);
        act.Should()
            .Throw<ArgumentException>()
            .WithMessage("*NonInheritedToolNames*")
            .WithMessage("*SetState*");
    }

    [Fact]
    public void Manager_AcceptsControllerOptions_WithInheritAllTemplates_AndStructuralExclusion()
    {
        var options = new SubAgentOptions
        {
            Templates = new Dictionary<string, SubAgentTemplate>
            {
                ["transparent"] = new SubAgentTemplate
                {
                    SystemPrompt = "transparent",
                    AgentFactory = () => ScriptedController(DriveMinimalToTerminal).Object,
                    EnabledTools = null, // transparent delegates inherit everything inheritable.
                },
            },
            NonInheritedToolNames =
            [
                .. WorkflowToolProvider.AllToolNames,
                .. StartWorkflowToolProvider.ToolNames,
            ],
        };

        var act = () => WorkflowManager.AssertRestrictedControllerTemplates(options);
        act.Should().NotThrow();
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
