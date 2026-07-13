using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using FluentAssertions;
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
}
