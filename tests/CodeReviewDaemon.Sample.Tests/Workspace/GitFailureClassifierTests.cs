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

    [Fact]
    public void Unrecognized_stderr_classifies_as_Unknown() =>
        GitFailureClassifier.Classify("something weird").Should().Be(GitFailureKind.Unknown);

    [Fact]
    public void Null_stderr_classifies_as_Unknown() =>
        GitFailureClassifier.Classify(null).Should().Be(GitFailureKind.Unknown);
}
