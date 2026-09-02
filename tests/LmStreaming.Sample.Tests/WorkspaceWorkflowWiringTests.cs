using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using AchieveAi.LmDotnetTools.LmWorkflow;
using AchieveAi.LmDotnetTools.LmWorkflow.Tools;
using LmStreaming.Sample.Configuration;
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
    [
        .. WorkflowToolProvider.AllToolNames,
        .. StartWorkflowToolProvider.ToolNames,
    ];

    private static SubAgentOptions RestrictedControllerOptions() =>
        new()
        {
            Templates = BuiltInSubAgentTemplates.CreateWorkflowControllerTemplates(FakeAgent),
            NonInheritedToolNames = [.. WorkflowAndLaunchToolNames],
        };

    [Fact]
    public void WorkflowControllerDefaults_UseDelegatedBudget_AndPreserveExplicitValues()
    {
        var policy = new AgentOutputTokenPolicy(new AgentOutputTokenOptions { Primary = 24_576, Delegated = 16_384 });

        global::Program
            .ApplyDelegatedOutputTokens(new GenerateReplyOptions { ModelId = "controller" }, policy)
            .MaxToken.Should()
            .Be(16_384);
        global::Program
            .ApplyDelegatedOutputTokens(new GenerateReplyOptions { ModelId = "controller", MaxToken = 12_000 }, policy)
            .MaxToken.Should()
            .Be(12_000);
    }

    [Fact]
    public void WorkflowControllerPolicy_InheritsConversationWideEffortAndTypeRouting()
    {
        Func<string, SubAgentSpawnModelSelection?> typePolicy = type =>
            type == "code-reviewer:security" ? new SubAgentSpawnModelSelection(null, 5, "type-policy") : null;
        var conversationOptions = new SubAgentOptions
        {
            Templates = BuiltInSubAgentTemplates.Create(FakeAgent),
            ConversationEffortFloor = ReasoningEffort.Xhigh,
            SpawnTypeModelSelectionResolver = typePolicy,
        };
        var controllerOptions = RestrictedControllerOptions();

        var applied = global::Program.ApplyConversationSubAgentPolicyToController(
            controllerOptions,
            conversationOptions
        );

        applied.ConversationEffortFloor.Should().Be(ReasoningEffort.Xhigh);
        applied.SpawnTypeModelSelectionResolver.Should().BeSameAs(typePolicy);
        applied
            .SpawnTypeModelSelectionResolver!("code-reviewer:security")
            .Should()
            .Be(new SubAgentSpawnModelSelection(null, 5, "type-policy"));
    }

    [Fact]
    public void WorkflowControllerPolicy_WithNoConversationOptions_PreservesControllerDefaults()
    {
        var controllerOptions = RestrictedControllerOptions();

        var applied = global::Program.ApplyConversationSubAgentPolicyToController(
            controllerOptions,
            conversationOptions: null
        );

        applied.Should().BeSameAs(controllerOptions);
    }

    [Fact]
    public void ControllerTemplates_AreInheritAll_AndAcceptedByWorkflowManager_WithStructuralExclusion()
    {
        var templates = BuiltInSubAgentTemplates.CreateWorkflowControllerTemplates(FakeAgent);

        templates.Should().NotBeEmpty();
        foreach (var (name, template) in templates)
        {
            // Transparency: controller delegates are inherit-all; the workflow tools are excluded
            // structurally via NonInheritedToolNames, not via a per-template allow-list.
            template
                .EnabledTools.Should()
                .BeNull($"controller template '{name}' should be inherit-all (transparent)");
        }

        // WorkflowManager asserts the structural exclusion at construction; options that exclude the
        // workflow/launch tools from inheritance must be accepted.
        var act = () => new WorkflowManager(FakeAgent, RestrictedControllerOptions());
        act.Should().NotThrow();
    }

    /// <summary>
    ///     Builds an "enriched" catalog like the one a normal conversation gets from the workspace/marketplace
    ///     discovery tiers: the two built-ins PLUS a discovered plugin sub-agent. The controller must share this
    ///     SAME catalog so a workflow delegate can spawn a discovered <c>subagent_type</c> — not just the two
    ///     built-ins. Every entry is inherit-all (<c>EnabledTools = null</c>), as both discovery tiers produce.
    /// </summary>
    private static Dictionary<string, SubAgentTemplate> EnrichedCatalog(Func<IStreamingAgent> agentFactory)
    {
        var catalog = BuiltInSubAgentTemplates.Create(agentFactory);
        catalog["code-reviewer:performance-review"] = new SubAgentTemplate
        {
            Name = "Performance review",
            Description = "Discovered marketplace sub-agent (performance review).",
            SystemPrompt = "You review code for performance issues.",
            AgentFactory = agentFactory,
            // Inherit-all, exactly as WorkspaceSubAgentLoader / MarketplaceSubAgentLoader emit.
            EnabledTools = null,
        };
        return catalog;
    }

    [Fact]
    public void WorkflowControllerTemplates_ShareEnrichedCatalog_NotJustBuiltins()
    {
        // The regression: a workflow controller could only spawn general-purpose + researcher, so a delegate
        // asking for a discovered plugin subagent_type got "Unknown template … Available: general-purpose,
        // researcher". The controller must be seeded from the SAME enriched catalog the primary agent uses.
        var enriched = EnrichedCatalog(FakeAgent);

        var controllerTemplates = BuiltInSubAgentTemplates.CreateWorkflowControllerTemplates(enriched, FakeAgent);

        controllerTemplates
            .Keys.Should()
            .Contain(
                "code-reviewer:performance-review",
                "the controller must share the primary agent's discovered/marketplace catalog, not only the built-ins"
            );
        controllerTemplates.Keys.Should().Contain(["general-purpose", "researcher"]);
    }

    [Fact]
    public void WorkflowControllerTemplates_FromEnrichedCatalog_StayInheritAll_AndPassStructuralGuard()
    {
        var enriched = EnrichedCatalog(FakeAgent);

        var controllerTemplates = BuiltInSubAgentTemplates.CreateWorkflowControllerTemplates(enriched, FakeAgent);

        // Every controller delegate — built-in AND discovered — must remain transparent (inherit-all), so the
        // structural NonInheritedToolNames exclusion (not a per-template allow-list) is what fences off the
        // workflow tools. A discovered template that pinned EnabledTools would break that contract.
        foreach (var (name, template) in controllerTemplates)
        {
            template.EnabledTools.Should().BeNull($"controller template '{name}' must be inherit-all (transparent)");
        }

        // The enriched controller options must still satisfy WorkflowManager's construction-time guard.
        var options = new SubAgentOptions
        {
            Templates = controllerTemplates,
            NonInheritedToolNames = [.. WorkflowAndLaunchToolNames],
        };
        var act = () => new WorkflowManager(FakeAgent, options);
        act.Should().NotThrow();
    }

    [Fact]
    public void WorkflowControllerTemplates_RebindEnrichedEntries_ToProviderFactory()
    {
        // A StartWorkflowAgent run with a preferred provider must spawn its delegates on THAT provider. The
        // enriched catalog was built with the conversation's factory; the controller overload must rebind every
        // entry's AgentFactory to the controller/provider factory so a discovered delegate uses it too.
        var conversationAgent = Mock.Of<IStreamingAgent>();
        var providerAgent = Mock.Of<IStreamingAgent>();

        var enriched = EnrichedCatalog(() => conversationAgent);
        var controllerTemplates = BuiltInSubAgentTemplates.CreateWorkflowControllerTemplates(
            enriched,
            () => providerAgent
        );

        foreach (var (name, template) in controllerTemplates)
        {
            template
                .AgentFactory()
                .Should()
                .BeSameAs(
                    providerAgent,
                    $"controller template '{name}' must spawn on the controller's provider factory"
                );
        }
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
        StartWorkflowToolProvider
            .ToolNames.Should()
            .BeEquivalentTo(["StartWorkflowAgent", "GetWorkflows", "CheckWorkflow", "WaitWorkflow"]);
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
        loop.RegisteredToolNames.Should()
            .Contain(["StartWorkflowAgent", "GetWorkflows", "CheckWorkflow", "WaitWorkflow"]);
        // ...and NEVER the workflow-state/authoring tools (those live only inside a controller loop).
        loop.RegisteredToolNames.Should()
            .NotContain(["SetWorkflow", "GetWorkflow", "SetCurrentNode", "SetState", "SetNotes"]);
    }
}
