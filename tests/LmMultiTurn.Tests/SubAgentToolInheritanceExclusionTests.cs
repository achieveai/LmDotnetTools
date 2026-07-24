using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace LmMultiTurn.Tests;

/// <summary>
/// Covers <see cref="SubAgentOptions.NonInheritedToolNames"/>: a host-declared launch/orchestration
/// tool (e.g. StartWorkflow) that lives on the parent's own registry must NOT leak into a sub-agent
/// spawned with an inherit-all template, while the parent keeps it. Also covers the
/// <see cref="MultiTurnAgentLoop.RegisteredToolNames"/> accessor used to assert a loop's tool surface.
/// </summary>
public class SubAgentToolInheritanceExclusionTests
{
    private static FunctionContract Contract(string name) =>
        new() { Name = name, Description = name, Parameters = [] };

    [Fact]
    public void FilterInheritableContracts_ExcludesNamedTools()
    {
        var contracts = new List<FunctionContract> { Contract("SafeTool"), Contract("StartWorkflow") };

        var filtered = MultiTurnAgentLoop.FilterInheritableContracts(contracts, ["StartWorkflow"]);

        filtered.Select(c => c.Name).Should().BeEquivalentTo(["SafeTool"]);
    }

    [Fact]
    public void FilterInheritableContracts_NullOrEmpty_ReturnsAllUnchanged()
    {
        var contracts = new List<FunctionContract> { Contract("SafeTool"), Contract("StartWorkflow") };

        MultiTurnAgentLoop.FilterInheritableContracts(contracts, null)
            .Select(c => c.Name).Should().BeEquivalentTo(["SafeTool", "StartWorkflow"]);
        MultiTurnAgentLoop.FilterInheritableContracts(contracts, [])
            .Select(c => c.Name).Should().BeEquivalentTo(["SafeTool", "StartWorkflow"]);
    }

    [Fact]
    public async Task RegisteredToolNames_KeepsExcludedToolOnParent_AndSelfRegistersAgentTools()
    {
        var providerAgent = new Mock<IStreamingAgent>().Object;

        var registry = new FunctionRegistry();
        _ = registry.AddFunction(
            Contract("SafeTool"),
            (_, _, _) => Task.FromResult<ToolHandlerResult>(ToolHandlerResult.FromText("ok")),
            "ParentTools");
        _ = registry.AddFunction(
            Contract("StartWorkflow"),
            (_, _, _) => Task.FromResult<ToolHandlerResult>(ToolHandlerResult.FromText("ok")),
            "ParentTools");

        var subAgentOptions = new SubAgentOptions
        {
            Templates = new Dictionary<string, SubAgentTemplate>
            {
                ["worker"] = new SubAgentTemplate
                {
                    SystemPrompt = "worker",
                    AgentFactory = () => new Mock<IStreamingAgent>().Object,
                    // inherit-all: without the exclusion this template WOULD inherit StartWorkflow.
                    EnabledTools = null,
                },
            },
            // The general Q3 exclusion: sub-agents must never inherit the launch tool.
            NonInheritedToolNames = ["StartWorkflow"],
        };

        await using var loop = new MultiTurnAgentLoop(
            providerAgent,
            registry,
            threadId: "exclusion-test",
            subAgentOptions: subAgentOptions);

        // The PARENT keeps both its own tools plus the self-registered Agent-family tools.
        loop.RegisteredToolNames.Should().Contain(["SafeTool", "StartWorkflow", "Agent", "SendMessage", "CheckAgent"]);
    }

    [Fact]
    public async Task GetInheritableToolSnapshot_ReturnsFilteredParentTools()
    {
        var providerAgent = new Mock<IStreamingAgent>().Object;

        var registry = new FunctionRegistry();
        _ = registry.AddFunction(Contract("SafeTool"), OkHandler(), "ParentTools");
        _ = registry.AddFunction(Contract("StartWorkflow"), OkHandler(), "ParentTools");

        var subAgentOptions = new SubAgentOptions
        {
            Templates = new Dictionary<string, SubAgentTemplate>
            {
                ["worker"] = new SubAgentTemplate
                {
                    SystemPrompt = "worker",
                    AgentFactory = () => new Mock<IStreamingAgent>().Object,
                },
            },
            NonInheritedToolNames = ["StartWorkflow"],
        };

        await using var loop = new MultiTurnAgentLoop(
            providerAgent,
            registry,
            threadId: "snapshot-test",
            subAgentOptions: subAgentOptions);

        var snapshot = loop.SubAgentManager!.GetInheritableToolSnapshot();

        // The snapshot is the INHERITABLE set a spawn hands down: keeps domain tools, excludes
        // NonInheritedToolNames AND the Agent-family tools (registered after the snapshot).
        snapshot.Contracts.Select(c => c.Name).Should().Contain("SafeTool");
        snapshot.Contracts.Select(c => c.Name)
            .Should().NotContain(["StartWorkflow", "Agent", "SendMessage", "CheckAgent"]);
    }

