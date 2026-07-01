using System.Text.Json;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;

namespace CodeReviewDaemon.Sample.Agents;

/// <summary>
/// Runs the collect-only arm of an A/B review (plan §5). It drives one collect-only run over an
/// <see cref="IMultiTurnAgent"/> built from the comparison variant (a different model and prompt) and
/// persists the raw output as a <c>b-variant-review</c> <see cref="ReviewArtifact"/> in SQLite.
/// <para>
/// <b>Isolation by construction.</b> This type depends only on the agent interface and the
/// <see cref="ReviewStore"/> — it holds no provider client, no sandbox command runner, and no
/// ReviewBot repo handle, so it <i>cannot</i> post a comment or write to the ReviewBot git repo. That
/// pairs with the <see cref="Workspace.OperationPolicy"/> capability denial (push/post hard-denied for a
/// collect-only variant) so the B arm's output can only ever land in SQLite (AC#7).
/// </para>
/// </summary>
internal sealed class VariantReviewer
{
    /// <summary>Schema version of the <c>b-variant-review</c> artifact payload (append-compatible).</summary>
    public const int VariantReviewArtifactSchemaVersion = 1;

    public const string VariantReviewArtifactKind = "b-variant-review";

    private readonly IMultiTurnAgent _agent;
    private readonly ReviewStore _store;
    private readonly ILogger<VariantReviewer> _logger;

    public VariantReviewer(IMultiTurnAgent agent, ReviewStore store, ILogger<VariantReviewer> logger)
    {
        _agent = agent ?? throw new ArgumentNullException(nameof(agent));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Collects the comparison <paramref name="variant"/>'s review of <paramref name="reviewInput"/> and
    /// persists it as a <c>b-variant-review</c> artifact under <paramref name="reviewRunId"/>. The variant
    /// must be collect-only (<see cref="ReviewVariant.CanWrite"/> is <c>false</c>) — a writing variant must
    /// never be routed through this SQLite-only path.
    /// </summary>
    public async Task<VariantReviewResult> ReviewAsync(
        long reviewRunId,
        string provider,
        ReviewVariant variant,
        string reviewInput,
        CancellationToken cancellationToken
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentNullException.ThrowIfNull(variant);

        if (variant.CanWrite)
        {
            throw new InvalidOperationException(
                $"variant '{variant.VariantId}' has write capability and must not run as a collect-only B arm");
        }

        var collected = await AgentTextCollector
            .CollectAsync(_agent, reviewInput, cancellationToken)
            .ConfigureAwait(false);

        var payload = new VariantReviewArtifactPayload(
            variant.VariantId,
            variant.ModelId,
            collected.Text,
            collected.RunId);

        // SQLite only — never the ReviewBot git repo (plan §5). The Judge reads B from here later.
        var artifact = _store.AddArtifact(new ReviewArtifact
        {
            ReviewRunId = reviewRunId,
            ArtifactSchemaVersion = VariantReviewArtifactSchemaVersion,
            ArtifactKind = VariantReviewArtifactKind,
            Provider = provider,
            Payload = JsonSerializer.Serialize(payload),
        });

        _logger.LogInformation(
            "Collect-only variant '{Variant}' (model {Model}) run {RunId} persisted b-variant-review artifact {ArtifactId}.",
            variant.VariantId,
            variant.ModelId,
            collected.RunId,
            artifact.Id
        );

        return new VariantReviewResult(collected.Text, collected.RunId, artifact.Id);
    }
}

/// <summary>
/// The bounded shape of a <c>b-variant-review</c> artifact: which variant produced it, the model used
/// (the A/B model axis), the raw review text, and the agent run id. New fields must be additive and
/// optional to preserve append-compatibility.
/// </summary>
internal sealed record VariantReviewArtifactPayload(
    string VariantId,
    string ModelId,
    string ReviewText,
    string? RunId);

/// <summary>The collect-only B-arm output and the id of the SQLite artifact it was written to.</summary>
internal sealed record VariantReviewResult(string ReviewText, string? RunId, long ArtifactId);
