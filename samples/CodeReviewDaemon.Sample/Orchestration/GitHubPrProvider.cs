using System.Net.Http.Headers;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmAgentInfra.Auth;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Workspace;

namespace CodeReviewDaemon.Sample.Orchestration;

/// <summary>
/// Real <see cref="IPrProvider"/> over the GitHub REST API. The daemon watches PRs by polling, so this
/// only needs to list the open PRs for a repo: <c>GET /repos/{owner}/{repo}/pulls?state=open</c>. Each
/// request carries a bearer token minted by the shared <see cref="IOAuthTokenProvider"/> (single bot
/// identity per provider, refreshed unattended — plan §5), the <c>User-Agent</c> GitHub mandates, and
/// the <c>vnd.github+json</c> accept header. The opaque cursor (§12) records the newest
/// <c>updated_at</c> seen as a high-water mark; GitHub's own pagination model never leaks across the seam.
/// </summary>
internal sealed class GitHubPrProvider : IPrProvider
{
    private const string BaseUrl = "https://api.github.com";
    private const string UserAgent = "LmDotnetTools-CodeReviewDaemon";

    private readonly HttpClient _httpClient;
    private readonly IOAuthTokenProvider _tokenProvider;
    private readonly ILogger<GitHubPrProvider> _logger;

    public GitHubPrProvider(HttpClient httpClient, IOAuthTokenProvider tokenProvider, ILogger<GitHubPrProvider> logger)
    {
        _httpClient = httpClient;
        _tokenProvider = tokenProvider;
        _logger = logger;
    }

    public string Provider => "github";

    /// <summary>Bounded pages per poll (PR #121 M5) so one poll can't spin unboundedly on a huge repo.</summary>
    private const int MaxPages = 10;

    public async Task<PullRequestPage> ListOpenPullRequestsAsync(PrPollRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var owner = request.Repo.OrgOrOwner;
        var repo = request.Repo.RepoName;

        var pullRequests = new List<PullRequestDescriptor>();
        string? highWaterMark = null;

        // Follow GitHub's Link rel="next" pagination (M5): the first page fixes the query, each subsequent
        // page URL comes verbatim from the previous response's Link header. Bounded by MaxPages per poll.
        var url = $"{BaseUrl}/repos/{owner}/{repo}/pulls?state=open&sort=updated&direction=desc&per_page=100";
        var pages = 0;
        while (url is not null && pages < MaxPages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            pages++;

            using var httpRequest = new HttpRequestMessage(HttpMethod.Get, url)
                .WithOperation(SandboxOperation.ReadProviderMetadata);
            var token = await _tokenProvider.GetAccessTokenAsync(ct: cancellationToken);
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Value);
            httpRequest.Headers.UserAgent.ParseAdd(UserAgent);
            httpRequest.Headers.Accept.ParseAdd("application/vnd.github+json");

            using var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
            response.EnsureSuccessStatusCode();

            await using (var stream = await response.Content.ReadAsStreamAsync(cancellationToken))
            {
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                foreach (var pr in document.RootElement.EnumerateArray())
                {
                    var updatedAt = pr.GetProperty("updated_at").GetString() ?? string.Empty;
                    pullRequests.Add(new PullRequestDescriptor
                    {
                        PrId = pr.GetProperty("number").GetRawText(),
                        HeadSha = pr.GetProperty("head").GetProperty("sha").GetString() ?? string.Empty,
                        BaseSha = pr.GetProperty("base").GetProperty("sha").GetString() ?? string.Empty,
                        TriggerWatermark = updatedAt,
                        LifecycleState = MapLifecycle(pr),
                    });

                    if (string.CompareOrdinal(updatedAt, highWaterMark) > 0)
                    {
                        highWaterMark = updatedAt;
                    }
                }
            }

            url = NextPageUrl(response);
        }

        _logger.LogDebug(
            "GitHub poll of {Owner}/{Repo} returned {Count} open PR(s) across {Pages} page(s).",
            owner, repo, pullRequests.Count, pages);

        return new PullRequestPage
        {
            PullRequests = pullRequests,
            NextCursor = new OpaqueCursor
            {
                Provider = Provider,
                Scope = request.Scope,
                CursorVersion = PrPollingService.CursorVersion,
                CursorPayload = JsonSerializer.Serialize(new { highWaterMark }),
                HighWaterMark = highWaterMark,
            },
        };
    }

    /// <summary>
    /// Extracts the <c>rel="next"</c> URL from the response's <c>Link</c> header, or <c>null</c> when this
    /// is the last page. GitHub's format is <c>&lt;url&gt;; rel="next", &lt;url&gt;; rel="last"</c>.
    /// </summary>
    private static string? NextPageUrl(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Link", out var linkHeaders))
        {
            return null;
        }

        foreach (var segment in string.Join(',', linkHeaders).Split(','))
        {
            var parts = segment.Split(';');
            if (parts.Length < 2 || !parts.Any(p => p.Contains("rel=\"next\"", StringComparison.Ordinal)))
            {
                continue;
            }

            var urlPart = parts[0].Trim();
            if (urlPart.StartsWith('<') && urlPart.EndsWith('>'))
            {
                return urlPart[1..^1];
            }
        }

        return null;
    }

    private static PrLifecycleState MapLifecycle(JsonElement pr)
    {
        var merged = pr.TryGetProperty("merged_at", out var mergedAt) && mergedAt.ValueKind is not JsonValueKind.Null;
        if (merged)
        {
            return PrLifecycleState.Merged;
        }

        var state = pr.GetProperty("state").GetString();
        return string.Equals(state, "open", StringComparison.OrdinalIgnoreCase)
            ? PrLifecycleState.Open
            : PrLifecycleState.Closed;
    }
}
