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

        result.Verified.Should().BeFalse("a Linux-style, non-drive-qualified path can never be this Windows host's filesystem");
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
}
