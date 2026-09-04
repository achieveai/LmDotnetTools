using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmMultiTurn.Audit;
using CodeReviewDaemon.Sample.Agents;
using CodeReviewDaemon.Sample.Configuration;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;
using PersistedReviewActionKind = CodeReviewDaemon.Sample.Persistence.Models.ReviewActionKind;

namespace CodeReviewDaemon.Sample.Orchestration;

/// <summary>The frozen inputs one <c>DiscussionFollowUp</c> round was admitted with (design §5.4).</summary>
/// <param name="RoundId">The engagement round.</param>
/// <param name="HeadSha">The head, which this round asserts is unchanged.</param>
/// <param name="NewComments">The unconsumed external comments the window was frozen around.</param>
/// <param name="OpenQuestions">Questions still awaiting an answer.</param>
/// <param name="AncestorContext">Older comments needed only to read the new ones.</param>
/// <param name="PriorContextManifest">The manifest the code-review round sourced.</param>
/// <param name="PriorObservations">What earlier rounds recorded.</param>
/// <param name="OmittedPriorObservationCount">Earlier observations intentionally left out of the bounded input.</param>
/// <param name="OmittedPriorObservationByteCount">UTF-8 bytes in the omitted summaries.</param>
/// <param name="PriorObservationSource">Complete same-round audit projection of the selected history.</param>
internal sealed record DiscussionRoundContext(
    long RoundId,
    string HeadSha,
    IReadOnlyList<DiscussionComment> NewComments,
    IReadOnlyList<OpenClarificationQuestion> OpenQuestions,
    IReadOnlyList<DiscussionComment>? AncestorContext = null,
    string? PriorContextManifest = null,
    IReadOnlyList<string>? PriorObservations = null,
    int OmittedPriorObservationCount = 0,
    int OmittedPriorObservationByteCount = 0,
    AuditSourceReference? PriorObservationSource = null
)
{
    /// <summary>Never null.</summary>
    public IReadOnlyList<DiscussionComment> AncestorContext { get; init; } = AncestorContext ?? [];

    /// <summary>Never null.</summary>
    public IReadOnlyList<string> PriorObservations { get; init; } = PriorObservations ?? [];
}

/// <summary>
/// One thing the round proposed that the executor refused, and why. Kept — rather than dropped — so a
/// refusal is auditable: "the agent proposed nothing" and "the agent proposed something inadmissible"
/// are different facts about a round and must not read the same.
/// </summary>
/// <param name="What">The proposal, named well enough to find it in the turn.</param>
/// <param name="Reason">Why it was refused.</param>
internal sealed record RejectedDiscussionItem(string What, string Reason);

/// <summary>What one <c>DiscussionFollowUp</c> round settled on, after the executor's gate.</summary>
/// <param name="RoundId">The round.</param>
/// <param name="Participated">
/// Whether the round decided it had anything to contribute — planned a publication, or concluded a
/// question. A DECISION-time fact: it says nothing about whether the provider accepted anything, which
/// is only known from a typed receipt (see <see cref="DiscussionRoundExecutor.RecordPublicationAsync"/>).
/// False is the genuine no-op §5.4 wants to be cheap, and it is always accompanied by a
/// <see cref="ObservationKind.DeliberateNoAction"/> observation.
/// </param>
/// <param name="BroadReviewRequested">The round recorded that a wider code review is warranted.</param>
/// <param name="Actions">Everything the round will do, including local finalization.</param>
/// <param name="PlannedPublicationActions">
/// The subset queued for the typed publication backend. PLANNED, never sent: naming it for what it will
/// be attempted as keeps a caller from reading the plan as the outcome.
/// </param>
/// <param name="QuestionInterpretations">Interpretations that survived the gate.</param>
/// <param name="Observations">Exactly what was appended to the observation store.</param>
/// <param name="Rejections">What was refused, and why.</param>
internal sealed record DiscussionRoundOutcome(
    long RoundId,
    bool Participated,
    bool BroadReviewRequested,
    IReadOnlyList<PlannedReviewAction> Actions,
    IReadOnlyList<PlannedReviewAction> PlannedPublicationActions,
    IReadOnlyList<QuestionInterpretation> QuestionInterpretations,
    IReadOnlyList<RoundObservationDraft> Observations,
    IReadOnlyList<RejectedDiscussionItem> Rejections
);

/// <summary>
/// What the typed publication backend did with one planned action (design §6.2). This is the ONLY thing
/// that may turn a plan into a record of something the round said: §5.1 makes a provider receipt the
/// moment a question counts as asked, and the same rule holds for a reply, a finding and a delta.
/// </summary>
/// <param name="Action">The planned action this outcome belongs to.</param>
/// <param name="Status">What the backend reported. Only <c>Accepted</c> means the provider took it.</param>
/// <param name="ProviderPermalink">Where it landed, when the provider issued a permalink.</param>
/// <param name="RejectionCode">The mechanical reason it was refused, for a non-accepted status.</param>
internal sealed record DiscussionPublicationOutcome(
    PlannedReviewAction Action,
    ReviewActionStatus Status,
    string? ProviderPermalink = null,
    string? RejectionCode = null
);

/// <summary>Keeps admitted discussion work pending when its production dependencies are unavailable.</summary>
internal sealed class DisabledDiscussionRoundExecutor : IEngagementRoundExecutor
{
    public EngagementRoundIntent Intent => EngagementRoundIntent.DiscussionFollowUp;

    public bool IsAvailable => false;

    public Task<EngagementRoundStatus> ExecuteAsync(EngagementRound round, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(round);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(EngagementRoundStatus.RetryPending);
    }
}

