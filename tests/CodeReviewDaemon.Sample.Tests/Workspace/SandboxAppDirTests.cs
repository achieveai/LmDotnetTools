using CodeReviewDaemon.Sample.Workspace.Sandbox;

namespace CodeReviewDaemon.Sample.Tests.Workspace;

public class SandboxAppDirTests
{
    // The exact <app-dir> the gateway (SandboxedOstoolsMcpServer app_dir.rs, ADR 0028) roots this daemon's
    // workspaces under. These MUST stay in lock-step with the gateway or the pooled slot mounts an empty dir.
    [Theory]
    [InlineData("code-review-daemon", "code-review-daemon-9815591a2bf6c8a2")]
    [InlineData("codereview-daemon-mcqdb", "codereview-daemon-mcqdb-7da177c9b5916ebd")]
    public void Derive_matches_the_gateway_app_dir(string appId, string expected) =>
        SandboxAppDir.Derive(appId).Should().Be(expected);

    [Fact]
    public void Derive_lowercases_and_filters_the_slug_but_hashes_the_raw_id()
    {
        // Slug drops disallowed chars + lowercases; the hash is over the RAW id.
        SandboxAppDir.Derive("My App!").Should().MatchRegex("^myapp-[0-9a-f]{16}$");
    }

    [Fact]
    public void Derive_truncates_the_slug_to_32_chars()
    {
        var dir = SandboxAppDir.Derive(new string('a', 50));
        dir.Split('-')[0].Length.Should().Be(32);
    }

    [Fact]
    public void EffectiveBase_appends_app_dir_only_when_rooting_is_on()
    {
        SandboxAppDir.EffectiveBase("B:/ws", "code-review-daemon", perAppRooting: false)
            .Should().Be("B:/ws");
        SandboxAppDir.EffectiveBase("B:/ws", "code-review-daemon", perAppRooting: true)
            .Should().Be("B:/ws/code-review-daemon-9815591a2bf6c8a2");
        SandboxAppDir.EffectiveBase(null, "code-review-daemon", perAppRooting: true)
            .Should().BeNull();
    }
}
