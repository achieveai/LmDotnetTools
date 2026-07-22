using CodeReviewDaemon.Sample.Workspace.Git;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

/// <summary>
/// P3.3 — submodule URLs are attacker-controlled, so <see cref="GitRemoteUrl"/> parses conservatively:
/// it classifies the transport (only HTTP(S) is later permitted), extracts host/path for allow-list
/// matching, resolves relative URLs against the superproject remote exactly as git does, and fails
/// closed (<see cref="GitUrlKind.Unknown"/>) on anything it does not plainly recognize.
/// </summary>
public sealed class GitRemoteUrlTests
{
    [Fact]
    public void Parse_https_extracts_host_and_canonical_repo_path()
    {
        var url = GitRemoteUrl.Parse("https://github.com/acme/shared-lib.git");

        url.Kind.Should().Be(GitUrlKind.Https);
        url.Host.Should().Be("github.com");
        url.RepoPath.Should().Be("/acme/shared-lib");
    }

    [Theory]
    [InlineData("git://github.com/acme/lib.git", "Git")]
    [InlineData("ssh://git@github.com/acme/lib.git", "Ssh")]
    [InlineData("file:///srv/repos/lib.git", "File")]
    [InlineData("ext::sh -c 'evil'", "Ext")]
    [InlineData("/srv/repos/lib.git", "File")]
    [InlineData("git@github.com:acme/lib.git", "Ssh")]
    public void Parse_classifies_non_http_transports(string raw, string expectedKind)
    {
        // Compare on the enum name so this public test method does not expose the internal enum type.
        GitRemoteUrl.Parse(raw).Kind.ToString().Should().Be(expectedKind);
    }

    [Theory]
    [InlineData("./relative.git")]
    [InlineData("../sibling.git")]
    public void Parse_marks_relative_urls(string raw)
    {
        GitRemoteUrl.Parse(raw).Kind.Should().Be(GitUrlKind.Relative);
    }

    [Fact]
    public void Parse_fails_closed_on_bare_unrecognized_tokens()
    {
        GitRemoteUrl.Parse("just-a-name").Kind.Should().Be(GitUrlKind.Unknown);
    }

    [Fact]
    public void Resolve_pops_the_parents_last_segment_for_dotdot()
    {
        var parent = GitRemoteUrl.Parse("https://github.com/acme/widgets.git");

        var resolved = GitRemoteUrl.Parse("../shared-lib.git").Resolve(parent);

        resolved.Kind.Should().Be(GitUrlKind.Https);
        resolved.Host.Should().Be("github.com");
        resolved.RepoPath.Should().Be("/acme/shared-lib");
    }

    [Fact]
    public void Resolve_can_walk_into_a_different_owner_outside_the_allowed_scope()
    {
        var parent = GitRemoteUrl.Parse("https://github.com/acme/widgets.git");

        // ../../evil/secret => pop widgets, pop acme, descend evil/secret.
        var resolved = GitRemoteUrl.Parse("../../evil/secret.git").Resolve(parent);

        resolved.Host.Should().Be("github.com");
        resolved.RepoPath.Should().Be("/evil/secret");
    }

    [Fact]
    public void Resolve_fails_closed_when_escaping_above_the_root()
    {
        var parent = GitRemoteUrl.Parse("https://github.com/acme/widgets.git");

        var resolved = GitRemoteUrl.Parse("../../../../x.git").Resolve(parent);

        resolved.Kind.Should().Be(GitUrlKind.Unknown);
    }

    [Fact]
    public void CanonicalizeAdoLegacyHost_rewrites_visualstudio_com_to_the_modern_dev_azure_shape()
    {
        // Legacy {org}.visualstudio.com/{project}/_git/{repo} — org is a HOST label — must reduce to the
        // exact same (Host, RepoPath) as the modern dev.azure.com/{org}/{project}/_git/{repo} — org is a PATH
        // segment — so the per-run allow-list (always dev.azure.com) matches either URL form.
        var legacy = GitRemoteUrl.CanonicalizeAdoLegacyHost(
            GitRemoteUrl.Parse("https://mcqdbdev.visualstudio.com/MCQdb_Development/_git/LibProfiler"));
        var modern = GitRemoteUrl.Parse(
            "https://dev.azure.com/mcqdbdev/MCQdb_Development/_git/LibProfiler");

        legacy.Kind.Should().Be(GitUrlKind.Https);
        legacy.Host.Should().Be("dev.azure.com");
        legacy.RepoPath.Should().Be("/mcqdbdev/MCQdb_Development/_git/LibProfiler");
        (legacy.Host, legacy.RepoPath).Should().Be((modern.Host, modern.RepoPath));
    }

    [Fact]
    public void CanonicalizeAdoLegacyHost_preserves_a_url_encoded_repo_segment_verbatim()
    {
        // Parse does NOT URL-decode, so the encoded space stays %20 — the config allow-list value must match
        // this exact spelling (proven in SubmoduleInitializerTests).
        var canonical = GitRemoteUrl.CanonicalizeAdoLegacyHost(
            GitRemoteUrl.Parse("https://mcqdbdev.visualstudio.com/MCQdb_Development/_git/Microsoft%20Orleans"));

        canonical.Host.Should().Be("dev.azure.com");
        canonical.RepoPath.Should().Be("/mcqdbdev/MCQdb_Development/_git/Microsoft%20Orleans");
    }

    [Theory]
    [InlineData("https://github.com/acme/widgets.git", "github.com", "/acme/widgets")]
    [InlineData("https://dev.azure.com/mcqdbdev/MCQdb_Development/_git/LibProfiler",
        "dev.azure.com", "/mcqdbdev/MCQdb_Development/_git/LibProfiler")]
    public void CanonicalizeAdoLegacyHost_leaves_non_legacy_urls_untouched(
        string raw, string expectedHost, string expectedPath)
    {
        var canonical = GitRemoteUrl.CanonicalizeAdoLegacyHost(GitRemoteUrl.Parse(raw));

        canonical.Host.Should().Be(expectedHost);
        canonical.RepoPath.Should().Be(expectedPath);
    }
}
