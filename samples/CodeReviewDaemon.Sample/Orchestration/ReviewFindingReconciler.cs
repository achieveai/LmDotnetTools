using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CodeReviewDaemon.Sample.Orchestration;

/// <summary>One reviewer's own output as the notes builder read it, ready to be reconciled.</summary>
/// <param name="Label">The reviewer's display name, as the findings file and the context manifest name it.</param>
/// <param name="Template">The sub-agent template it ran, so a row can be traced back to a roster entry.</param>
/// <param name="OwnText">Everything the agent itself said, concatenated. Captured BEFORE the artifact size
/// budget trims anything, so a finding cut from the findings file for space is still reconciled.</param>
internal sealed record ReviewFindingSource(string Label, string Template, string OwnText);

/// <summary>
/// What became of one specialist finding by the time the review shipped. The set is deliberately small and
/// mutually exclusive; anything the daemon cannot place lands in <see cref="Dropped"/>, which is named for
/// what the daemon can OBSERVE (the cited location is not in the shipped review) and not for a judgement
/// about whether the finding mattered.
/// </summary>
internal enum ReviewFindingOutcome
{
    /// <summary>Present in the shipped review at the same location, carrying the same severity.</summary>
    Kept,

    /// <summary>Present at the same location, but the shipped review assigned a different severity.</summary>
    SeverityChanged,

    /// <summary>The shipped review carries this location as a question where the specialist raised it as a
    /// finding. A transformation — an item that was already a question and stayed one is <see cref="Kept"/>.</summary>
    Reframed,

    /// <summary>Two or more specialist findings landed on one shipped item.</summary>
    MergedInto,

    /// <summary>No shipped item cites this finding's location.</summary>
    Dropped,
}

/// <summary>One <c>path:line</c> (or <c>path:line-line</c>) location cited by a finding.</summary>
internal sealed record ReviewFindingCitation(string Path, int StartLine, int EndLine)
{
    public override string ToString() =>
        StartLine == EndLine
            ? $"{Path}:{StartLine.ToString(CultureInfo.InvariantCulture)}"
            : $"{Path}:{StartLine.ToString(CultureInfo.InvariantCulture)}-{EndLine.ToString(CultureInfo.InvariantCulture)}";
}

/// <summary>One finding-shaped block lifted out of a reviewer's markdown.</summary>
/// <param name="Title">The heading or list-item lead line, verbatim.</param>
/// <param name="SeverityPhrase">The canonical severity token(s) the title carried, e.g. <c>Blocker/High</c>.</param>
/// <param name="SeverityTokens">Those tokens, distinct and sorted, for comparison.</param>
/// <param name="Citations">Every <c>path:line</c> the block cites, title included.</param>
/// <param name="Body">The block's text, title line included.</param>
/// <param name="IsQuestion">Whether the block, or a heading above it, is a question rather than a finding.</param>
internal sealed record ParsedReviewFinding(
    string Title,
    string SeverityPhrase,
    IReadOnlyList<string> SeverityTokens,
    IReadOnlyList<ReviewFindingCitation> Citations,
    string Body,
    bool IsQuestion
);

/// <summary>One specialist finding and what the shipped review did with it.</summary>
/// <param name="SourceIndex">
/// Which reviewer in the source list produced this row — its position, not its name. This is the ONLY
/// identity a caller may group these rows by. <paramref name="Source"/> is a display label derived from
/// <c>node.Name ?? node.Template</c>, and <c>Name</c> is chosen by the model off the wire, so two
/// specialists running one template with no name of their own carry the same label. Grouping on that label
/// fans out and credits every colliding reviewer with the whole group's rows, which makes the per-reviewer
/// accounting arithmetically impossible while the global totals stay correct — i.e. silently.
/// </param>
/// <param name="Source">The reviewer's display name, as the findings file names it. Not unique.</param>
/// <param name="Template">The sub-agent template it ran.</param>
/// <param name="Title">The finding's lead line, verbatim.</param>
/// <param name="Location">The cited <c>path:line</c>, rendered.</param>
/// <param name="SpecialistSeverity">The severity phrase the reviewer wrote.</param>
/// <param name="SpecialistSeverityTokens">The severity as canonical tokens rather than as the phrase the
/// reviewer happened to type. Carried alongside <paramref name="SpecialistSeverity"/> and not instead of it:
/// the phrase is what a human reads, the tokens are what a query groups by, and deriving one from the other
/// after the fact would mean a second severity parser disagreeing with the first.</param>
/// <param name="Outcome">What became of it by the time the review shipped.</param>
/// <param name="ShippedSeverity">The severity the shipped review assigned, or null when nothing cited it.</param>
/// <param name="ShippedTitle">The shipped item's lead line, or null when nothing cited it.</param>
/// <param name="SynthesisNote">The shipped review's own stated reason, never a generated one.</param>
internal sealed record ReconciledFinding(
    int SourceIndex,
    string Source,
    string Template,
    string Title,
    string Location,
    string SpecialistSeverity,
    IReadOnlyList<string> SpecialistSeverityTokens,
    ReviewFindingOutcome Outcome,
    string? ShippedSeverity,
    string? ShippedTitle,
    string? SynthesisNote
);

/// <summary>How many finding-shaped blocks one reviewer contributed, before any matching happened.</summary>
/// <param name="Index">The reviewer's position in the source list — the join key that
/// <see cref="ReconciledFinding.SourceIndex"/> matches. <paramref name="Label"/> is a display name and can
/// repeat across reviewers, so it is not one.</param>
/// <param name="Label">The reviewer's display name. Not unique.</param>
/// <param name="Template">The sub-agent template it ran.</param>
/// <param name="Parsed">Finding-shaped blocks extracted from its own text.</param>
internal sealed record ReviewFindingSourceCount(int Index, string Label, string Template, int Parsed);

