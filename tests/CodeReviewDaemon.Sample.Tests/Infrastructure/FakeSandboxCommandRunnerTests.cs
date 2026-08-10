using CodeReviewDaemon.Sample.Workspace.Sandbox;

namespace CodeReviewDaemon.Sample.Tests.Infrastructure;

/// <summary>
/// Pins the answers <see cref="FakeSandboxCommandRunner"/> gives to commands no test scripted.
///
/// This file exists because the fixture's old blanket default — exit 0 with empty stdout — was
/// byte-identical to the capture race in #104, and 44 of the preparer tests passed only because that
/// default drove the reviewed-checkout head gate down its fail-open branch and returned early. Those
/// tests were green either way, so the suite could not see the difference: a fixture change here is
/// invisible from the suites that consume it, which is exactly why it needs assertions of its own.
///
/// The rule these tests encode: a command whose success implies OUTPUT must never be answered with a
/// silent success. Either the fake knows the answer (because production told it one command earlier)
/// or it fails loudly.
/// </summary>
public class FakeSandboxCommandRunnerTests
{
    private const string TargetDir = "/workspace/slot/target";
    private const string OwnerRepo = "/workspace/store/repo";
    private const string HeadSha = "0f1e2d3c4b5a69788796a5b4c3d2e1f00f1e2d3c";

    private static Task<SandboxCommandResult> RunAsync(
        FakeSandboxCommandRunner runner, params string[] argv) =>
        runner.RunAsync(new SandboxCommand(argv), CancellationToken.None);

    /// <summary>
    /// The whole point of the state tracking: production checks the PR head out and probes HEAD one
    /// command later, so the fake has been told the answer and does not have to guess a constant. It
    /// cannot guess one — the gate compares the reply against the run's own HeadSha, and the fake's
    /// dependents use 19 different values for it.
    /// </summary>
    [Fact]
    public async Task RevParseHead_AnswersTheCommitTheDirectoryWasCheckedOutTo()
    {
        var runner = new FakeSandboxCommandRunner();
        await RunAsync(runner, "-C", TargetDir, "checkout", "--force", "--detach", HeadSha);

        var head = await RunAsync(runner, "-C", TargetDir, "rev-parse", "HEAD");

        head.Succeeded.Should().BeTrue();
        head.Stdout.Trim().Should().Be(
            HeadSha,
            "production checks the sha out immediately before probing HEAD, so a fake that models git "
                + "answers from what it was told rather than from a hardcoded constant");
    }

    /// <summary>
    /// THE regression guard for the finding. An unscripted HEAD probe must not look like a successful
    /// read of an empty answer, because that is precisely the shape a lost capture produces — and a
    /// caller cannot tell the two apart. Failing loudly means the caller's fail-open branch is entered
    /// deliberately and says why, instead of being reached by a default that impersonates success.
    /// </summary>
    [Fact]
    public async Task RevParseHead_WithoutACheckout_FailsRatherThanReturningAnEmptySuccess()
    {
        var runner = new FakeSandboxCommandRunner();

        var head = await RunAsync(runner, "-C", TargetDir, "rev-parse", "HEAD");

        head.Succeeded.Should().BeFalse(
            "exit 0 with empty stdout is indistinguishable from the #104 capture race, so the fixture "
                + "must not produce it for a command whose success implies output");
        head.Stderr.Should().NotBeEmpty("the failure has to name why, or it is just a different silence");
        head.Stderr.Should().Contain(TargetDir, "the message must say which tree it could not answer for");
    }

    /// <summary>
    /// Real git leaves HEAD alone when the checkout fails, and so must the fake: a test that scripts a
    /// checkout failure is exercising the failure path, and it would be worthless if the tree it never
    /// checked out still satisfied the head gate afterwards.
    /// </summary>
    [Fact]
    public async Task AFailedCheckout_DoesNotMoveHead()
    {
        var runner = new FakeSandboxCommandRunner()
            .OnArgvContains("checkout", new SandboxCommandResult(1, string.Empty, "fatal: reference is not a tree"));

        await RunAsync(runner, "-C", TargetDir, "checkout", "--force", "--detach", HeadSha);
        var head = await RunAsync(runner, "-C", TargetDir, "rev-parse", "HEAD");

        head.Succeeded.Should().BeFalse(
            "the checkout failed, so nothing moved HEAD and the fake must not claim it landed on the sha");
    }

