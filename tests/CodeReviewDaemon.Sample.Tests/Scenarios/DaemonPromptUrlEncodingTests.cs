using System.Text.RegularExpressions;
using CodeReviewDaemon.Sample.Agents;
using CodeReviewDaemon.Sample.Orchestration;
using CodeReviewDaemon.Sample.Persistence.Models;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

/// <summary>
/// Issue #218 item 9 — the ADO posting contract in <c>daemon-prompts.yaml</c> interpolates the org/project/
/// repo straight into a URL the review agent runs through <c>curl</c>. Azure DevOps project and repository
/// names may contain spaces (and other characters that are not legal in a URL path segment). Interpolated
/// raw, <c>curl</c> rejects the argument (exit 3) before any request is made, so the review is never posted
/// — the run "completed" with nothing delivered.
/// <para>
/// The C# HTTP path is NOT affected, though not because <see cref="Uri"/> escapes everything (issue #472
/// item 3): <see cref="Uri.AbsoluteUri"/> escapes the space and puts <c>%20</c> on the wire for
/// <c>AdoPrProvider</c>/<c>AdoReviewCommentPublisher</c>, but it passes the DELIMITERS through — <c>#</c>
/// truncates the path into the fragment, <c>?</c> into the query, <c>/</c> opens a new segment. That path is
/// safe because provider naming rules forbid all three, so every character that can actually arrive is one
/// <see cref="Uri"/> escapes. The prompt has no such backstop: it hands a bare string to a shell, so a legal
/// ADO name with a space already breaks it, and the segments must be encoded up front.
/// </para>
/// </summary>
public sealed class DaemonPromptUrlEncodingTests
{
    private static readonly RepoIdentity SpacedAdoRepo = new()
    {
        Provider = "azure-devops",
        OrgOrOwner = "contoso org",
        Project = "MCQdb Development",
        RepoName = "My Repo",
        RepoStableId = "repo-guid-1",
    };

    private static readonly RepoIdentity SpacedGitHubRepo = new()
    {
        Provider = "github",
        OrgOrOwner = "contoso org",
        RepoName = "My Repo",
        RepoStableId = "repo-guid-4",
    };

    private static Dictionary<string, object> Variables(RepoIdentity repo) =>
        DaemonReviewStageExecutor.BuildPromptVariables(
            botName: "Revobot",
            repo: repo,
            prId: "118",
            shouldPost: true,
            checkoutRoot: "/workspace/target",
            storeRoot: null,
            notesDir: null,
            headSha: "head-sha",
            prevHeadSha: null,
            reviewRound: 1,
            priorNotesFiles: [],
            buildTooling: new DaemonReviewStageExecutor.BuildToolingFacts(
                DaemonReviewStageExecutor.BuildToolingState.Indeterminate,
                "No build tooling was probed."));

    [Fact]
    public void The_ado_url_identity_variables_are_url_encoded_path_segments()
    {
        var vars = Variables(SpacedAdoRepo);

        vars["ado_org"].Should().Be("contoso%20org");
        vars["ado_project"].Should().Be("MCQdb%20Development");
        vars["ado_repo"].Should().Be("My%20Repo");
    }

    /// <summary>
    /// The assertion that actually pins the defect: the URL the prompt tells the agent to curl must contain
    /// no raw space at all. Asserting on the rendered prompt (not just the variables dict) is what keeps the
    /// encoding wired to the thing that consumes it.
    /// </summary>
    [Fact]
    public void The_rendered_ado_posting_url_is_a_single_shell_safe_token()
    {
        var prompt = DaemonAgentFactory.CreateSynthesisPrompt(
            Variables(SpacedAdoRepo),
            "- code-reviewer:architecture-review (architecture) — completed");

        var url = Regex.Match(prompt, @"https://dev\.azure\.com/\S*");
        url.Success.Should().BeTrue("the ADO posting contract must render its base URL");

        // The match stops at the first whitespace. If any segment carried a raw space the URL would be
        // truncated there — so a complete URL is the proof that curl receives one argument, not several.
        url.Value.Should().Be(
            "https://dev.azure.com/contoso%20org/MCQdb%20Development/_apis/git/repositories/My%20Repo"
                + "/pullRequests/118",
            "curl rejects a raw space in a URL argument before it ever sends the request");
    }

    /// <summary>
    /// Issue #472 item 1. Every assertion on <c>gh_owner</c> used to name a value
    /// <see cref="Uri.EscapeDataString(string)"/> leaves untouched (<c>"contoso"</c>), and the one fixture
    /// with an encoding-VISIBLE owner is the ADO one, which never asserts <c>gh_owner</c> at all. So dropping
    /// the escape from <c>gh_owner</c> ALONE passed the entire suite: the claim below —
    /// "the GitHub arm gets the identical treatment" — was asserted about <c>gh_repo</c> and merely hoped
    /// for about <c>gh_owner</c>. GitHub owner names are <c>[A-Za-z0-9-]</c>, so this is a proof gap and
    /// not a live defect; the point is that the seam, not the provider's naming rules, is what holds.
    /// </summary>
    [Fact]
    public void The_github_url_identity_variables_are_url_encoded_path_segments()
    {
        var vars = Variables(SpacedGitHubRepo);

        vars["gh_owner"].Should().Be("contoso%20org");
        vars["gh_repo"].Should().Be("My%20Repo");
    }