/// <summary>
/// Maps each specialist finding to its outcome in the shipped review, and renders that map as a notes
/// artifact beside the per-agent findings files.
/// <para>
/// <b>Why this exists.</b> The specialists' findings are captured — one <c>PR_Findings_*</c> file per roster
/// node, committed and pushed. What was captured NOWHERE is what happened to each one. Traced by hand on one
/// live run with seven specialists, every round-01 finding that cited a file:line survived in some form, but
/// two were transformed invisibly: an architecture <c>[BLOCKER] High</c> at a DI-coupling site shipped as
/// MEDIUM and reframed from an architecture defect into a test gap, and a telemetry <c>[MEDIUM]</c> shipped
/// as a context question. Same file:line, different severity, different meaning, and no artifact anywhere
/// recorded that the change happened. This class records it.
/// </para>
/// <para>
/// <b>What it will not do.</b> It never states WHY a finding changed unless the shipped review said so in its
/// own words — see <see cref="DispositionVerb"/>. A blank note is the correct output for an unexplained
/// demotion, and is already far more than existed before. A generated rationale would be worse than the
/// nothing it replaced, because it would read exactly like a recorded one.
/// </para>
/// </summary>
internal static partial class ReviewFindingReconciler
{
    /// <summary>File name stem; the round number and <c>.md</c> follow.</summary>
    internal const string FileNamePrefix = "PR_Reconciliation_";

    /// <summary>
    /// Rows rendered in the table before the rest are summarised, and the character budget that also bounds
    /// them.
    /// <para>
    /// The per-PR notes dir is a shared budget, and this file is inside it twice over: the at-close knowledge
    /// extractor concatenates <b>every</b> file under <c>PRs/&lt;slug&gt;-&lt;n&gt;/</c> with no prefix
    /// filter, so an unbounded table here is spent out of the extraction prompt's context window. (The
    /// next-round prior-notes input reads only the <c>PR_Context_</c>/<c>PR_Findings_</c> prefixes, which this
    /// file deliberately sits outside of.)
    /// </para>
    /// <para>
    /// Totals are always computed over ALL rows, never over the rendered subset: a truncated table that also
    /// truncated its own arithmetic would be worse than no table.
    /// </para>
    /// </summary>
    internal const int MaxRenderedRows = 60;

    /// <summary>Character budget for the rendered rows, matching the per-artifact budget used elsewhere.</summary>
    internal const int MaxRowChars = UntrustedTranscriptText.MaxArtifactChars;

    /// <summary>Longest quoted synthesis note; it is untrusted text on a markdown table row.</summary>
    private const int MaxNoteChars = 200;

    /// <summary>
    /// What authorises quoting a line as the synthesis's stated reason.
    /// <para>
    /// <b>Derived from the corpus, not invented.</b> The previous version was five phrase shapes reasoned out
    /// from first principles (<c>downgraded from|to</c>, <c>merged into</c>, <c>superseded by</c>,
    /// <c>severity lowered</c>, <c>no longer a blocker</c>) and it matched <b>0 of 265</b> rows — a column that
    /// is permanently blank reads as a working feature with nothing to report, which is worse than one with
    /// occasional false positives. Scanning 525 non-empty review texts showed reviewers phrase this quite
    /// differently: <c>not raised as a separate finding</c>, <c>subsumed by the blocker above</c>,
    /// <c>already covered by the existing unresolved thread</c>, <c>consolidated into the … finding</c>,
    /// <c>previously raised … superseded by …</c>, <c>escalated … from MEDIUM to HIGH because …</c>.
    /// </para>
    /// <para>
    /// <b>The discriminator is the object, and it had to change.</b> Requiring a preposition
    /// (<c>downgraded from</c>) was what produced the zero. Requiring a <see cref="FindingNoun"/> on the same
    /// line is what separates a disposition from prose about the reviewed code — the corpus is full of
    /// <c>downgrades @pkg from 1.25.1 to 1.24.1</c> and <c>covered by tests</c>, which are the same trap that
    /// the earlier bare <c>deduplicat</c> stem fell into. A severity-to-severity transition
    /// (<see cref="SeverityTransition"/>) qualifies on its own, because <c>escalated from Medium to High</c>
    /// cannot be about anything but a finding.
    /// </para>
    /// </summary>
    [GeneratedRegex(
        @"\b(?:"
            + @"not\s+raised\s+as"
            + @"|subsumed\s+by"
            + @"|superseded\s+by"
            + @"|consolidated\s+(?:\S+\s+){0,6}?into"
            + @"|(?:already\s+)?covered\s+by"
            + @"|(?:down|up)graded"
            + @"|escalated"
            + @"|graded\s+as"
            + @"|retracted"
            + @"|withdrawn"
            + @"|duplicate\s+of"
            + @"|merged\s+into"
            + @"|folded\s+into"
            + @"|previously\s+raised"
            + @"|no\s+longer\s+appl(?:y|ies)"
            + @"|do(?:es)?\s+not\s+apply"
            + @")\b",
        RegexOptions.IgnoreCase
    )]
    private static partial Regex DispositionVerb();

    /// <summary>
    /// The nouns that make a line be about a FINDING rather than about the reviewed code. Deliberately
    /// excludes <c>review</c>, <c>test</c> and <c>risk</c>: all three are common in ordinary review prose and
    /// including them re-admits exactly the false positives this pairing exists to exclude.
    /// </summary>
    [GeneratedRegex(
        @"\b(?:finding|concern|issue|comment|thread|blocker|item|recommendation)s?\b",
        RegexOptions.IgnoreCase
    )]
    private static partial Regex FindingNoun();

