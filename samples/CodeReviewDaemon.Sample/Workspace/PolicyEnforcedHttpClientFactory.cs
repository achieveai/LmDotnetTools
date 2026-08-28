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
    private readonly ILogger<RetryHandler> _retryLogger;
    private readonly IPolicyRefusalRecorder? _refusals;

    public PolicyEnforcedHttpClientFactory(
        CodeReviewDaemonOptions options,
        ILogger<OperationPolicyHandler> logger,
        ILogger<RetryHandler> retryLogger,
        IPolicyRefusalRecorder? refusals = null
    )
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _retryLogger = retryLogger ?? throw new ArgumentNullException(nameof(retryLogger));
        _refusals = refusals;
    }

    /// <summary>
    /// Creates a policy-enforced client for <paramref name="provider"/> (<c>github</c> / <c>ado</c>),
    /// scoped to that provider's allow-listed repos. The returned client owns its handler chain:
    /// <see cref="RetryHandler"/> (transient-failure resilience, PR #121 M7) → <see cref="OperationPolicyHandler"/>
    /// (route-scoped egress + credential enforcement) → the socket handler.
    /// <para>
    /// The write capability of every policy this builds follows
    /// <see cref="CodeReviewDaemonOptions.EnableCommentPosting"/>. On a COLLECT-ONLY run that is what makes
    /// "the daemon does not post" a property of the client rather than of the call sites: the publishers are
    /// handed a client that structurally cannot POST/PATCH/PUT/DELETE to the provider API, so a code path
    /// that reached one anyway — a new caller, a resumed stage, a mis-evaluated <c>shouldPost</c> — is
    /// refused at the seam instead of succeeding. It stays exactly as capable as before when posting IS
    /// enabled: this narrows the collect-only case only, it does not remove the feature.
    /// </para>
    /// </summary>
    public HttpClient Create(string provider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);

        var policyHandler = new OperationPolicyHandler(BuildPolicies(provider), provider, _logger, _refusals)
        {
            InnerHandler = new HttpClientHandler(),
        };

        return new HttpClient(new RetryHandler(_retryLogger) { InnerHandler = policyHandler });
    }

    /// <summary>
    /// The policies <see cref="Create"/> enforces, one per allow-listed repo for <paramref name="provider"/>.
    /// Exposed separately from the client because the client's inner handler is a real socket: the write
    /// capability these carry is a decision, and a decision has to be assertable without a network.
    /// </summary>
    public IReadOnlyList<OperationPolicy> BuildPolicies(string provider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);

        return
        [
            .. AllowedReposForProvider(provider)
                .Select(repo =>
                    DaemonOperationPolicy.BuildForRun(
                        repo,
                        _options.ReviewBotRepoUrl,
                        allowWriteOperations: _options.EnableCommentPosting
                    )
                ),
        ];
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
            var segments = (entry ?? string.Empty).Split(
                '/',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
            );

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
