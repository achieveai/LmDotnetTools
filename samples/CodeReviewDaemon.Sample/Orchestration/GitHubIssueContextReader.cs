using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmAgentInfra.Auth;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Workspace;

namespace CodeReviewDaemon.Sample.Orchestration;

/// <summary>How the linked-issue lookup ended. Mirrors <see cref="AdoWorkItemContextReader"/>'s
/// <c>AdoWorkItemLookup</c> exactly — four DIFFERENT statements about the pull request (three) or the
/// daemon (one), and collapsing any pair of them is the defect this enum exists to prevent.</summary>
internal enum GitHubIssueLookup
{
    /// <summary>Nothing was ever asked — a non-GitHub repo (e.g. an ADO daemon) has no GraphQL endpoint to
    /// query. Deliberately the default (value 0) so a record nobody filled in renders no block, rather than
    /// defaulting into <see cref="NoneLinked"/>, which would tell the reviewer a false fact about the pull
    /// request on the strength of nobody having looked.</summary>
    Unavailable = 0,

    /// <summary>The lookup was attempted and could not be completed — a non-success HTTP status, a
    /// GraphQL-level error returned alongside HTTP 200, or a response shape this parser cannot read.
    /// Distinct from <see cref="NoneLinked"/> on purpose: "this PR closes no issues" and "we could not read
    /// this PR's linked issues" license opposite reviewer behaviour.</summary>
    Failed,

    /// <summary>The PR was read and closes no issues at all. A positive finding, not an absence of one.</summary>
    NoneLinked,

    /// <summary>At least one linked issue was read.</summary>
    Linked,
}

/// <summary>One pull request GitHub reports as closing (or closed by) a linked issue. <see cref="NodeId"/> is
/// GitHub's own GraphQL <c>id</c> — the deterministic identity a (repository, number) pair cannot give by
/// itself, since numbers are only unique within one repository.</summary>
internal sealed record GitHubRelatedPullRequest(string NodeId, string Repository, int Number, string Url);

/// <summary>One issue the pull request closes, plus the PRs GitHub already associates with it. <see cref="NodeId"/>
/// is GitHub's own GraphQL <c>id</c> for this issue — the deterministic identity a (repository, number) pair
/// cannot give by itself.</summary>
internal sealed record GitHubLinkedIssue(
    string NodeId,
    string Repository,
    int Number,
    string Url,
    string Title,
    string State,
    IReadOnlyList<GitHubRelatedPullRequest> RelatedPullRequests
);

/// <summary>What the pull request was asked to do, as far as the daemon could establish it from GitHub's
/// issue-linking graph. Bounded so it can be dropped straight into a review brief.</summary>
internal sealed record GitHubIssueContext(
    GitHubIssueLookup Outcome,
    IReadOnlyList<GitHubLinkedIssue> Issues,
    bool Truncated
)
{
    /// <summary>Nobody looked. Renders no block at all.</summary>
    public static GitHubIssueContext Unavailable { get; } = new(GitHubIssueLookup.Unavailable, [], false);

    /// <summary>Somebody looked and could not read the answer. Renders an explicit failure marker.</summary>
    public static GitHubIssueContext Failed { get; } = new(GitHubIssueLookup.Failed, [], false);

    /// <summary>The PR genuinely closes nothing. Renders an explicit "none linked" statement.</summary>
    public static GitHubIssueContext NoneLinked { get; } = new(GitHubIssueLookup.NoneLinked, [], false);
}