    /// <summary>
    /// A severity-to-severity move, which is self-evidently about a finding whatever else the line says. The
    /// severity target is what keeps <c>downgraded from `@P0` to `@P1`</c> (a test-priority annotation in the
    /// reviewed code, present in the corpus) out.
    /// </summary>
    [GeneratedRegex(
        @"\b(?:escalated|downgraded|upgraded|promoted|demoted|raised|lowered|reduced)\b"
            + @"[^.!?]{0,40}?\b(?:from|to)\s+\**\s*(?:blocker|critical|high|medium|low|nit)\b",
        RegexOptions.IgnoreCase
    )]
    private static partial Regex SeverityTransition();

    /// <summary>
    /// Lifts every finding-shaped block out of one reviewer's markdown.
    /// <para>
    /// A block starts at a markdown heading, or at a top-level list item, whose lead line carries a severity
    /// word; it runs until the next such start or the next heading. Nested list items do not start blocks, so
    /// a finding's own sub-bullets stay with it. Never throws: unparseable text yields no findings, which is
    /// reported as "no parseable findings" rather than as an empty success.
    /// </para>
    /// </summary>
    internal static IReadOnlyList<ParsedReviewFinding> ParseFindings(string? markdown)
    {
        var text = UntrustedTranscriptText.Sanitize(markdown);
        if (text.Length == 0)
        {
            return [];
        }

        var findings = new List<ParsedReviewFinding>();
        var headings = new List<(int Level, string Text)>();
        string? openTitle = null;
        var openIsQuestion = false;
        var openBody = new StringBuilder();

        foreach (var line in text.Split('\n'))
        {
            var heading = HeadingLine().Match(line);
            if (heading.Success)
            {
                Flush(findings, ref openTitle, ref openIsQuestion, openBody);
                var level = heading.Groups["hashes"].Value.Length;
                var headingText = heading.Groups["text"].Value.Trim();
                while (headings.Count > 0 && headings[^1].Level >= level)
                {
                    headings.RemoveAt(headings.Count - 1);
                }

                headings.Add((level, headingText));
                if (StartsFinding(headingText))
                {
                    openTitle = headingText;
                    openIsQuestion = AnyQuestion(headings);
                    _ = openBody.AppendLine(headingText);
                }

                continue;
            }

            var item = ListItemLine().Match(line);
            if (item.Success)
            {
                var itemText = item.Groups["text"].Value.Trim();
                if (StartsFinding(Head(itemText)))
                {
                    Flush(findings, ref openTitle, ref openIsQuestion, openBody);
                    openTitle = itemText;
                    openIsQuestion = AnyQuestion(headings) || IsQuestionMarker(Head(itemText));
                    _ = openBody.AppendLine(itemText);
                    continue;
                }
            }

            if (openTitle is not null)
            {
                _ = openBody.AppendLine(line);
            }
        }

        Flush(findings, ref openTitle, ref openIsQuestion, openBody);
        return findings;
    }

    /// <summary>
    /// Maps every parsed specialist finding to its outcome in <paramref name="shippedReviewBody"/>.
    /// Returns an empty list when there is no shipped body to compare against — the caller renders that as a
    /// stated absence, never as a page of <c>dropped</c> rows, because "not compared" and "not carried" are
    /// different facts and only one of them is a loss.
    /// </summary>
    internal static IReadOnlyList<ReconciledFinding> Reconcile(
        IReadOnlyList<ReviewFindingSource> sources,
        string? shippedReviewBody
    )
    {
        ArgumentNullException.ThrowIfNull(sources);
        if (string.IsNullOrWhiteSpace(shippedReviewBody))
        {
            return [];
        }

        var shipped = ParseFindings(shippedReviewBody);
        var pending =
            new List<(int SourceIndex, ReviewFindingSource Source, ParsedReviewFinding Finding, int ShippedIndex)>();
        for (var sourceIndex = 0; sourceIndex < sources.Count; sourceIndex++)
        {
            var source = sources[sourceIndex];
            foreach (var finding in ParseFindings(source.OwnText))
            {
                var best = -1;
                var bestScore = 0;
                for (var i = 0; i < shipped.Count; i++)
                {
                    var score = SharedCitationCount(finding, shipped[i]);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = i;
                    }
                }

                pending.Add((sourceIndex, source, finding, best));
            }
        }

        // How many specialist findings landed on each shipped item. Computed across ALL specialists before any
        // row is classified, because "merged" is a property of the shipped item and cannot be seen from one
        // specialist's side.
        var absorbed = new int[shipped.Count];
        foreach (var (_, _, _, index) in pending)
        {
            if (index >= 0)
            {
                absorbed[index]++;
            }
        }

        var rows = new List<ReconciledFinding>(pending.Count);
        foreach (var (sourceIndex, source, finding, index) in pending)
        {
            if (index < 0)
            {
                rows.Add(
                    new ReconciledFinding(
                        sourceIndex,
                        source.Label,
                        source.Template,
                        finding.Title,
                        RenderLocation(finding),
                        finding.SeverityPhrase,
                        finding.SeverityTokens,
                        ReviewFindingOutcome.Dropped,
                        ShippedSeverity: null,
                        ShippedTitle: null,
                        SynthesisNote: null
                    )
                );
                continue;
            }

            var match = shipped[index];

            // Reframed means a TRANSFORMATION — a finding that shipped as a question. An item that was
            // already phrased as a question and stayed one has not been reframed at all, and labelling it so
            // makes the one outcome this artifact exists to surface indistinguishable from a no-op. Measured
            // over 283 real rows exactly one landed here, and inspection showed it was already a [QUESTION]
            // in the source — i.e. the entire observed population of this label was the no-op case.
            var outcome =
                match.IsQuestion && !finding.IsQuestion ? ReviewFindingOutcome.Reframed
                : absorbed[index] >= 2 ? ReviewFindingOutcome.MergedInto
                : !finding.SeverityTokens.SequenceEqual(match.SeverityTokens, StringComparer.Ordinal)
                    ? ReviewFindingOutcome.SeverityChanged
                : ReviewFindingOutcome.Kept;

            rows.Add(
                new ReconciledFinding(
                    sourceIndex,
                    source.Label,
                    source.Template,
                    finding.Title,
                    RenderLocation(finding),
                    finding.SeverityPhrase,
                    finding.SeverityTokens,
                    outcome,
                    match.SeverityPhrase,
                    match.Title,
                    StatedDisposition(match)
                )
            );
        }

