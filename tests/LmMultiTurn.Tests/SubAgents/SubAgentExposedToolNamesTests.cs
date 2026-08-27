using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using FluentAssertions;
using Moq;
using Xunit;

namespace LmMultiTurn.Tests.SubAgents;

/// <summary>
/// <see cref="SubAgentOptions.ExposedToolNames"/> narrows the delegation surface to exactly the
/// tools a host offered its user.
/// </summary>
/// <remarks>
/// A host whose UI lists <c>Agent</c>, <c>SendMessage</c>, <c>CheckAgent</c> and <c>WaitAgent</c>
/// as separate checkboxes previously had no way to grant one of them: the provider emitted a whole
/// shape, so ticking a single box silently granted the family. The runtime grant has to match the
/// granularity the host advertises, or the choice it offers is fiction.
/// </remarks>
public class SubAgentExposedToolNamesTests
{
    private static SubAgentToolProvider CreateProvider(IReadOnlySet<string>? exposed)
    {
        var options = new SubAgentOptions
        {
            Templates = new Dictionary<string, SubAgentTemplate>
            {
                ["researcher"] = new()
                {
                    Name = "researcher",
                    SystemPrompt = "You are a researcher.",
                    Description = "Researches topics.",
                    WhenToUse = "Use for investigation.",
                    AgentFactory = () => new Mock<IStreamingAgent>().Object,
                },
            },
            MaxConcurrentSubAgents = 5,
            ExposedToolNames = exposed,
        };

        var source = new MutableSubAgentTemplateSource(options.Templates);
        var manager = new SubAgentManager(
            parentAgent: new Mock<IMultiTurnAgent>().Object,
            parentContracts: [],
            parentHandlers: new Dictionary<string, ToolHandler>(),
            options: options,
            source: source);

        return new SubAgentToolProvider(manager, source, options.ExposedToolNames);
    }

    private static IReadOnlyList<string> NamesFrom(SubAgentToolProvider provider) =>
        [.. provider.GetFunctions().Select(f => f.Contract.Name)];

    [Fact]
    public void NullAllowList_KeepsTheWholeShape()
    {
        // The legacy path: a host that never narrows must see no change at all.
        NamesFrom(CreateProvider(null))
            .Should()
            .BeEquivalentTo(
                [
                    SubAgentToolProvider.SpawnToolName,
                    SubAgentToolProvider.SendMessageToolName,
                    SubAgentToolProvider.CheckAgentToolName,
                    SubAgentToolProvider.WaitAgentToolName,
                ]
            );
    }

    [Fact]
    public void SingleName_GrantsExactlyThatTool()
    {
        var names = NamesFrom(
            CreateProvider(new HashSet<string>(StringComparer.Ordinal) { SubAgentToolProvider.SpawnToolName })
        );

        names.Should().ContainSingle().Which.Should().Be(SubAgentToolProvider.SpawnToolName);
    }

    [Fact]
    public void SubsetOfTheShape_GrantsOnlyTheNamedTools()
    {
        var names = NamesFrom(
            CreateProvider(
                new HashSet<string>(StringComparer.Ordinal)
                {
                    SubAgentToolProvider.SpawnToolName,
                    SubAgentToolProvider.CheckAgentToolName,
                }
            )
        );

        names.Should().BeEquivalentTo([SubAgentToolProvider.SpawnToolName, SubAgentToolProvider.CheckAgentToolName]);
        names.Should().NotContain(SubAgentToolProvider.SendMessageToolName);
    }

    [Fact]
    public void AllowListCannotWidenTheShape()
    {
        // Collaboration-only names are not emitted by the non-collaboration shape, so listing them
        // must be inert rather than conjuring a tool with no manager behind it.
        var names = NamesFrom(
            CreateProvider(
                new HashSet<string>(StringComparer.Ordinal)
                {
                    SubAgentToolProvider.SpawnToolName,
                    SubAgentToolProvider.CheckAgentsToolName,
                    SubAgentToolProvider.GetAgentsToolName,
                }
            )
        );

        names.Should().ContainSingle().Which.Should().Be(SubAgentToolProvider.SpawnToolName);
    }

    [Fact]
    public void EmptyAllowList_GrantsNothing()
    {
        NamesFrom(CreateProvider(new HashSet<string>(StringComparer.Ordinal))).Should().BeEmpty();
    }
}
