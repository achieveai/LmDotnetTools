using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace CodeReviewDaemon.Sample.Orchestration;

/// <summary>The three action kinds the SYNTHESIS turn may emit in a `review-actions` fence (spec §9).</summary>
internal enum ReviewActionKind
{
    Reply = 0,
    Finding,
    Summary,
}

/// <summary>
/// One successfully validated action. <see cref="Index"/> is its 0-based position in the raw YAML
/// list — preserved rather than renumbered, so a caller can tell "this action" apart from a sibling
/// that was rejected without needing to re-derive positions from two separate lists.
/// </summary>
internal sealed record ReviewAction(
    int Index,
    ReviewActionKind Kind,
    string Body,
    string? Ref = null,
    string? Path = null,
    int? Line = null
);

/// <summary>
/// One action that failed validation, scoped to itself: a bad `reply` or an unknown `kind` never
/// takes the rest of the block down with it. <see cref="Index"/> is <c>-1</c> only for the single
/// whole-block-failure sentinel <see cref="ReviewActionsParser"/> emits when the fence itself (not
/// one item inside it) could not be parsed.
/// </summary>
internal sealed record RejectedReviewAction(int Index, string? Kind, string? Ref, string Reason);

/// <summary>
/// The full outcome of one <see cref="ReviewActionsParser.Parse"/> call. <see cref="Markdown"/> is
/// always populated — the prose review must survive a structured-block failure just as reliably as
/// it survives a clean one.
/// </summary>
internal sealed record ReviewActionsParseResult(
    string Markdown,
    IReadOnlyList<ReviewAction> Actions,
    IReadOnlyList<RejectedReviewAction> Rejections,
    bool WholeBlockFailed
);

/// <summary>
/// Parses the single tolerant `review-actions` fenced YAML block a SYNTHESIS turn may append after
/// its Markdown review (spec §9). Deliberately standalone and unwired: nothing in the daemon calls
/// this yet (that is #652's job) — it exists so the parsing contract can be built and tested in
/// isolation, safe to ship as dead code exercised only by its own tests.
/// <para>
/// "Tolerant, but not everything is optional": unknown YAML keys are ignored and minor indentation
/// slips are tolerated by using a real YAML parser rather than a hand-rolled line parser, but a
/// missing required field on one action rejects only that action, and only a genuine structural
/// failure (the fence itself unparsable, or more than one fence) fails the whole block.
/// </para>
/// </summary>
internal static class ReviewActionsParser
{
    private const string OpenFenceMarker = "```review-actions";
    private const string CloseFenceMarker = "```";
    private const string WholeBlockFailureReason = "review-actions block could not be parsed";

