using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using CodeReviewDaemon.Sample.Workspace.Git;
using CodeReviewDaemon.Sample.Workspace.Sandbox;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

/// <summary>
/// <see cref="ReviewBranchManager.DeleteReviewBranchAsync"/> against REAL git, mirroring
/// <see cref="ReviewBranchManagerRealGitTests"/>'s use of <see cref="RealGitFixture"/>. An argv matcher can
/// script a rejected push and a re-read that happens to disagree with it, but it cannot show the one thing
/// this method exists for: that <c>--force-with-lease=&lt;ref&gt;:&lt;expect&gt;</c> is a genuine server-side
/// compare-and-swap that a real <c>receive-pack</c> enforces even when the remote advances between the
/// precheck and the delete push. That needs a remote that really advances.
/// </summary>
public sealed class ReviewBranchManagerDeletionRealGitTests : LoggingTestBase
{
    private const string DefaultBranch = "main";
    private const string ReviewBranch = "review/widgets-42";

    public ReviewBranchManagerDeletionRealGitTests(ITestOutputHelper output)
        : base(output) { }

    [Fact]
    public async Task DeleteReviewBranchAsync_deletes_the_remote_and_local_branch_when_the_remote_is_at_the_expected_sha()
    {
        using var fixture = await RealGitFixture.CreateAsync();

        var seed = await fixture.CloneAsync("seed");
        RealGitFixture.Write(seed, "notes.txt", "initial\n");
        await fixture.GitAsync(seed, CancellationToken.None, "add", "-A");
        await fixture.GitAsync(seed, CancellationToken.None, "commit", "-m", "seed notes");
        await fixture.GitAsync(seed, CancellationToken.None, "checkout", "-b", ReviewBranch);
        await fixture.GitAsync(seed, CancellationToken.None, "push", "origin", ReviewBranch);
        var expectedSha = (await fixture.GitAsync(seed, CancellationToken.None, "rev-parse", ReviewBranch)).Trim();

        var store = await fixture.CloneAsync("store");
        // A pooled store clone can hold a local branch alongside the remote one; the delete must drop both.
        await fixture.GitAsync(store, CancellationToken.None, "branch", ReviewBranch, $"origin/{ReviewBranch}");

        var manager = CreateManager(fixture);

        var outcome = await manager.DeleteReviewBranchAsync(store, ReviewBranch, expectedSha, CancellationToken.None);

        outcome.Should().Be(ReviewBranchDeletionOutcome.Deleted);

        var remoteAfter = await fixture.GitAsync(
            store,
            CancellationToken.None,
            "ls-remote",
            "--heads",
            "origin",
            $"refs/heads/{ReviewBranch}"
        );
        remoteAfter.Trim().Should().BeEmpty("the guarded push --delete must have removed the ref on origin");

        var localAfter = await fixture.GitAsync(store, CancellationToken.None, "branch", "--list", ReviewBranch);
        localAfter
            .Trim()
            .Should()
            .BeEmpty("the local branch is only dropped once the remote is proven absent, and it now is");
    }

    [Fact]
    public async Task DeleteReviewBranchAsync_is_a_noop_when_the_branch_was_never_pushed()
    {
        using var fixture = await RealGitFixture.CreateAsync();

        var seed = await fixture.CloneAsync("seed");
        RealGitFixture.Write(seed, "notes.txt", "initial\n");
        await fixture.GitAsync(seed, CancellationToken.None, "add", "-A");
        await fixture.GitAsync(seed, CancellationToken.None, "commit", "-m", "seed notes");
        await fixture.GitAsync(seed, CancellationToken.None, "push", "origin", DefaultBranch);
        var someRealSha = (await fixture.GitAsync(seed, CancellationToken.None, "rev-parse", DefaultBranch)).Trim();

        // The review branch was never pushed to origin, so the store clone never sees it either.
        var store = await fixture.CloneAsync("store");
        var manager = CreateManager(fixture);

        var outcome = await manager.DeleteReviewBranchAsync(store, ReviewBranch, someRealSha, CancellationToken.None);

        outcome.Should().Be(ReviewBranchDeletionOutcome.AlreadyAbsent);

        fixture
            .Commands.Should()
            .NotContain(
                c => c.Contains("push", StringComparison.Ordinal),
                "the ls-remote precheck must short-circuit on 'no matching ref' before any delete push is attempted"
            );
    }

