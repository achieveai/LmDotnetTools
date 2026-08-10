using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Workspace.Sandbox;

namespace CodeReviewDaemon.Sample.Tests.Orchestration;

/// <summary>
/// #108 — a review context artifact must never carry an empty diff for a pull request that changed files.
/// <para>
/// <c>git diff base...head</c> and <c>git diff --name-only base...head</c> are the same symmetric difference
/// asked two ways, so they cannot disagree about whether anything changed: a mode-only change still prints an
/// <c>old mode/new mode</c> header, a binary change still prints <c>Binary files … differ</c>. When the
/// listing names files and the diff is empty, the diff is the one that was lost.
/// </para>
/// <para>
/// #104 is how that happened in production — a capture race returned exit 0 with empty stdout, and the empty
/// diff went into the artifact with no exception and no warning. This is the second layer, and it is
/// deliberately independent of the first: it holds whether or not the race is fixed, and whatever else might
/// one day produce the same silence. Two layers, because the race fix is timing-dependent and the invariant
/// is not.
/// </para>
/// </summary>
public sealed class LostDiffGuardTests
{
    private const string DiffRange = "diff base-sha...head-sha";
    private const string NameOnly = "diff --name-only";

    private static ContextStageHarness Harness() =>
        new(ContextStageHarness.DedicatedMount, s2s: true);

    /// <summary>
    /// The bite. Nothing about the empty diff is detectable on its own — the guard exists because the
    /// changed-file listing contradicts it.
    /// </summary>
    [Fact]
    public async Task A_diff_that_came_back_empty_while_files_changed_fails_the_run()
    {
        using var harness = Harness();
        _ = harness.Runner
            .OnArgvContainsFirst(DiffRange, new SandboxCommandResult(0, string.Empty, string.Empty))
            .OnArgvContainsFirst(
                NameOnly, new SandboxCommandResult(0, "src/Foo.cs\nsrc/Bar.cs", string.Empty));
        var run = harness.SeedRun(isForkPr: false, isTargetRepoPublic: false);

        var act = async () =>
            await harness.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);

        var thrown = await act.Should().ThrowAsync<InvalidOperationException>(
            "handing the reviewer a pull request with no content to review produces a confident review of "
                + "nothing, and nothing downstream can tell that from a genuinely empty PR");
        thrown.Which.Message.Should().Contain(
            "2 changed file(s)", "the message has to carry the evidence that contradicts the empty diff");
    }

    /// <summary>
    /// The control that stops the guard being a blanket ban on empty diffs. A pull request really can change
    /// nothing — a branch reopened after its commits landed elsewhere, say — and gating on length alone would
    /// fail those runs forever while looking like rigour.
    /// </summary>
    [Fact]
    public async Task A_pull_request_that_genuinely_changed_nothing_still_reviews()
    {
        using var harness = Harness();
        _ = harness.Runner
            .OnArgvContainsFirst(DiffRange, new SandboxCommandResult(0, string.Empty, string.Empty))
            .OnArgvContainsFirst(NameOnly, new SandboxCommandResult(0, string.Empty, string.Empty));
        var run = harness.SeedRun(isForkPr: false, isTargetRepoPublic: false);

        var act = async () =>
            await harness.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);

        await act.Should().NotThrowAsync(
            "an empty diff is only evidence of a lost capture when something else says files changed");
    }

    /// <summary>
    /// The second control: an ordinary run is untouched, and the diff it carries is the one git produced.
    /// Without this, a guard that threw on everything would satisfy the test above.
    /// </summary>
    [Fact]
    public async Task An_ordinary_run_still_carries_its_diff()
    {
        using var harness = Harness();
        _ = harness.Runner
            .OnArgvContainsFirst(
                DiffRange,
                new SandboxCommandResult(0, "diff --git a/src/Foo.cs b/src/Foo.cs\n+ added", string.Empty))
            .OnArgvContainsFirst(NameOnly, new SandboxCommandResult(0, "src/Foo.cs", string.Empty));
        var run = harness.SeedRun(isForkPr: false, isTargetRepoPublic: false);

        await harness.Executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);

        harness.ContextPayload(run).GetProperty("Diff").GetString()
            .Should().Contain("+ added", "the guard must not alter what a healthy run hands over");
    }
}
