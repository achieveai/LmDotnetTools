using System.Globalization;

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
/// <param name="MatchScore">
/// The shared-citation count that decided <paramref name="ShippedTitle"/>, copied from
/// <see cref="ReconciledFinding.MatchScore"/>. Zero on a <c>dropped</c> row, where nothing was scored — a
/// MEASURED zero, not an absent one. <see langword="null"/> means the opposite: this row was hydrated from
/// an artifact written before match tracing existed (schema-v1 JSON, with no <c>MatchScore</c> property at
/// all) and <see cref="System.Text.Json"/> left the field unset rather than guessing. A row this old is
/// simply outside the question "what did the join score here" — it must never be read as if it scored zero.
/// </param>
/// <param name="MatchTiedCandidates">
/// How many shipped items tied at <paramref name="MatchScore"/>, copied from
/// <see cref="ReconciledFinding.MatchTiedCandidates"/>. Above 1 means the join broke a tie arbitrarily —
/// this is what <see cref="ReviewFindingsArtifactPayload.AmbiguousMatches"/> counts across the round.
/// <see langword="null"/> for the same pre-match-tracing rows as <paramref name="MatchScore"/>, and for the
/// same reason: a legacy row was never scored, so it cannot be reported as unambiguous either.
/// </param>
/// <param name="ShippedIndex">
/// The winning shipped item's position in <c>ParseFindings(shippedReviewBody)</c>, copied from
/// <see cref="ReconciledFinding.ShippedIndex"/> — a collision-safe identity that survives duplicate shipped
/// titles, where a title-keyed lookup would silently merge two distinct shipped items into one. <c>-1</c> is
/// a MEASURED "no shipped item won this join" (a <c>dropped</c> row), not an absent value.
/// <see langword="null"/> means the opposite: this row was hydrated from an artifact written before this
/// identity existed, and it must never be read as <c>-1</c> — that would misreport a never-tracked row as a
/// deliberately dropped one.
/// </param>
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
    string? SynthesisNote,
    int? MatchScore,
    int? MatchTiedCandidates,
    int? ShippedIndex
);

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
/// <param name="CapturedAtUtc">
/// When this record was written, ISO-8601 round-trip. Present so that every future query is windowed BY
/// CONSTRUCTION: this artifact kind did not exist before it started being written, so its absence on older
/// runs is a fact about the daemon and not about those reviews. Without a first-write timestamp on the row
/// itself, someone querying in six months reads the pre-write period as "zero findings", which is the one
/// conclusion the data cannot support.
/// </param>
/// <param name="PromptTemplateHash">
/// The review prompt template the run was dispatched under, copied from <c>review_run</c>. This is what
/// makes a before/after comparison legitimate — a change in finding counts across a prompt change is a
/// different event from a change within one. Null means the run has no recorded hash, which is itself the
/// honest answer and not a zero.
/// </param>
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
    string CapturedAtUtc,
    string? PromptTemplateHash,
    bool Compared,
    int ParsedCount,
    int RecordedCount,
    IReadOnlyList<ReviewFindingSourceTotal> Sources,
    IReadOnlyList<ReviewFindingRecord> Findings
)
{
    /// <summary>
    /// Where these rows came from, recorded as a stable token because the answer is about to become
    /// ambiguous and the ambiguity is permanent.
    /// <para>
    /// These findings are read out of <see cref="ReviewFindingReconciler"/>'s typed list — the reviewers'
    /// own transcript text — and NOT out of any stored review prose. That distinction is load-bearing from
    /// the moment the infra-narration filter (#113) lands: from then on the review an author reads and the
    /// review text this daemon persists are different documents, because the filter runs on the posted
    /// comment only and every stored artifact keeps the unfiltered text. Anyone querying stored prose in
    /// six months is querying PRE-FILTER text and needs to know it. This record is unaffected either way,
    /// and this field is how a future reader establishes that rather than inferring it.
    /// </para>
    /// </summary>
    public string DerivedFrom => "reviewer-transcripts-via-reconciler";

    /// <summary>
    /// Findings extracted but not recorded. Zero is the only healthy value on a compared round; on an
    /// uncompared one it equals <see cref="ParsedCount"/> and means the comparison never ran.
    /// </summary>
    public int Shortfall => ParsedCount - RecordedCount;

    /// <summary>
    /// Rows in <see cref="Findings"/> whose <see cref="ReviewFindingRecord.MatchTiedCandidates"/> is above 1 —
    /// the join to a shipped item broke a tie rather than finding one clear best candidate. This is a count
    /// of the JOIN's confidence, not of the review's quality: a tied-but-correct match and a clean-but-wrong
    /// one are both invisible to <see cref="Shortfall"/>, which is why this is tracked separately.
    /// <para>
    /// A row with a <see langword="null"/> <see cref="ReviewFindingRecord.MatchTiedCandidates"/> — one
    /// hydrated from a pre-match-tracing artifact — is never counted here. The nullable comparison already
    /// evaluates to <see langword="false"/> for a <see langword="null"/> operand, which is the correct answer:
    /// a legacy row's join was never scored, so it cannot be reported as ambiguous OR as clean.
    /// </para>
    /// </summary>
    public int AmbiguousMatches => Findings.Count(f => f.MatchTiedCandidates > 1);

    /// <summary>
    /// Projects the reconciled list the notes builder already holds. Takes both the sources and the
    /// reconciled rows so the parsed count comes off a separate pass over the source text — a count derived
    /// from <paramref name="reconciled"/> would agree with itself no matter what the loop dropped.
    /// <para>
    /// Every row this produces carries a non-null <see cref="ReviewFindingRecord.MatchScore"/>,
    /// <see cref="ReviewFindingRecord.MatchTiedCandidates"/>, and <see cref="ReviewFindingRecord.ShippedIndex"/>
    /// — <see langword="null"/> is reserved for rows hydrated from an artifact written before these fields
    /// existed, and this method never reads one back in; it only ever builds from a fresh
    /// <see cref="ReconciledFinding"/> list, whose own fields are non-nullable ints.
    /// </para>
    /// </summary>
    public static ReviewFindingsArtifactPayload Build(
        int round,
        IReadOnlyList<ReviewFindingSource> sources,
        IReadOnlyList<ReconciledFinding> reconciled,
        bool compared,
        string? promptTemplateHash
    )
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(reconciled);

        var parsed = ReviewFindingReconciler.CountParsed(sources);

        // Grouped by the reviewer's POSITION, never by its label. The label is `node.Name ?? node.Template`
        // and Name comes off the wire chosen by the model, so two specialists running one template with no
        // name of their own share it. A join on the label fans out and credits each colliding reviewer with
        // the whole group's rows — the per-reviewer counts then sum past RecordedCount and can exceed the
        // reviewer's own Parsed, while the global totals stay right and the shortfall warning never fires.
        // Silent, and permanent: the transcripts these rows came from are not kept.
        var recordedBySource = reconciled.GroupBy(r => r.SourceIndex).ToDictionary(g => g.Key, g => g.Count());

        var totals = parsed
            .Select(p => new ReviewFindingSourceTotal(
                p.Label,
                p.Template,
                p.Parsed,
                recordedBySource.TryGetValue(p.Index, out var recorded) ? recorded : 0
            ))
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
                r.SynthesisNote,
                r.MatchScore,
                r.MatchTiedCandidates,
                r.ShippedIndex
            ))
            .ToArray();

        return new ReviewFindingsArtifactPayload(
            round,
            DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            promptTemplateHash,
            compared,
            parsed.Sum(p => p.Parsed),
            rows.Length,
            totals,
            rows
        );
    }
}
