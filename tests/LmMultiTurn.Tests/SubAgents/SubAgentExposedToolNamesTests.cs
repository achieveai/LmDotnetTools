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
            source: source
        );

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
            .BeEquivalentTo([
                SubAgentToolProvider.SpawnToolName,
                SubAgentToolProvider.SendMessageToolName,
                SubAgentToolProvider.CheckAgentToolName,
                SubAgentToolProvider.WaitAgentToolName,
            ]);
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

    [Fact]
    public void AMisspelledName_IsRefusedAtComposition_NotSilentlyIgnored()
    {
        // The whole failure mode in one list: a host meant to grant two tools, typed one of them
        // wrong, and got a surface one tool short with nothing anywhere saying why. Composition is the
        // last point at which the typo is still next to the thing that wrote it.
        var act = () =>
            CreateProvider(
                new HashSet<string>(StringComparer.Ordinal) { SubAgentToolProvider.SpawnToolName, "SendMesage" }
            );

        act.Should()
            .Throw<ArgumentException>()
            .WithMessage("*SendMesage*")
            .Which.Message.Should()
            .Contain(
                SubAgentToolProvider.SendMessageToolName,
                "the refusal has to show the spelling that would have worked"
            );
    }

    [Fact]
    public void AnAllowListOfMisspellings_IsRefusedRatherThanYieldingNoToolsAtAll()
    {
        // The #635 shape: every name wrong, so the filter grants nothing and the agent looks exactly
        // like one configured to have no sub-agent tools.
        var act = () => CreateProvider(new HashSet<string>(StringComparer.Ordinal) { "agent", "sendmessage" });

        act.Should().Throw<ArgumentException>().WithMessage("*agent*sendmessage*");
    }

    [Fact]
    public void ACaseInsensitiveAllowList_IsAccepted_BecauseItReallyDoesGrant()
    {
        // Judged by what it selects, not by equality with a constant: this set's own comparer makes
        // "agent" match, so refusing it would refuse a configuration that works.
        var names = NamesFrom(CreateProvider(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "agent" }));

        names.Should().ContainSingle().Which.Should().Be(SubAgentToolProvider.SpawnToolName);
    }
}
