using System.Text.Json.Serialization;

namespace CodeReviewDaemon.Sample.Agents.DeveloperLearnings;

/// <summary>One surviving review finding, attributed to a pattern, as recorded against a PR.</summary>
/// <param name="PatternId">The pattern slug this finding was classified into.</param>
/// <param name="Dimension">The specialist template that raised it — a closed set, derived in code.</param>
/// <param name="Severity">The severity the shipped review carried.</param>
/// <param name="Reconciliation">The reconciler outcome, in its existing wire spelling.</param>
/// <param name="Location">The <c>path:line</c> the finding cited.</param>
/// <param name="Evidence">A verbatim quote from the shipped review. Never paraphrased.</param>
internal sealed record DeveloperObservationHit(
    string PatternId,
    string Dimension,
    string Severity,
    string Reconciliation,
    string Location,
    string Evidence);

/// <summary>
/// One PR's immutable contribution to a developer's ledger. Written once at PR close, never edited.
/// <para>
/// One file per PR rather than one appended ledger, and the reason is concurrency: two PRs closing at once
/// both write, and an append-only single file conflicts on its last line while separate files cannot
/// conflict at all. It also makes every count in every rendered view auditable back to a specific PR — a
/// number whose corpus cannot be named cannot be checked.
/// </para>
/// </summary>
/// <param name="SchemaVersion">Payload shape. New fields append; this does not move for an addition.</param>
/// <param name="SourcePr">Fully qualified PR reference, provider included.</param>
/// <param name="Provider">The PR provider, e.g. <c>azure-devops</c> or <c>github</c>.</param>
/// <param name="Repo">The code repository the PR belongs to, within this store.</param>
/// <param name="PrId">The provider's PR identifier.</param>
/// <param name="ObservedAtUtc">When the record was written, ISO-8601 round-trip.</param>
/// <param name="ReviewRunIds">The review runs this PR accumulated, for audit back to the run store.</param>
/// <param name="Exposure">
/// The specialist templates that RAN and reached Completed on this PR. This is the denominator of every
/// rate in the system. A pattern can only recur where its dimension was exercised, so a PR that never ran
/// the exception-handling specialist is not evidence about exception handling — counting it would let a
/// developer's rate fall because the reviewer got narrower, which is the opposite of improvement.
/// </param>
/// <param name="Hits">
/// Surviving findings, already deduplicated to at most one per <see cref="DeveloperObservationHit.PatternId"/>.
/// </param>
internal sealed record DeveloperObservation(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("sourcePr")] string SourcePr,
    [property: JsonPropertyName("provider")] string Provider,
    [property: JsonPropertyName("repo")] string Repo,
    [property: JsonPropertyName("prId")] string PrId,
    [property: JsonPropertyName("observedAtUtc")] string ObservedAtUtc,
    [property: JsonPropertyName("reviewRunIds")] IReadOnlyList<long> ReviewRunIds,
    [property: JsonPropertyName("exposure")] IReadOnlyList<string> Exposure,
    [property: JsonPropertyName("hits")] IReadOnlyList<DeveloperObservationHit> Hits)
{
    /// <summary>Current observation schema. Never bumped for an appended field.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>
    /// The reconciler outcomes that count as a developer mistake. <c>dropped</c> is deliberately absent:
    /// the daemon can observe that no shipped item cites the finding's location, and a finding the lead
    /// reviewer threw out is not evidence about the author.
    /// </summary>
    private static readonly HashSet<string> SurvivingOutcomes =
        new(StringComparer.Ordinal) { "kept", "severity-changed", "reframed", "merged-into" };

    /// <summary>Whether a reconciler outcome survived into the shipped review.</summary>
    public static bool Survived(string reconciliationOutcome) =>
        SurvivingOutcomes.Contains(reconciliationOutcome);

    /// <summary>
    /// Collapses findings to at most one per pattern, keeping the first occurrence.
    /// <para>
    /// <b>Mandatory, and not cosmetic.</b> Every rate in this system is a per-PR probability. Five
    /// instances of one mistake in one PR is one occurrence; counting per-finding lets the rate exceed 1.0,
    /// at which point <c>(1-p)^n</c> is negative or zero and the resolution maths stops meaning anything.
    /// </para>
    /// </summary>
    public static IReadOnlyList<DeveloperObservationHit> DedupeByPattern(
        IEnumerable<DeveloperObservationHit> hits)
    {
        ArgumentNullException.ThrowIfNull(hits);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var kept = new List<DeveloperObservationHit>();
        foreach (var hit in hits)
        {
            if (seen.Add(hit.PatternId))
            {
                kept.Add(hit);
            }
        }

        return kept;
    }
}
