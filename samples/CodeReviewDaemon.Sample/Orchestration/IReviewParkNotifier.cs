using CodeReviewDaemon.Sample.Configuration;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;

namespace CodeReviewDaemon.Sample.Orchestration;

/// <summary>
/// Announces that a review run has been PERMANENTLY parked — it spent its durable retry budget and nothing
/// will attempt it again for this commit.
/// <para>
/// A seam rather than a direct call so <see cref="PrOrchestrator"/>'s policy stays testable without a
/// provider: the orchestrator takes it as nullable and every existing test leaves it null, which is also the
/// behaviour of a daemon with no publisher wired.
/// </para>
/// </summary>
internal interface IReviewParkNotifier
{
    /// <summary>
    /// Announces the park of <paramref name="run"/>. Called once when
    /// <see cref="ReviewStore.TryMarkReviewRunParked"/> reports that THIS attempt is the one that parked the
    /// row, and again on a later poll for as long as the notice has no terminal outbox row — the delivery
    /// having failed or crashed is the only way a second call happens, and the outbox is what stops a third
    /// once it lands.
    /// </summary>
    Task NotifyParkedAsync(ReviewRun run, string reason, CancellationToken cancellationToken);
}

/// <summary>
/// Posts the park notice as an ordinary pull-request comment, through the same outbox-guarded
/// <see cref="ReviewPoster"/> the review itself uses.
/// </summary>
/// <remarks>
/// <para>
/// <b>It does not run <see cref="InfraNarrationFilter"/>, deliberately.</b> That filter's job is to keep the
/// daemon's own infrastructure commentary out of a REVIEW, and its <c>ExecutionBlockedPattern</c> matches
/// phrases like "could not run" and "not available" — which is close to the only vocabulary a park notice
/// has. Run over this message the filter would hold back the entire comment and the notice would silently
/// never appear. The filter is applied by the review path at its own call site
/// (<c>DaemonReviewStageExecutor.PostReviewCommentHostSideAsync</c>) and nowhere inside
/// <see cref="ReviewPoster"/> or the publishers, so not calling it here is the whole of the bypass. The
/// wording below is nevertheless kept factual and free of the environment nouns the filter pairs those
/// phrases with, so routing it through the filter later would still not swallow it.
/// </para>
/// <para>
/// Once-only is not this class's guarantee to make and it does not try to make one. The first call arrives
/// because the once-only <c>UPDATE ... WHERE parked_at IS NULL</c> reported that it did the parking; any
/// later call arrives because <c>PrOrchestrator</c>'s park guard found no terminal outbox row for the notice
/// and is retrying a delivery that failed. The outbox key below is what makes that retry safe — a
/// <c>Posted</c> row is a replay no-op inside <see cref="ReviewPoster"/> — and it also covers a crash between
/// the park write and the post.
/// </para>
/// </remarks>
internal sealed class ReviewParkNotifier : IReviewParkNotifier
{
    /// <summary>Outbox operation discriminator for a park notice — distinct from a posted review, so the
    /// notice can never be mistaken for evidence that the review itself was delivered.</summary>
    public const string PostParkNoticeOperation = "post-park-notice";

    private readonly ReviewStore _store;
    private readonly IReadOnlyList<IReviewCommentPublisher> _publishers;
    private readonly CodeReviewDaemonOptions _options;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<ReviewParkNotifier> _logger;

    public ReviewParkNotifier(
        ReviewStore store,
        IEnumerable<IReviewCommentPublisher> publishers,
        CodeReviewDaemonOptions options,
        ILoggerFactory loggerFactory
    )
    {
        ArgumentNullException.ThrowIfNull(publishers);

        _store = store ?? throw new ArgumentNullException(nameof(store));
        _publishers = [.. publishers];
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _logger = loggerFactory.CreateLogger<ReviewParkNotifier>();
    }

    public async Task NotifyParkedAsync(ReviewRun run, string reason, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);

        // A missing repo row or a provider with no registered publisher is a wiring problem, not a reason to
        // fail the park: the park is already durable in the store by the time this runs, and the notice is a
        // courtesy on top of it. Say so at Warning and stop.
        if (_store.GetRepo(run.RepoId) is not { } repo)
        {
            _logger.LogWarning(
                "Run {RunId} parked, but repo {RepoId} is unknown; no notice posted.",
                run.Id,
                run.RepoId
            );
            return;
        }

        // The stored provider is in the STORAGE namespace and publishers are keyed by the publisher one, so
        // the lookup has to cross the two — an ADO repo persists "azure-devops" and its publisher answers to
        // "ado", which matched nothing and posted nothing on every Azure DevOps pull request.
        var provider = RepoIdentity.ToPublisherNamespace(repo.Provider);
        var publisher = _publishers.FirstOrDefault(p => string.Equals(p.Provider, provider, StringComparison.Ordinal));
        if (publisher is null)
        {
            _logger.LogWarning(
                "Run {RunId} parked, but no review-comment publisher is registered for provider {Provider}; "
                    + "no notice posted.",
                run.Id,
                provider
            );
            return;
        }

        var poster = new ReviewPoster(publisher, _store, _loggerFactory.CreateLogger<ReviewPoster>());
        var outcome = await poster
            .PostReviewAsync(
                new PostReviewRequest(
                    run.Id,
                    new IdempotencyKeyComponents(
                        // The MAPPED namespace, matching what the review's own key carries: the review path
                        // builds its key from DaemonReviewStageExecutor.ResolveRepo's already-mapped provider,
                        // so a park notice keyed on the raw stored spelling would sit in a namespace no other
                        // key for this pull request uses.
                        Provider: provider,
                        OrgOrOwner: repo.OrgOrOwner,
                        Project: repo.Project,
                        RepoStableId: string.IsNullOrWhiteSpace(repo.RepoStableId)
                            ? repo.NormalizedKey
                            : repo.RepoStableId,
                        PrId: run.PrId,
                        Operation: PostParkNoticeOperation,
                        ArtifactKind: PostParkNoticeOperation,
                        ArtifactSubject: "park",
                        HeadSha: run.HeadSha,
                        VariantId: run.VariantId
                    ),
                    new ReviewCommentTarget(repo, run.PrId),
                    BuildBody(run, reason),
                    // The same gate the review itself posts behind. Unauthorized does not mean "skip": the
                    // poster records the notice as Collected, so a collect-only daemon still leaves proof
                    // that a park happened and was deliberately not published.
                    LivePostingAuthorized: _options.EnableCommentPosting
                ),
                cancellationToken
            )
            .ConfigureAwait(false);

        _logger.LogInformation(
            "Run {RunId} park notice for pr {PrId} resolved as {Outcome}.",
            run.Id,
            run.PrId,
            outcome.Kind
        );
    }

    /// <summary>
    /// The notice text. Short, factual, and it names the way out — a new commit is a new run with a full
    /// budget, so the author's own next push resumes reviewing without anyone touching the daemon.
    /// </summary>
    private string BuildBody(ReviewRun run, string reason)
    {
        var shortSha = run.HeadSha.Length >= 7 ? run.HeadSha[..7] : run.HeadSha;
        return $"[{_options.BotName}]\n\n"
            + $"Automated review parked for commit `{shortSha}`.\n\n"
            + "This review reached its retry limit, so the daemon has stopped attempting it for this commit. "
            + "Pushing a new commit starts a fresh review with a full retry budget.\n\n"
            + $"Reason: {reason}";
    }
}