    /// <summary>
    /// The same end-to-end proof the ADO arm gets: the URL the prompt tells the agent to curl must survive
    /// as ONE whitespace-free token. Asserting the rendered prompt is what keeps <c>gh_owner</c>'s encoding
    /// wired to the thing that consumes it, rather than to a dictionary nobody would notice going raw.
    /// </summary>
    [Fact]
    public void The_rendered_github_posting_url_is_a_single_shell_safe_token()
    {
        var prompt = DaemonAgentFactory.CreateSynthesisPrompt(
            Variables(SpacedGitHubRepo),
            "- code-reviewer:architecture-review (architecture) — completed");

        var url = Regex.Match(prompt, @"https://api\.github\.com/\S*");
        url.Success.Should().BeTrue("the GitHub posting contract must render its review URL");

        // The match stops at the first whitespace, so a complete URL is the proof that curl receives one
        // argument. A raw space in the OWNER truncates it after "repos/contoso".
        url.Value.Should().Be(
            "https://api.github.com/repos/contoso%20org/My%20Repo/pulls/118/reviews",
            "curl rejects a raw space in a URL argument before it ever sends the request");
    }

    /// <summary>
    /// Encoding must be a no-op for the ordinary names every real deployment uses, so this change cannot
    /// silently repoint an existing ADO run at a differently-spelled URL.
    /// </summary>
    [Fact]
    public void Ordinary_ado_names_are_unchanged_by_the_encoding()
    {
        var vars = Variables(new RepoIdentity
        {
            Provider = "azure-devops",
            OrgOrOwner = "mcqdbdev",
            Project = "MCQdb_Development",
            RepoName = "MCQdbDEV",
            RepoStableId = "repo-guid-2",
        });

        vars["ado_org"].Should().Be("mcqdbdev");
        vars["ado_project"].Should().Be("MCQdb_Development");
        vars["ado_repo"].Should().Be("MCQdbDEV");
    }

    /// <summary>
    /// Issue #492 item 1 — <c>is_ado</c> is derived from the SHARED <c>GitRemoteUrl.IsAzureDevOps</c>
    /// classifier, which reconciles both provider spellings the daemon carries, case-insensitively:
    /// <c>azure-devops</c> (the storage namespace, what <c>PrPollTargetBuilder</c> persists on
    /// <c>RepoIdentity</c>) and <c>ado</c> (the publisher/poll-target namespace).
    /// <para>
    /// To be precise about where <c>ado</c> comes from, because it is easy to misattribute:
    /// <c>DaemonReviewStageExecutor.ResolveRepo</c> does NOT normalize <c>repo.Provider</c> — it returns the
    /// repo unchanged and maps the spelling onto the SEPARATE publisher provider string it returns alongside.
    /// <c>RepoIdentity</c> is the type both namespaces flow through, though, and callers do construct one
    /// spelled <c>ado</c>. Using the shared classifier is what keeps this seam agreeing with every other
    /// <c>IsAzureDevOps</c> caller instead of hand-rolling a narrower, case-sensitive test — the desync shape
    /// #478 unified away. The <c>"ado"</c> case is the distinguishing one: it goes true here and false under
    /// the old <c>== "azure-devops"</c> hand-check.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("azure-devops", true)]
    [InlineData("ado", true)]
    [InlineData("github", false)]
    public void Is_ado_uses_the_shared_provider_classifier(string provider, bool expected)
    {
        var vars = Variables(new RepoIdentity
        {
            Provider = provider,
            OrgOrOwner = "contoso",
            Project = "Platform",
            RepoName = "widgets",
            RepoStableId = "repo-guid-ado",
        });

        vars["is_ado"].Should().Be(expected);
    }

    /// <summary>
    /// A repo name is one path segment. Encoding it means a name carrying a separator can never open a new
    /// segment in the REST URL the agent is told to call — it stays data, not structure.
    /// </summary>
    [Fact]
    public void A_separator_in_a_repo_name_cannot_open_a_new_url_path_segment()
    {
        var vars = Variables(new RepoIdentity
        {
            Provider = "azure-devops",
            OrgOrOwner = "contoso",
            Project = "Platform",
            RepoName = "widgets/../../other",
            RepoStableId = "repo-guid-3",
        });

        vars["ado_repo"].Should().Be("widgets%2F..%2F..%2Fother");
        vars["ado_repo"].ToString().Should().NotContain("/");
        // The GitHub arm of the same prompt builds api.github.com URLs from these two, so it gets the
        // identical treatment rather than relying on GitHub's own name rules to hold at this seam.
        vars["gh_repo"].Should().Be("widgets%2F..%2F..%2Fother");
        vars["gh_owner"].Should().Be("contoso");
    }
}
