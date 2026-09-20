using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CodeReviewDaemon.Sample.Orchestration;

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

/// <summary>Compatibility parser for historical markdown reviews. New workflows supply typed findings.</summary>
internal static partial class ReviewFindingReconciler
{
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
        int? openQuestionItemIndent = null;
        var openBody = new StringBuilder();

        foreach (var line in text.Split('\n'))
        {
            var heading = HeadingLine().Match(line);
            if (heading.Success)
            {
                Flush(findings, ref openTitle, ref openIsQuestion, openBody);
                openQuestionItemIndent = null;
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
                var indent = item.Groups["indent"].Value.Length;
                var itemText = item.Groups["text"].Value.Trim();
                var headLine = Head(itemText);

                // A bullet more indented than the question item currently open is that question's own
                // nested sub-bullet, not a sibling question — it stays folded into the open item's body
                // even though it independently matches ListItemLine (which only tracks 0-3 leading spaces
                // and, on its own, cannot tell "another top-level item" from "this item's own nested
                // detail"). `openQuestionItemIndent` is only set while the currently open item was itself
                // opened as a question, so this never touches ordinary (non-question) findings elsewhere.
                var isNestedUnderOpenQuestionItem = openQuestionItemIndent is int parentIndent && indent > parentIndent;

                if (!isNestedUnderOpenQuestionItem)
                {
                    // Under a Question heading (`AnyQuestion(headings)`), a top-level bullet with no severity
                    // word and no [QUESTION]/`Question:` marker of its own is still one question in that
                    // section's list — the heading is what marks it, not the bullet. `StartsFinding` alone never
                    // opens a block for it, so its citations were silently invisible to the parser: not a
                    // finding, not carried as anyone else's body, gone. `IsNotAFinding` still applies, so a tally
                    // or grading line under a Questions heading is excluded exactly as it would be anywhere else.
                    var opensQuestionItem = AnyQuestion(headings) && !IsNotAFinding(headLine);
                    if (StartsFinding(headLine) || opensQuestionItem)
                    {
                        Flush(findings, ref openTitle, ref openIsQuestion, openBody);
                        openTitle = itemText;
                        openIsQuestion = AnyQuestion(headings) || IsQuestionMarker(headLine);
                        openQuestionItemIndent = openIsQuestion ? indent : null;
                        _ = openBody.AppendLine(itemText);
                        continue;
                    }
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

    [GeneratedRegex(@"^(?<hashes>\#{1,6})\s+(?<text>.*)$")]
    private static partial Regex HeadingLine();

    [GeneratedRegex(@"^(?<indent> {0,3})(?:[-*+]|\d{1,3}[.)])\s+(?<text>\S.*)$")]
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
