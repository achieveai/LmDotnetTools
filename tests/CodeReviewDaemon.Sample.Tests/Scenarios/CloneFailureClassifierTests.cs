using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
using CodeReviewDaemon.Sample.Workspace.ReviewBot;
using Xunit.Abstractions;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

/// <summary>
/// Thread #3 (PR #121) — <c>reviewbot init</c> requires the ReviewBot remote to already exist (it does
/// not create it). When the clone of that pre-created remote fails, the operator needs a precise reason,
/// not a generic "clone failed". <see cref="CloneFailureClassifier"/> maps git's stderr + exit code to a
/// distinct diagnostic (not-found vs permission vs bad-credential vs transient-gateway vs unknown), each
/// with its own actionable message and process exit code, so the failure is self-explanatory.
/// </summary>
public sealed class CloneFailureClassifierTests : LoggingTestBase
{
    public CloneFailureClassifierTests(ITestOutputHelper output)
        : base(output)
    {
    }

    [Fact]
    public void Repository_not_found_is_classified_as_not_found()
    {
        var diagnosis = CloneFailureClassifier.Classify(
            exitCode: 128,
            stderr: "remote: Repository not found.\nfatal: repository 'https://github.com/acme/reviewbot.git/' not found");

        diagnosis.Kind.Should().Be(CloneFailureKind.RepositoryNotFound);
        diagnosis.ExitCode.Should().Be(CloneFailureClassifier.NotFoundExitCode);
        diagnosis.Message.Should().Contain("pre-created", "the narrowed contract requires an existing remote");
    }

    [Fact]
    public void Permission_denied_is_classified_as_permission()
    {
        var diagnosis = CloneFailureClassifier.Classify(
            exitCode: 128,
            stderr: "remote: Permission to acme/reviewbot.git denied to bot.\nfatal: unable to access");

        diagnosis.Kind.Should().Be(CloneFailureKind.PermissionDenied);
        diagnosis.ExitCode.Should().Be(CloneFailureClassifier.PermissionExitCode);
    }

    [Fact]
    public void Authentication_failure_is_classified_as_bad_credential()
    {
        var diagnosis = CloneFailureClassifier.Classify(
            exitCode: 128,
            stderr: "fatal: Authentication failed for 'https://github.com/acme/reviewbot.git/'");

        diagnosis.Kind.Should().Be(CloneFailureKind.BadCredential);
        diagnosis.ExitCode.Should().Be(CloneFailureClassifier.BadCredentialExitCode);
    }

    [Fact]
    public void Connection_failure_is_classified_as_transient_gateway()
    {
        var diagnosis = CloneFailureClassifier.Classify(
            exitCode: 128,
            stderr: "fatal: unable to access 'https://github.com/...': Could not resolve host: github.com");

        diagnosis.Kind.Should().Be(CloneFailureKind.TransientGateway);
        diagnosis.ExitCode.Should().Be(CloneFailureClassifier.TransientExitCode);
    }

    [Fact]
    public void An_unrecognized_failure_is_classified_as_unknown_and_surfaces_the_stderr()
    {
        var diagnosis = CloneFailureClassifier.Classify(
            exitCode: 1,
            stderr: "fatal: something nobody anticipated");

        diagnosis.Kind.Should().Be(CloneFailureKind.Unknown);
        diagnosis.ExitCode.Should().Be(CloneFailureClassifier.UnknownExitCode);
        diagnosis.Message.Should().Contain("something nobody anticipated", "an unknown failure must still surface the raw stderr");
    }
}