        return rows;
    }

    /// <summary>
    /// How many finding-shaped blocks each source contributes, counted by the same extractor
    /// <see cref="Reconcile"/> uses but on its own pass over the same text.
    /// <para>
    /// Exists so the persisted findings record can assert a round trip — every parsed block must produce
    /// exactly one row — and so a shortfall is recorded rather than absorbed. What it can catch is a drop
    /// inside the matching and classification loop, which is where a row can go missing with nothing to
    /// show for it. What it CANNOT catch is the extractor itself: if <c>ParseFindings</c> never sees a
    /// finding, both sides of the comparison miss it identically and the counts agree. Those are different
    /// guarantees and only the first one is claimed here.
    /// </para>
    /// </summary>
    internal static IReadOnlyList<ReviewFindingSourceCount> CountParsed(IReadOnlyList<ReviewFindingSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        return
        [
            .. sources.Select(
                (s, i) => new ReviewFindingSourceCount(i, s.Label, s.Template, ParseFindings(s.OwnText).Count)
            ),
        ];
    }

    /// <summary>
    /// The round's reconciliation artifact. Always produced, including when there is nothing to reconcile:
    /// a file that says "no specialist findings were parseable" is a fact, while an absent file is
    /// indistinguishable from a build that never ran — which is the failure family this whole notes route
    /// exists to end.
    /// </summary>
    internal static string Render(
        string round,
        IReadOnlyList<ReviewFindingSource> sources,
        IReadOnlyList<ReconciledFinding> rows,
        string? shippedReviewBody
    )
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(rows);
        var shippedBodyAvailable = !string.IsNullOrWhiteSpace(shippedReviewBody);

        var builder = new StringBuilder()
            .Append("# Specialist findings vs the shipped review — round ")
            .AppendLine(round)
            .AppendLine()
            .AppendLine("Authored by the review daemon from its own run state. No agent wrote this file.")
            .AppendLine()
            .AppendLine("Each `PR_Findings_*` file records what one reviewer SAID. This file records what")
            .AppendLine("happened to it: whether the shipped `review.md` carried the finding, changed its")
            .AppendLine("severity, turned it into a question, folded it into another finding, or does not cite")
            .AppendLine("its location at all.")
            .AppendLine();

        if (!shippedBodyAvailable)
        {
            builder
                .AppendLine("## Not compared")
                .AppendLine()
                .AppendLine("The shipped review body was not available to this build, so no outcome could be")
                .AppendLine("determined for any finding. This is **not** a report that findings were lost — it is")
                .AppendLine("a report that the comparison did not run.")
                .AppendLine();
            AppendSources(builder, sources);
            return builder.ToString();
        }

        AppendMethod(builder);

        if (rows.Count == 0)
        {
            builder
                .AppendLine("## Outcomes")
                .AppendLine()
                .AppendLine("No severity-labelled findings could be parsed out of any reviewer's own output, so")
                .AppendLine("there is nothing to map. See the `PR_Findings_*` files for what each reviewer said.")
                .AppendLine();
            AppendSources(builder, sources);
            return builder.ToString();
        }

        builder
            .AppendLine("## Outcomes")
            .AppendLine()
            .AppendLine(
                "| # | Reviewer | Template | Specialist finding | Location | Specialist severity | Outcome | Shipped severity | Shipped as | Stated reason |"
            )
            .AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |");

        var rendered = 0;
        var spent = 0;
        foreach (var row in rows)
        {
            if (rendered >= MaxRenderedRows || spent >= MaxRowChars)
            {
                break;
            }

            rendered++;
            var before = builder.Length;
            builder
                .Append("| ")
                .Append(rendered.ToString(CultureInfo.InvariantCulture))
                .Append(" | ")
                .Append(Cell(row.Source, 60))
                .Append(" | ")
                .Append(Cell(row.Template, 40))
                .Append(" | ")
                .Append(Cell(row.Title, 90))
                .Append(" | ")
                .Append(Cell(row.Location, 80))
                .Append(" | ")
                .Append(Cell(row.SpecialistSeverity, 40))
                .Append(" | `")
                .Append(Wire(row.Outcome))
                .Append('`')
                .Append(" | ")
                .Append(row.ShippedSeverity is null ? "—" : Cell(row.ShippedSeverity, 40))
                .Append(" | ")
                .Append(row.ShippedTitle is null ? "—" : Cell(row.ShippedTitle, 90))
                .Append(" | ")
                .Append(row.SynthesisNote is null ? "—" : Cell(row.SynthesisNote, MaxNoteChars))
                .AppendLine(" |");
            spent += builder.Length - before;
        }

        if (rows.Count > rendered)
        {
            builder
                .AppendLine()
                .Append("_[daemon: ")
                .Append((rows.Count - rendered).ToString(CultureInfo.InvariantCulture))
                .AppendLine(" further row(s) omitted for artifact size. The totals below count every row,")
                .AppendLine("including these.]_");
        }

        builder
            .AppendLine()
            .AppendLine("## Totals")
            .AppendLine()
            .AppendLine("| Outcome | Count |")
            .AppendLine("| --- | --- |");
        foreach (var outcome in Enum.GetValues<ReviewFindingOutcome>())
        {
            builder
                .Append("| `")
                .Append(Wire(outcome))
                .Append("` | ")
                .Append(rows.Count(r => r.Outcome == outcome).ToString(CultureInfo.InvariantCulture))
                .AppendLine(" |");
        }

        builder
            .Append("| **total** | ")
            .Append(rows.Count.ToString(CultureInfo.InvariantCulture))
            .AppendLine(" |")
            .AppendLine();

        AppendUnattributedDispositions(builder, shippedReviewBody, rows);
        AppendSources(builder, sources);
        return builder.ToString();
    }

    /// <summary>
    /// Disposition statements the shipped review made that could NOT be tied to a row, listed verbatim and
    /// labelled as unattributed.
    /// <para>
    /// This exists because of where the corpus actually puts them. Reviewers do state why they downgraded or
    /// declined a finding — but they say it in a grading/consolidation section about the review as a whole
    /// ("the review-grader confirmed …, downgraded the performance concerns to LOW"), not inside the finding's
    /// own block. Block-scoped quoting therefore finds almost none of them, and a blank column would report
    /// "the review said nothing" when the review said a great deal.
    /// </para>
    /// <para>
    /// They are NOT attached to a row, because attaching a review-level sentence to a particular finding is a
    /// guess, and a guessed attribution reads exactly like a recorded one. Unattributed and visible is the
    /// honest form.
    /// </para>
    /// </summary>
    private static void AppendUnattributedDispositions(
        StringBuilder builder,
        string? shippedReviewBody,
        IReadOnlyList<ReconciledFinding> rows
    )
    {
        var quoted = new HashSet<string>(
            rows.Where(r => r.SynthesisNote is not null).Select(r => r.SynthesisNote!),
            StringComparer.Ordinal
        );
        var loose = new List<string>();
        foreach (var line in UntrustedTranscriptText.Sanitize(shippedReviewBody).Split('\n'))
        {
            var trimmed = line.Trim();
            if (
                trimmed.Length > 0
                && !quoted.Contains(trimmed)
                && IsDispositionStatement(trimmed)
                && !loose.Contains(trimmed, StringComparer.Ordinal)
            )
            {
                loose.Add(trimmed);
            }
        }

        builder.AppendLine("## Disposition statements not tied to a row").AppendLine();
        if (loose.Count == 0)
        {
            builder
                .AppendLine("The shipped review stated no disposition anywhere outside the findings above.")
                .AppendLine();
            return;
        }

        builder
            .AppendLine("Quoted verbatim from `review.md`. These say something about what happened to a")
            .AppendLine("finding, but the daemon could not tell WHICH finding, so they are listed rather than")
            .AppendLine("attached to a row — an attribution the daemon guessed would read like one it recorded.")
            .AppendLine();
        foreach (var line in loose.Take(MaxLooseDispositions))
        {
            builder.Append("- ").AppendLine(Cell(line, MaxNoteChars));
        }

        if (loose.Count > MaxLooseDispositions)
        {
            builder
                .Append("- _[daemon: ")
                .Append((loose.Count - MaxLooseDispositions).ToString(CultureInfo.InvariantCulture))
                .AppendLine(" further statement(s) omitted for artifact size]_");
        }

        builder.AppendLine();
    }

    /// <summary>Cap on the unattributed list, for the same shared-budget reason as the row cap.</summary>
    private const int MaxLooseDispositions = 20;

    /// <summary>
    /// The matching rule, stated in the artifact itself. A mapping whose reader cannot see how it was computed
    /// invites being read as authoritative, and this one is not: it is a location join over text two different
    /// models wrote independently.
    /// </summary>
    private static void AppendMethod(StringBuilder builder) =>
        builder
            .AppendLine("## How this mapping was computed")
            .AppendLine()
            .AppendLine("A finding is a heading or top-level list item whose lead line carries a severity word")
            .AppendLine("(`blocker`, `critical`, `high`, `medium`, `low`, `nit`, `info`) or a bracketed")
            .AppendLine("`[QUESTION]` tag or `Question:` prefix. A lead line that COUNTS or GRADES findings is")
            .AppendLine("skipped rather than parsed as one: a tally (`3 HIGH/BLOCKER findings — …`), a severity")
            .AppendLine("roll-up (`0 Critical, 2 High`), a summary/overview lead, a sentence narrating the")
            .AppendLine("review-grader's decision, and a bare label with no text after it. A question must be")
            .AppendLine("the bracketed tag, a `Question:` prefix, or a heading naming a questions section; the")
            .AppendLine("bare word in a sentence does not make one.")
            .AppendLine()
            .AppendLine("Location(s) are the `path:line` and `path:line-line` citations in a finding's text. A")
            .AppendLine("specialist finding is matched to the shipped finding it shares the most citations with,")
            .AppendLine("where two citations match if their line ranges overlap AND one path is a suffix of the")
            .AppendLine("other at a path-segment boundary (`src/Foo.cs` matches `sub/src/Foo.cs`, and does not")
            .AppendLine("match `src/BarFoo.cs`).")
            .AppendLine()
            .AppendLine("`reframed` means a transformation — raised as a finding, shipped as a question. An item")
            .AppendLine("already phrased as a question that stayed one is `kept`, because nothing happened to it.")
            .AppendLine()
            .AppendLine("**What that gets wrong, in both directions.** It over-matches when two different")
            .AppendLine("problems are reported at the same lines, and when prose trailing the last finding in a")
            .AppendLine("block (\"examined and cleared: …\") is read as that finding's own citations.")
            .AppendLine("It under-matches when the shipped review restates a finding without repeating its")
            .AppendLine("`file:line`, cites a nearby line instead, or writes findings in a shape with no severity")
            .AppendLine("word in the lead line. Neither direction is silent: every row shows the location and")
            .AppendLine("both titles, so a wrong join is visible.")
            .AppendLine()
            .AppendLine("**`dropped` is not a loss rate.** It means only that no shipped finding cites this")
            .AppendLine("location. Specialists also cite locations they examined and CLEARED, and the synthesis")
            .AppendLine("legitimately declines findings it disagrees with. Read the count as \"not traceable to")
            .AppendLine("the shipped review by location\", and read the row to see which it was.")
            .AppendLine()
            .AppendLine("**Stated reason is quoted, never generated.** The cell holds a line from the finding's")
            .AppendLine("own block that names a disposition AND a finding it acts on (\"not raised as a separate")
            .AppendLine("finding\", \"subsumed by …\", \"already covered by … thread\"), or a severity-to-severity")
            .AppendLine("move (\"downgraded to LOW\"). The daemon does not supply one when the review gave none.")
            .AppendLine()
            .AppendLine("**A blank cell is normal here, and does not mean silence.** Measured over 260 shipped")
            .AppendLine("reviews from this repository's own store, reviewers state a disposition in 28 of them")
            .AppendLine("(10.8%) — but almost never inside the finding's own block, so block-scoped quoting")
            .AppendLine("attaches one to roughly 1 row in 250. They write it in a review-level grading or")
            .AppendLine("consolidation section instead. Those sentences are not thrown away: they are listed")
            .AppendLine("under **Disposition statements not tied to a row**, unattached, because deciding WHICH")
            .AppendLine("finding a review-level sentence refers to would be a guess, and a guessed attribution")
            .AppendLine("is indistinguishable from a recorded one. Read that section together with this column.")
            .AppendLine();

    private static void AppendSources(StringBuilder builder, IReadOnlyList<ReviewFindingSource> sources)
    {
        builder.AppendLine("## Reviewers read for this mapping").AppendLine();
        if (sources.Count == 0)
        {
            builder.AppendLine("This review dispatched no specialists, so there is nothing to reconcile.");
            return;
        }

        builder.AppendLine("| Reviewer | Template | Findings parsed |").AppendLine("| --- | --- | --- |");
        foreach (var source in sources)
        {
            var parsed = ParseFindings(source.OwnText).Count;
            builder
                .Append("| ")
                .Append(Cell(source.Label, 60))
                .Append(" | ")
                .Append(Cell(source.Template, 40))
                .Append(" | ")
                .Append(parsed.ToString(CultureInfo.InvariantCulture))
                .AppendLine(" |");
        }
    }

    /// <summary>The stable on-disk spelling of an outcome. Kept apart from the enum name so a rename in code
    /// cannot silently change what a committed artifact says.</summary>
    internal static string Wire(ReviewFindingOutcome outcome) =>
        outcome switch
        {
            ReviewFindingOutcome.Kept => "kept",
            ReviewFindingOutcome.SeverityChanged => "severity-changed",
            ReviewFindingOutcome.Reframed => "reframed",
            ReviewFindingOutcome.MergedInto => "merged-into",
            _ => "dropped",
        };

    private static void Flush(
        List<ParsedReviewFinding> findings,
        ref string? openTitle,
        ref bool openIsQuestion,
        StringBuilder openBody
    )
    {
        if (openTitle is null)
        {
            return;
        }

        var body = openBody.ToString();
        var tokens = SeverityTokens(openTitle);
        findings.Add(
            new ParsedReviewFinding(
                openTitle,
                tokens.Count == 0 ? "(unlabelled)" : string.Join('/', tokens),
                tokens,
                Citations(body),
                body,
                openIsQuestion
            )
        );
        openTitle = null;
        openIsQuestion = false;
        _ = openBody.Clear();
    }

    private static bool AnyQuestion(List<(int Level, string Text)> headings)
    {
        foreach (var (_, text) in headings)
        {
            if (IsQuestionMarker(text))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether a lead line marks a QUESTION, as opposed to merely containing the word.
    /// <para>
    /// It used to be <c>Contains("question")</c>, and over real review text that flagged prose sentences —
    /// <c>"…the analyzer version-skew question remains unresolved"</c> was classified as a question item. A
    /// question is a <b>marker</b>: a bracketed <c>[QUESTION]</c> tag, or a heading whose whole text names a
    /// questions section. Anything else is prose that happens to use the word.
    /// </para>
    /// </summary>
    private static bool IsQuestionMarker(string text)
    {
        var lead = StripEmphasis(text).Trim();
        return QuestionTag().IsMatch(lead) || QuestionPrefix().IsMatch(lead) || QuestionSectionHeading().IsMatch(lead);
    }

    /// <summary>
    /// Whether a lead line TALLIES or NARRATES findings rather than being one.
    /// <para>
    /// Three classes, all measured over 4,469 real lead lines rather than guessed:
    /// </para>
    /// <list type="number">
    /// <item>A leading count — <c>3 HIGH/BLOCKER findings</c>, and the same followed by its own colon
    /// delimited description, which an end-of-line anchor used to let through.</item>
    /// <item>A severity roll-up — <c>Findings: 0 Critical, 2 High, 1 Medium</c>, or a statement that there
    /// are none (<c>zero Critical, High, Medium … findings</c>, <c>No critical … issues</c>). These carry no
    /// leading digit, so the count rule alone never saw them.</item>
    /// <item>Grading narration — <c>The review-grader confirmed …</c>, <c>Severity grading: …</c>. These are
    /// the synthesis describing its own grading pass, and they are where the corpus actually states a
    /// disposition; they are excluded as findings and read as reasons instead.</item>
    /// </list>
    /// <para>
    /// The leading-digit guard on the first rule is what keeps a genuine <c>[MEDIUM] duplicate findings</c>
    /// safe, and the sentence-terminator exclusion keeps the match inside one clause.
    /// </para>
    /// </summary>
    private static bool IsNotAFinding(string text)
    {
        var lead = StripEmphasis(text).Trim();
        return FindingTally().IsMatch(lead)
            || TallyPrefix().IsMatch(lead)
            || NoneOfSeverity().IsMatch(lead)
            || GradingNarration().IsMatch(lead)
            || ContentlessSeverityLine().IsMatch(lead)
            || SeverityRollup().Matches(lead).Count >= 2;
    }

    /// <summary>Drops the markdown emphasis and code ticks a lead line wraps its text in.</summary>
    private static string StripEmphasis(string text) =>
        text.Replace("*", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("`", string.Empty, StringComparison.Ordinal)
            .Replace("#", string.Empty, StringComparison.Ordinal);

    /// <summary>The lead of a list item, so a severity word buried in a long paragraph does not start one.</summary>
    private static string Head(string text) => text.Length <= 160 ? text : text[..160];

    /// <summary>
    /// The severity token(s) a lead line carries. The bracketed question tag contributes
    /// <c>Question</c> here; the bare word does NOT, because a sentence using the word "question" is not a
    /// question item and used to become one.
    /// </summary>
    private static IReadOnlyList<string> SeverityTokens(string text)
    {
        var tokens = new SortedSet<string>(StringComparer.Ordinal);
        var lead = StripEmphasis(text);
        if (QuestionTag().IsMatch(lead) || QuestionPrefix().IsMatch(lead.TrimStart()))
        {
            _ = tokens.Add("Question");
        }

        foreach (Match match in SeverityWord().Matches(text))
        {
            _ = tokens.Add(Canonical(match.Groups[1].Value));
        }

        return [.. tokens];
    }

    /// <summary>Whether this lead line opens a finding block at all: severity-bearing, and not a tally.</summary>
    private static bool StartsFinding(string leadLine) =>
        !IsNotAFinding(leadLine) && SeverityTokens(leadLine).Count > 0;

    private static string Canonical(string raw) =>
        raw.ToLowerInvariant() switch
        {
            "blocker" => "Blocker",
            "critical" => "Critical",
            "high" => "High",
            "medium" or "moderate" => "Medium",
            "low" => "Low",
            "nit" or "nitpick" => "Nit",
            _ => "Info",
        };

    private static IReadOnlyList<ReviewFindingCitation> Citations(string text)
    {
        var cited = new List<ReviewFindingCitation>();
        foreach (Match match in Citation().Matches(text))
        {
            if (!int.TryParse(match.Groups["start"].Value, CultureInfo.InvariantCulture, out var start))
            {
                continue;
            }

            var end = start;
            if (
                match.Groups["end"].Success
                && int.TryParse(match.Groups["end"].Value, CultureInfo.InvariantCulture, out var parsedEnd)
                && parsedEnd >= start
            )
            {
                end = parsedEnd;
            }

            var path = NormalizePath(match.Groups["path"].Value);
            if (path.Length > 0 && !cited.Any(c => c.Path == path && c.StartLine == start && c.EndLine == end))
            {
                cited.Add(new ReviewFindingCitation(path, start, end));
            }
        }

        return cited;
    }

    private static string NormalizePath(string raw)
    {
        var path = raw.Replace('\\', '/').Trim();
        while (path.StartsWith("./", StringComparison.Ordinal))
        {
            path = path[2..];
        }

        return path.TrimStart('/');
    }

    private static int SharedCitationCount(ParsedReviewFinding left, ParsedReviewFinding right)
    {
        var shared = 0;
        foreach (var a in left.Citations)
        {
            foreach (var b in right.Citations)
            {
                if (CitationsMatch(a, b))
                {
                    shared++;
                    break;
                }
            }
        }

        return shared;
    }

    /// <summary>
    /// Same place, allowing for the two ways two independent writers describe one: a different path prefix
    /// (repo-relative against submodule-relative) and a range against a point inside it.
    /// </summary>
    private static bool CitationsMatch(ReviewFindingCitation a, ReviewFindingCitation b) =>
        a.StartLine <= b.EndLine && b.StartLine <= a.EndLine && PathsMatch(a.Path, b.Path);

    private static bool PathsMatch(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase) || IsPathSuffix(a, b) || IsPathSuffix(b, a);

    private static bool IsPathSuffix(string longer, string shorter) =>
        longer.Length > shorter.Length
        && longer.EndsWith(shorter, StringComparison.OrdinalIgnoreCase)
        && longer[longer.Length - shorter.Length - 1] == '/';

    /// <summary>
    /// Whether one line states a disposition. Either a disposition verb paired with a finding-noun, or a
    /// severity-to-severity transition which needs no noun to be unambiguous.
    /// </summary>
    internal static bool IsDispositionStatement(string line) =>
        (DispositionVerb().IsMatch(line) && FindingNoun().IsMatch(line)) || SeverityTransition().IsMatch(line);

    /// <summary>
    /// The shipped review's own words about what it did with a finding, or null. Scanned within the matched
    /// finding's own block only — a disposition stated elsewhere in the review is real but is not evidence
    /// about THIS row, and is surfaced unattributed by <see cref="AppendUnattributedDispositions"/> instead.
    /// </summary>
    private static string? StatedDisposition(ParsedReviewFinding shipped)
    {
        foreach (var line in shipped.Body.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0 && IsDispositionStatement(trimmed))
            {
                return trimmed;
            }
        }

        return null;
    }

    private static string RenderLocation(ParsedReviewFinding finding)
    {
        if (finding.Citations.Count == 0)
        {
            return "(no file:line cited)";
        }

        var shown = finding.Citations.Take(3).Select(c => c.ToString());
        var text = string.Join(", ", shown);
        return finding.Citations.Count > 3
            ? text + $" (+{(finding.Citations.Count - 3).ToString(CultureInfo.InvariantCulture)} more)"
            : text;
    }

    /// <summary>
    /// A markdown table cell built from untrusted text. <see cref="UntrustedTranscriptText.Inline"/> handles
    /// escapes, control characters and backticks; the pipe is escaped here because only a table cares, and an
    /// unescaped one would let one reviewer's prose invent columns in a daemon-authored table.
    /// </summary>
    private static string Cell(string? raw, int maxChars) =>
        UntrustedTranscriptText.Inline(raw, maxChars).Replace("|", "\\|", StringComparison.Ordinal);

    [GeneratedRegex(@"^(?<hashes>\#{1,6})\s+(?<text>.*)$")]
    private static partial Regex HeadingLine();

    [GeneratedRegex(@"^ {0,3}(?:[-*+]|\d{1,3}[.)])\s+(?<text>\S.*)$")]
    private static partial Regex ListItemLine();

    /// <summary>
    /// Severity vocabulary. <c>informational</c> was removed on measurement: across 4,469 real lead lines it
    /// appeared 3 times and was label-shaped 0 of 3 — pure prose, exactly as the bare word <c>question</c> was
    /// before it. Every other token earns its place (<c>blocker</c> 250/270 label-shaped, <c>high</c> 318/373,
    /// <c>medium</c> 264/304, <c>low</c> 31/42). <c>critical</c> is the weakest at 11/23, but its prose half is
    /// severity roll-ups and grading narration, which <see cref="IsNotAFinding"/> now removes as a class
    /// rather than by deleting a token that carries real labels.
    /// </summary>
    [GeneratedRegex(@"\b(blocker|critical|high|medium|moderate|low|nitpick|nit|info)\b(?!-)", RegexOptions.IgnoreCase)]
    private static partial Regex SeverityWord();

    /// <summary>A bracketed question tag — the marker, as against the word.</summary>
    [GeneratedRegex(@"\[\s*(?:open\s+)?questions?\s*\]", RegexOptions.IgnoreCase)]
    private static partial Regex QuestionTag();

    /// <summary>
    /// The bold-colon question convention (<c>**Question:** …</c>), which reads as <c>Question:</c> once
    /// emphasis is stripped. A distinct convention from the bracketed tag, used in 2 of 810 corpus texts —
    /// and until it was recognised those items produced no row at all and nothing was logged. Rare, but
    /// silent, which is what made it worth having.
    /// </summary>
    [GeneratedRegex(@"^questions?\s*:", RegexOptions.IgnoreCase)]
    private static partial Regex QuestionPrefix();

    /// <summary>A heading whose WHOLE text names a questions section (<c>Context questions</c>).</summary>
    [GeneratedRegex(@"^(?:[\w-]+\s+){0,2}questions?(?:\s+for\b.*)?\s*:?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex QuestionSectionHeading();

    /// <summary>A count of findings (<c>3 HIGH/BLOCKER findings</c>), optionally followed by its own
    /// colon-delimited description — the anchored form let the described variant through.</summary>
    [GeneratedRegex(
        @"^\W*\d+\b[^.!?]{0,80}?\b(?:finding|issue|item|comment|blocker|problem|concern)s?\b"
            + @"[\s.;]*(?:[:—–-]\s*.*)?$",
        RegexOptions.IgnoreCase
    )]
    private static partial Regex FindingTally();

    /// <summary>A lead line that announces a summary block, or reports what was posted, rather than a
    /// finding (<c>Findings posted: 2 Medium</c>).</summary>
    [GeneratedRegex(
        @"^(?:summary|totals?|counts?|breakdown|overview|at a glance|tl;dr)\b"
            + @"|^(?:findings?|questions?|comments?)\s+posted\b",
        RegexOptions.IgnoreCase
    )]
    private static partial Regex TallyPrefix();

    /// <summary>
    /// A lead line that is nothing but a severity label or a bare count of one (<c>1 MEDIUM</c>,
    /// <c>[QUESTION]</c>) — a label with no finding attached to it. Distinguished from a real finding by
    /// having no text after the label.
    /// </summary>
    [GeneratedRegex(
        @"^\W*(?:\d+\s*)?\**\s*\[?\s*(?:blocker|critical|high|medium|low|nit|question)s?\s*\]?\s*\**[\s.:;—–-]*$",
        RegexOptions.IgnoreCase
    )]
    private static partial Regex ContentlessSeverityLine();

    /// <summary>One <c>&lt;count&gt; &lt;severity&gt;</c> pair; two or more on a line make it a roll-up.</summary>
    [GeneratedRegex(@"\b(?:zero|no|\d+)\s+\**\s*(?:blocker|critical|high|medium|low|nit)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SeverityRollup();

    /// <summary>A statement that there are NONE of some severity, which is never itself a finding.</summary>
    [GeneratedRegex(@"\b(?:zero|no)\s+\**\s*(?:blocker|critical|high|medium|low|nit)\b", RegexOptions.IgnoreCase)]
    private static partial Regex NoneOfSeverity();

    /// <summary>The synthesis narrating its own grading pass rather than reporting a finding.</summary>
    [GeneratedRegex(
        @"^(?:the\s+)?(?:review|severity)[-\s]?grad(?:er|ing)\b|\bgrad(?:er|ing)\s+confirmed\b",
        RegexOptions.IgnoreCase
    )]
    private static partial Regex GradingNarration();

    [GeneratedRegex(
        @"(?<path>[A-Za-z0-9_~][A-Za-z0-9_./\\+-]*\.[A-Za-z][A-Za-z0-9]{0,7})(?::|\#L)(?<start>\d{1,6})"
            + @"(?:\s*[-–—]\s*L?(?<end>\d{1,6}))?"
    )]
    private static partial Regex Citation();
}
