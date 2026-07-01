using CodeReviewDaemon.Sample.Configuration;
using CodeReviewDaemon.Sample.Orchestration;
using CodeReviewDaemon.Sample.Persistence.Models;

namespace CodeReviewDaemon.Sample.Workspace;

/// <summary>
/// Builds the policy-enforced <see cref="HttpClient"/> the daemon's PR providers and comment publishers
/// use (PR #121 H2). The client's pipeline is <see cref="OperationPolicyHandler"/> → inner handler, where
/// the handler enforces one <see cref="OperationPolicy"/> per allow-listed repo for that provider: a
/// provider-API request is permitted only when it targets an allow-listed repo's own route (host +
/// method + repo path), and a denied request is both egress-blocked and credential-withheld. With no
/// repo allow-listed, every outbound call is denied — matching the inert default.
/// </summary>
internal sealed class PolicyEnforcedHttpClientFactory
{
    private readonly CodeReviewDaemonOptions _options;
    private readonly ILogger<OperationPolicyHandler> _logger;

    public PolicyEnforcedHttpClientFactory(
        CodeReviewDaemonOptions options, ILogger<OperationPolicyHandler> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Creates a policy-enforced client for <paramref name="provider"/> (<c>github</c> / <c>ado</c>),
    /// scoped to that provider's allow-listed repos. The returned client owns its handler chain.
    /// </summary>
    public HttpClient Create(string provider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);

        var policies = AllowedReposForProvider(provider)
            .Select(repo => DaemonOperationPolicy.BuildForRun(repo, _options.ReviewBotRepoUrl))
            .ToList();

        return new HttpClient(new OperationPolicyHandler(policies, provider, _logger)
        {
            InnerHandler = new HttpClientHandler(),
        });
    }

    /// <summary>
    /// Maps the <see cref="CodeReviewDaemonOptions.EnabledRepos"/> allow-list to the repo identities for
    /// one provider, mirroring <see cref="PrPollTargetBuilder"/>'s 2-segment (GitHub) / 3-segment (ADO)
    /// parsing so the HTTP scope matches exactly what the poller watches.
    /// </summary>
    private IEnumerable<RepoIdentity> AllowedReposForProvider(string provider)
    {
        foreach (var entry in _options.EnabledRepos)
        {
            var segments = (entry ?? string.Empty)
                .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            RepoIdentity? repo = segments.Length switch
            {
                2 when string.Equals(provider, "github", StringComparison.Ordinal) => new RepoIdentity
                {
                    Provider = "github",
                    OrgOrOwner = segments[0],
                    RepoName = segments[1],
                },
                3 when string.Equals(provider, "ado", StringComparison.Ordinal) => new RepoIdentity
                {
                    Provider = "azure-devops",
                    OrgOrOwner = segments[0],
                    Project = segments[1],
                    RepoName = segments[2],
                },
                _ => null,
            };

            if (repo is not null)
            {
                yield return repo;
            }
        }
    }
}
