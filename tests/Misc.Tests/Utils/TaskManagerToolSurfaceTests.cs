using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.Misc.Utils;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.Misc.Tests.Utils;

/// <summary>
///     The todo-board tool family is a fixed surface of fifteen tools, and its size is a measured
///     quantity: every mode that enables the board pays for all fifteen contracts in the input tokens
///     of every turn, and the evaluation corpus hashes the vocabulary, so a sixteenth tool silently
///     invalidates recorded runs (#669 wave, shared decisions 3 and 15).
/// </summary>
/// <remarks>
///     This is a pinning test, not a proof of a fix: it is green before and after the change that
///     added it. Its value is the mutation — adding a <c>[Function]</c> method to
///     <see cref="TaskManager" /> reddens it — so a tool cannot be added without someone deciding to.
///     <c>release-task</c> is named explicitly because #672 proposed it and the lead dropped it:
///     it duplicates <c>update-task status='not started'</c>, so it would buy nothing for the cost.
/// </remarks>
public class TaskManagerToolSurfaceTests
{
    private static IReadOnlyList<string> ToolNames()
    {
        var registry = new FunctionRegistry();
        _ = registry.AddFunctionsFromObject(new TaskManager(), providerName: "TaskManager");
        var (contracts, _) = registry.Build();
        return [.. contracts.Select(contract => contract.Name)];
    }

    [Fact]
    public void TheBoardExposesExactlyFifteenTools()
    {
        ToolNames().Should().HaveCount(15);
    }

    [Fact]
    public void TheBoardDoesNotExposeReleaseTask()
    {
        ToolNames().Should().NotContain("release-task");
    }
}
