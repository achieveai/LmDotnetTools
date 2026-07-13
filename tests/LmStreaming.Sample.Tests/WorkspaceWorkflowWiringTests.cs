using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using AchieveAi.LmDotnetTools.LmWorkflow;
using AchieveAi.LmDotnetTools.LmWorkflow.Tools;
using LmStreaming.Sample.Services;

namespace LmStreaming.Sample.Tests;

/// <summary>
///     Covers the Workspace Agent migration off #130's direct <c>SetWorkflow</c>/<c>GetWorkflow</c> wiring
///     onto the <c>StartWorkflow</c> tool family: the controller node-delegate templates are restricted (so
///     <c>WorkflowManager</c> accepts them), the default conversation templates are inherit-all (which is
///     why the launch tools must be excluded from inheritance), and a migrated conversation surface exposes
///     the launch tools but NEVER the workflow-state tools.
/// </summary>
public sealed class WorkspaceWorkflowWiringTests
{
    private static IStreamingAgent FakeAgent() => Mock.Of<IStreamingAgent>();

    private static readonly HashSet<string> WorkflowAndLaunchToolNames =
        [.. WorkflowToolProvider.AllToolNames, .. StartWorkflowToolProvider.ToolNames];

    [Fact]
    public void ControllerTemplates_AreRestricted_AndAcceptedByWorkflowManager()
    {
        var templates = BuiltInSubAgentTemplates.CreateWorkflowControllerTemplates(FakeAgent);

        templates.Should().NotBeEmpty();
        foreach (var (name, template) in templates)
        {
            template.EnabledTools.Should().NotBeNull($"controller template '{name}' must not be inherit-all");
            template.EnabledTools!.Should()
                .NotIntersectWith(WorkflowAndLaunchToolNames, $"controller template '{name}' must not leak workflow tools");
        }

        // WorkflowManager asserts the restricted-template invariant at construction; these must pass.
        var act = () =>
            new WorkflowManager(
                FakeAgent,
                new SubAgentOptions { Templates = templates }
            );
        act.Should().NotThrow();
    }

    [Fact]
    public void DefaultConversationTemplates_AreInheritAll_SoLaunchToolsMustBeExcluded()
    {
        var defaults = BuiltInSubAgentTemplates.Create(FakeAgent);

        // The default sub-agent templates inherit ALL parent tools — so once StartWorkflow is on the
        // conversation registry, the launch tools would leak into every sub-agent unless excluded. This is
        // exactly what the migration's NonInheritedToolNames = StartWorkflowToolProvider.ToolNames guards.
        defaults.Values.Should().OnlyContain(t => t.EnabledTools == null);
        StartWorkflowToolProvider.ToolNames.Should()
            .BeEquivalentTo(["StartWorkflow", "CheckWorkflow", "WaitWorkflow"]);
    }

    [Fact]
    public async Task MigratedConversation_ExposesLaunchTools_NotWorkflowStateTools()
    {
        // Reproduce the migrated Workspace Agent wiring: StartWorkflow family on the conversation registry,
        // default (inherit-all) sub-agent templates, and the launch tools excluded from inheritance.
        var manager = new WorkflowManager(
            FakeAgent,
            new SubAgentOptions { Templates = BuiltInSubAgentTemplates.CreateWorkflowControllerTemplates(FakeAgent) }
        );

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
        loop.RegisteredToolNames.Should().Contain(["StartWorkflow", "CheckWorkflow", "WaitWorkflow"]);
        // ...and NEVER the workflow-state/authoring tools (those live only inside a controller loop).
        loop.RegisteredToolNames.Should()
            .NotContain(["SetWorkflow", "GetWorkflow", "SetCurrentNode", "SetState", "SetNotes"]);
    }
}