/// <summary>
/// Reads a pull request's linked issues — and each issue's own related PRs — from GitHub's GraphQL API, so
/// the review brief can state what the change was actually asked to do. The ADO analog is
/// <see cref="AdoWorkItemContextReader"/>; this reader follows the same four-outcome shape and the same
/// "never throw for a failed read" discipline, adapted to GitHub's issue-linking graph instead of ADO's
/// work-item hierarchy.
/// <para>
/// GitHub exposes no REST route for "issues this PR closes" — only GraphQL's
/// <c>PullRequest.closingIssuesReferences</c> carries it — so this is the first GraphQL consumer in the
/// codebase. Requests go through the injected policy-enforced <see cref="HttpClient"/> tagged
/// <see cref="SandboxOperation.ReadProviderMetadata"/>, same as <see cref="GitHubPrProvider"/>'s REST calls;
/// <see cref="OperationPolicy"/> carves out exactly this one route (POST to <c>/graphql</c> on the API host)
/// for it.
/// </para>
/// <para>
/// Issue #647 scope stops at reading and returning this context — nothing in the daemon calls
/// <see cref="ReadAsync"/> yet, and no review brief renders it. Wiring it into the rendered prompt is
/// issue #650's job; this type exists ahead of that consumer so #650 can be a pure wiring change against an
/// already-tested reader, not a combined read-and-render change.
/// </para>
/// </summary>
internal sealed class GitHubIssueContextReader
{
    private const string GraphQlUrl = "https://api.github.com/graphql";
    private const string UserAgent = "LmDotnetTools-CodeReviewDaemon";

    /// <summary>Cap on a free-text field (issue title) carried into the brief — mirrors
    /// <c>AdoWorkItemContextReader.MaxTitleChars</c>. GitHub's <c>state</c> is a fixed GraphQL enum
    /// (<c>OPEN</c>/<c>CLOSED</c>), not free text, and cannot itself carry the line-collapse hazard the
    /// title can, so only the title is condensed.</summary>
    internal const int MaxTitleChars = 200;

    /// <summary>
    /// How many issues are requested per <c>closingIssuesReferences</c> page. Also the fixed window used for
    /// each issue's own <c>closedByPullRequestsReferences</c> — related PRs are bounded navigation, not
    /// exhaustive enumeration (see the doc comment on <see cref="ReadAsync"/>'s pagination loop), so that
    /// one is not driven by a variable at all.
    /// </summary>
    internal const int PageSize = 100;

    /// <summary>
    /// Hard cap on how many linked issues are fetched in total. A PR can legitimately close many issues, but
    /// the brief has room for what the change was asked to do, not for an unbounded backlog. Reaching this
    /// cap while more pages remain is reported through <see cref="GitHubIssueContext.Truncated"/> rather than
    /// silently dropped — the same counted-not-hidden convention <c>AdoWorkItemContextReader</c> uses for its
    /// own caps.
    /// </summary>
    internal const int MaxIssues = 200;

    /// <summary>
    /// Absolute cap on how many <c>closingIssuesReferences</c> page requests one <see cref="ReadAsync"/> call
    /// will ever issue — independent of how many issues actually parsed out of any of them, so a page that
    /// comes back empty or entirely unparseable still counts against it. <see cref="MaxIssues"/> divided by
    /// <see cref="PageSize"/> is the fewest requests a well-behaved server could possibly need to fill the cap
    /// exactly; the one request beyond that is what lets this walk tell "the cap landed exactly on a page
    /// boundary" (see the exact-cap-boundary test) apart from "the server never really made progress". A
    /// server needing more than that is not a large PR — it is a pagination cursor this walk cannot trust, and
    /// the no-progress guard below already gives that its own diagnosis; this bound is strictly the
    /// last-resort stop for the case where the cursor keeps nominally advancing without ever getting anywhere.
    /// </summary>
    private const int MaxPageRequests = (MaxIssues / PageSize) + 1;

