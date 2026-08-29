using System.Text.RegularExpressions;

namespace AchieveAi.LmDotnetTools.LmEval.Findings;

/// <summary>
/// One citation recovered from a review's prose, with the text that surrounds it.
/// </summary>
/// <param name="Path">The cited file path, exactly as written.</param>
/// <param name="Line">
/// The cited line number, or <b>null</b> when the citation named one this parser could not read —
/// an overflowing number, most plausibly a hallucinated one. Null is "cited but not resolvable",
/// which is a different review defect from not citing at all, and §4.3(2) measures anchor
/// resolution, so the two must not collapse into the same absence.
/// </param>
/// <param name="Severity">
/// The severity tag governing this citation, lowercased, when one is present. Null when the review
/// used no tag — never a default like "info", because a review that stated no severity has not
/// stated a low one.
/// <para>
/// Scoped to the citation, not to the line: a line carrying two tags gives each anchor the nearest
/// tag that <i>precedes</i> it. A line carrying one tag gives it to every anchor on the line,
/// whichever side of them it sits on, because a reviewer who stated one severity stated it about
/// the whole line.
/// </para>
/// </param>
/// <param name="Excerpt">The line the citation appeared on, trimmed.</param>
public sealed record ReviewFinding(string Path, int? Line, string? Severity, string Excerpt);

/// <summary>
/// Recovers <see cref="ReviewFinding"/>s from a review's Markdown prose.
/// <para>
/// <b>Deliberately shallow, and the depth is the point.</b> A review is prose; severity tags and
/// <c>file:line</c> citations exist only as text conventions, and there is no structured finding
/// type anywhere upstream to read instead. This parser answers "what did the review cite, and how
/// did it label it" — it does <b>not</b> answer "does that line exist", which needs a checkout it
/// has no access to, nor "are these two findings the same finding", which needs a similarity model.
/// </para>
/// <para>
/// It is therefore usable for comparing the citation surface of two reviews over the same input,
/// and it is not usable as a correctness check. Reading it as the latter is the mistake this
/// paragraph exists to prevent.
/// </para>
/// </summary>
public static partial class ReviewFindingParser
{
    private static readonly string[] KnownSeverities =
    [
        "blocker",
        "critical",
        "high",
        "major",
        "medium",
        "moderate",
        "minor",
        "low",
        "nit",
        "info",
        "suggestion",
    ];

    /// <summary>
    /// Parses every citation in the review text, in the order they appear.
    /// <para>
    /// Duplicates are preserved rather than collapsed: a review citing the same line three times is
    /// a different review from one citing it once, and deduplicating here would hide that from the
    /// comparison this parser feeds.
    /// </para>
    /// </summary>
    /// <param name="reviewText">The review's Markdown prose. Null or empty yields no findings.</param>
    public static IReadOnlyList<ReviewFinding> Parse(string? reviewText)
    {
        if (string.IsNullOrWhiteSpace(reviewText))
        {
            return [];
        }

        var findings = new List<ReviewFinding>();

        foreach (var rawLine in reviewText.Split('\n'))
        {
            var line = rawLine.Trim('\r', ' ', '\t');
            if (line.Length == 0)
            {
                continue;
            }

            var severities = SeveritiesOn(line);

            foreach (Match match in AnchorPattern().Matches(line))
            {
                // An unreadable number does not drop the citation. The anchor pattern already
                // decided this text is a citation; how well its line number resolves is the
                // measurement, not the admission criterion.
                var lineNumber = int.TryParse(
                    match.Groups["line"].Value,
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var parsed
                )
                    ? parsed
                    : (int?)null;

                findings.Add(
                    new ReviewFinding(
                        match.Groups["path"].Value,
                        lineNumber,
                        SeverityFor(severities, match.Index),
                        line
                    )
                );
            }
        }

        return findings;
    }

    /// <summary>
    /// Every severity tag on one line with the offset it sits at, in order. Matched against a
    /// closed vocabulary rather than "any bracketed word", so that an ordinary bracketed reference
    /// does not read as a severity and silently populate a field a caller then segments on.
    /// </summary>
    private static List<(int Offset, string Value)> SeveritiesOn(string line)
    {
        var found = new List<(int Offset, string Value)>();

        foreach (Match match in SeverityPattern().Matches(line))
        {
            var candidate = match.Groups["severity"].Value.ToLowerInvariant();
            if (Array.IndexOf(KnownSeverities, candidate) >= 0)
            {
                found.Add((match.Index, candidate));
            }
        }

        return found;
    }

    /// <summary>
    /// The tag governing the citation at <paramref name="anchorOffset"/>: the nearest one that
    /// precedes it, falling back to the line's first tag when none does.
    /// <para>
    /// The fallback is what keeps a trailing tag covering the whole line, which is the common
    /// single-tag shape. It is a fallback, NOT a pairing: on a line whose tags all trail its
    /// citations — <c>a.cs:1 b.cs:2 [must] [nit]</c> — every citation takes the first tag and the
    /// rest are dropped, because nothing in the text says which citation a trailing tag belongs to.
    /// That is the deliberate cost of not smearing the last tag forward: a confident wrong pairing
    /// reads as a measurement, while a coarse one is at least the severity the line leads with.
    /// </para>
    /// </summary>
    private static string? SeverityFor(List<(int Offset, string Value)> severities, int anchorOffset)
    {
        if (severities.Count == 0)
        {
            return null;
        }

        string? nearest = null;
        foreach (var (offset, value) in severities)
        {
            if (offset >= anchorOffset)
            {
                break;
            }

            nearest = value;
        }

        return nearest ?? severities[0].Value;
    }

    /// <summary>
    /// A <c>path/file.ext:line</c> citation — the same shape the required-anchor gate recognises,
    /// with the path and line captured. The directory separator is required so that ordinary prose
    /// containing a bare "word.cs:1" style token does not read as a resolvable path.
    /// </summary>
    [GeneratedRegex(
        @"(?<path>[\w.\-/\\]+[/\\][\w.\-]+\.\w+):(?<line>\d+)",
        RegexOptions.None,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex AnchorPattern();

    /// <summary>A bracketed or bold-prefixed severity tag.</summary>
    [GeneratedRegex(
        @"(?:\[|\*\*|^)\s*(?<severity>[A-Za-z]+)\s*(?:\]|\*\*|:)",
        RegexOptions.None,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex SeverityPattern();
}