    [Fact]
    public async Task DeleteReviewBranchAsync_refuses_to_delete_when_the_remote_is_at_a_different_sha()
    {
        using var fixture = await RealGitFixture.CreateAsync();

        var seed = await fixture.CloneAsync("seed");
        RealGitFixture.Write(seed, "notes.txt", "initial\n");
        await fixture.GitAsync(seed, CancellationToken.None, "add", "-A");
        await fixture.GitAsync(seed, CancellationToken.None, "commit", "-m", "seed notes");
        await fixture.GitAsync(seed, CancellationToken.None, "push", "origin", DefaultBranch);
        var recordedSha = (await fixture.GitAsync(seed, CancellationToken.None, "rev-parse", DefaultBranch)).Trim();

        await fixture.GitAsync(seed, CancellationToken.None, "checkout", "-b", ReviewBranch);
        RealGitFixture.Write(seed, "notes.txt", "revised\n");
        await fixture.GitAsync(seed, CancellationToken.None, "add", "-A");
        await fixture.GitAsync(seed, CancellationToken.None, "commit", "-m", "pr notes");
        await fixture.GitAsync(seed, CancellationToken.None, "push", "origin", ReviewBranch);
        var actualSha = (await fixture.GitAsync(seed, CancellationToken.None, "rev-parse", ReviewBranch)).Trim();

        actualSha.Should().NotBe(recordedSha, "the mismatch case needs two genuinely different real commits");

        var store = await fixture.CloneAsync("store");
        await fixture.GitAsync(store, CancellationToken.None, "branch", ReviewBranch, $"origin/{ReviewBranch}");

        var manager = CreateManager(fixture);

        var outcome = await manager.DeleteReviewBranchAsync(store, ReviewBranch, recordedSha, CancellationToken.None);

        outcome.Should().Be(ReviewBranchDeletionOutcome.RemoteShaMismatch);

        var remoteAfter = await fixture.GitAsync(
            store,
            CancellationToken.None,
            "ls-remote",
            "--heads",
            "origin",
            $"refs/heads/{ReviewBranch}"
        );
        remoteAfter.Should().Contain(actualSha, "a refused delete must leave origin's ref exactly where it was");

        var localAfter = await fixture.GitAsync(store, CancellationToken.None, "branch", "--list", ReviewBranch);
        localAfter
            .Should()
            .Contain(ReviewBranch, "the local branch must not be dropped when the remote was never proven absent");
    }

