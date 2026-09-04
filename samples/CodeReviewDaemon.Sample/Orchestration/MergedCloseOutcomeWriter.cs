using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Workspace.Git;

namespace CodeReviewDaemon.Sample.Orchestration;

/// <summary>
/// Renders <c>outcome.json</c> and <c>OUTCOME.md</c> for a merged close (spec §9.6). Both files are a
/// pure projection of persisted typed rows: every aggregate is recomputed from those rows, the content
/// hash excludes the generation timestamp, and rewriting is byte-identical, so a deleted report
/// regenerates without rerunning the analyst or verifier.
/// </summary>
internal sealed class MergedCloseOutcomeWriter(ReviewStore store, TimeProvider timeProvider)
{
    private const string JsonFileName = "outcome.json";
    private const string MarkdownFileName = "OUTCOME.md";

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    public async Task WriteAsync(long closeRoundId, string notesRoot, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(notesRoot);

        var round =
            store.GetEngagementRound(closeRoundId)
            ?? throw new InvalidOperationException($"Engagement round {closeRoundId} was not found.");
        var engagement =
            store.GetEngagement(round.PrEngagementId)
            ?? throw new InvalidOperationException($"Engagement {round.PrEngagementId} was not found.");
        var repo =
            store.GetRepo(engagement.RepoId)
            ?? throw new InvalidOperationException($"Repository {engagement.RepoId} was not found.");

        var items = store.ListCloseOutcomeItems(closeRoundId);
        var promotions = store.ListPromotionOutcomes(closeRoundId);
        var counts = CountsOf(items);

        var directory = Path.Combine(notesRoot, ResolveNotesRelativePath(repo, engagement.PrId));
        _ = Directory.CreateDirectory(directory);

        var body = BuildBody(engagement, repo, closeRoundId, round.HeadSha, items, promotions, counts);
        var contentSha256 = Sha256(body.ToJsonString(SerializerOptions));
        body["contentSha256"] = contentSha256;
        body["generatedAtUtc"] = timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);