    /// <summary>
    /// The one GraphQL document this reader ever sends. <c>closingIssuesReferences</c> is cursor-paginated
    /// (<c>$after</c>) because issue #647's acceptance criteria requires it walked exhaustively; each issue's
    /// <c>closedByPullRequestsReferences</c> is a FIXED <c>first: 20</c> window, deliberately not
    /// cursor-walked — cursor-walking it too would multiply request count per issue (up to
    /// <see cref="MaxIssues"/> issues, each running its own page loop), which is exactly the
    /// unbounded-nested-pagination shape this reader avoids. A related-PR list longer than 20 is simply
    /// reported as whatever the fixed window returns; there is no per-issue truncation flag for it, by
    /// controller ruling on issue #647 (out of that issue's acceptance criteria as written).
    /// <c>orderBy: CREATED_AT ASC</c> is pinned explicitly (GitHub's own connection default is unspecified) so
    /// that walking pages in order — and the no-progress/cursor-repeat guard that depends on a stable
    /// ordering — is not resting on an implicit, unversioned server default. Each node also selects <c>id</c>
    /// — GitHub's own GraphQL identity for the issue/PR — because <c>(repository, number)</c> alone is not a
    /// deterministic identity across repositories.
    /// </summary>
    private const string Query = """
        query($owner: String!, $repo: String!, $number: Int!, $pageSize: Int!, $after: String) {
          repository(owner: $owner, name: $repo) {
            pullRequest(number: $number) {
              closingIssuesReferences(first: $pageSize, after: $after, orderBy: { field: CREATED_AT, direction: ASC }) {
                pageInfo { hasNextPage endCursor }
                nodes {
                  id
                  number
                  url
                  title
                  state
                  repository { nameWithOwner }
                  closedByPullRequestsReferences(first: 20) {
                    nodes {
                      id
                      number
                      url
                      repository { nameWithOwner }
                    }
                  }
                }
              }
            }
          }
        }
        """;

    private readonly HttpClient _httpClient;
    private readonly IOAuthTokenProvider _tokenProvider;
    private readonly ILogger<GitHubIssueContextReader> _logger;

