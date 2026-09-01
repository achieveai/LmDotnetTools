using LmStreaming.Sample.Services;

namespace LmStreaming.Sample.Tests.Services;

/// <summary>
/// PR #660 Revobot F-002: a deterministic, non-Skippable proof that the code-review-daemon
/// primary prompt's date + mode + workspace composition survives without a live sandbox gateway.
/// <c>LmStreaming.Sample.E2E.Tests.Scenarios.SystemPromptCompositionTests.CodeReviewDaemonMode_ComposesDateThenModeThenWorkspaceThenAppendix_InOrder</c>
/// proves the same ordering end-to-end at the outbound wire, but is gated on a real gateway and is
/// genuinely skipped in ordinary CI (confirmed: no gateway is configured there). This test combines
/// the same REAL, already-unit-tested production pieces Program.cs's agent factory applies in order
/// (<see cref="SystemPromptAugmenter.PrependCurrentDate"/>, then, because the mode needs a sandbox,
/// <c>Program.BuildWorkspaceSuffix</c> appended to the mode's own prompt) and asserts
/// the composed text still carries the #648 fixed-exact-path Knowledge Base contract, in order, on
/// every machine.
/// </summary>
/// <remarks>
/// Scope: this does not execute Program.cs's own one-line composition statement
/// (<c>mode with { SystemPrompt = mode.SystemPrompt + wsSuffix }</c>) or the outbound wire hop the
/// SkippableFact above proves - it proves the real text each of those steps would combine. A
/// regression in the yaml content, the date-prepend, or the workspace-suffix wording fails here
/// without a gateway; the one remaining gap (that exact glue statement) is simple enough that
/// building an in-process sandbox-gateway fake, or extracting a seam from Program.cs's ~1100-line
/// agent-factory lambda, was judged disproportionate for closing it.
/// </remarks>
public class CodeReviewDaemonPromptCompositionTests
{
    [Fact]
    public void ComposesDateThenModePromptThenWorkspaceSuffix_WithKnowledgeBaseContentIntact()
    {
        var mode = SystemChatModes.GetById(SystemChatModes.CodeReviewDaemonModeId)!;
        var now = DateTimeOffset.Parse("2026-01-15T00:00:00Z");

        var datedPrompt = SystemPromptAugmenter.PrependCurrentDate(mode.SystemPrompt, now);
        var workspaceSuffix = global::Program.BuildWorkspaceSuffix(
            "/host/workspaces/review-1",
            sandboxToolAllowList: null
        );
        var composed = datedPrompt + workspaceSuffix;

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
