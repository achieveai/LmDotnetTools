using System.Text.Json;
using AchieveAi.LmDotnetTools.LmEval;
using AchieveAi.LmDotnetTools.LmEval.Corpus;
using CodeReviewDaemon.Sample.Agents;
using CodeReviewDaemon.Sample.Orchestration;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;

namespace CodeReviewDaemon.Sample.Eval;

/// <summary>
/// Resolves a model id to its model family, so generator-family exclusion can be applied.
/// <para>
/// Supplied by the host because <c>LmEval</c> owns no model taxonomy and the daemon's model ids are
/// provider-specific. Returning null is the honest answer for a model the host cannot classify: the
/// resulting candidate carries a null generator family, which is recorded as <i>unknown</i> — never
/// as <i>not the judge's family</i> — and the eval run segments those rows out of its aggregates
/// rather than pooling them.
/// </para>
/// </summary>
/// <param name="modelId">The model that produced the review, or null when the run recorded none.</param>
internal delegate string? ModelFamilyResolver(string? modelId);

/// <summary>
/// Assembles an eval corpus from the daemon's own recorded reviews, pairing each run's
/// <c>review-context</c> artifact (the input) with its <c>review</c> artifact (the output), and
/// admitting the collect-only B arm's <c>b-variant-review</c> as a second candidate over the
/// <b>same</b> input.
/// <para>
/// This is the best corpus available and it is already accumulating. It is also, stated plainly,
/// <b>unlabelled</b>: nothing in the daemon records "a human read this review and accepted it", so
/// no candidate produced here carries a <see cref="Candidate.Reference"/>. Reference-guided grading
/// is the single largest accuracy lever a judge has, and its absence is a property of the data, not
/// an oversight in this reader — synthesising a reference from the review being judged would make
/// every judge grade an answer against itself.
/// </para>
/// <para>
/// It lives in the daemon rather than in <c>LmEval</c> because the rows, the schema and the payload
/// shapes are all the daemon's. <c>LmEval</c> owns no persistence and knows nothing about this
/// database; it declares <see cref="ICorpusReader"/> and this class satisfies it.
/// </para>
/// </summary>
internal sealed class DaemonCorpusReader : ICorpusReader
{
    /// <summary>The task type every candidate this reader produces carries.</summary>
    public const string CodeReviewTaskType = "code-review";

    /// <summary>
    /// The <see cref="Candidate.Metadata"/> key carrying the review run a candidate came from — the
    /// join back to the daemon's own rows. A named constant because a consumer has to read it, and
    /// the same string in two files is the one that drifts.
    /// </summary>
    public const string ReviewRunIdMetadataKey = "reviewRunId";

    /// <summary>Variant label for the primary arm when the run recorded none.</summary>
    private const string PrimaryVariantFallback = "primary";

    private static readonly JsonSerializerOptions PayloadOptions =
        new() { PropertyNameCaseInsensitive = true };

    private readonly ReviewStore _store;
    private readonly ModelFamilyResolver _familyResolver;
    private readonly ILogger<DaemonCorpusReader>? _logger;

    /// <summary>Builds a reader over the daemon's store.</summary>
    /// <param name="store">The daemon's review store.</param>
    /// <param name="familyResolver">Maps a model id to its family; may return null for unknown.</param>
    /// <param name="logger">Optional diagnostics.</param>
    /// <remarks>
    /// The window is <b>not</b> a constructor argument. It used to be, and that shape is what made
    /// the original freeze possible: <c>ORDER BY id LIMIT n</c> takes the OLDEST n rows, so a reader
    /// holding one fixed lower edge returned the same earliest history on every load once the store
    /// held more than <c>limit</c> qualifying runs — silently, because the snapshot hash stays
    /// stable and the comparability refusal that would otherwise say "you are not measuring what you
    /// think you are measuring" is perfectly happy: the corpus genuinely has not changed. A window
    /// stated per call has no such state to go stale.
    /// </remarks>
    public DaemonCorpusReader(
        ReviewStore store,
        ModelFamilyResolver familyResolver,
        ILogger<DaemonCorpusReader>? logger = null
    )
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _familyResolver =
            familyResolver ?? throw new ArgumentNullException(nameof(familyResolver));
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<CorpusPage> LoadAsync(
        string corpusId,
        long afterCursor,
        int limit,
        CancellationToken cancellationToken
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(corpusId);
        ArgumentOutOfRangeException.ThrowIfNegative(afterCursor);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        cancellationToken.ThrowIfCancellationRequested();

        var candidates = new List<Candidate>();
        var runs = _store.ListReviewRuns(ReviewStage.Reviewed, limit, afterCursor);

        foreach (var run in runs)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var artifacts = _store.GetArtifacts(run.Id);

            var context = Latest<ContextArtifactPayload>(
                artifacts,
                DaemonReviewStageExecutor.ContextArtifactKind
            );

            if (context is null || string.IsNullOrWhiteSpace(context.Diff))
            {
                // No input means no pair. A run with a review but no recorded diff cannot be
                // judged against the task it answered, and admitting it with an empty task input
                // would put a candidate in the corpus that every judge scores blind.
                _logger?.LogDebug(
                    "Review run {ReviewRunId} has no usable {ArtifactKind} artifact; it forms no corpus pair.",
                    run.Id,
                    DaemonReviewStageExecutor.ContextArtifactKind
                );
                continue;
            }

            var review = Latest<ReviewArtifactPayload>(
                artifacts,
                DaemonReviewStageExecutor.ReviewArtifactKind
            );

            if (review is not null && !string.IsNullOrWhiteSpace(review.ReviewText))
            {
                candidates.Add(
                    Build(
                        run,
                        context.Diff,
                        variantId: Blank(review.VariantId) ?? Blank(run.VariantId)
                            ?? PrimaryVariantFallback,
                        modelId: run.ModelId,
                        content: review.ReviewText
                    )
                );
            }

            var variant = Latest<VariantReviewArtifactPayload>(
                artifacts,
                VariantReviewer.VariantReviewArtifactKind
            );

            if (variant is not null && !string.IsNullOrWhiteSpace(variant.ReviewText))
            {
                // The B arm is paired by construction: the executor computes the review input once
                // and hands the same string to both arms under the same review_run_id, so this
                // candidate answers byte-for-byte the same task as the primary one above. That is
                // what makes these the most valuable rows in the corpus — an A/B judgement needs
                // exactly this shape and nothing else in the store has it.
                candidates.Add(
                    Build(
                        run,
                        context.Diff,
                        variantId: Blank(variant.VariantId) ?? "b",
                        modelId: variant.ModelId,
                        content: variant.ReviewText
                    )
                );
            }
        }

