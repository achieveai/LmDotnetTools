using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using LmStreaming.Sample.Services;

namespace LmStreaming.Sample.Tests.Services;

/// <summary>
/// Pattern-resolution pins for the mode's <c>SubAgentRequiredTools</c> language (#623):
/// <c>group:*</c> wildcards, qualified <c>group:tool</c> ids, and bare names, resolved to the
/// concrete bare tool names <c>SubAgentOptions.RequiredToolNames</c> consumes.
/// </summary>
public sealed class ModeSubAgentRequiredToolsTests
{
    [Fact]
    public void TasksWildcard_ResolvesToTheFullTaskManagerToolFamily()
    {
        var resolved = ModeSubAgentRequiredTools.Resolve(["tasks:*"]);

        // The family is enumerated from the real TaskManager registry, so this pins BOTH that the
        // wildcard expands and that the expansion tracks the actual [Function] surface (15 tools
        // today; the count assertion fails loudly if a task tool is added or removed, which is the
        // moment to re-check the enforcement story rather than silently drift).
        resolved.Should().NotBeNull();
        resolved!.Count.Should().Be(15);
        resolved.Should().Contain(["claim-task", "assign-task", "update-task", "list-tasks", "bulk-initialize"]);
        resolved
            .Should()
            .BeEquivalentTo(ModeSubAgentRequiredTools.TaskToolNames, "the wildcard IS the family, nothing else");
    }

    [Fact]
    public void SubAgentsWildcard_ResolvesToTheSubAgentToolSurface()
    {
        var resolved = ModeSubAgentRequiredTools.Resolve(["subagents:*"]);

        resolved.Should().BeEquivalentTo(SubAgentToolProvider.AllToolNames);
    }

    [Fact]
    public void QualifiedId_ResolvesToTheBareName_TheModelNeverSeesPrefixes()
    {
        ModeSubAgentRequiredTools
            .Resolve(["subagents:SendMessage", "tasks:claim-task"])
            .Should()
            .Equal("SendMessage", "claim-task");
    }

    [Fact]
    public void BareNames_PassThroughVerbatim_AndUnknownPrefixesStayPartOfTheName()
    {
        // An unknown prefix is NOT a group: it stays part of the name, so a typo'd group surfaces as
        // an unmatched (inert) name rather than silently resolving to a different tool.
        ModeSubAgentRequiredTools.Resolve(["claim-task", "typo:*", "  "]).Should().Equal("claim-task", "typo:*");
    }

    [Fact]
    public void DuplicatesAcrossPatterns_AreResolvedOnce()
    {
        ModeSubAgentRequiredTools.Resolve(["claim-task", "tasks:claim-task"]).Should().Equal("claim-task");
    }

    [Fact]
    public void NullOrEmpty_ResolvesToNull_TheNotEnforcedShape()
    {
        ModeSubAgentRequiredTools.Resolve(null).Should().BeNull();
        ModeSubAgentRequiredTools.Resolve([]).Should().BeNull();
        ModeSubAgentRequiredTools.Resolve(["", "   "]).Should().BeNull();
    }

    /// <summary>
    /// PR #626 review F-004: patterns that expand to nothing usable must be reported, not
    /// swallowed — a dynamic-group wildcard (<c>sandbox:*</c> has no static roster) and a typo'd
    /// group (<c>taks:*</c> does not split, so it passes through as an inert name).
    /// </summary>
    [Fact]
    public void UnresolvablePatterns_AreReported_AndValidOnesAreNot()
    {
        var reported = new List<string>();

        var resolved = ModeSubAgentRequiredTools.Resolve(
            ["tasks:*", "claim-task", "sandbox:*", "taks:*", "subagents:SendMessage"],
            reported.Add
        );

        reported.Should().Equal("sandbox:*", "taks:*");
        // Reporting does not change resolution behavior: the typo'd entry still passes through as
        // an inert name, and everything valid still resolves.
        resolved.Should().Contain(["claim-task", "SendMessage", "taks:*"]);
    }

    [Fact]
    public void BareNamesWithoutPatternSyntax_AreNeverReportedAsUnresolved()
    {
        var reported = new List<string>();

        _ = ModeSubAgentRequiredTools.Resolve(["claim-task", "get_weather"], reported.Add);

        reported.Should().BeEmpty("a plain bare name may legitimately match a runtime contract");
    }
}