    private static readonly IDeserializer YamlDeserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    /// <summary>
    /// Parses <paramref name="response"/>. Never throws: every failure mode (malformed YAML, an
    /// unterminated fence, more than one fence) is reported through
    /// <see cref="ReviewActionsParseResult.WholeBlockFailed"/> rather than an exception, because a
    /// caller receiving raw model output cannot be expected to wrap every call in a try/catch.
    /// </summary>
    public static ReviewActionsParseResult Parse(string response)
    {
        ArgumentNullException.ThrowIfNull(response);

        var lines = response.Split('\n');
        var lineStarts = ComputeLineStarts(lines);

        var openIndices = new List<int>();
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].TrimEnd() == OpenFenceMarker)
            {
                openIndices.Add(i);
            }
        }

        if (openIndices.Count == 0)
        {
            return new ReviewActionsParseResult(response.Trim(), [], [], false);
        }

        if (openIndices.Count > 1)
        {
            var spans = openIndices.Select(open => (Open: open, Close: FindClosingIndex(lines, open))).ToArray();
            var markdown = StripFencedSpans(response, lines, lineStarts, spans);
            return WholeBlockFailure(markdown);
        }

        var openIdx = openIndices[0];
        var closeIdx = FindClosingIndex(lines, openIdx);
        if (closeIdx < 0)
        {
            // Unterminated fence: no body can be extracted, so this is a structural failure rather
            // than an accident of whatever falls out of naive string splitting.
            var markdown = StripFencedSpans(response, lines, lineStarts, [(openIdx, lines.Length - 1)]);
            return WholeBlockFailure(markdown);
        }

        var body = string.Join('\n', lines[(openIdx + 1)..closeIdx]);
        var trimmedMarkdown = StripFencedSpans(response, lines, lineStarts, [(openIdx, closeIdx)]);

        List<RawReviewAction>? raw;
        try
        {
            raw = YamlDeserializer.Deserialize<List<RawReviewAction>>(body);
        }
        catch (YamlException)
        {
            return WholeBlockFailure(trimmedMarkdown);
        }

        if (raw is null || raw.Count == 0)
        {
            return new ReviewActionsParseResult(trimmedMarkdown, [], [], false);
        }

        var actions = new List<ReviewAction>();
        var rejections = new List<RejectedReviewAction>();
        for (var i = 0; i < raw.Count; i++)
        {
            Validate(i, raw[i], actions, rejections);
        }

        return new ReviewActionsParseResult(trimmedMarkdown, actions, rejections, false);
    }

    private static ReviewActionsParseResult WholeBlockFailure(string markdown) =>
        new(markdown, [], [new RejectedReviewAction(-1, null, null, WholeBlockFailureReason)], true);

    /// <summary>
    /// Validates one raw action and appends the result to either <paramref name="actions"/> or
    /// <paramref name="rejections"/>. Field-presence checks run in a fixed order per kind (ref before
    /// body for `reply`; path, then a positive line, then body for `finding`) purely so the outcome is
    /// deterministic when more than one field is missing at once — the spec pins only the single-field
    /// cases.
    /// </summary>
    private static void Validate(
        int index,
        RawReviewAction raw,
        List<ReviewAction> actions,
        List<RejectedReviewAction> rejections
    )
    {
        switch (raw.Kind)
        {
            case "reply":
                if (string.IsNullOrWhiteSpace(raw.Ref))
                {
                    Reject(index, raw, "reply requires ref", rejections);
                }
                else if (string.IsNullOrWhiteSpace(raw.Body))
                {
                    Reject(index, raw, "reply requires body", rejections);
                }
                else
                {
                    actions.Add(new ReviewAction(index, ReviewActionKind.Reply, raw.Body, Ref: raw.Ref));
                }

                break;

            case "finding":
                if (string.IsNullOrWhiteSpace(raw.Path))
                {
                    Reject(index, raw, "finding requires path", rejections);
                }
                else if (raw.Line is not > 0)
                {
                    Reject(index, raw, "finding requires a positive line", rejections);
                }
                else if (string.IsNullOrWhiteSpace(raw.Body))
                {
                    Reject(index, raw, "finding requires body", rejections);
                }
                else
                {
                    actions.Add(
                        new ReviewAction(index, ReviewActionKind.Finding, raw.Body, Path: raw.Path, Line: raw.Line)
                    );
                }

                break;

            case "summary":
                if (string.IsNullOrWhiteSpace(raw.Body))
                {
                    Reject(index, raw, "summary requires body", rejections);
                }
                else
                {
                    actions.Add(new ReviewAction(index, ReviewActionKind.Summary, raw.Body));
                }

                break;

            default:
                Reject(index, raw, $"unknown kind '{raw.Kind ?? "(missing)"}'", rejections);
                break;
        }
    }

    private static void Reject(int index, RawReviewAction raw, string reason, List<RejectedReviewAction> rejections) =>
        rejections.Add(new RejectedReviewAction(index, raw.Kind, raw.Ref, reason));

    /// <summary>Finds the next line at or after <paramref name="afterIndex"/>+1 that is a closing fence.</summary>
    private static int FindClosingIndex(string[] lines, int afterIndex)
    {
        for (var i = afterIndex + 1; i < lines.Length; i++)
        {
            if (lines[i].TrimEnd() == CloseFenceMarker)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Character offset each line begins at in the original (unsplit) response.</summary>
    private static int[] ComputeLineStarts(string[] lines)
    {
        var starts = new int[lines.Length];
        var offset = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            starts[i] = offset;
            offset += lines[i].Length + 1; // +1 for the '\n' the split consumed.
        }

        return starts;
    }

    /// <summary>
    /// Removes every (open, close) fenced span from <paramref name="response"/> by character offset —
    /// not by re-joining the split lines — so that Markdown text outside the fence(s) survives
    /// byte-for-byte; only the final trim normalizes the edges. A span whose close index is negative
    /// (no closing fence found) is treated as running to the end of the document.
    /// </summary>
    private static string StripFencedSpans(
        string response,
        string[] lines,
        int[] lineStarts,
        IReadOnlyList<(int Open, int Close)> spans
    )
    {
        var charSpans = spans
            .Select(s =>
            {
                var start = lineStarts[s.Open];
                var end = s.Close >= 0 && s.Close + 1 < lines.Length ? lineStarts[s.Close + 1] : response.Length;
                return (Start: start, End: end);
            })
            .OrderBy(s => s.Start)
            .ToArray();

        var builder = new System.Text.StringBuilder(response.Length);
        var cursor = 0;
        foreach (var (start, end) in charSpans)
        {
            if (start > cursor)
            {
                builder.Append(response, cursor, start - cursor);
            }

            cursor = Math.Max(cursor, end);
        }

        if (cursor < response.Length)
        {
            builder.Append(response, cursor, response.Length - cursor);
        }

        return builder.ToString().Trim();
    }

    /// <summary>
    /// Mutable YAML deserialization target. <see cref="Kind"/> is deliberately <c>string?</c>, not
    /// <see cref="ReviewActionKind"/>: mapping it straight onto the enum would make YamlDotNet throw
    /// for any unrecognized value while deserializing the whole list, turning "unknown kind rejects
    /// only that action" into an incorrect whole-block failure. The C# switch in
    /// <see cref="Validate"/> does that mapping instead, one item at a time.
    /// </summary>
    private sealed class RawReviewAction
    {
        public string? Kind { get; set; }

        public string? Ref { get; set; }

        public string? Body { get; set; }

        public string? Path { get; set; }

        public int? Line { get; set; }
    }
}