    /// <summary>
    /// The race this method exists to close: the precheck reads the remote at SHA X, then — before the
    /// guarded delete push runs — a second clone lands a new commit on the SAME branch, advancing origin to
    /// SHA Y. A plain <c>push --delete</c> would destroy that concurrent commit; the
    /// <c>--force-with-lease=&lt;ref&gt;:X</c> compare-and-swap must instead be rejected as stale, and the
    /// read-back must classify from the branch's real (advanced) state rather than from the push's exit
    /// code.
    /// </summary>
    [Fact]
    public async Task DeleteReviewBranchAsync_refuses_the_delete_when_the_remote_advances_after_the_precheck()
    {
        using var fixture = await RealGitFixture.CreateAsync();

        var seed = await fixture.CloneAsync("seed");
        RealGitFixture.Write(seed, "notes.txt", "initial\n");
        await fixture.GitAsync(seed, CancellationToken.None, "add", "-A");
        await fixture.GitAsync(seed, CancellationToken.None, "commit", "-m", "seed notes");
        await fixture.GitAsync(seed, CancellationToken.None, "checkout", "-b", ReviewBranch);
        await fixture.GitAsync(seed, CancellationToken.None, "push", "origin", ReviewBranch);
        var shaX = (await fixture.GitAsync(seed, CancellationToken.None, "rev-parse", ReviewBranch)).Trim();

        var store = await fixture.CloneAsync("store");
        await fixture.GitAsync(store, CancellationToken.None, "branch", ReviewBranch, $"origin/{ReviewBranch}");

        // Positioned ahead of time so the race callback below only has to commit + push, not clone/checkout —
        // those extra git calls would otherwise run through the raced runner too and could themselves match
        // "ls-remote" or "push" and confuse the single-fire guard.
        var racer = await fixture.CloneAsync("racer");
        await fixture.GitAsync(racer, CancellationToken.None, "checkout", ReviewBranch);

        string? shaY = null;
        var raceRunner = new LsRemoteRaceRunner(
            fixture.Runner,
            async ct =>
            {
                RealGitFixture.Write(racer, "race.txt", "advanced-by-a-concurrent-push\n");
                await fixture.GitAsync(racer, ct, "add", "-A");
                await fixture.GitAsync(racer, ct, "commit", "-m", "race: advance the branch under the lease");
                await fixture.GitAsync(racer, ct, "push", "origin", ReviewBranch);
                shaY = (await fixture.GitAsync(racer, ct, "rev-parse", ReviewBranch)).Trim();
            }
        );

        var manager = new ReviewBranchManager(
            new GitRunner(raceRunner),
            new HostFileSystem(),
            LoggerFactory.CreateLogger<ReviewBranchManager>()
        );

        var outcome = await manager.DeleteReviewBranchAsync(store, ReviewBranch, shaX, CancellationToken.None);

        outcome.Should().Be(ReviewBranchDeletionOutcome.RemoteShaMismatch);
        shaY.Should().NotBeNullOrEmpty("the race callback must have actually run and landed a real commit");
        shaY.Should().NotBe(shaX, "the race only means something if the branch genuinely moved");

        var remoteAfter = await fixture.GitAsync(
            store,
            CancellationToken.None,
            "ls-remote",
            "--heads",
            "origin",
            $"refs/heads/{ReviewBranch}"
        );
        remoteAfter.Should().Contain(shaY!, "the concurrent commit must survive — the lease exists to protect it");

        // The compare-and-swap is what makes the refusal safe rather than merely lucky: pin that the expected
        // old value actually travelled with the delete push.
        fixture
            .Commands.Should()
            .Contain(
                c =>
                    c.Contains(
                        $"push --force-with-lease=refs/heads/{ReviewBranch}:{shaX} origin --delete {ReviewBranch}",
                        StringComparison.Ordinal
                    ),
                "the guarded delete must have sent the precheck's SHA as the lease's expected old value"
            );
    }

    private ReviewBranchManager CreateManager(RealGitFixture fixture) =>
        new(new GitRunner(fixture.Runner), new HostFileSystem(), LoggerFactory.CreateLogger<ReviewBranchManager>());

    /// <summary>
    /// Decorates a real <see cref="ISandboxCommandRunner"/> to inject a one-time side effect right after the
    /// FIRST command whose argv contains <c>ls-remote</c> completes — used to advance the remote branch
    /// between <see cref="ReviewBranchManager.DeleteReviewBranchAsync"/>'s precheck and its guarded delete
    /// push, while still returning that precheck's own (pre-race) result to the caller.
    /// </summary>
    private sealed class LsRemoteRaceRunner : ISandboxCommandRunner
    {
        private readonly ISandboxCommandRunner _inner;
        private readonly Func<CancellationToken, Task> _onFirstLsRemote;
        private bool _fired;

        public LsRemoteRaceRunner(ISandboxCommandRunner inner, Func<CancellationToken, Task> onFirstLsRemote)
        {
            _inner = inner;
            _onFirstLsRemote = onFirstLsRemote;
        }

        public async Task<SandboxCommandResult> RunAsync(SandboxCommand command, CancellationToken cancellationToken)
        {
            var result = await _inner.RunAsync(command, cancellationToken).ConfigureAwait(false);
            if (!_fired && command.Argv.Contains("ls-remote"))
            {
                _fired = true;
                await _onFirstLsRemote(cancellationToken).ConfigureAwait(false);
            }

            return result;
        }
    }
}
