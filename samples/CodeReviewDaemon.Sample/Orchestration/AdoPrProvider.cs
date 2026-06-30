using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmAgentInfra.Auth;
using CodeReviewDaemon.Sample.Persistence.Models;

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

    public async Task<PullRequestPage> ListOpenPullRequestsAsync(PrPollRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var org = request.Repo.OrgOrOwner;
        var project = request.Repo.Project;
        var repo = request.Repo.RepoName;
        var url =
            $"{BaseUrl}/{org}/{project}/_apis/git/repositories/{repo}/pullrequests"
            + $"?searchCriteria.status=active&api-version={ApiVersion}";

        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, url);
        var token = await _tokenProvider.GetAccessTokenAsync(ct: cancellationToken);
        var basic = Convert.ToBase64String(Encoding.ASCII.GetBytes($":{token.Value}"));
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
        httpRequest.Headers.Accept.ParseAdd("application/json");

        using var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        var pullRequests = new List<PullRequestDescriptor>();
        var highWaterMark = 0L;
        foreach (var pr in document.RootElement.GetProperty("value").EnumerateArray())
        {
            var prId = pr.GetProperty("pullRequestId").GetInt64();
            pullRequests.Add(new PullRequestDescriptor
            {
                PrId = prId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                HeadSha = CommitId(pr, "lastMergeSourceCommit"),
                BaseSha = CommitId(pr, "lastMergeTargetCommit"),
                // ADO's PR list exposes no last-activity timestamp, so a new source commit (the head SHA)
                // is the re-review trigger; same-head comment threads do not re-trigger here.
                TriggerWatermark = CommitId(pr, "lastMergeSourceCommit"),
                LifecycleState = MapLifecycle(pr.GetProperty("status").GetString()),
            });

            if (prId > highWaterMark)
            {
                highWaterMark = prId;
            }
        }

        _logger.LogDebug("ADO poll of {Org}/{Project}/{Repo} returned {Count} active PR(s).", org, project, repo, pullRequests.Count);

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

    private static PrLifecycleState MapLifecycle(string? status) => status switch
    {
        "active" => PrLifecycleState.Open,
        "completed" => PrLifecycleState.Merged,
        "abandoned" => PrLifecycleState.Abandoned,
        _ => PrLifecycleState.Closed,
    };
}
