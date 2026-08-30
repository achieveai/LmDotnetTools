using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using AchieveAi.LmDotnetTools.Misc.Utils;
using LmStreaming.Sample.Services;

namespace LmStreaming.Sample.Tests;

/// <summary>
/// Spawn-path assembly tests for the mode-level <c>SubAgentRequiredTools</c> property (#623). The
/// composition root resolves the mode's patterns into <c>SubAgentOptions.RequiredToolNames</c>, and
/// <c>SubAgentManager</c> reads that in exactly one place, so asserting on the options the
/// composition root produces IS asserting on what every spawn enforces.
/// </summary>
public sealed class ProgramModeRequiredToolsTests
{
    private static AgentProfile Profile(IReadOnlyList<string>? requiredTools) =>
        new("mode-1", "Mode One", "primary prompt") { SubAgentRequiredTools = requiredTools };

    /// <summary>
    /// Runs the REAL composition-root path (non-test provider branch, no sandbox) so deleting the
    /// apply call in <c>BuildSubAgentOptionsAsync</c> — not just breaking the resolver — turns
    /// this red.
    /// </summary>
    private static async Task<SubAgentOptions?> BuildAsync(AgentProfile mode) =>
        await global::Program.BuildSubAgentOptionsAsync(
            isTestMode: false,
            testAgentBuilder: Mock.Of<LmStreaming.Sample.Services.ITestAgentBuilder>(),
            loggerFactory: NullLoggerFactory.Instance,
            providerAgentFactory: () => Mock.Of<IStreamingAgent>(),
            characteristicsAgentFactory: _ => throw new InvalidOperationException("not spawned here"),
            sandboxSession: null,
            workspaceLoader: null!,
            marketplaceLoader: null!,
            workspaceStore: null!,
            logger: NullLogger.Instance,
            mode: mode
        );

    [Fact]
    public async Task ModeWithRequiredTools_ResolvesThemIntoTheSpawnOptions()
    {
        var options = await BuildAsync(Profile(["tasks:*", "SendMessage"]));

        options.Should().NotBeNull();
        options!.RequiredToolNames.Should().NotBeNull();
        options.RequiredToolNames.Should().Contain("claim-task", "the tasks wildcard expands to the board family");
        options.RequiredToolNames.Should().Contain("SendMessage", "bare names pass through");
        options.RequiredToolNames!.Count.Should().Be(ModeSubAgentRequiredTools.TaskToolNames.Count + 1);
    }

    /// <summary>The opt-in pin: a mode WITHOUT the property changes nothing at spawn time.</summary>
    [Fact]
    public async Task ModeWithoutTheProperty_LeavesRequiredToolNamesNull()
    {
        var options = await BuildAsync(Profile(requiredTools: null));

        options.Should().NotBeNull();
        options!.RequiredToolNames.Should().BeNull("unset must be byte-for-byte today's behavior");
    }

    [Fact]
    public async Task ModeWithAnEmptyList_AlsoLeavesRequiredToolNamesNull()
    {
        var options = await BuildAsync(Profile(requiredTools: []));

        options!.RequiredToolNames.Should().BeNull("empty is the same 'not enforced' shape as unset");
    }

    [Fact]
    public void ToAgentProfile_CarriesSubAgentRequiredTools()
    {
        var mode = new ChatMode
        {
            Id = "m",
            Name = "M",
            SystemPrompt = "p",
            SubAgentRequiredTools = ["tasks:*", "claim-task"],
        };

        mode.ToAgentProfile().SubAgentRequiredTools.Should().Equal("tasks:*", "claim-task");
    }

    [Fact]
    public void ToAgentProfile_LeavesAbsentRequiredToolsNull()
    {
        var mode = new ChatMode
        {
            Id = "m",
            Name = "M",
            SystemPrompt = "p",
        };

        mode.ToAgentProfile().SubAgentRequiredTools.Should().BeNull();
    }

    [Fact]
    public void HasOpenTaskAssignedTo_SeesOpenAssignments_IncludingNestedOnes_AndIgnoresClosedOnes()
    {
        var taskManager = new TaskManager();
        _ = taskManager.BulkInitialize([
            new TaskManager.BulkTaskItem { Task = "Parent task", SubTasks = ["Nested task"] },
            new TaskManager.BulkTaskItem { Task = "Other task" },
        ]);

        var tasks = taskManager.GetTasks();
        var nested = tasks[0].SubTasks[0];
        var other = tasks[1];
        _ = taskManager.AssignTask(nested.Id, "boardworker");
        // Completion requires a live claim, so walk the real lifecycle: claim, then complete.
        _ = taskManager.UpdateTask(other.Id, "in progress", "doneworker");
        _ = taskManager.UpdateTask(other.Id, "completed");

        global::Program.HasOpenTaskAssignedTo(taskManager, "boardworker").Should().BeTrue("nested open assignment");
        global::Program
            .HasOpenTaskAssignedTo(taskManager, "doneworker")
            .Should()
            .BeFalse("a completed task is not an open assignment");
        global::Program.HasOpenTaskAssignedTo(taskManager, "stranger").Should().BeFalse();
    }
}