        var truncated = runs.Count == limit;

        // The upper edge REACHED, not the edge of what yielded candidates. A run the reader looked
        // at and rejected — no recorded diff, an unparseable payload — is still a run it will never
        // learn anything new about, so leaving the cursor behind it would make the next window
        // re-read it for ever and never reach what came after.
        var nextCursor = runs.Count > 0 ? runs[^1].Id : afterCursor;

        if (truncated)
        {
            // The condition under which one window stops short, said out loud as well as returned.
            // The caller acts on CorpusPage.Truncated; this line is for the operator reading logs.
            _logger?.LogWarning(
                "Corpus '{CorpusId}' filled its limit of {Limit} review runs and did not reach the "
                    + "end of its window: it covers ids ({AfterCursor}, {NextCursor}] and every run "
                    + "recorded later is outside it. The next load must resume from that edge, or "
                    + "this corpus will not change again.",
                corpusId,
                limit,
                afterCursor,
                nextCursor
            );
        }
        else
        {
            _logger?.LogInformation(
                "Corpus '{CorpusId}' read {RunCount} review runs from window ({AfterCursor}, end] "
                    + "and built {CandidateCount} candidates; the next window starts after "
                    + "{NextCursor}.",
                corpusId,
                runs.Count,
                afterCursor,
                candidates.Count,
                nextCursor
            );
        }

        return Task.FromResult(
            new CorpusPage
            {
                // Null, not an empty snapshot: CorpusSnapshot.Create refuses an empty item list
                // because an empty denominator makes every rate over it undefined rather than zero,
                // and a window in which nothing new was recorded is the normal outcome of a
                // scheduled sweep rather than an error.
                Snapshot = candidates.Count > 0
                    ? CorpusSnapshot.Create(corpusId, candidates)
                    : null,
                NextCursor = nextCursor,
                Truncated = truncated,
            }
        );
    }

    private Candidate Build(
        ReviewRun run,
        string diff,
        string variantId,
        string? modelId,
        string content
    ) =>
        new()
        {
            // The variant is part of the id, not just a field: one run yields two candidates over
            // the same input, and an id that named only the run would collide between them — which
            // the snapshot refuses outright rather than silently double-counting.
            CandidateId = $"{run.Id}:{variantId}",
            TaskType = CodeReviewTaskType,
            TaskInput = diff,
            Content = content,
            Reference = null,
            VariantId = variantId,
            ModelId = modelId,
            GeneratorFamily = _familyResolver(modelId),
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [ReviewRunIdMetadataKey] = run.Id.ToString(
                    System.Globalization.CultureInfo.InvariantCulture
                ),
                ["prId"] = run.PrId,
                ["headSha"] = run.HeadSha,
                ["baseSha"] = run.BaseSha,
                ["reviewKind"] = run.ReviewKind,
                ["prLifecycleState"] = run.PrLifecycleState.ToString(),
                ["promptTemplateHash"] = run.PromptTemplateHash ?? string.Empty,
            },
        };

    /// <summary>
    /// The most recently appended artifact of a kind, deserialized. Latest wins, matching the rule
    /// every other reader of this table already applies; a run re-reviewed in place would otherwise
    /// contribute its superseded text.
    /// </summary>
    private T? Latest<T>(IReadOnlyList<ReviewArtifact> artifacts, string kind)
        where T : class
    {
        var artifact = artifacts
            .Where(a => string.Equals(a.ArtifactKind, kind, StringComparison.Ordinal))
            .OrderByDescending(a => a.Id)
            .FirstOrDefault();

        if (artifact is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(artifact.Payload, PayloadOptions);
        }
        catch (JsonException ex)
        {
            // A payload written by an older schema version is host data of unknown quality. It
            // costs this run one corpus item; letting it throw would cost the whole corpus.
            _logger?.LogWarning(
                ex,
                "Artifact {ArtifactId} of kind {ArtifactKind} did not deserialize; it is skipped.",
                artifact.Id,
                kind
            );
            return null;
        }
    }

    private static string? Blank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
