using FluentAssertions;

namespace LmStreaming.Sample.Browser.E2E.Tests.Infrastructure;

/// <summary>
/// Deterministic, gateway-free coverage of <see cref="HostWorkspacePathVerifier.Verify"/> — the pure
/// decision logic <see cref="GitHubClonePrerequisites.VerifyHostVerifiableWorkspaceAsync"/> uses to
/// gate <c>SandboxGitCloneInstructionChainTests</c> on a genuinely host-verifiable adopted gateway.
/// </summary>
public sealed class HostWorkspacePathVerifierTests
{
    [Fact]
    public void Verify_rejects_the_fixed_Docker_backend_mount_point()
    {
        var result = HostWorkspacePathVerifier.Verify("/workspace", @"B:\sandbox-workspaces\e2e-clone-abc12345");

        // Rejected on either host flavour, by different clauses: on Windows the shape pre-check settles
        // it; on Unix "/workspace" is shape-legal, and it is the existence/equality checks that do —
        // which is why this assertion deliberately does not name a mechanism.
        result.Verified.Should().BeFalse("a container-internal mount point is not this host's workspace");
        result.Reason.Should().Contain("/workspace");
    }

    [Fact]
    public void Verify_rejects_null_or_blank_reported_path()
    {
        HostWorkspacePathVerifier.Verify(null, @"B:\sandbox-workspaces\e2e-clone-abc12345").Verified.Should().BeFalse();
        HostWorkspacePathVerifier.Verify("   ", @"B:\sandbox-workspaces\e2e-clone-abc12345").Verified.Should().BeFalse();
    }

    [Fact]
    public void Verify_rejects_a_drive_qualified_path_that_does_not_exist_on_this_host()
    {
        var missing = Path.Combine(Path.GetTempPath(), "host-workspace-verifier-missing-" + Guid.NewGuid().ToString("N"));

        var result = HostWorkspacePathVerifier.Verify(missing, missing);

        result.Verified.Should().BeFalse("the reported directory must actually exist on this host to be verifiable");
    }

    [Fact]
    public void Verify_rejects_a_real_directory_that_does_not_match_the_expected_workspace()
    {
        var reportedDir = Directory.CreateTempSubdirectory("host-workspace-verifier-reported-");
        var expectedDir = Directory.CreateTempSubdirectory("host-workspace-verifier-expected-");
        try
        {
            var result = HostWorkspacePathVerifier.Verify(reportedDir.FullName, expectedDir.FullName);

            result.Verified.Should().BeFalse("an existing but different directory is still not the expected workspace");
        }
        finally
        {
            reportedDir.Delete(recursive: true);
            expectedDir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Verify_accepts_a_matching_real_Windows_directory()
    {
        var dir = Directory.CreateTempSubdirectory("host-workspace-verifier-match-");
        try
        {
            var result = HostWorkspacePathVerifier.Verify(dir.FullName, dir.FullName);

            result.Verified.Should().BeTrue(result.Reason);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Verify_accepts_a_matching_directory_reported_with_a_long_path_prefix()
    {
        var dir = Directory.CreateTempSubdirectory("host-workspace-verifier-longprefix-");
        try
        {
            var result = HostWorkspacePathVerifier.Verify(@"\\?\" + dir.FullName, dir.FullName);

            result.Verified.Should().BeTrue(result.Reason);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    // The shape pre-check takes the host flavour as an ARGUMENT rather than reading the running
    // platform, so both branches are exercised wherever these tests run. That matters twice over: the
    // rule used to be drive-letters-only regardless of platform, which rejected every legitimate
    // workspace path on a Linux CI agent, and a platform-sensing rule can only ever be half-tested by
    // the machine that happens to run it.

    [Theory]
    [InlineData(@"B:\sandbox-workspaces\leaf", true)]
    [InlineData("c:/sandbox-workspaces/leaf", true)]
    [InlineData("/workspace", false)] // the container mount a Docker-backed gateway reports
    [InlineData(@"sandbox-workspaces\leaf", false)]
    [InlineData("", false)]
    public void LooksLikeAHostPath_on_a_Windows_host_admits_only_drive_qualified_paths(string path, bool expected) =>
        HostWorkspacePathVerifier.LooksLikeAHostPath(path, windowsHost: true).Should().Be(expected);

    [Theory]
    [InlineData("/srv/sandbox-workspaces/leaf", true)]
    [InlineData("/workspace", true)] // syntactically indistinguishable here; Verify's existence and
                                     // equality checks are what reject it on a Unix host
    [InlineData("sandbox-workspaces/leaf", false)]
    [InlineData(@"B:\sandbox-workspaces\leaf", false)]
    [InlineData("", false)]
    public void LooksLikeAHostPath_on_a_Unix_host_admits_only_slash_rooted_paths(string path, bool expected) =>
        HostWorkspacePathVerifier.LooksLikeAHostPath(path, windowsHost: false).Should().Be(expected);
}
