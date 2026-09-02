using System.Reflection;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using AchieveAi.LmDotnetTools.Misc.Utils;
using TodoEval.Runner.Metrics;

namespace TodoEval.Runner.Tests;

/// <summary>
/// The Runner deliberately references NOTHING from the host, so its two tool vocabularies are
/// hand-mirrored copies. A copy drifts silently: a tool renamed in the host would simply stop being
/// counted, and the eval would report an improvement that is really a measurement hole. These tests
/// are the drift alarm - the test project alone takes the host references to ring it.
/// </summary>
public class VocabularyParityTests
{
    [Fact]
    public void TaskTools_MatchTheBoardToolsTaskManagerActuallyExposes()
    {
        var exposed = typeof(TaskManager)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(m => m.GetCustomAttribute<FunctionAttribute>())
            .Where(a => a is not null)
            .Select(a => a!.FunctionName)
            .ToList();

        exposed.Should().NotContainNulls("every board [Function] names itself explicitly");
        exposed.Should().BeEquivalentTo(TaskTools.All);
    }

    [Fact]
    public void CoordinationTools_MatchTheSubAgentToolProvidersUnion()
    {
        CoordinationTools.All.Should().BeEquivalentTo(SubAgentToolProvider.AllToolNames);
    }

    [Fact]
    public void SubAgentThreadPrefix_MatchesTheOneTheLibraryMints()
    {
        // The Runner decides which threads are sub-agent threads - and therefore what `primaryTurns`
        // and `subAgentCount` mean - from this hand-copied prefix. #710 renamed the rest of the thread
        // id under the wave and left the prefix alone; the next such change would silently reclassify
        // every sub-agent thread as a root one, and every affected count would move without failing.
        ConversationStoreReader.SubAgentDirPrefix.Should().Be(SubAgentThreadIds.Prefix);
    }

    [Fact]
    public void TheTwoFamilies_DoNotOverlap()
    {
        // An overlap would make Classify order-dependent and silently move a tool between the
        // task and coordination totals.
        TaskTools.All.Should().NotIntersectWith(CoordinationTools.All);
    }

    [Fact]
    public void RowOrder_IsEveryToolOfBothFamilies_TaskFirst()
    {
        // The report's row order is part of the committed output; pinning it keeps a diff of two
        // sweeps readable.
        ToolFamilies.RowOrder.Should().Equal([.. TaskTools.All, .. CoordinationTools.All]);
    }

    [Theory]
    [InlineData("add-task", "task")]
    [InlineData("WaitForAgents", "coordination")]
    [InlineData("web-search", "other")]
    public void Classify_NamesTheFamilyTheReportPrints(string tool, string expected)
    {
        ToolFamilies.Name(ToolFamilies.Classify(tool)).Should().Be(expected);
    }

    [Fact]
    public void Classify_IsCaseSensitive_BecauseTheStoreRecordsExactFunctionNames()
    {
        // "checkagents" is not a tool this host has; treating it as one would invent calls.
        ToolFamilies.Classify("checkagents").Should().Be(ToolFamily.Other);
        ToolFamilies.Classify("Add-Task").Should().Be(ToolFamily.Other);
    }
}