internal interface IDiscussionRoundPolicy
{
    Task<EngagementRoundStatus> ExecuteAsync(DiscussionRoundContext context, CancellationToken cancellationToken);
}

/// <summary>
/// Composes one admitted unchanged-head discussion round onto a run-less exact-head workspace and one
/// parent S2S turn. This boundary owns no provider credentials and creates no <see cref="ReviewRun"/> or
/// pool lease; publication authority is carried only by the daemon-owned immutable conversation scope.
/// </summary>
internal sealed class ProductionDiscussionRoundPolicy : IDiscussionRoundPolicy
{
    private readonly ReviewStore _store;
    private readonly S2SReviewWorkspacePreparer _workspacePreparer;
    private readonly IReviewAgentLoopFactory _loopFactory;
    private readonly IRoundObservationSink _observations;
    private readonly IReviewPublicationOperations _publication;
    private readonly IdempotentQuestionObservationSink _questionObservations;
    private readonly CodeReviewDaemonOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILoggerFactory _loggerFactory;

    public ProductionDiscussionRoundPolicy(
        ReviewStore store,
        S2SReviewWorkspacePreparer workspacePreparer,
        IReviewAgentLoopFactory loopFactory,
        IRoundObservationSink observations,
        IReviewPublicationOperations publication,
        CodeReviewDaemonOptions options,
        TimeProvider timeProvider,
        ILoggerFactory loggerFactory
    )
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _workspacePreparer = workspacePreparer ?? throw new ArgumentNullException(nameof(workspacePreparer));
        _loopFactory = loopFactory ?? throw new ArgumentNullException(nameof(loopFactory));
        _observations = observations ?? throw new ArgumentNullException(nameof(observations));
        _publication = publication ?? throw new ArgumentNullException(nameof(publication));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _questionObservations = new IdempotentQuestionObservationSink(_store, _observations);
    }

    public async Task<EngagementRoundStatus> ExecuteAsync(
        DiscussionRoundContext context,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        var round =
            _store.GetEngagementRound(context.RoundId)
            ?? throw new InvalidOperationException($"Engagement round {context.RoundId} does not exist.");
        if (round.Intent != EngagementRoundIntent.DiscussionFollowUp)
        {
            throw new InvalidOperationException($"Engagement round {round.Id} is not a discussion follow-up.");
        }

        if (round.Status != EngagementRoundStatus.Running)
        {
            throw new InvalidOperationException($"Engagement round {round.Id} is not running.");
        }

        if (!string.Equals(round.HeadSha, context.HeadSha, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Discussion context for round {round.Id} does not match its frozen head."
            );
        }

        var engagement =
            _store.GetEngagement(round.PrEngagementId)
            ?? throw new InvalidOperationException($"Engagement {round.PrEngagementId} does not exist.");
        var repo =
            _store.GetRepo(engagement.RepoId)
            ?? throw new InvalidOperationException($"Repository {engagement.RepoId} does not exist.");
        var provider = RepoIdentity.ToPublisherNamespace(repo.Provider);
        var workspace = await _workspacePreparer
            .PrepareDiscussionAsync(repo, provider, engagement.PrId, round.BaseSha, round.HeadSha, cancellationToken)
            .ConfigureAwait(false);
        var threadId = $"engagement-{engagement.Id}-discussion-round-{round.Id}";
        var reviewScope = new ReviewConversationScope(engagement.Id.ToString(), round.Id.ToString());
        var publicationScope = new ReviewPublicationConversationScope(
            round.Id,
            provider,
            engagement.RepoId,
            engagement.PrId,
            round.HeadSha,
            LivePostingAuthorized: _options.EnableCommentPosting
        );

        await using var loop = _loopFactory.Create(
            DaemonAgentFactory.CreateDiscussionProfile(),
            _options.ReviewModelId,
            threadId,
            reasoningEffort: _options.ToolAssistedReasoningEffort,
            reviewWorkspace: workspace,
            reviewScope: reviewScope,
            publicationScope: publicationScope
        );
        var surface = ReviewLoopSubAgentSurface.Resolve(loop);
        if (surface?.SuppressSpawning is null)
        {
            throw new InvalidOperationException(
                $"Discussion round {round.Id}: the review loop ({loop.GetType().Name}) exposes no "
                    + "IReviewLoopSubAgentSurface spawn-suppression scope, so the one-turn no-spawn contract "
                    + "cannot be enforced."
            );
        }

        var agent = new DiscussionAgent(
            loop,
            _loggerFactory.CreateLogger<DiscussionAgent>(),
            surface.SuppressSpawning,
            _timeProvider
        );
        var executor = new DiscussionRoundExecutor(
            agent,
            _questionObservations,
            _loggerFactory.CreateLogger<DiscussionRoundExecutor>()
        );
        var deadline = _timeProvider.GetUtcNow().AddMinutes(_options.ReviewStageDeadlineMinutes);
        var outcome = await executor.ExecuteAsync(context, deadline, cancellationToken).ConfigureAwait(false);
        try
        {
            if (!PersistQuestionInterpretations(round.Id, outcome.QuestionInterpretations))
            {
                return EngagementRoundStatus.RetryPending;
            }

            var committedAnswers = outcome.Observations.Where(item => item.Kind == ObservationKind.Answer).ToArray();
            if (committedAnswers.Length > 0)
            {
                await _questionObservations
                    .RecordAsync(round.Id, committedAnswers, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (!TryPersistTypedPublicationOutcomes(round.Id, out var acceptedPublication))
            {
                return EngagementRoundStatus.RetryPending;
            }

            var finalization = await _publication
                .FinalizeRoundAsync(
                    new FinalizeRoundRequest(
                        new PublicationScope(
                            round.Id,
                            "discussion-finalize",
                            provider,
                            engagement.RepoId,
                            engagement.PrId,
                            round.HeadSha,
                            [.. context.NewComments.Select(comment => comment.Source)],
                            LivePostingAuthorized: false
                        ),
                        NoOp: !outcome.Participated && !acceptedPublication
                    ),
                    cancellationToken
                )
                .ConfigureAwait(false);

            return finalization.Status == ReviewActionStatus.Accepted
                ? EngagementRoundStatus.Completed
                : EngagementRoundStatus.RetryPending;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _loggerFactory
                .CreateLogger<ProductionDiscussionRoundPolicy>()
                .LogWarning(ex, "Discussion round {RoundId} could not durably apply its outcomes; retrying.", round.Id);
            return EngagementRoundStatus.RetryPending;
        }
    }

    private bool TryPersistTypedPublicationOutcomes(long roundId, out bool accepted)
    {
        accepted = false;
        foreach (
            var action in _store
                .ListReviewActions(roundId)
                .Where(action => action.Kind != PersistedReviewActionKind.FinalizeRound)
        )
        {
            if (action.Status is ReviewActionStatus.Planned or ReviewActionStatus.Sending)
            {
                return false;
            }

            var sources = _store.ListReviewActionSources(roundId, action.ActionId);
            var observation = DescribeTypedPublication(action, sources);
            _ = _store.AppendRoundObservationOnce(
                $"review-action-observation-{roundId}-{action.ActionId}",
                roundId,
                observation,
                action.UpdatedAtUtc
            );
            accepted |= action.Status == ReviewActionStatus.Accepted;
        }

        return true;
    }

    private static RoundObservationDraft DescribeTypedPublication(
        CodeReviewDaemon.Sample.Persistence.Models.ReviewAction action,
        IReadOnlyList<AuditSourceReference> sources
    )
    {
        if (action.Status == ReviewActionStatus.Accepted)
        {
            var receipt =
                ReadMechanicalValue(action.ProviderReceiptJson, "providerPermalink")
                ?? ReadMechanicalValue(action.ProviderReceiptJson, "providerCommentId");
            var suffix = receipt is null ? string.Empty : $"; provider receipt {receipt}";
            return new RoundObservationDraft(
                ObservationFor(action.Kind),
                $"typed action {action.ActionId} ({action.Kind}) was accepted{suffix}",
                sources
            );
        }

        if (action.Status == ReviewActionStatus.CollectedOnly)
        {
            return new RoundObservationDraft(
                ObservationKind.Gap,
                $"typed action {action.ActionId} ({action.Kind}) was collected only and not sent",
                sources
            );
        }

        var code = ReadMechanicalValue(action.RejectionJson, "code");
        var reason = code is null ? string.Empty : $": {code}";
        return new RoundObservationDraft(
            ObservationKind.Gap,
            $"typed action {action.ActionId} ({action.Kind}) was rejected{reason}",
            sources
        );
    }

    private static ObservationKind ObservationFor(PersistedReviewActionKind kind) =>
        kind switch
        {
            PersistedReviewActionKind.SubmitInlineFindings => ObservationKind.Finding,
            PersistedReviewActionKind.PostClarificationQuestion => ObservationKind.Question,
            _ => ObservationKind.DiscussionContribution,
        };

    private static string? ReadMechanicalValue(string? json, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return
                document.RootElement.TryGetProperty(propertyName, out var value)
                && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private bool PersistQuestionInterpretations(long roundId, IReadOnlyList<QuestionInterpretation> interpretations)
    {
        var observedAt = _store.GetEngagementRound(roundId)?.StartedAt ?? _timeProvider.GetUtcNow();
        foreach (var interpretation in interpretations)
        {
            var question = _store.GetClarificationQuestion(interpretation.QuestionId);
            if (question is null)
            {
                return false;
            }

            var providerReferenceJson = JsonSerializer.Serialize(
                new { commentIds = interpretation.CandidateCommentIds },
                DaemonReviewStageExecutor.PayloadOptions
            );
            var interpretationSummary = $"question {interpretation.QuestionId} is {interpretation.State}";
            var identity = Encoding.UTF8.GetBytes(
                $"{roundId}\n{interpretation.QuestionId}\n{providerReferenceJson}\n{interpretation.State}"
            );
            var candidate = new ClarificationCandidateAnswer(
                $"candidate-answer-{roundId}-{Sha256(identity)[..24]}",
                interpretation.QuestionId,
                roundId,
                providerReferenceJson,
                interpretationSummary,
                observedAt
            );

            _ = _store.AddClarificationCandidateAnswer(candidate, interpretation.Evidence);

            if (question.State == interpretation.State)
            {
                continue;
            }

            if (
                question.State is not (ClarificationQuestionState.Open or ClarificationQuestionState.Contested)
                || !_store.TryTransitionClarificationQuestion(
                    interpretation.QuestionId,
                    question.State,
                    interpretation.State,
                    _timeProvider.GetUtcNow()
                )
            )
            {
                return false;
            }
        }

        return true;
    }

    private static string Sha256(ReadOnlySpan<byte> content) =>
        Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    private sealed class IdempotentQuestionObservationSink(ReviewStore store, IRoundObservationSink inner)
        : IRoundObservationSink
    {
        public Task RecordAsync(
            long roundId,
            IReadOnlyList<RoundObservationDraft> observations,
            CancellationToken cancellationToken
        )
        {
            var deferred = new List<RoundObservationDraft>();
            foreach (var observation in observations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (observation.Kind != ObservationKind.Answer)
                {
                    deferred.Add(observation);
                    continue;
                }

                var identity = Encoding.UTF8.GetBytes($"{roundId}\n{observation.Kind}\n{observation.Summary}");
                _ = store.AppendRoundObservationOnce(
                    $"question-observation-{roundId}-{Sha256(identity)[..24]}",
                    roundId,
                    observation,
                    store.GetEngagementRound(roundId)?.StartedAt ?? DateTimeOffset.UnixEpoch
                );
            }

            return deferred.Count == 0 ? Task.CompletedTask : inner.RecordAsync(roundId, deferred, cancellationToken);
        }
    }
}

/// <summary>
/// Re-reads one discussion round's immutable provider window and anchors every included comment to exact
/// same-round audit bytes before any model policy can inspect it.
/// </summary>
internal sealed class EngagementDiscussionRoundExecutor : IEngagementRoundExecutor
{
    internal const string ProviderCommentRecordType = "provider_comment";
    internal const string PriorObservationProjectionRecordType = "prior_observation_projection";
    private const int MaxPriorObservationCount = 100;

    private readonly ReviewStore _store;
    private readonly IReadOnlyDictionary<string, IPrProvider> _providers;
    private readonly IDiscussionRoundPolicy _policy;
    private readonly ILogger<EngagementDiscussionRoundExecutor> _logger;

    public EngagementDiscussionRoundExecutor(
        ReviewStore store,
        IEnumerable<IPrProvider> providers,
        IDiscussionRoundPolicy policy,
        ILogger<EngagementDiscussionRoundExecutor> logger
    )
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        ArgumentNullException.ThrowIfNull(providers);
        _providers = providers.ToDictionary(provider => provider.Provider, StringComparer.OrdinalIgnoreCase);
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public EngagementRoundIntent Intent => EngagementRoundIntent.DiscussionFollowUp;

    public async Task<EngagementRoundStatus> ExecuteAsync(EngagementRound round, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(round);
        if (round.Intent != Intent)
        {
            throw new ArgumentException("The engagement round is not a discussion follow-up.", nameof(round));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var engagement =
            _store.GetEngagement(round.PrEngagementId)
            ?? throw new InvalidOperationException($"Engagement {round.PrEngagementId} does not exist.");
        var repo =
            _store.GetRepo(engagement.RepoId)
            ?? throw new InvalidOperationException($"Repository {engagement.RepoId} does not exist.");
        var providerName = RepoIdentity.ToPublisherNamespace(repo.Provider);
        if (!_providers.TryGetValue(providerName, out var provider))
        {
            throw new InvalidOperationException($"No IPrProvider registered for '{providerName}'.");
        }

        var receiptIds = _store.GetPostedProviderReceiptIds(engagement.RepoId, engagement.PrId);
        var snapshot = await provider
            .GetEngagementSnapshotAsync(repo, engagement.PrId, round.ActivityLowerBound, receiptIds, cancellationToken)
            .ConfigureAwait(false);
        var comments = snapshot
            .ExternalActivity.Where(activity =>
                activity.CreatesDiscussionDemand
                && IsAfter(activity.Watermark, round.ActivityLowerBound)
                && IsAtOrBefore(activity.Watermark, round.ActivityUpperBound)
                && !receiptIds.Contains(activity.ProviderObjectId)
            )
            .Select(activity => ToDiscussionComment(engagement.Id, round.Id, activity))
            .ToArray();
        var ancestors = snapshot
            .AncestorActivity.Where(activity =>
                activity.CreatesDiscussionDemand
                && IsAtOrBefore(activity.Watermark, round.ActivityUpperBound)
                && !receiptIds.Contains(activity.ProviderObjectId)
            )
            .Select(activity => ToDiscussionComment(engagement.Id, round.Id, activity))
            .ToArray();
        var openQuestions = _store
            .ListOpenAskedClarificationQuestionsForEngagement(engagement.Id)
            .Select(ToOpenQuestion)
            .ToArray();
        var allPriorObservations = _store
            .ListEngagementRounds(engagement.Id)
            .Where(prior =>
                prior.Id < round.Id
                && prior.Id <= round.PriorObservationBoundary
                && prior.Status == EngagementRoundStatus.Completed
            )
            .SelectMany(prior => _store.ListRoundObservations(prior.Id))
            .OrderBy(observation => observation.EngagementRoundId)
            .ThenBy(observation => observation.Sequence)
            .ToArray();
        var priorObservations = allPriorObservations.TakeLast(MaxPriorObservationCount).ToArray();
        var omittedPriorObservations = allPriorObservations.Take(
            Math.Max(0, allPriorObservations.Length - priorObservations.Length)
        );
        var omittedPriorObservationByteCount = omittedPriorObservations.Sum(observation =>
            Encoding.UTF8.GetByteCount(observation.Summary)
        );
        var priorObservationSource = StorePriorObservationProjection(engagement.Id, round.Id, allPriorObservations);
        var priorContextManifest = ReadPriorContextManifest(engagement.Id, round);
        var context = new DiscussionRoundContext(
            round.Id,
            round.HeadSha,
            comments,
            openQuestions,
            ancestors,
            PriorContextManifest: priorContextManifest,
            PriorObservations: [.. priorObservations.Select(observation => observation.Summary)],
            OmittedPriorObservationCount: allPriorObservations.Length - priorObservations.Length,
            OmittedPriorObservationByteCount: omittedPriorObservationByteCount,
            PriorObservationSource: priorObservationSource
        );

        _logger.LogInformation(
            "Discussion round {RoundId} froze {CommentCount} external provider comment(s) through {ActivityUpperBound}.",
            round.Id,
            comments.Length,
            round.ActivityUpperBound
        );
        return await _policy.ExecuteAsync(context, cancellationToken).ConfigureAwait(false);
    }

    private static OpenClarificationQuestion ToOpenQuestion(ClarificationQuestion question)
    {
        var target = ReadQuestionPublicationMetadata(question.ProviderTargetJson);
        var receipt = ReadQuestionPublicationMetadata(question.ProviderReceiptJson);
        return new OpenClarificationQuestion(
            question.Id,
            question.Wording,
            question.WithheldConclusion,
            receipt.ProviderCommentId ?? target.ProviderCommentId ?? target.ProviderTargetId,
            receipt.ProviderThreadId ?? target.ProviderThreadId,
            receipt.ProviderPermalink ?? target.ProviderPermalink,
            question.ActionId
        );
    }

    private static QuestionPublicationMetadata ReadQuestionPublicationMetadata(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return default;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            return new QuestionPublicationMetadata(
                ReadString(root, "providerCommentId"),
                ReadString(root, "providerTargetId"),
                ReadString(root, "providerThreadId"),
                ReadString(root, "providerPermalink")
            );
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private static string? ReadString(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private readonly record struct QuestionPublicationMetadata(
        string? ProviderCommentId,
        string? ProviderTargetId,
        string? ProviderThreadId,
        string? ProviderPermalink
    );

    private string? ReadPriorContextManifest(long engagementId, EngagementRound round)
    {
        foreach (
            var run in _store.ListPriorCompletedCodeReviewRuns(
                engagementId,
                round.HeadSha,
                round.PriorObservationBoundary
            )
        )
        {
            if (
                _store.TryGetLatestArtifact(run.Id, DaemonReviewStageExecutor.ContextManifestArtifactKind)
                is not { } artifact
            )
            {
                continue;
            }

            DynamicContextManifest? manifest;
            try
            {
                manifest = JsonSerializer.Deserialize<DynamicContextManifest>(
                    artifact.Payload,
                    DaemonReviewStageExecutor.PayloadOptions
                );
            }
            catch (JsonException)
            {
                continue;
            }

            if (manifest is not null && DynamicContextEvidenceValidator.IsPersistedManifestValid(_store, run, manifest))
            {
                return artifact.Payload;
            }
        }

        return null;
    }

    private AuditSourceReference? StorePriorObservationProjection(
        long engagementId,
        long roundId,
        IReadOnlyList<RoundObservation> observations
    )
    {
        if (observations.Count == 0)
        {
            return null;
        }

        var content = JsonSerializer.SerializeToUtf8Bytes(observations);
        var contentSha256 = Sha256(content);
        var source = _store.StoreAuditRecord(
            new ModelTurnAuditRecord(
                $"prior-observations-{roundId}-{contentSha256[..24]}",
                new MultiTurnAuditScope(
                    engagementId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    roundId.ToString(System.Globalization.CultureInfo.InvariantCulture)
                ),
                $"engagement-round:{roundId}",
                $"engagement-round:{roundId}",
                $"prior-observations:{roundId}",
                null,
                0,
                PriorObservationProjectionRecordType,
                "system",
                null,
                null,
                content,
                contentSha256,
                content.Length,
                AuditCaptureOutcome.Complete,
                null,
                observations[^1].ObservedAtUtc
            )
        );
        return new AuditSourceReference(source.Id, source.ContentSha256);
    }

    private DiscussionComment ToDiscussionComment(long engagementId, long roundId, ProviderDiscussionRef activity)
    {
        var content = Encoding.UTF8.GetBytes(activity.Body);
        var contentSha256 = Sha256(content);
        var sourceRecordId = ProviderSourceRecordId(roundId, activity);
        var source = _store.StoreAuditRecord(
            new ModelTurnAuditRecord(
                sourceRecordId,
                new MultiTurnAuditScope(
                    engagementId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    roundId.ToString(System.Globalization.CultureInfo.InvariantCulture)
                ),
                $"provider:{activity.Provider}:{activity.ThreadId}",
                $"engagement-round:{roundId}",
                $"provider-object:{activity.ProviderObjectId}",
                null,
                activity.PublishedAt.ToUnixTimeMilliseconds(),
                ProviderCommentRecordType,
                "user",
                null,
                activity.Provider,
                content,
                contentSha256,
                content.Length,
                AuditCaptureOutcome.Complete,
                null,
                activity.PublishedAt
            )
        );
        return new DiscussionComment(
            activity.CommentId,
            activity.Author,
            activity.Body,
            new AuditSourceReference(source.Id, source.ContentSha256),
            activity.ProviderObjectId,
            activity.ParentCommentId,
            activity.ThreadId,
            activity.Permalink,
            activity.Path,
            activity.EndLine ?? activity.StartLine
        );
    }

    private static bool IsAfter(ProviderActivityWatermark candidate, ProviderActivityWatermark? lowerBound) =>
        lowerBound is null || candidate.CompareTo(lowerBound) > 0;

    private static bool IsAtOrBefore(ProviderActivityWatermark candidate, ProviderActivityWatermark? upperBound) =>
        upperBound is null || candidate.CompareTo(upperBound) <= 0;

    private static string ProviderSourceRecordId(long roundId, ProviderDiscussionRef activity)
    {
        var identity = Encoding.UTF8.GetBytes(
            $"{activity.Provider}\n{activity.ProviderObjectId}\n{activity.ThreadId}\n{activity.CommentId}"
        );
        return $"provider-comment-{roundId}-{Sha256(identity)[..24]}";
    }

    private static string Sha256(ReadOnlySpan<byte> content) =>
        Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
}

/// <summary>
/// Runs one discussion-only round (design §5.3, §5.4): correlate candidates mechanically, hand the
/// frozen inputs to the discussion agent, then GATE what came back and append the round's observations.
/// <para>
/// The gate is the whole point. The agent supplies judgement; this class supplies the rules that
/// judgement may not break — an interpretation may not cite a comment the correlator never linked, a new
/// finding may not exist without a cause in the frozen window and evidence behind it, the root summary
/// belongs to the code review, and a need for broader review is RECORDED rather than acted on. Nothing
/// here can dispatch a code review or a sub-agent: it holds no orchestrator, no stage machine and no
/// loop factory, so "an unchanged head does not silently become a second review" is a property of what
/// this class can reach, not of what it remembers to avoid.
/// </para>
/// <para>
/// It depends on <see cref="IDiscussionInterpreter"/> rather than on a loop, so every rule below is
/// verifiable without a provider.
/// </para>
/// </summary>
internal sealed class DiscussionRoundExecutor
{
    private readonly IDiscussionInterpreter _interpreter;
    private readonly IRoundObservationSink _observations;
    private readonly ILogger<DiscussionRoundExecutor> _logger;

    public DiscussionRoundExecutor(
        IDiscussionInterpreter interpreter,
        IRoundObservationSink observations,
        ILogger<DiscussionRoundExecutor> logger
    )
    {
        _interpreter = interpreter ?? throw new ArgumentNullException(nameof(interpreter));
        _observations = observations ?? throw new ArgumentNullException(nameof(observations));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Runs <paramref name="context"/> to a durable outcome within <paramref name="deadlineUtc"/>.
    /// Cancellation propagates untouched and BEFORE anything is appended: a round that was cancelled
    /// mid-interpretation has not decided anything, so recording its half-formed conclusions would make
    /// an abandoned round indistinguishable from a finished one.
    /// </summary>
    public async Task<DiscussionRoundOutcome> ExecuteAsync(
        DiscussionRoundContext context,
        DateTimeOffset deadlineUtc,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        var candidates = DiscussionCandidateCorrelator.Correlate(context.OpenQuestions, context.NewComments);
        var input = new DiscussionRoundInput(
            context.RoundId,
            context.HeadSha,
            context.NewComments,
            context.OpenQuestions,
            candidates,
            context.AncestorContext,
            context.PriorContextManifest,
            context.PriorObservations,
            context.OmittedPriorObservationCount,
            context.OmittedPriorObservationByteCount,
            context.PriorObservationSource
        );

        var decision = await _interpreter.InterpretAsync(input, deadlineUtc, cancellationToken).ConfigureAwait(false);

        var gate = new Gate(context, candidates);
        gate.Apply(decision);

        var observations = gate.BuildObservations(decision);
        var preCommitObservations = observations.Where(item => item.Kind != ObservationKind.Answer).ToArray();
        if (preCommitObservations.Length > 0)
        {
            await _observations
                .RecordAsync(context.RoundId, preCommitObservations, cancellationToken)
                .ConfigureAwait(false);
        }

        _logger.LogInformation(
            "Discussion round {RoundId} on unchanged head {HeadSha}: participated={Participated}, "
                + "{PublicationCount} publication action(s), {InterpretationCount} question interpretation(s), "
                + "{RejectionCount} refused, broadReviewRequested={BroadReviewRequested}.",
            context.RoundId,
            context.HeadSha,
            gate.Participated,
            gate.AcceptedActions.Count,
            gate.AcceptedInterpretations.Count,
            gate.Rejections.Count,
            gate.BroadReviewRequested
        );

        return new DiscussionRoundOutcome(
            context.RoundId,
            gate.Participated,
            gate.BroadReviewRequested,
            [.. gate.AcceptedActions, Finalization(context.RoundId)],
            gate.AcceptedActions,
            gate.AcceptedInterpretations,
            observations,
            gate.Rejections
        );
    }

    /// <summary>
    /// Appends what publication ACTUALLY did with the round's planned actions, and answers whether any of
    /// them reached the provider.
    /// <para>
    /// Separate from <see cref="ExecuteAsync"/> because the two know different things. The gate decides
    /// what MAY be published; only the typed backend knows what was. Design §5.1 makes that explicit for a
    /// question — "a question counts as asked only after a provider receipt exists" — and the same holds
    /// for a reply, a finding and a delta: a collect-only default, a stale-head rejection or a crash all
    /// leave a planned action unsaid. Writing the contribution at plan time would make every one of those
    /// read, in the observation store and then in the merged-close counts, as something the daemon said.
    /// </para>
    /// </summary>
    /// <param name="roundId">The round these outcomes belong to.</param>
    /// <param name="outcomes">One entry per planned action the backend reported on.</param>
    /// <param name="cancellationToken">Cancels before anything is appended.</param>
    /// <returns>Whether the provider accepted at least one action.</returns>
    public async Task<bool> RecordPublicationAsync(
        long roundId,
        IReadOnlyList<DiscussionPublicationOutcome> outcomes,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(outcomes);
        cancellationToken.ThrowIfCancellationRequested();

        if (outcomes.Count == 0)
        {
            return false;
        }

        var observations = outcomes.Select(Describe).ToArray();
        await _observations.RecordAsync(roundId, observations, cancellationToken).ConfigureAwait(false);

        var accepted = outcomes.Count(o => o.Status == ReviewActionStatus.Accepted);
        _logger.LogInformation(
            "Discussion round {RoundId} publication: {AcceptedCount} of {OutcomeCount} action(s) accepted by the provider.",
            roundId,
            accepted,
            outcomes.Count
        );

        return accepted > 0;
    }

    /// <summary>
    /// One publication outcome as an observation. Accepted becomes the contribution the action made;
    /// anything else becomes a <see cref="ObservationKind.Gap"/> that names the status, so an unsent
    /// action is visibly unsent rather than absent.
    /// </summary>
    private static RoundObservationDraft Describe(DiscussionPublicationOutcome outcome)
    {
        var action = outcome.Action;
        if (outcome.Status != ReviewActionStatus.Accepted)
        {
            return new RoundObservationDraft(
                ObservationKind.Gap,
                $"{action.Kind} was not published ({outcome.Status}"
                    + (outcome.RejectionCode is { Length: > 0 } code ? $": {code}" : string.Empty)
                    + $"): {action.Body}",
                action.Evidence
            );
        }

        var where = outcome.ProviderPermalink is { Length: > 0 } link ? $" (published at {link})" : string.Empty;
        return new RoundObservationDraft(ObservationFor(action.Kind), action.Body + where, action.Evidence);
    }

    private static ObservationKind ObservationFor(PersistedReviewActionKind kind) =>
        kind switch
        {
            PersistedReviewActionKind.SubmitInlineFindings => ObservationKind.Finding,
            PersistedReviewActionKind.PostClarificationQuestion => ObservationKind.Question,
            _ => ObservationKind.DiscussionContribution,
        };

    /// <summary>
    /// The round's local close. The one planned kind that writes nothing to the provider, which is what
    /// lets a genuine no-op finalize durably while producing no PR noise.
    /// </summary>
    private static PlannedReviewAction Finalization(long roundId) =>
        new(PersistedReviewActionKind.FinalizeRound, $"discussion round {roundId} finalized locally");

    /// <summary>
    /// The admissibility rules, kept in one place so the executor above reads as a sequence and each rule
    /// can be pointed at the clause of §5.3/§5.4 it enforces.
    /// </summary>
    private sealed class Gate(DiscussionRoundContext context, IReadOnlyList<CandidateAnswer> candidates)
    {
        private readonly HashSet<string> _windowCommentIds = [.. context.NewComments.Select(c => c.CommentId)];

        /// <summary>Refs this round was actually shown, and may therefore continue a thread from.</summary>
        private readonly HashSet<string> _knownRefs =
        [
            .. context
                .NewComments.Concat(context.AncestorContext)
                .SelectMany(c => new[] { c.CommentId, c.ThreadId })
                .OfType<string>(),
        ];

        private readonly List<PlannedReviewAction> _accepted = [];
        private readonly List<QuestionInterpretation> _interpretations = [];
        private readonly List<RejectedDiscussionItem> _rejections = [];
        private bool _summaryDeltaTaken;

        public IReadOnlyList<PlannedReviewAction> AcceptedActions => _accepted;

        public IReadOnlyList<QuestionInterpretation> AcceptedInterpretations => _interpretations;

        public IReadOnlyList<RejectedDiscussionItem> Rejections => _rejections;

        /// <summary>
        /// The round's recorded need for a wider code review, AFTER the relevance gate. Not read straight
        /// off the decision: a round that declares itself irrelevant has disowned everything it proposed,
        /// and the demand is not free — the coordinator acts on it.
        /// </summary>
        public bool BroadReviewRequested { get; private set; }

        /// <summary>
        /// A round participated when it planned a publication or concluded a question. Decision-time
        /// only; whether anything reached the provider is a separate, later fact.
        /// </summary>
        public bool Participated => _accepted.Count > 0 || _interpretations.Count > 0;

        public void Apply(DiscussionDecision decision)
        {
            if (!decision.IsRelevant)
            {
                // The agent's own answer to "have I anything to add" is authoritative. Acting on proposals
                // it disowned in the same breath would publish on the strength of a contradiction.
                if (
                    decision.Actions.Count > 0
                    || decision.QuestionInterpretations.Count > 0
                    || decision.BroadReviewNeeded
                )
                {
                    _rejections.Add(
                        new RejectedDiscussionItem(
                            "the whole decision",
                            "the round declared itself irrelevant while still proposing work; nothing was acted on"
                        )
                    );
                }

                return;
            }

            BroadReviewRequested = decision.BroadReviewNeeded;

            foreach (var interpretation in decision.QuestionInterpretations)
            {
                Admit(interpretation);
            }

            foreach (var action in decision.Actions)
            {
                Admit(action);
            }
        }

        /// <summary>
        /// §5.3: the agent interprets, but only over comments a machine already linked. Every refusal
        /// below leaves the question exactly as it was — <see cref="ClarificationQuestionState.Open"/> —
        /// because a question the daemon could not honestly close is still open.
        /// </summary>
        private void Admit(QuestionInterpretation interpretation)
        {
            var what = $"interpretation of question {interpretation.QuestionId}";

            if (context.OpenQuestions.All(q => q.QuestionId != interpretation.QuestionId))
            {
                Reject(
                    what,
                    $"question {interpretation.QuestionId} is not one of the open questions this round was given"
                );
                return;
            }

            if (
                interpretation.State
                is not (ClarificationQuestionState.Answered or ClarificationQuestionState.Contested)
            )
            {
                Reject(what, $"a discussion round may only conclude Answered or Contested, not {interpretation.State}");
                return;
            }

            if (interpretation.CandidateCommentIds.Count == 0)
            {
                Reject(what, "no mechanically correlated candidate was cited, so nothing links a comment to it");
                return;
            }

            var linked = candidates
                .Where(c => c.QuestionId == interpretation.QuestionId)
                .Select(c => c.CommentId)
                .ToHashSet(StringComparer.Ordinal);

            var unlinked = interpretation.CandidateCommentIds.FirstOrDefault(id => !linked.Contains(id));
            if (unlinked is not null)
            {
                Reject(what, $"cited comment {unlinked} is not a mechanically correlated candidate for it");
                return;
            }

            if (interpretation.Evidence.Count == 0)
            {
                Reject(what, "no interpretation evidence was cited");
                return;
            }

            if (
                interpretation.State == ClarificationQuestionState.Contested
                && interpretation.CandidateCommentIds.Count < 2
            )
            {
                // Contested asserts that sourced answers DISAGREE. One candidate cannot disagree with
                // itself, so a single-candidate contest is an unsourced verdict wearing a sourced label.
                Reject(what, "a contested question needs at least two conflicting sourced candidates");
                return;
            }

            _interpretations.Add(interpretation);
        }

        /// <summary>§5.4: what this round may propose, and on what terms.</summary>
        private void Admit(PlannedReviewAction action)
        {
            var what = $"{action.Kind} action";

            if (string.IsNullOrWhiteSpace(action.Body))
            {
                Reject(what, "the action body is blank");
                return;
            }

            if (action.Kind == PersistedReviewActionKind.CreateRootSummary)
            {
                Reject(what, "the root summary belongs to the code review; a discussion round only appends to it");
                return;
            }

            if (action.Kind == PersistedReviewActionKind.FinalizeRound)
            {
                Reject(what, "the executor owns finalization; a round may not plan its own");
                return;
            }

            if (action.Kind == PersistedReviewActionKind.SubmitInlineFindings && !AdmitFinding(what, action))
            {
                return;
            }

            if (action.Kind == PersistedReviewActionKind.ReplyToDiscussion && !AdmitReply(what, action))
            {
                return;
            }

            if (action.Kind == PersistedReviewActionKind.AppendSummaryDelta)
            {
                if (_summaryDeltaTaken)
                {
                    Reject(what, "a round contributes one root-summary delta; the extra one was refused");
                    return;
                }

                _summaryDeltaTaken = true;
            }

            // PostClarificationQuestion needs nothing beyond a body: asking is always admissible, and
            // §5.2 makes it non-blocking, so there is no further bar for it to clear here.
            _accepted.Add(action);
        }

        /// <summary>
        /// §5.4: a new finding is permitted ONLY when causally tied to the new discussion AND supported
        /// by focused code evidence. Without both, the round has quietly resumed the code review it was
        /// told not to repeat — on a head nobody changed.
        /// </summary>
        private bool AdmitFinding(string what, PlannedReviewAction action)
        {
            if (action.CausalCommentIds.Count == 0)
            {
                Reject(what, "a new finding must be causally tied to a comment in this round's frozen window");
                return false;
            }

            var outside = action.CausalCommentIds.FirstOrDefault(id => !_windowCommentIds.Contains(id));
            if (outside is not null)
            {
                Reject(what, $"its stated cause {outside} is not in this round's frozen window");
                return false;
            }

            if (action.Evidence.Count == 0)
            {
                Reject(what, "a new finding must cite the code evidence behind it");
                return false;
            }

            return true;
        }

        /// <summary>A reply must continue a thread this round was actually shown.</summary>
        private bool AdmitReply(string what, PlannedReviewAction action)
        {
            if (action.TargetRef is { Length: > 0 } target && _knownRefs.Contains(target))
            {
                return true;
            }

            Reject(what, $"its target ref {action.TargetRef ?? "(missing)"} is not one this round was shown");
            return false;
        }

        private void Reject(string what, string reason) => _rejections.Add(new RejectedDiscussionItem(what, reason));

        /// <summary>
        /// Everything this round appends at DECISION time. Derived observations sit alongside the agent's
        /// own, so an operator reading the store sees both what the round claimed and what the gate did
        /// about it.
        /// <para>
        /// Accepted actions are deliberately absent. They have not been sent, so there is nothing here to
        /// say about them yet; <see cref="DiscussionRoundExecutor.RecordPublicationAsync"/> writes them
        /// once a typed receipt says what became of each one.
        /// </para>
        /// </summary>
        public IReadOnlyList<RoundObservationDraft> BuildObservations(DiscussionDecision decision)
        {
            var observations = new List<RoundObservationDraft>();

            // A blank summary is not an observation, it is a row that looks like one.
            observations.AddRange(decision.Observations.Where(o => !string.IsNullOrWhiteSpace(o.Summary)));

            foreach (var interpretation in _interpretations)
            {
                observations.Add(
                    new RoundObservationDraft(
                        ObservationKind.Answer,
                        $"question {interpretation.QuestionId} is {interpretation.State} on candidate(s) "
                            + string.Join(", ", interpretation.CandidateCommentIds),
                        interpretation.Evidence
                    )
                );
            }

            foreach (var rejection in _rejections)
            {
                observations.Add(
                    new RoundObservationDraft(ObservationKind.Gap, $"refused {rejection.What}: {rejection.Reason}")
                );
            }

            if (BroadReviewRequested)
            {
                // Recorded, never dispatched. This observation IS the whole response to the need.
                observations.Add(
                    new RoundObservationDraft(
                        ObservationKind.Gap,
                        "a broader code review is warranted but was not started on an unchanged head: "
                            + (decision.BroadReviewReason ?? "(no reason given)")
                    )
                );
            }

            if (!Participated)
            {
                observations.Add(
                    new RoundObservationDraft(
                        ObservationKind.DeliberateNoAction,
                        $"round {context.RoundId} had nothing to add to the discussion on head {context.HeadSha}"
                    )
                );
            }

            return observations;
        }
    }
}