    public GitHubIssueContextReader(
        HttpClient httpClient,
        IOAuthTokenProvider tokenProvider,
        ILogger<GitHubIssueContextReader> logger
    )
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _tokenProvider = tokenProvider ?? throw new ArgumentNullException(nameof(tokenProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Reads the issues linked to one pull request, plus each issue's related PRs.
    /// </summary>
    /// <param name="repo">The run's repository identity; supplies the owner/repo the query is built from.</param>
    /// <param name="prId">The pull request number.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>
    /// The context. <see cref="GitHubIssueContext.Unavailable"/> when there was nothing to ask,
    /// <see cref="GitHubIssueContext.Failed"/> when the ask could not be completed, and never an exception
    /// for a failed read.
    /// </returns>
    public async Task<GitHubIssueContext> ReadAsync(RepoIdentity repo, string prId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentException.ThrowIfNullOrEmpty(prId);

        if (!string.Equals(repo.Provider, "github", StringComparison.Ordinal))
        {
            // Storage-namespace check (see RepoIdentity.Provider / ToPublisherNamespace) — an ADO repo has
            // no GraphQL endpoint to ask, so nobody attempted anything. Exact/case-sensitive on purpose: every
            // producer of RepoIdentity.Provider in this codebase writes the canonical lower-case spelling
            // (RepoIdentity.ToPublisherNamespace compares the same way against "azure-devops"), so a
            // differently-cased value is not a GitHub repo spelled unusually — it is data nothing in this
            // codebase has ever promised to normalize, and Unavailable is the right answer for it too.
            return GitHubIssueContext.Unavailable;
        }

        try
        {
            var number = int.Parse(prId, CultureInfo.InvariantCulture);
            var issues = new List<GitHubLinkedIssue>();
            var truncated = false;
            var degenerate = false;
            string? cursor = null;
            var hasNextPage = true;
            var seenCursors = new HashSet<string>(StringComparer.Ordinal);
            var pageRequestCount = 0;

            while (hasNextPage)
            {
                if (pageRequestCount >= MaxPageRequests)
                {
                    // The absolute safety net (see MaxPageRequests) — stop a server that keeps answering
                    // hasNextPage: true forever from turning one review into an unbounded fetch loop.
                    truncated = true;
                    degenerate = issues.Count == 0;
                    break;
                }

                var page = await FetchPageAsync(repo, number, cursor, cancellationToken).ConfigureAwait(false);
                pageRequestCount++;
                if (page is null)
                {
                    return GitHubIssueContext.Failed;
                }

                if (page.RawNodeCount > 0 && page.Nodes.Count < page.RawNodeCount)
                {
                    // At least one node in a non-empty page could not be parsed. Never claim NoneLinked (or a
                    // partial Linked) on the strength of only the nodes that DID parse — the brief has to say
                    // the lookup could not be completed, not state a fact built on a silently dropped node.
                    _logger.LogDebug(
                        "GitHub linked-issue page for {Repository} PR {PrId} had {RawCount} node(s) but only "
                            + "{ParsedCount} parsed; the brief will say the lookup failed.",
                        repo.DisplayName,
                        prId,
                        page.RawNodeCount,
                        page.Nodes.Count
                    );
                    return GitHubIssueContext.Failed;
                }

                if (page.HasNextPage && string.IsNullOrEmpty(page.EndCursor))
                {
                    // The server claims more pages exist but handed back no cursor to ask for them with —
                    // walking further is impossible, not merely capped.
                    truncated = true;
                    degenerate = issues.Count == 0;
                    break;
                }

                if (page.HasNextPage && !seenCursors.Add(page.EndCursor!))
                {
                    // The cursor repeated (or never changed) — walking again would just re-fetch this same
                    // page forever instead of making progress.
                    truncated = true;
                    degenerate = issues.Count == 0;
                    break;
                }

                hasNextPage = page.HasNextPage;
                cursor = page.EndCursor;

                foreach (var node in page.Nodes)
                {
                    if (issues.Count >= MaxIssues)
                    {
                        // The cap was already full before this node — something in THIS page is still
                        // pending, so the walk stops here and reports it rather than silently dropping it.
                        truncated = true;
                        hasNextPage = false;
                        break;
                    }

                    issues.Add(node);
                }

                if (issues.Count >= MaxIssues && hasNextPage)
                {
                    // The cap filled exactly at a page boundary and the server still has more.
                    truncated = true;
                    hasNextPage = false;
                }
            }

            if (degenerate)
            {
                // Pagination could not make progress and nothing was actually read — Failed, never
                // NoneLinked: the brief must not state a fact about the pull request on the strength of a
                // walk that never got anywhere.
                _logger.LogDebug(
                    "GitHub linked-issue pagination for {Repository} PR {PrId} could not make progress and "
                        + "read no issues; the brief will say the lookup failed.",
                    repo.DisplayName,
                    prId
                );
                return GitHubIssueContext.Failed;
            }

            _logger.LogDebug(
                "Read GitHub linked issues for {Repository} PR {PrId}: {Outcome}, {IssueCount} issue(s), truncated: {Truncated}.",
                repo.DisplayName,
                prId,
                issues.Count == 0 ? GitHubIssueLookup.NoneLinked : GitHubIssueLookup.Linked,
                issues.Count,
                truncated
            );

            return issues.Count == 0
                ? GitHubIssueContext.NoneLinked
                : new GitHubIssueContext(GitHubIssueLookup.Linked, issues, truncated);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A real caller cancellation (the review was abandoned) — propagate; nobody wants this result.
            throw;
        }
        catch (Exception ex)
        {
            // Everything else — an egress denial from the operation policy, an HttpClient timeout, a
            // malformed body, or a non-numeric prId — is a lookup that was ATTEMPTED and did not complete.
            // That is Failed, not Unavailable: the brief has to say the daemon could not read the linked
            // issues, because the alternative is a reviewer that reads silence as "this PR closes none".
            _logger.LogDebug(
                ex,
                "GitHub linked-issue read for {Repository} PR {PrId} failed; the brief will say the lookup failed.",
                repo.DisplayName,
                prId
            );
            return GitHubIssueContext.Failed;
        }
    }

    /// <summary>One page of <c>closingIssuesReferences</c>, parsed. <see cref="RawNodeCount"/> is the number
    /// of entries GitHub actually returned in <c>nodes</c> — deliberately kept apart from
    /// <see cref="Nodes"/>.Count (the number that parsed) so the caller can tell "an empty page" apart from
    /// "a page where something could not be read".</summary>
    private sealed record GraphQlPage(
        IReadOnlyList<GitHubLinkedIssue> Nodes,
        bool HasNextPage,
        string? EndCursor,
        int RawNodeCount
    );

    /// <summary>
    /// One GraphQL request/response round trip. Returns <c>null</c> when the response could not be read at
    /// all — a non-success HTTP status, a top-level GraphQL <c>errors</c> entry (GitHub returns HTTP 200 with
    /// an <c>errors</c> array and a null/partial <c>data</c> for e.g. a NOT_FOUND pull request — an
    /// HTTP-success check alone is not enough here, unlike the REST readers elsewhere in this codebase), or a
    /// response missing the expected shape.
    /// </summary>
    private async Task<GraphQlPage?> FetchPageAsync(
        RepoIdentity repo,
        int number,
        string? cursor,
        CancellationToken cancellationToken
    )
    {
        var variables = new Dictionary<string, object?>
        {
            ["owner"] = repo.OrgOrOwner,
            ["repo"] = repo.RepoName,
            ["number"] = number,
            ["pageSize"] = PageSize,
            ["after"] = cursor,
        };
        var requestBody = JsonSerializer.Serialize(new { query = Query, variables });

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, GraphQlUrl).WithOperation(
            SandboxOperation.ReadProviderMetadata
        );
        var token = await _tokenProvider.GetAccessTokenAsync(ct: cancellationToken).ConfigureAwait(false);
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Value);
        httpRequest.Headers.UserAgent.ParseAdd(UserAgent);
        httpRequest.Headers.Accept.ParseAdd("application/vnd.github+json");
        httpRequest.Content = new StringContent(requestBody, Encoding.UTF8, "application/json");

        using var response = await _httpClient.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogDebug(
                "GitHub GraphQL issue-link fetch returned {Status}; that part of the lookup stays unread.",
                (int)response.StatusCode
            );
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument
            .ParseAsync(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var root = document.RootElement;

        if (
            root.TryGetProperty("errors", out var errors)
            && errors.ValueKind is JsonValueKind.Array
            && errors.GetArrayLength() > 0
        )
        {
            _logger.LogDebug(
                "GitHub GraphQL issue-link fetch returned {ErrorCount} error(s); that part of the lookup stays unread.",
                errors.GetArrayLength()
            );
            return null;
        }

        if (
            !root.TryGetProperty("data", out var data)
            || data.ValueKind is not JsonValueKind.Object
            || !data.TryGetProperty("repository", out var repository)
            || repository.ValueKind is not JsonValueKind.Object
            || !repository.TryGetProperty("pullRequest", out var pullRequest)
            || pullRequest.ValueKind is not JsonValueKind.Object
            || !pullRequest.TryGetProperty("closingIssuesReferences", out var connection)
            || connection.ValueKind is not JsonValueKind.Object
        )
        {
            return null;
        }

        var pageInfoPresent =
            connection.TryGetProperty("pageInfo", out var pageInfo) && pageInfo.ValueKind is JsonValueKind.Object;
        var hasNextPage =
            pageInfoPresent
            && pageInfo.TryGetProperty("hasNextPage", out var hasNextPageEl)
            && hasNextPageEl.ValueKind is JsonValueKind.True;
        var endCursor =
            pageInfoPresent
            && pageInfo.TryGetProperty("endCursor", out var endCursorEl)
            && endCursorEl.ValueKind is JsonValueKind.String
                ? endCursorEl.GetString()
                : null;

        var nodes = new List<GitHubLinkedIssue>();
        var rawNodeCount = 0;
        if (connection.TryGetProperty("nodes", out var nodesEl) && nodesEl.ValueKind is JsonValueKind.Array)
        {
            foreach (var node in nodesEl.EnumerateArray())
            {
                rawNodeCount++;
                if (ParseLinkedIssue(node) is { } issue)
                {
                    nodes.Add(issue);
                }
            }
        }

        return new GraphQlPage(nodes, hasNextPage, endCursor, rawNodeCount);
    }

    private static GitHubLinkedIssue? ParseLinkedIssue(JsonElement node)
    {
        if (node.ValueKind is not JsonValueKind.Object || !TryGetInt(node, "number", out var number))
        {
            return null;
        }

        var nodeId = StringOf(node, "id");
        if (nodeId is null)
        {
            // No deterministic identity — never fabricate one, and never let this node parse silently
            // without it (see the RawNodeCount / Nodes.Count mismatch check in ReadAsync).
            return null;
        }

        var relatedPrs = new List<GitHubRelatedPullRequest>();
        if (
            node.TryGetProperty("closedByPullRequestsReferences", out var related)
            && related.ValueKind is JsonValueKind.Object
            && related.TryGetProperty("nodes", out var relatedNodes)
            && relatedNodes.ValueKind is JsonValueKind.Array
        )
        {
            foreach (var relatedNode in relatedNodes.EnumerateArray())
            {
                if (ParseRelatedPullRequest(relatedNode) is { } pr)
                {
                    relatedPrs.Add(pr);
                }
            }
        }

        return new GitHubLinkedIssue(
            nodeId,
            RepositoryNameOf(node),
            number,
            StringOf(node, "url") ?? string.Empty,
            // The author's words — condensed for the same reason AdoWorkItemContextReader condenses a work
            // item title: this reaches the brief as a rendered list entry, and an embedded line break would
            // forge entries the daemon never wrote.
            Condense(StringOf(node, "title")) ?? string.Empty,
            StringOf(node, "state") ?? string.Empty,
            relatedPrs
        );
    }

    private static GitHubRelatedPullRequest? ParseRelatedPullRequest(JsonElement node)
    {
        if (node.ValueKind is not JsonValueKind.Object || !TryGetInt(node, "number", out var number))
        {
            return null;
        }

        var nodeId = StringOf(node, "id");
        if (nodeId is null)
        {
            return null;
        }

        return new GitHubRelatedPullRequest(
            nodeId,
            RepositoryNameOf(node),
            number,
            StringOf(node, "url") ?? string.Empty
        );
    }

    private static string RepositoryNameOf(JsonElement node) =>
        node.TryGetProperty("repository", out var repository)
            ? StringOf(repository, "nameWithOwner") ?? string.Empty
            : string.Empty;

    private static bool TryGetInt(JsonElement element, string property, out int value)
    {
        value = 0;
        return element.ValueKind is JsonValueKind.Object
            && element.TryGetProperty(property, out var el)
            && el.ValueKind is JsonValueKind.Number
            && el.TryGetInt32(out value);
    }

    /// <summary>Collapses the issue title to a single truncated line — see the doc comment on
    /// <see cref="ParseLinkedIssue"/> for why. Mirrors <c>AdoWorkItemContextReader.Condense</c>.</summary>
    private static string? Condense(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var line = value.ReplaceLineEndings(" ").Trim();
        return line.Length <= MaxTitleChars ? line : line[..(MaxTitleChars - 1)] + "…";
    }

    private static string? StringOf(JsonElement element, string property) =>
        element.ValueKind is JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind is JsonValueKind.String
            ? value.GetString()
            : null;
}
