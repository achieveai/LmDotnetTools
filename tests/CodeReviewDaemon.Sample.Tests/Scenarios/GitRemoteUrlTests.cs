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
            GitRemoteUrl.Parse("https://mcqdbdev.visualstudio.com/MCQdb_Development/_git/LibProfiler")
        );
        var modern = GitRemoteUrl.Parse("https://dev.azure.com/mcqdbdev/MCQdb_Development/_git/LibProfiler");

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
            GitRemoteUrl.Parse("https://mcqdbdev.visualstudio.com/MCQdb_Development/_git/Microsoft%20Orleans")
        );

        canonical.Host.Should().Be("dev.azure.com");
        canonical.RepoPath.Should().Be("/mcqdbdev/MCQdb_Development/_git/Microsoft%20Orleans");
    }

    [Theory]
    [InlineData("https://github.com/acme/widgets.git", "github.com", "/acme/widgets")]
    [InlineData(
        "https://dev.azure.com/mcqdbdev/MCQdb_Development/_git/LibProfiler",
        "dev.azure.com",
        "/mcqdbdev/MCQdb_Development/_git/LibProfiler"
    )]
    public void CanonicalizeAdoLegacyHost_leaves_non_legacy_urls_untouched(
        string raw,
        string expectedHost,
        string expectedPath
    )
    {
        var canonical = GitRemoteUrl.CanonicalizeAdoLegacyHost(GitRemoteUrl.Parse(raw));

        canonical.Host.Should().Be(expectedHost);
        canonical.RepoPath.Should().Be(expectedPath);
    }

    /// <summary>
    /// Issue #478 — the clone URL and the submodule ALLOW-LIST path are two spellings of one identity, and
    /// they are compared against each other: the clone URL is re-parsed and its <c>RepoPath</c> matched
    /// against the rules built from the same org/project/repo. Encoding one side alone — the obvious fix for
    /// a spaced Azure DevOps org, which raw makes the remote malformed — desynchronizes a security matcher.
    /// This pins the agreement itself rather than either spelling: whatever the encoding is, the parsed clone
    /// URL must land exactly on the allow-list path.
    /// </summary>
    [Theory]
    [InlineData("ado", "contoso org", "MCQdb Development", "My Repo")]
    [InlineData("azure-devops", "contoso org", "MCQdb Development", "My Repo")]
    [InlineData("azure-devops", "mcqdbdev", "MCQdb_Development", "MCQdbDEV")]
    [InlineData("github", "acme", null, "widgets")]
    [InlineData("github", "acme org", null, "my widgets")]
    public void The_clone_url_reparses_onto_exactly_the_allow_list_path(
        string provider,
        string org,
        string? project,
        string repoName
    )
    {
        var parsed = GitRemoteUrl.Parse(GitRemoteUrl.CloneUrlFor(provider, org, project, repoName));

        parsed.Kind.Should().Be(GitUrlKind.Https);
        parsed.Host.Should().Be(GitRemoteUrl.HostFor(provider));
        parsed
            .RepoPath.Should()
            .Be(
                GitRemoteUrl.RepoPathFor(provider, org, project, repoName),
                "the allow-list rule and the URL actually cloned must be the same path, or the matcher gates "
                    + "nothing while looking like it does"
            );
    }

    /// <summary>
    /// The encoding itself, stated once so a change to it is a visible edit rather than a silent drift: a
    /// space becomes <c>%20</c> and never survives raw into an argv element <c>git clone</c> has to parse
    /// as a URL.
    /// </summary>
    [Fact]
    public void A_spaced_ado_identity_produces_a_well_formed_clone_url()
    {
        GitRemoteUrl
            .CloneUrlFor("ado", "contoso org", "MCQdb Development", "My Repo")
            .Should()
            .Be("https://dev.azure.com/contoso%20org/MCQdb%20Development/_git/My%20Repo");
    }

    /// <summary>
    /// A separator inside a NAME must stay data. Encoded it is one segment (<c>%2F</c>); raw it would open a
    /// second path segment and address a different repo than the allow rule was written for.
    /// </summary>
    [Fact]
    public void A_separator_inside_a_name_stays_one_path_segment()
    {
        var path = GitRemoteUrl.RepoPathFor("github", "acme", null, "widgets/../secrets");

        path.Should().Be("/acme/widgets%2F..%2Fsecrets");
        path.Split('/', StringSplitOptions.RemoveEmptyEntries).Should().HaveCount(2);
    }

    /// <summary>
    /// Both provider spellings the daemon carries (<c>azure-devops</c> as persisted, <c>ado</c> as
    /// normalized) classify identically. They did not before: the clone URL tested <c>"ado"</c>
    /// case-SENSITIVELY while the allow-list accepted either spelling case-insensitively, so a differently
    /// cased provider built a github.com clone URL against dev.azure.com allow rules.
    /// </summary>
    [Theory]
    [InlineData("ado")]
    [InlineData("ADO")]
    [InlineData("azure-devops")]
    [InlineData("Azure-DevOps")]
    public void Every_azure_devops_provider_spelling_resolves_to_the_same_host(string provider)
    {
        GitRemoteUrl.IsAzureDevOps(provider).Should().BeTrue();
        GitRemoteUrl.HostFor(provider).Should().Be("dev.azure.com");
    }

    /// <summary>
    /// The identity prefix is encoded even when the leaf is a configured, already-URL-form name: those two
    /// halves come from different sources (human-form repo identity vs. a setting documented as the URL's own
    /// spelling) and both must end up as the <c>.gitmodules</c> URL spells them. Re-encoding the leaf would
    /// make <c>%20</c> into <c>%2520</c> and drop a configured submodule off the allow-list.
    /// </summary>
    [Fact]
    public void A_configured_url_form_submodule_name_is_not_encoded_again_under_an_encoded_prefix()
    {
        GitRemoteUrl
            .RepoPathForUrlSegment("ado", "contoso org", "MCQdb Development", "Microsoft%20Orleans")
            .Should()
            .Be("/contoso%20org/MCQdb%20Development/_git/Microsoft%20Orleans");
    }
}
