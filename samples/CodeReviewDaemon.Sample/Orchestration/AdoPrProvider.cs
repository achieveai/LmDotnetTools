using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmAgentInfra.Auth;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Workspace;

namespace CodeReviewDaemon.Sample.Orchestration;

/// <summary>
/// Real <see cref="IPrProvider"/> over the Azure DevOps REST API. Lists active PRs for a repo:
/// <c>GET /{org}/{project}/_apis/git/repositories/{repo}/pullrequests?searchCriteria.status=active&amp;api-version=7.1</c>.
/// ADO authenticates REST calls with HTTP Basic auth carrying the token in the password field (the
/// username is ignored), so the bearer minted by the shared <see cref="IOAuthTokenProvider"/> is sent
/// base64-encoded as <c>:{token}</c>. The opaque cursor (§12) records the highest active
/// <c>pullRequestId</c>; ADO's continuation/skip model never leaks across the seam. The daemon is
/// GitHub-only by default — this provider is registered only when <c>EnableAdoProvider</c> is set.
/// </summary>
internal sealed class AdoPrProvider : IPrProvider
{
    private const string BaseUrl = "https://dev.azure.com";
    private const string ApiVersion = "7.1";

    private readonly HttpClient _httpClient;
    private readonly IOAuthTokenProvider _tokenProvider;
    private readonly ILogger<AdoPrProvider> _logger;

    public AdoPrProvider(HttpClient httpClient, IOAuthTokenProvider tokenProvider, ILogger<AdoPrProvider> logger)
    {
        _httpClient = httpClient;
        _tokenProvider = tokenProvider;
        _logger = logger;
    }

    public string Provider => "ado";

    /// <summary>Bounded pages per poll (PR #121 M5) so one poll can't spin unboundedly on a huge repo.</summary>
    private const int MaxPages = 10;

    public async Task<PullRequestPage> ListOpenPullRequestsAsync(PrPollRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var org = request.Repo.OrgOrOwner;
        var project = request.Repo.Project;
        var repo = request.Repo.RepoName;
        var baseUrl =
            $"{BaseUrl}/{org}/{project}/_apis/git/repositories/{repo}/pullrequests"
            + $"?searchCriteria.status=active&api-version={ApiVersion}";

        var pullRequests = new List<PullRequestDescriptor>();
        var highWaterMark = 0L;

        // Follow ADO's continuation-token pagination (M5): each response may carry an
        // x-ms-continuationtoken header, which is passed back as &continuationToken= on the next page.
        // Bounded by MaxPages per poll.
        string? continuationToken = null;
        var pages = 0;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            pages++;

            var url = continuationToken is null
                ? baseUrl
                : $"{baseUrl}&continuationToken={Uri.EscapeDataString(continuationToken)}";

            using var httpRequest = new HttpRequestMessage(HttpMethod.Get, url)
                .WithOperation(SandboxOperation.ReadProviderMetadata);
            var token = await _tokenProvider.GetAccessTokenAsync(ct: cancellationToken);
            var basic = Convert.ToBase64String(Encoding.ASCII.GetBytes($":{token.Value}"));
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
            httpRequest.Headers.Accept.ParseAdd("application/json");

            using var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
            response.EnsureSuccessStatusCode();

            await using (var stream = await response.Content.ReadAsStreamAsync(cancellationToken))
            {
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                foreach (var pr in document.RootElement.GetProperty("value").EnumerateArray())
                {
                    var prId = pr.GetProperty("pullRequestId").GetInt64();
                    pullRequests.Add(new PullRequestDescriptor
                    {
                        PrId = prId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        HeadSha = CommitId(pr, "lastMergeSourceCommit"),
                        BaseSha = CommitId(pr, "lastMergeTargetCommit"),
                        // ADO's PR list exposes no last-activity timestamp, so a new source commit (the head
                        // SHA) is the re-review trigger; same-head comment threads do not re-trigger here.
                        TriggerWatermark = CommitId(pr, "lastMergeSourceCommit"),
                        LifecycleState = MapLifecycle(pr.GetProperty("status").GetString()),
                        // Recency filter: ADO's list has only creationDate (no last-activity), so UpdatedAt is
                        // left null and the filter falls back to the opened date.
                        CreatedAt = ParseTimestamp(pr, "creationDate"),
                    });

                    if (prId > highWaterMark)
                    {
                        highWaterMark = prId;
                    }
                }
            }

            continuationToken = response.Headers.TryGetValues("x-ms-continuationtoken", out var values)
                ? values.FirstOrDefault()
                : null;
        }
        while (!string.IsNullOrEmpty(continuationToken) && pages < MaxPages);