    /// <summary>
    /// <c>worktree add</c> moves HEAD in the tree it creates, not in the owner repo named by <c>-C</c>.
    /// Keying on the wrong one would let the store's HEAD answer for the reviewed target dir.
    /// </summary>
    [Fact]
    public async Task WorktreeAdd_RecordsHeadForTheNewTree_NotTheOwnerRepo()
    {
        var runner = new FakeSandboxCommandRunner();
        await RunAsync(
            runner, "-C", OwnerRepo, "worktree", "add", "--relative-paths", "--force", TargetDir,
            "--detach", HeadSha);

        var addedTree = await RunAsync(runner, "-C", TargetDir, "rev-parse", "HEAD");
        var owner = await RunAsync(runner, "-C", OwnerRepo, "rev-parse", "HEAD");

        addedTree.Stdout.Trim().Should().Be(HeadSha, "the new worktree is what was positioned");
        owner.Succeeded.Should().BeFalse("the owner repo's own HEAD was never checked out by that command");
    }

    /// <summary>
    /// Production checks a BRANCH out into the store root and a sha into the reviewed tree. Keying by
    /// directory is what stops the first from answering for the second.
    /// </summary>
    [Fact]
    public async Task ACheckoutInAnotherDirectory_DoesNotAnswerForThisOne()
    {
        var runner = new FakeSandboxCommandRunner();
        await RunAsync(runner, "-C", OwnerRepo, "checkout", "--force", "main");

        var target = await RunAsync(runner, "-C", TargetDir, "rev-parse", "HEAD");

        target.Succeeded.Should().BeFalse(
            "a branch checked out into the store root says nothing about the reviewed tree's HEAD");
    }

    /// <summary>
    /// A bare <c>checkout --detach</c> detaches in place; the commit does not change. Treating the
    /// absent ref as a position would record an empty sha and answer HEAD with it.
    /// </summary>
    [Fact]
    public async Task BareCheckoutDetach_LeavesTheCommitWhereItWas()
    {
        var runner = new FakeSandboxCommandRunner();
        await RunAsync(runner, "-C", TargetDir, "checkout", "--force", "--detach", HeadSha);
        await RunAsync(runner, "-C", TargetDir, "checkout", "--detach");

        var head = await RunAsync(runner, "-C", TargetDir, "rev-parse", "HEAD");

        head.Stdout.Trim().Should().Be(HeadSha, "detaching in place does not move the commit");
    }

    /// <summary>
    /// <c>git status --porcelain --branch</c> always prints a <c>##</c> header, clean or dirty — that is
    /// the property that makes an empty read detectable, and the reason #87 switches the probe to it.
    /// Slots run detached, so the fake reproduces the detached header shape rather than a plain
    /// <c>## &lt;branch&gt;</c> stub that would not cover the case production is actually in.
    /// </summary>
    [Fact]
    public async Task StatusPorcelainBranch_AlwaysEmitsAHeader_AndReportsDetachedAfterADetachedCheckout()
    {
        var attached = new FakeSandboxCommandRunner();
        var beforeAnyCheckout = await RunAsync(attached, "-C", TargetDir, "status", "--porcelain", "--branch");

        var detached = new FakeSandboxCommandRunner();
        await RunAsync(detached, "-C", TargetDir, "checkout", "--force", "--detach", HeadSha);
        var afterDetach = await RunAsync(detached, "-C", TargetDir, "status", "--porcelain", "--branch");

        beforeAnyCheckout.Stdout.Should().StartWith(
            "## ", "the header is unconditional, which is what makes empty stdout mean 'lost read'");
        afterDetach.Stdout.Trim().Should().Be(
            "## HEAD (no branch)",
            "a reviewed slot sits on a detached head, and that is the header git 2.53.0 prints for it");
    }

    /// <summary>Scripted rules are first-match-wins and must still outrank the modelled answers.</summary>
    [Fact]
    public async Task AScriptedRule_StillOverridesTheModelledAnswer()
    {
        var runner = new FakeSandboxCommandRunner()
            .OnArgvContains("rev-parse HEAD", new SandboxCommandResult(0, "scripted-sha\n", string.Empty));
        await RunAsync(runner, "-C", TargetDir, "checkout", "--force", "--detach", HeadSha);

        var head = await RunAsync(runner, "-C", TargetDir, "rev-parse", "HEAD");

        head.Stdout.Trim().Should().Be("scripted-sha", "the four tests that already script this must keep winning");
    }

    /// <summary>
    /// The counterweight: commands that genuinely succeed without output keep doing so. Production reads
    /// their exit code and never their stdout, so empty is the right answer there — widening the change
    /// to every unscripted command would break that and would not close the finding.
    /// </summary>
    [Theory]
    [InlineData("fetch", "origin")]
    [InlineData("clean", "-ffdx")]
    [InlineData("checkout", "--force")]
    public async Task CommandsThatSucceedSilentlyStillDo(string verb, string argument)
    {
        var runner = new FakeSandboxCommandRunner();

        var result = await RunAsync(runner, "-C", TargetDir, verb, argument);

        result.Succeeded.Should().BeTrue();
        result.Stdout.Should().BeEmpty(
            "these are read for their exit code, so silence is an answer rather than a lost capture");
    }
}
