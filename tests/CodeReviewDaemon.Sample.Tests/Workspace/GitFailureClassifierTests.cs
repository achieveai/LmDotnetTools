using CodeReviewDaemon.Sample.Workspace.Git;

namespace CodeReviewDaemon.Sample.Tests.Workspace;

/// <summary>
/// Pins <see cref="GitFailureClassifier"/>'s stderr → {Transient|Corrupt|Unknown} routing — the decision that
/// tells the ContextReady recovery ladder whether to retry the warm store (transient) or re-clone it
/// (corrupt). The corrupt cases include the exact stderr from the 2026-07-12 incident.
/// </summary>
public class GitFailureClassifierTests
{
    [Theory]
    [InlineData("fatal: Unable to create '/x/.git/index.lock': File exists.")]
    [InlineData("error: object file .git/objects/ab/cd is empty")]
    [InlineData("fatal: not a git repository")]
    [InlineData("error: Your local changes to the following files would be overwritten by checkout")]
    public void Corrupt_slot_stderr_classifies_as_Corrupt(string stderr) =>
        GitFailureClassifier.Classify(stderr).Should().Be(GitFailureKind.Corrupt);

    [Theory]
    [InlineData("fatal: unable to access 'https://x': Could not resolve host: github.com")]
    [InlineData("fatal: unable to access 'https://x': The requested URL returned error: 503")]
    [InlineData("ssh: connect to host x port 22: Connection timed out")]
    public void Transient_network_stderr_classifies_as_Transient(string stderr) =>
        GitFailureClassifier.Classify(stderr).Should().Be(GitFailureKind.Transient);

    [Theory]
    [InlineData("error: unable to create file foo/bar: Permission denied")] // a perm/disk error, not local corruption
    [InlineData("remote: The repository is empty.")] // a normal empty remote, not local corruption
    public void Non_corruption_stderr_containing_broad_fragments_is_not_Corrupt(string stderr) =>
        GitFailureClassifier.Classify(stderr).Should().NotBe(GitFailureKind.Corrupt);

    [Theory]
    [InlineData("fatal: not a git repository: sub/../.git/modules/sub")]
    [InlineData(@"fatal: not a git repository: sub/../.git\modules\sub")]
    public void A_deinitialized_submodules_missing_gitdir_is_not_Corrupt(string stderr) =>
        // A prior lease can leave a submodule registered with its `.git/modules/<name>` gitdir removed. Read as
        // corruption, that drove SlotHygiene into a second force-reset pass and then a re-clone — of a store
        // whose superproject was never broken, and which the re-clone could not have fixed anyway, because
        // hygiene leaves a deinit'd submodule for the review's own policy-enforced initializer to re-establish.
        GitFailureClassifier.Classify(stderr).Should().NotBe(GitFailureKind.Corrupt);

    [Fact]
    public void A_missing_nested_gitdir_alongside_real_corruption_still_classifies_as_Corrupt() =>
        // The subtraction removes exactly one marker, not the whole verdict: a stderr that also carries a stuck
        // lock is still a slot a fresh clone repairs.
        GitFailureClassifier
            .Classify(
                "fatal: not a git repository: sub/../.git/modules/sub\n"
                    + "fatal: Unable to create '/store/.git/modules/repos/X/index.lock': File exists."
            )
            .Should()
            .Be(GitFailureKind.Corrupt);

    [Fact]
    public void Unrecognized_stderr_classifies_as_Unknown() =>
        GitFailureClassifier.Classify("something weird").Should().Be(GitFailureKind.Unknown);

    [Fact]
    public void Null_stderr_classifies_as_Unknown() =>
        GitFailureClassifier.Classify(null).Should().Be(GitFailureKind.Unknown);

    // --- issue #582: the deinit carve-out must consult a filesystem oracle, not the message shape alone ---

    [Theory]
    [InlineData("fatal: not a git repository: sub/../.git/modules/sub")]
    [InlineData(@"fatal: not a git repository: sub/../.git\modules\sub")]
    public void A_confirmed_absent_nested_gitdir_still_takes_the_benign_carve_out(string stderr) =>
        // nestedGitDirExists: false — a caller that probed and found nothing. The #279 deinit case: unchanged.
        GitFailureClassifier.Classify(stderr, nestedGitDirExists: false).Should().NotBe(GitFailureKind.Corrupt);

    [Theory]
    [InlineData("fatal: not a git repository: sub/../.git/modules/sub")]
    [InlineData(@"fatal: not a git repository: sub/../.git\modules\sub")]
    public void An_unprobed_nested_gitdir_defaults_to_the_benign_carve_out(string stderr) =>
        // nestedGitDirExists: null (the default) — every existing caller that never passes the oracle keeps
        // today's behavior, so this fix cannot regress a caller it was not wired into.
        GitFailureClassifier.Classify(stderr).Should().NotBe(GitFailureKind.Corrupt);

    [Theory]
    [InlineData("fatal: not a git repository: sub/../.git/modules/sub")]
    [InlineData(@"fatal: not a git repository: sub/../.git\modules\sub")]
    public void A_confirmed_present_nested_gitdir_classifies_as_Corrupt(string stderr) =>
        // nestedGitDirExists: true — the mcqdb shape: the gitdir is genuinely there, so the byte-identical
        // "not a git repository" message is corruption, not a deinit, and must condemn the slot.
        GitFailureClassifier.Classify(stderr, nestedGitDirExists: true).Should().Be(GitFailureKind.Corrupt);

    [Fact]
    public void A_confirmed_present_nested_gitdir_does_not_suppress_an_unrelated_corrupt_marker() =>
        // The oracle only ever removes the ONE subtraction the deinit carve-out makes; it must never widen what
        // Classify considers benign beyond that single marker.
        GitFailureClassifier
            .Classify(
                "fatal: Unable to create '/store/.git/modules/repos/X/index.lock': File exists.",
                nestedGitDirExists: true
            )
            .Should()
            .Be(GitFailureKind.Corrupt);

    [Theory]
    [InlineData("/store/sub/../.git/modules/sub", "/store/.git/modules/sub")]
    [InlineData(@"/store/sub/../.git\modules\sub", "/store/.git/modules/sub")]
    public void ResolveNestedGitDirPath_collapses_a_relative_message_against_the_working_directory(
        string relativeTail,
        string expected
    ) =>
        GitFailureClassifier
            .ResolveNestedGitDirPath("/store", $"fatal: not a git repository: {relativeTail}")
            .Should()
            .Be(expected);

    [Fact]
    public void ResolveNestedGitDirPath_uses_an_absolute_message_as_is_without_reapplying_the_working_directory() =>
        // Measured on git 2.53: an absolute `-C <target>` failure prints the FULLY resolved absolute path
        // (uncollapsed `..` included), not a path relative to the working directory — combining it with the
        // working directory again would double the submodule segment.
        GitFailureClassifier
            .ResolveNestedGitDirPath("/unrelated/cwd", "fatal: not a git repository: /store/sub/../.git/modules/sub")
            .Should()
            .Be("/store/.git/modules/sub");

    [Fact]
    public void ResolveNestedGitDirPath_returns_null_for_a_message_that_does_not_match_the_shape() =>
        GitFailureClassifier.ResolveNestedGitDirPath("/store", "fatal: not a git repository").Should().BeNull();

    [Fact]
    public void ResolveNestedGitDirPath_returns_null_for_null_stderr() =>
        GitFailureClassifier.ResolveNestedGitDirPath("/store", null).Should().BeNull();
}
