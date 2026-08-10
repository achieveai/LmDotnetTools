namespace CodeReviewDaemon.Sample.Orchestration;

/// <summary>One specialist finding, as a row a query can count and group rather than a line a human reads.</summary>
/// <param name="Source">The reviewer's display name, matching the per-agent findings file.</param>
/// <param name="Template">The sub-agent template it ran, so a row traces back to a roster entry.</param>
/// <param name="Title">The finding's lead line, verbatim.</param>
/// <param name="Location">The <c>path:line</c> (or <c>path:line-line</c>) the finding cites, rendered.</param>
/// <param name="Severity">The severity phrase as the reviewer wrote it, e.g. <c>Blocker/High</c>.</param>
/// <param name="SeverityTokens">That phrase as canonical tokens — this is the bucketing key.</param>
/// <param name="Outcome">
/// What the shipped review did with it, in the same wire spelling the reconciliation table prints
/// (<c>kept</c>, <c>severity-changed</c>, <c>reframed</c>, <c>merged-into</c>, <c>dropped</c>). Taken from
/// <see cref="ReviewFindingReconciler.Wire"/> rather than from a second name for the same enum, so the
/// artifact and the rendered table can never disagree about what an outcome is called.
/// </param>
/// <param name="ShippedSeverity">The severity the shipped review assigned, or null when nothing cited it.</param>
/// <param name="ShippedTitle">The shipped item's lead line, or null when nothing cited it.</param>
/// <param name="SynthesisNote">The shipped review's own stated reason for the change, never a generated one.</param>
internal sealed record ReviewFindingRecord(
    string Source,
    string Template,
    string Title,
    string Location,
    string Severity,
    IReadOnlyList<string> SeverityTokens,
    string Outcome,
    string? ShippedSeverity,
    string? ShippedTitle,
    string? SynthesisNote);

/// <summary>
/// Per-reviewer accounting for the round trip: how many finding blocks were extracted from this reviewer's
/// own text, and how many rows that reviewer contributed to <see cref="ReviewFindingsArtifactPayload.Findings"/>.
/// The two are counted on separate passes, so an inequality here is a real shortfall and not a restatement.
/// </summary>
internal sealed record ReviewFindingSourceTotal(string Label, string Template, int Parsed, int Recorded);

/// <summary>
/// The round's findings as structured data — the payload of the <c>review-findings</c> artifact.
/// <para>
/// <b>Why this exists.</b> Reviews were stored as prose only. Every question about review quality —
/// how many findings did this round produce, at what severities, how many survived to the shipped review —
/// was answerable only by a human reading markdown, which means it was never answered across more than one
/// run. That makes any change to the reviewer (a model tier, a prompt, a fan-out width) unmeasurable: there
/// is no before to compare an after against. This is that before.
/// </para>
/// <para>
/// <b>It invents no representation.</b> Every field is copied from the <see cref="ReconciledFinding"/> list
/// the reconciliation artifact already builds, on the same call, from the same extraction. The artifact and
/// the rendered <c>PR_Reconciliation_NN.md</c> are two serialisations of one list, so they cannot drift.
/// </para>
/// </summary>
/// <param name="Round">The review round this covers.</param>
/// <param name="Compared">
/// Whether a shipped review body was available to reconcile against. When false there is nothing to compare
/// each finding to, and <see cref="Findings"/> is empty BY CONSTRUCTION rather than by loss — which is why
/// <see cref="ParsedCount"/> is recorded separately and stays non-zero. "Not compared" and "not carried" are
/// different facts and a reader that cannot tell them apart will read the first as a catastrophe.
/// </param>
/// <param name="ParsedCount">
/// Finding blocks extracted from the reviewers' own text, counted independently of <see cref="Findings"/>.
/// </param>
/// <param name="RecordedCount">Rows in <see cref="Findings"/>. Equals <see cref="ParsedCount"/> when compared.</param>
/// <param name="Sources">The same two counts split per reviewer, so a shortfall names who it happened to.</param>
/// <param name="Findings">One row per specialist finding.</param>
internal sealed record ReviewFindingsArtifactPayload(
    int Round,
    bool Compared,
    int ParsedCount,
    int RecordedCount,
    IReadOnlyList<ReviewFindingSourceTotal> Sources,
    IReadOnlyList<ReviewFindingRecord> Findings)
{
    /// <summary>
    /// Findings extracted but not recorded. Zero is the only healthy value on a compared round; on an
    /// uncompared one it equals <see cref="ParsedCount"/> and means the comparison never ran.
    /// </summary>
    public int Shortfall => ParsedCount - RecordedCount;

    /// <summary>
    /// Projects the reconciled list the notes builder already holds. Takes both the sources and the
    /// reconciled rows so the parsed count comes off a separate pass over the source text — a count derived
    /// from <paramref name="reconciled"/> would agree with itself no matter what the loop dropped.
    /// </summary>
    public static ReviewFindingsArtifactPayload Build(
        int round,
        IReadOnlyList<ReviewFindingSource> sources,
        IReadOnlyList<ReconciledFinding> reconciled,
        bool compared)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(reconciled);

        var parsed = ReviewFindingReconciler.CountParsed(sources);
        var recordedBySource = reconciled
            .GroupBy(r => r.Source, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        var totals = parsed
            .Select(p => new ReviewFindingSourceTotal(
                p.Label,
                p.Template,
                p.Parsed,
                recordedBySource.TryGetValue(p.Label, out var recorded) ? recorded : 0))
            .ToArray();

        var rows = reconciled
            .Select(r => new ReviewFindingRecord(
                r.Source,
                r.Template,
                r.Title,
                r.Location,
                r.SpecialistSeverity,
                r.SpecialistSeverityTokens,
                ReviewFindingReconciler.Wire(r.Outcome),
                r.ShippedSeverity,
                r.ShippedTitle,
                r.SynthesisNote))
            .ToArray();

        return new ReviewFindingsArtifactPayload(
            round,
            compared,
            parsed.Sum(p => p.Parsed),
            rows.Length,
            totals,
            rows);
    }
}