        _logger.LogDebug(
            "ADO poll of {Org}/{Project}/{Repo} returned {Count} active PR(s) across {Pages} page(s).",
            org, project, repo, pullRequests.Count, pages);

        var hwm = highWaterMark.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return new PullRequestPage
        {
            PullRequests = pullRequests,
            NextCursor = new OpaqueCursor
            {
                Provider = Provider,
                Scope = request.Scope,
                CursorVersion = PrPollingService.CursorVersion,
                CursorPayload = JsonSerializer.Serialize(new { highWaterMark = hwm }),
                HighWaterMark = hwm,
            },
        };
    }

    private static string CommitId(JsonElement pr, string property) =>
        pr.TryGetProperty(property, out var commit)
        && commit.ValueKind is JsonValueKind.Object
        && commit.TryGetProperty("commitId", out var id)
            ? id.GetString() ?? string.Empty
            : string.Empty;

    /// <summary>Parses an ISO-8601 timestamp property (e.g. ADO's <c>creationDate</c>) to a
    /// <see cref="DateTimeOffset"/>, or null when absent/unparseable — a missing date leaves the PR
    /// unfiltered by the recency window rather than silently dropping it.</summary>
    private static DateTimeOffset? ParseTimestamp(JsonElement pr, string property) =>
        pr.TryGetProperty(property, out var value)
        && value.ValueKind is JsonValueKind.String
        && DateTimeOffset.TryParse(
            value.GetString(),
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind,
            out var parsed)
            ? parsed
            : null;

    /// <summary>
    /// Classifies a single PR's lifecycle via
    /// <c>GET /{org}/{project}/_apis/git/repositories/{repo}/pullrequests/{prId}</c> — mapping ADO's
    /// <c>status</c> field (<c>active</c>/<c>completed</c>/<c>abandoned</c>) to <see cref="PrLifecycle"/>.
    /// Used by the PR-lifecycle sweep (a later task) to decide whether to merge or delete the PR's notes
    /// branch. Mirrors <see cref="ListOpenPullRequestsAsync"/>'s basic-auth + accept-json request shape.
    /// </summary>
    public async Task<PrLifecycle> GetPrStateAsync(RepoIdentity repo, string prId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentException.ThrowIfNullOrEmpty(prId);

        var org = repo.OrgOrOwner;
        var project = repo.Project;
        var repoName = repo.RepoName;
        var url =
            $"{BaseUrl}/{org}/{project}/_apis/git/repositories/{repoName}/pullrequests/{prId}"
            + $"?api-version={ApiVersion}";

        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, url)
            .WithOperation(SandboxOperation.ReadProviderMetadata);
        var token = await _tokenProvider.GetAccessTokenAsync(ct: cancellationToken);
        var basic = Convert.ToBase64String(Encoding.ASCII.GetBytes($":{token.Value}"));
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
        httpRequest.Headers.Accept.ParseAdd("application/json");

        using var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return MapPrLifecycle(document.RootElement.GetProperty("status").GetString());
    }

    /// <summary>
    /// Maps ADO's single-PR <c>status</c> to <see cref="PrLifecycle"/>: <c>active</c> is Open,
    /// <c>completed</c> is Merged, <c>abandoned</c> is Abandoned. An unrecognized status is treated as Open
    /// so the sweep leaves the notes branch untouched rather than risk a wrong merge or delete.
    /// </summary>
    private static PrLifecycle MapPrLifecycle(string? status) => status switch
    {
        "active" => PrLifecycle.Open,
        "completed" => PrLifecycle.Merged,
        "abandoned" => PrLifecycle.Abandoned,
        _ => PrLifecycle.Open,
    };

    private static PrLifecycleState MapLifecycle(string? status) => status switch
    {
        "active" => PrLifecycleState.Open,
        "completed" => PrLifecycleState.Merged,
        "abandoned" => PrLifecycleState.Abandoned,
        _ => PrLifecycleState.Closed,
    };
}
