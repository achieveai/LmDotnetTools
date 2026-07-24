using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using AchieveAi.LmDotnetTools.LmWorkflow;
using AchieveAi.LmDotnetTools.LmWorkflow.Tools;
using LmStreaming.Sample.Services;

namespace LmStreaming.Sample.Tests;

/// <summary>
///     Covers the Workspace Agent migration off #130's direct <c>SetWorkflow</c>/<c>GetWorkflow</c> wiring
///     onto the <c>StartWorkflowAgent</c> tool family: the controller node-delegate templates are inherit-all
///     (transparent), the workflow-state/launch tools are excluded from inheritance structurally (via
///     <c>NonInheritedToolNames</c>, which <c>WorkflowManager</c> asserts), and a migrated conversation
///     surface exposes the launch tools but NEVER the workflow-state tools.
/// </summary>
public sealed class WorkspaceWorkflowWiringTests
{
    private static IStreamingAgent FakeAgent() => Mock.Of<IStreamingAgent>();

    private static readonly HashSet<string> WorkflowAndLaunchToolNames =
        [.. WorkflowToolProvider.AllToolNames, .. StartWorkflowToolProvider.ToolNames];

    private static SubAgentOptions RestrictedControllerOptions() =>
        new()
        {
            Templates = BuiltInSubAgentTemplates.CreateWorkflowControllerTemplates(FakeAgent),
            NonInheritedToolNames = [.. WorkflowAndLaunchToolNames],
        };

    [Fact]
    public void ControllerTemplates_AreInheritAll_AndAcceptedByWorkflowManager_WithStructuralExclusion()
    {
        var templates = BuiltInSubAgentTemplates.CreateWorkflowControllerTemplates(FakeAgent);

        templates.Should().NotBeEmpty();
        foreach (var (name, template) in templates)
        {
            // Transparency: controller delegates are inherit-all; the workflow tools are excluded
            // structurally via NonInheritedToolNames, not via a per-template allow-list.
            template.EnabledTools.Should().BeNull($"controller template '{name}' should be inherit-all (transparent)");
        }

        // WorkflowManager asserts the structural exclusion at construction; options that exclude the
        // workflow/launch tools from inheritance must be accepted.
        var act = () => new WorkflowManager(FakeAgent, RestrictedControllerOptions());
        act.Should().NotThrow();
    }

    [Fact]
    public void WorkflowManager_RejectsControllerOptions_WithoutStructuralExclusion()
    {
        // Inherit-all templates WITHOUT excluding the workflow tools from inheritance is the
        // misconfiguration the assertion now guards.
        var act = () =>
            new WorkflowManager(
                FakeAgent,
                new SubAgentOptions
                {
                    Templates = BuiltInSubAgentTemplates.CreateWorkflowControllerTemplates(FakeAgent),
                }
            );
        act.Should().Throw<ArgumentException>().WithMessage("*NonInheritedToolNames*");
    }

    [Fact]
    public void DefaultConversationTemplates_AreInheritAll_SoLaunchToolsMustBeExcluded()
    {
        var defaults = BuiltInSubAgentTemplates.Create(FakeAgent);

        // The default sub-agent templates inherit ALL parent tools — so once StartWorkflowAgent is on the
        // conversation registry, the launch tools would leak into every sub-agent unless excluded. This is
        // exactly what the migration's NonInheritedToolNames = StartWorkflowToolProvider.ToolNames guards.
        defaults.Values.Should().OnlyContain(t => t.EnabledTools == null);
        StartWorkflowToolProvider.ToolNames.Should()
            .BeEquivalentTo(["StartWorkflowAgent", "CheckWorkflow", "WaitWorkflow"]);
    }

    [Fact]
    public async Task MigratedConversation_ExposesLaunchTools_NotWorkflowStateTools()
    {
        // Reproduce the migrated Workspace Agent wiring: StartWorkflowAgent family on the conversation registry,
        // default (inherit-all) sub-agent templates, and the launch tools excluded from inheritance.
        var manager = new WorkflowManager(FakeAgent, RestrictedControllerOptions());

        var registry = new FunctionRegistry();
        _ = registry.AddProvider(new StartWorkflowToolProvider(manager));

        var subAgentOptions = new SubAgentOptions
        {
            Templates = BuiltInSubAgentTemplates.Create(FakeAgent),
            NonInheritedToolNames = StartWorkflowToolProvider.ToolNames,
        };

        await using var manager2 = manager;
        await using var loop = new MultiTurnAgentLoop(
            FakeAgent(),
            registry,
            threadId: "workspace-workflow-surface",
            subAgentOptions: subAgentOptions
        );

        // A normal agent sees the launch tools...
        loop.RegisteredToolNames.Should().Contain(["StartWorkflowAgent", "CheckWorkflow", "WaitWorkflow"]);
        // ...and NEVER the workflow-state/authoring tools (those live only inside a controller loop).
        loop.RegisteredToolNames.Should()
            .NotContain(["SetWorkflow", "GetWorkflow", "SetCurrentNode", "SetState", "SetNotes"]);
    }
}
