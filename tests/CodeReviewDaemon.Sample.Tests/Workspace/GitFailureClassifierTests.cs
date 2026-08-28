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
}
