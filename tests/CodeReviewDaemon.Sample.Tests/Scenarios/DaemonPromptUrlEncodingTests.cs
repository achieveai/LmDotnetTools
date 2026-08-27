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
/// The C# HTTP path is NOT affected: <see cref="Uri"/> escapes an illegal path character when the request is
/// built, so <c>AdoPrProvider</c>/<c>AdoReviewCommentPublisher</c> put <c>%20</c> on the wire already. Only
/// the prompt, which hands a bare string to a shell, needs the segments encoded up front.
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
