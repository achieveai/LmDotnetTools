namespace CodeReviewDaemon.Sample.Workspace;

/// <summary>
/// The GitHub GraphQL request identity the daemon's own reader ever legitimately issues:
/// <c>variables.owner</c>/<c>variables.repo</c>/<c>variables.number</c> from the reviewed-safe
/// linked-issues query (<see cref="GitHubGraphQlContract.Query"/>). One record serves two roles —
/// the EXPECTED scope a trusted caller tags onto its own request (see
/// <see cref="GitHubGraphQlRequestTagging.WithGitHubGraphQlScope"/>), and the ACTUAL scope
/// <see cref="OperationPolicyHandler"/> parses back out of that same request's body — so the policy
/// can compare "what the trusted caller meant to ask" against "what the body on the wire actually
/// says" without two near-duplicate shapes to keep in sync.
/// </summary>
/// <param name="Owner">The repository owner/org the query is scoped to.</param>
/// <param name="Repo">The repository name the query is scoped to.</param>
/// <param name="Number">The pull request number the query is scoped to. Must be positive to be trusted.</param>
internal sealed record GitHubGraphQlRequestScope(string Owner, string Repo, int Number);

/// <summary>
/// The one reviewed-safe GitHub GraphQL document, shared by the single trusted sender
/// (<c>GitHubIssueContextReader</c>, in the Orchestration namespace) and the policy that verifies it
/// (<see cref="OperationPolicy"/>, in this namespace). Defined here — a small neutral Workspace file —
/// rather than in either of those, so the policy never has to depend on Orchestration to see the
/// document it is comparing against (issue #647 / #666 review).
/// </summary>
internal static class GitHubGraphQlContract
{
    /// <summary>
    /// The linked-issues query text, verbatim. See <c>GitHubIssueContextReader</c>'s own remarks for why
    /// its shape is what it is (pagination, ordering, identity fields); this file only owns WHERE the
    /// text lives, not why it reads the way it does.
    /// </summary>
    internal const string Query = """
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
}

/// <summary>
/// Tags an <see cref="HttpRequestMessage"/> with the <see cref="GitHubGraphQlRequestScope"/> it expects
/// to ask about, mirroring <see cref="OperationRequestTagging"/>'s <c>WithOperation</c>/<c>GetOperation</c>
/// shape. Set once, by the one trusted call site that knows owner/repo/number from its own method
/// arguments (<c>GitHubIssueContextReader.FetchPageAsync</c>) — never derived from ambient/current-run
/// state, because the policy that reads it back is a DI singleton shared across every concurrent run.
/// </summary>
internal static class GitHubGraphQlRequestTagging
{
    private static readonly HttpRequestOptionsKey<GitHubGraphQlRequestScope> ScopeKey = new("crd.github-graphql-scope");

    /// <summary>Tags <paramref name="request"/> with <paramref name="scope"/> and returns it (fluent).</summary>
    public static HttpRequestMessage WithGitHubGraphQlScope(
        this HttpRequestMessage request,
        GitHubGraphQlRequestScope scope
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(scope);
        request.Options.Set(ScopeKey, scope);
        return request;
    }

    /// <summary>Reads the expected-scope tag, or <c>null</c> when the request was never tagged.</summary>
    public static GitHubGraphQlRequestScope? GetGitHubGraphQlScope(this HttpRequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.Options.TryGetValue(ScopeKey, out var scope) ? scope : null;
    }
}