    [Fact]
    public async Task ExternalInheritableTools_AreInheritedByDelegates_WithoutTouchingParentSurfaceOrExcludedNames()
    {
        var providerAgent = new Mock<IStreamingAgent>().Object;

        // The controller registry carries its own workflow-state tool "SetState" (a control-plane tool).
        var registry = new FunctionRegistry();
        _ = registry.AddFunction(Contract("SetState"), OkHandler(), "WorkflowTools");

        // The launching conversation's inheritable snapshot: a domain tool "Foo" the delegate SHOULD
        // inherit, plus a "SetState" name-collision it must NOT (the workflow tool wins, transparently).
        var external = new InheritableToolSnapshot(
            [Contract("Foo"), Contract("SetState")],
            new Dictionary<string, ToolHandler>(StringComparer.Ordinal)
            {
                ["Foo"] = OkHandler(),
                ["SetState"] = OkHandler(),
            });

        var subAgentOptions = new SubAgentOptions
        {
            Templates = new Dictionary<string, SubAgentTemplate>
            {
                ["worker"] = new SubAgentTemplate
                {
                    SystemPrompt = "worker",
                    AgentFactory = () => new Mock<IStreamingAgent>().Object,
                    EnabledTools = null, // transparent: inherit everything inheritable.
                },
            },
            NonInheritedToolNames = ["SetState"],
            ExternalInheritableTools = external,
        };

        await using var loop = new MultiTurnAgentLoop(
            providerAgent,
            registry,
            threadId: "transparency-test",
            subAgentOptions: subAgentOptions);

        var snapshot = loop.SubAgentManager!.GetInheritableToolSnapshot();

        // Delegates inherit the external domain tool "Foo"...
        snapshot.Contracts.Select(c => c.Name).Should().Contain("Foo");
        snapshot.Handlers.Should().ContainKey("Foo");
        // ...but NOT the excluded workflow tool, even though it appears in BOTH the controller registry
        // AND the external snapshot (excluded structurally + the collision is skipped).
        snapshot.Contracts.Select(c => c.Name).Should().NotContain("SetState");

        // The controller's OWN surface is untouched: it still exposes its workflow tool.
        loop.RegisteredToolNames.Should().Contain("SetState");
    }

    [Fact]
    public async Task SpawnedSubAgents_DoNotInheritTheAgentFamilyTools_RecursionGuard()
    {
        // The recursion guard: a sub-agent must NOT inherit the Agent/CheckAgent/SendMessage tools, so a
        // sub-agent can never spawn a sub-agent (preventing unbounded recursive delegation). Those tools are
        // registered on the parent loop AFTER the inheritable snapshot is taken, so the snapshot a spawn
        // hands down (GetInheritableToolSnapshot) excludes them — while the parent keeps them for itself.
        var providerAgent = new Mock<IStreamingAgent>().Object;

        var registry = new FunctionRegistry();
        _ = registry.AddFunction(Contract("SafeTool"), OkHandler(), "ParentTools");

        var subAgentOptions = new SubAgentOptions
        {
            Templates = new Dictionary<string, SubAgentTemplate>
            {
                ["worker"] = new SubAgentTemplate
                {
                    SystemPrompt = "worker",
                    AgentFactory = () => new Mock<IStreamingAgent>().Object,
                },
            },
        };

        await using var loop = new MultiTurnAgentLoop(
            providerAgent,
            registry,
            threadId: "recursion-guard",
            subAgentOptions: subAgentOptions);

        // The parent advertises the Agent-family tools to ITSELF...
        loop.RegisteredToolNames.Should().Contain(["Agent", "CheckAgent", "SendMessage"]);

        // ...but the snapshot handed to a spawned sub-agent must NOT include them (only the domain tool).
        var inherited = loop.SubAgentManager!.GetInheritableToolSnapshot().Contracts.Select(c => c.Name).ToList();
        inherited.Should().Contain("SafeTool");
        inherited.Should().NotContain(["Agent", "CheckAgent", "SendMessage"]);
    }

    private static ToolHandler OkHandler() =>
        (_, _, _) => Task.FromResult<ToolHandlerResult>(ToolHandlerResult.FromText("ok"));

    [Fact]
    public async Task ExternalInheritableTools_Merge_IsLoggedAtDebug_ForObservability()
    {
        var logger = new CapturingLogger<MultiTurnAgentLoop>();
        var providerAgent = new Mock<IStreamingAgent>().Object;

        var registry = new FunctionRegistry();
        _ = registry.AddFunction(Contract("SetState"), OkHandler(), "WorkflowTools");

        var external = new InheritableToolSnapshot(
            [Contract("Foo")],
            new Dictionary<string, ToolHandler>(StringComparer.Ordinal) { ["Foo"] = OkHandler() });

        var subAgentOptions = new SubAgentOptions
        {
            Templates = new Dictionary<string, SubAgentTemplate>
            {
                ["worker"] = new SubAgentTemplate
                {
                    SystemPrompt = "worker",
                    AgentFactory = () => new Mock<IStreamingAgent>().Object,
                },
            },
            NonInheritedToolNames = ["SetState"],
            ExternalInheritableTools = external,
        };

        await using var loop = new MultiTurnAgentLoop(
            providerAgent,
            registry,
            threadId: "merge-log-test",
            subAgentOptions: subAgentOptions,
            logger: logger);

        // The transparency merge must be observable in the logs (content-free: counts only).
        logger.CountAtLevel(LogLevel.Debug, "Merged external inheritable tools").Should().BeGreaterThan(0);
    }
}
