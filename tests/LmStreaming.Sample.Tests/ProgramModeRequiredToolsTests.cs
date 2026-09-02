using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
using AchieveAi.LmDotnetTools.Misc.Utils;
using LmStreaming.Sample.Services;
using Microsoft.Extensions.Logging;

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

    private static AgentProfile ReviewPolicyProfile() =>
        new("review-mode", "Review Mode", "primary prompt")
        {
            SubAgentReasoningEffort = "xhigh",
            SubAgentModelIntelligenceByType = new Dictionary<string, int>
            {
                ["code-reviewer:architecture-review"] = 5,
                ["code-reviewer:duplicate-code-detector"] = 1,
            },
            DefaultSubAgentModelIntelligence = 3,
        };

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
    public async Task BuildSubAgentOptions_AppliesTheModeReasoningAndTypeRoutingPolicy()
    {
        var options = await BuildAsync(ReviewPolicyProfile());

        options!.ConversationEffortFloor.Should().Be(ReasoningEffort.Xhigh);
        options
            .SpawnTypeModelSelectionResolver!("code-reviewer:architecture-review")
            .Should()
            .Be(new SubAgentSpawnModelSelection(null, 5, "type-policy"));
        options
            .SpawnTypeModelSelectionResolver("code-reviewer:unmapped-review")
            .Should()
            .Be(new SubAgentSpawnModelSelection(null, 3, "type-policy-default"));
        options
            .SpawnTypeModelSelectionResolver("general-purpose")
            .Should()
            .BeNull("the Terra fallback is for review children and must not override unrelated templates");
    }

    [Fact]
    public async Task BuildSubAgentOptions_ModeWithoutPolicyPreservesTheExistingSpawnBehavior()
    {
        var options = await BuildAsync(Profile(requiredTools: null));

        options!.ConversationEffortFloor.Should().BeNull();
        options.SpawnTypeModelSelectionResolver.Should().BeNull();
    }

    [Fact]
    public void ToAgentProfile_CarriesTheModeReasoningAndTypeRoutingPolicy()
    {
        var mode = new ChatMode
        {
            Id = "m",
            Name = "M",
            SystemPrompt = "p",
            SubAgentReasoningEffort = "xhigh",
            SubAgentModelIntelligenceByType = new Dictionary<string, int> { ["code-reviewer:architecture-review"] = 5 },
            DefaultSubAgentModelIntelligence = 3,
        };

        var profile = mode.ToAgentProfile();
        profile.SubAgentReasoningEffort.Should().Be("xhigh");
        profile.SubAgentModelIntelligenceByType.Should().Contain("code-reviewer:architecture-review", 5);
        profile.DefaultSubAgentModelIntelligence.Should().Be(3);
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

    /// <summary>
    /// PR #626 review F-003: both sides of the compare are human-typed (assign-task's assignee,
    /// the spawn's name), so a casing mismatch must not silence the warning floor's probe.
    /// </summary>
    [Fact]
    public void HasOpenTaskAssignedTo_MatchesTheAssignee_CaseInsensitively()
    {
        var taskManager = new TaskManager();
        _ = taskManager.BulkInitialize([new TaskManager.BulkTaskItem { Task = "Board task" }]);
        _ = taskManager.AssignTask(taskManager.GetTasks()[0].Id, "boardworker");

        global::Program.HasOpenTaskAssignedTo(taskManager, "BoardWorker").Should().BeTrue();
    }

    /// <summary>
    /// PR #626 review F-004: the composition root turns an unresolvable required-tool pattern into
    /// a Warning naming the mode and the pattern — the only signal an operator hand-editing
    /// Prompts.yaml gets that a mode is typo'd rather than enforced.
    /// </summary>
    [Fact]
    public void ApplyModeRequiredTools_LogsAWarning_ForAnUnresolvablePattern()
    {
        var logger = new CapturingLogger<object>();
        var options = new SubAgentOptions { Templates = new Dictionary<string, SubAgentTemplate>() };

        var applied = global::Program.ApplyModeRequiredTools(options, Profile(["sandbox:*", "claim-task"]), logger);

        logger.CountAtLevel(LogLevel.Warning, "sandbox:*").Should().Be(1);
        logger.MessagesAtLevel(LogLevel.Warning)[0].Should().Contain("mode-1", "the line must name the mode");
        // The warning does not block what DID resolve.
        applied.RequiredToolNames.Should().Equal("claim-task");
    }

    [Fact]
    public void ApplyModeRequiredTools_LogsNothing_WhenEveryPatternResolves()
    {
        var logger = new CapturingLogger<object>();
        var options = new SubAgentOptions { Templates = new Dictionary<string, SubAgentTemplate>() };

        _ = global::Program.ApplyModeRequiredTools(options, Profile(["tasks:*", "claim-task"]), logger);

        logger.MessagesAtLevel(LogLevel.Warning).Should().BeEmpty();
    }
}
