namespace CodeReviewDaemon.Sample.Workspace;

/// <summary>
/// The GitHub GraphQL request identity a review run legitimately asks about:
/// <c>variables.owner</c>/<c>variables.repo</c>/<c>variables.number</c> from the reviewed-safe
/// linked-issues query (<see cref="GitHubGraphQlContract.Query"/>). Serves two roles — the CANONICAL
/// scope bound once into <see cref="OperationPolicyHandler"/>'s constructor at client-construction
/// time, and the ACTUAL scope <see cref="OperationPolicyHandler"/> parses back out of a candidate
/// request's body — so the handler can compare "what this client was built to ask about" against
/// "what the body on the wire actually says" without two near-duplicate shapes to keep in sync.
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