        await File.WriteAllTextAsync(
                Path.Combine(directory, JsonFileName),
                body.ToJsonString(SerializerOptions),
                cancellationToken
            )
            .ConfigureAwait(false);
        await File.WriteAllTextAsync(
                Path.Combine(directory, MarkdownFileName),
                RenderMarkdown(engagement, round.HeadSha, items, promotions, counts, contentSha256),
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The notes directory for a pull request, matching the review branch layout the notes branch
    /// already uses (<c>review/{slug}-{pr}</c> is published under <c>PRs/{slug}-{pr}</c>).
    /// </summary>
    internal static string ResolveNotesRelativePath(RepoIdentity repo, string prId) =>
        Path.Combine("PRs", $"{ReviewBranchManager.RepoSlug(repo)}-{prId}");

    private static MergedCloseCounts CountsOf(IReadOnlyList<CloseOutcomeItem> items)
    {
        var indeterminate = items.Count(item => item.VerifiedLabel == CloseOutcomeLabel.Indeterminate);
        return new MergedCloseCounts(items.Count, items.Count - indeterminate, indeterminate);
    }

    private static JsonObject BuildBody(
        PrEngagement engagement,
        RepoIdentity repo,
        long closeRoundId,
        string mergeCommitSha,
        IReadOnlyList<CloseOutcomeItem> items,
        IReadOnlyList<PromotionOutcome> promotions,
        MergedCloseCounts counts
    ) =>
        new()
        {
            ["schemaVersion"] = 1,
            ["provider"] = engagement.Provider,
            ["repo"] = repo.NormalizedKey,
            ["prId"] = engagement.PrId,
            ["closeRoundId"] = closeRoundId,
            ["mergeCommitSha"] = mergeCommitSha,
            ["counts"] = new JsonObject
            {
                ["total"] = counts.Total,
                ["definitive"] = counts.Definitive,
                ["indeterminate"] = counts.Indeterminate,
                ["byLabel"] = GroupCounts(items, item => item.VerifiedLabel.ToString()),
                ["byKind"] = GroupCounts(items, item => item.Kind.ToString()),
            },
            ["items"] = new JsonArray([.. items.Select(RenderItem)]),
            ["promotions"] = new JsonArray([.. promotions.Select(RenderPromotion)]),
        };

    private static JsonObject GroupCounts(IReadOnlyList<CloseOutcomeItem> items, Func<CloseOutcomeItem, string> key)
    {
        var grouped = new JsonObject();
        foreach (var group in items.GroupBy(key, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            grouped[group.Key] = group.Count();
        }

        return grouped;
    }

    private static JsonObject RenderItem(CloseOutcomeItem item) =>
        new()
        {
            ["candidateId"] = item.CandidateId,
            ["kind"] = item.Kind.ToString(),
            ["summary"] = item.Summary,
            ["proposedLabel"] = item.ProposedLabel,
            ["verifiedLabel"] = item.VerifiedLabel.ToString(),
            ["verificationReasonCode"] = item.VerificationReasonCode,
            ["observedAtUtc"] = item.ObservedAtUtc.ToString("O", CultureInfo.InvariantCulture),
            ["sources"] = new JsonArray([
                .. item
                    .Sources.OrderBy(source => source.SourceRecordId, StringComparer.Ordinal)
                    .Select(source => new JsonObject
                    {
                        ["sourceRecordId"] = source.SourceRecordId,
                        ["contentSha256"] = source.ContentSha256,
                    }),
            ]),
        };

    private static JsonObject RenderPromotion(PromotionOutcome promotion) =>
        new()
        {
            ["sourceObservationId"] = promotion.SourceObservationId,
            ["destinationKind"] = promotion.DestinationKind,
            ["disposition"] = promotion.Disposition.ToString(),
            ["destinationPath"] = promotion.DestinationPath,
            ["destinationContentHash"] = promotion.DestinationContentHash,
            ["reasonCode"] = promotion.ReasonCode,
        };

    /// <summary>
    /// Renders the human-readable report. It states linked, recomputed counts only: the reviewer never
    /// rates itself, so there is no rating, testimonial, or self-assessment anywhere in this output.
    /// </summary>
    private static string RenderMarkdown(
        PrEngagement engagement,
        string mergeCommitSha,
        IReadOnlyList<CloseOutcomeItem> items,
        IReadOnlyList<PromotionOutcome> promotions,
        MergedCloseCounts counts,
        string contentSha256
    )
    {
        var markdown = new StringBuilder()
            .Append("# Merged review outcome — ")
            .Append(engagement.Provider)
            .Append(" PR ")
            .Append(engagement.PrId)
            .Append("\n\nMerge commit: `")
            .Append(mergeCommitSha)
            .Append("`  \nContent hash: `")
            .Append(contentSha256)
            .Append("`\n\n## Measures\n\n| Measure | Count |\n| --- | --- |\n")
            .Append(CultureInfo.InvariantCulture, $"| Total candidates | {counts.Total} |\n")
            .Append(CultureInfo.InvariantCulture, $"| Definitive outcomes | {counts.Definitive} |\n")
            .Append(CultureInfo.InvariantCulture, $"| Indeterminate | {counts.Indeterminate} |\n");

        _ = markdown
            .Append("\n## Verified items\n\n| Candidate | Kind | Proposed | Verified | Reason | Sources |\n")
            .Append("| --- | --- | --- | --- | --- | --- |\n");
        foreach (var item in items)
        {
            _ = markdown
                .Append("| `")
                .Append(item.CandidateId)
                .Append("` | ")
                .Append(item.Kind)
                .Append(" | ")
                .Append(item.ProposedLabel ?? "—")
                .Append(" | ")
                .Append(item.VerifiedLabel)
                .Append(" | `")
                .Append(item.VerificationReasonCode)
                .Append("` | ")
                .Append(
                    string.Join(
                        ", ",
                        item.Sources.OrderBy(s => s.SourceRecordId, StringComparer.Ordinal)
                            .Select(s => $"`{s.SourceRecordId}`")
                    )
                )
                .Append(" |\n");
        }

        _ = markdown
            .Append("\n## Promotions\n\n| Source observation | Destination | Disposition | Path | Reason |\n")
            .Append("| --- | --- | --- | --- | --- |\n");
        foreach (var promotion in promotions)
        {
            _ = markdown
                .Append("| `")
                .Append(promotion.SourceObservationId)
                .Append("` | ")
                .Append(promotion.DestinationKind)
                .Append(" | ")
                .Append(promotion.Disposition)
                .Append(" | ")
                .Append(promotion.DestinationPath ?? "—")
                .Append(" | ")
                .Append(promotion.ReasonCode ?? "—")
                .Append(" |\n");
        }

        return markdown.ToString();
    }

    private static string Sha256(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
}
