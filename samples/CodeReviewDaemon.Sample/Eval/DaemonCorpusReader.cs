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

    /// <summary>Variant label for the primary arm when the run recorded none.</summary>
    private const string PrimaryVariantFallback = "primary";

    private static readonly JsonSerializerOptions PayloadOptions =
        new() { PropertyNameCaseInsensitive = true };

    private readonly ReviewStore _store;
    private readonly ModelFamilyResolver _familyResolver;
    private readonly int _limit;
    private readonly ILogger<DaemonCorpusReader>? _logger;

    /// <summary>Builds a reader over the daemon's store.</summary>
    /// <param name="store">The daemon's review store.</param>
    /// <param name="familyResolver">Maps a model id to its family; may return null for unknown.</param>
    /// <param name="limit">Most runs to consider.</param>
    /// <param name="logger">Optional diagnostics.</param>
    public DaemonCorpusReader(
        ReviewStore store,
        ModelFamilyResolver familyResolver,
        int limit = 1000,
        ILogger<DaemonCorpusReader>? logger = null
    )
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _familyResolver =
            familyResolver ?? throw new ArgumentNullException(nameof(familyResolver));
        _limit = limit;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<CorpusSnapshot> LoadAsync(string corpusId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(corpusId);
        cancellationToken.ThrowIfCancellationRequested();

        var candidates = new List<Candidate>();

        foreach (var run in _store.ListReviewRuns(ReviewStage.Reviewed, _limit))
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

        return Task.FromResult(CorpusSnapshot.Create(corpusId, candidates));
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
                ["reviewRunId"] = run.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
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
