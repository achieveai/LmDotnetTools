using LmStreaming.Sample.Services;

namespace LmStreaming.Sample.Tests.Services;

/// <summary>
/// PR #660 Revobot F-002: a deterministic, non-Skippable proof that the code-review-daemon
/// primary prompt's date + mode + workspace composition survives without a live sandbox gateway.
/// Unlike the earlier version of this test, every step below calls the REAL production code Program.cs's
/// agent factory calls, including its own one-line composition glue (extracted to
/// <c>Program.ApplyWorkspaceSuffix</c> for exactly this purpose) - not a hand-reimplementation of it.
/// The E2E SkippableFact
/// (<c>LmStreaming.Sample.E2E.Tests.Scenarios.SystemPromptCompositionTests.CodeReviewDaemonMode_ComposesDateThenModeThenWorkspaceThenAppendix_InOrder</c>)
/// remains the only proof of the outbound wire hop; this test closes the remaining evidence gap
/// (the composition glue itself) that gap left unverified in ordinary CI.
/// </summary>
public class CodeReviewDaemonPromptCompositionTests
{
    [Fact]
    public void ComposesDateThenModePromptThenWorkspaceSuffix_WithKnowledgeBaseContentIntact()
    {
        AgentProfile mode = SystemChatModes.GetById(SystemChatModes.CodeReviewDaemonModeId)!;
        var now = DateTimeOffset.Parse("2026-01-15T00:00:00Z");

        var dated = mode with { SystemPrompt = SystemPromptAugmenter.PrependCurrentDate(mode.SystemPrompt, now) };
        var wsSuffix = global::Program.BuildWorkspaceSuffix("/host/workspaces/review-1", sandboxToolAllowList: null);
        var effectiveMode = global::Program.ApplyWorkspaceSuffix(dated, wsSuffix);
        var composed = effectiveMode.SystemPrompt;

        composed.Should().StartWith("The current date is", "PrependCurrentDate runs first, at the mode entry point");
        composed.Should().Contain("Revobot", "the mode prompt must survive composition");
        composed.Should().Contain("Your workspace directory is:", "the workspace enrichment must be appended");
        composed
            .Should()
            .Contain(
                "/workspace/store/KnowledgeBase/",
                "issue #648 ruling: the fixed exact-path KB navigation must survive composition"
            );
        composed
            .Should()
            .Contain(
                "do NOT start from a KnowledgeBase/_toc.md file",
                "the no-search/no-_toc.md-start rule must survive composition"
            );
        composed
            .Should()
            .NotContain(
                "Start with KnowledgeBase/_toc.md",
                "the superseded relative-navigation wording must not return"
            );

        var dateIndex = composed.IndexOf("The current date is", StringComparison.Ordinal);
        var modeIndex = composed.IndexOf("Revobot", StringComparison.Ordinal);
        var workspaceIndex = composed.IndexOf("Your workspace directory is:", StringComparison.Ordinal);
        dateIndex.Should().Be(0, "the date line is prepended first");
        modeIndex.Should().BeGreaterThan(dateIndex, "the mode prompt follows the date line");
        workspaceIndex.Should().BeGreaterThan(modeIndex, "the workspace enrichment follows the mode prompt");
    }
}
