using System.Text.RegularExpressions;

namespace AchieveAi.LmDotnetTools.LmEval.Findings;

/// <summary>
/// One citation recovered from a review's prose, with the text that surrounds it.
/// </summary>
/// <param name="Path">The cited file path, exactly as written.</param>
/// <param name="Line">The cited line number.</param>
/// <param name="Severity">
/// The severity tag on the same line, lowercased, when one is present. Null when the review used no
/// tag — never a default like "info", because a review that stated no severity has not stated a low
/// one.
/// </param>
/// <param name="Excerpt">The line the citation appeared on, trimmed.</param>
public sealed record ReviewFinding(string Path, int Line, string? Severity, string Excerpt);

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

            var severity = SeverityOf(line);

            foreach (Match match in AnchorPattern().Matches(line))
            {
                if (
                    int.TryParse(
                        match.Groups["line"].Value,
                        System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out var lineNumber
                    )
                )
                {
                    findings.Add(
                        new ReviewFinding(
                            match.Groups["path"].Value,
                            lineNumber,
                            severity,
                            line
                        )
                    );
                }
            }
        }

        return findings;
    }

    /// <summary>
    /// The severity tag on one line, or null. Matched against a closed vocabulary rather than "any
    /// bracketed word", so that an ordinary bracketed reference does not read as a severity and
    /// silently populate a field a caller then segments on.
    /// </summary>
    private static string? SeverityOf(string line)
    {
        foreach (Match match in SeverityPattern().Matches(line))
        {
            var candidate = match.Groups["severity"].Value.ToLowerInvariant();
            if (Array.IndexOf(KnownSeverities, candidate) >= 0)
            {
                return candidate;
            }
        }

        return null;
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
