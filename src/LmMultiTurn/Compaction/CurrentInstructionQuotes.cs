using System.Globalization;
using System.Text.RegularExpressions;
using AchieveAi.LmDotnetTools.LmCore.Messages;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.Compaction;

/// <summary>
///     <c>CurrentInstruction</c> as a manifest quotes it (spec 679 §2.3, §3.4): each row whole while the rows fit half of
///     the V9 envelope cap; past that, the largest rows first become a head and tail around a marker naming the seq that
///     <see cref="RecallConversationToolProvider" /> reads in full, until the rows fit. A pasted prompt larger than the
///     envelope would otherwise fail V9 on every checkpoint, the fallback included. Pure: the assembler writes this and
///     the validator recomputes it, so a trim nobody can reproduce fails V3.
/// </summary>
internal static partial class CurrentInstructionQuotes
{
    /// <summary>The share of the V9 envelope cap the current instruction may take before it is trimmed.</summary>
    public const double EnvelopeCapShare = 0.5;

    /// <summary>The least of a trimmed row that is kept, however tight the budget.</summary>
    internal const int MinKeptChars = 400;

    /// <summary>Share of the kept text taken from the start of the row; the rest comes from its end.</summary>
    private const double HeadShare = 0.7;

    /// <summary>The token budget for an envelope cap of <paramref name="checkpointTokenCap" />.</summary>
    public static long Budget(long checkpointTokenCap) => (long)(checkpointTokenCap * EnvelopeCapShare);

    /// <summary>The quotes for <paramref name="rows" />, in their order, within <paramref name="budgetTokens" /> when trimming can get there.</summary>
    public static IReadOnlyList<QuotedItem> Quote(
        IReadOnlyList<SequencedMessage> rows,
        long budgetTokens,
        Func<string?, long> estimate
    )
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(estimate);

        var quotes = rows.Select(r => new QuotedItem { Seq = r.Seq, Quote = r.Text ?? string.Empty }).ToArray();
        var total = quotes.Sum(q => estimate(q.Quote));
        var largestFirst = Enumerable
            .Range(0, quotes.Length)
            .OrderByDescending(i => quotes[i].Quote.Length)
            .ThenBy(i => quotes[i].Seq)
            .ToArray();
        foreach (var i in largestFirst)
        {
            if (total <= budgetTokens)
            {
                break;
            }

            var whole = quotes[i].Quote;
            var others = total - estimate(whole);
            if (Trim(whole, quotes[i].Seq, budgetTokens - others, estimate) is not { } trimmed)
            {
                continue;
            }

            quotes[i] = quotes[i] with { Quote = trimmed };
            total = others + estimate(trimmed);
        }

        return quotes;
    }

    /// <summary>The longest head/tail form of <paramref name="text" /> within <paramref name="allowance" />, or null when none is shorter.</summary>
    private static string? Trim(string text, long seq, long allowance, Func<string?, long> estimate)
    {
        if (text.Length <= MinKeptChars)
        {
            return null;
        }

        var (low, high) = (MinKeptChars, text.Length - 1);
        while (low < high)
        {
            var keep = (low + high + 1) / 2;
            if (estimate(Build(text, seq, keep)) <= allowance)
            {
                low = keep;
            }
            else
            {
                high = keep - 1;
            }
        }

        var trimmed = Build(text, seq, low);
        return trimmed.Length < text.Length ? trimmed : null;
    }

    /// <summary>
    ///     <paramref name="text" /> whole when it has at most <paramref name="keepChars" /> chars, else its head and tail
    ///     around a marker naming <paramref name="seq" /> (only the marker at zero). Never parses
    ///     <paramref name="text" />, so text that itself holds a marker is still counted exactly.
    /// </summary>
    public static string Bound(string text, long seq, int keepChars)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Length <= keepChars ? text : Build(text, seq, Math.Max(0, keepChars));
    }

    /// <summary>
    ///     <paramref name="quote" /> with at most <paramref name="keepChars" /> of its text visible: a quote that is
    ///     longer becomes the same head/tail form, and a quote already in that form loses from its head and tail with
    ///     the marker's count grown to match, so a chain that shrinks a quote twice still has one exact marker. A quote
    ///     verbatim in <paramref name="rowText" /> is never read as that form (see <see cref="VisibleChars" />).
    /// </summary>
    public static QuotedItem Shrink(QuotedItem quote, int keepChars, string? rowText = null)
    {
        ArgumentNullException.ThrowIfNull(quote);
        if (VisibleChars(quote, rowText) <= keepChars)
        {
            return quote;
        }

        if (!TryParseTrim(quote, rowText, out var head, out var omitted, out var tail))
        {
            return quote with { Quote = Build(quote.Quote, quote.Seq, keepChars) };
        }

        var headKept = Math.Min(head.Length, (int)(keepChars * HeadShare));
        var tailKept = Math.Min(tail.Length, keepChars - headKept);
        headKept = Math.Min(head.Length, keepChars - tailKept);
        var (headEnd, tailStart) = Edges(head, headKept, tail, tail.Length - tailKept);

        // What the head and tail lose joins the count: in the row they sit either side of the omitted span.
        var dropped = omitted + (head.Length - headEnd) + tailStart;
        return quote with
        {
            Quote = string.Concat(head.AsSpan(0, headEnd), Marker(dropped, quote.Seq), tail.AsSpan(tailStart)),
        };
    }

    /// <summary>
    ///     The characters of the row a quote shows: its head and tail when trimmed, else all of it. A quote verbatim in
    ///     <paramref name="rowText" /> is whole even when it holds a marker naming its seq: the row itself can.
    /// </summary>
    public static int VisibleChars(QuotedItem quote, string? rowText = null)
    {
        ArgumentNullException.ThrowIfNull(quote);
        return TryParseTrim(quote, rowText, out var head, out _, out var tail)
            ? head.Length + tail.Length
            : quote.Quote.Length;
    }

    /// <summary>The trimmed form of <paramref name="quote" />, unless it is verbatim in <paramref name="rowText" />.</summary>
    private static bool TryParseTrim(
        QuotedItem quote,
        string? rowText,
        out string head,
        out long omitted,
        out string tail
    ) =>
        TryParse(quote.Quote, quote.Seq, out head, out omitted, out tail)
        && (rowText is null || !rowText.Contains(quote.Quote, StringComparison.Ordinal));

    /// <summary>
    ///     V3's exact form of a trimmed standing quote: exactly one marker naming <paramref name="seq" /> and omitting at
    ///     least one char between a non-empty head and a non-empty tail, and a place in
    ///     <paramref name="text" /> where the head starts and the tail starts exactly the marker's count after it.
    /// </summary>
    public static bool IsTrimOf(string quote, long seq, string text)
    {
        ArgumentNullException.ThrowIfNull(quote);
        ArgumentNullException.ThrowIfNull(text);
        if (!TryParse(quote, seq, out var head, out var omitted, out var tail))
        {
            return false;
        }

        for (var at = text.IndexOf(head, StringComparison.Ordinal); at >= 0; at = NextIndex(text, head, at))
        {
            var tailStart = (long)at + head.Length + omitted;
            if (tailStart + tail.Length <= text.Length && text.AsSpan((int)tailStart).StartsWith(tail))
            {
                return true;
            }
        }

        return false;
    }

    private static int NextIndex(string text, string value, int after) =>
        after + 1 > text.Length ? -1 : text.IndexOf(value, after + 1, StringComparison.Ordinal);

    private static bool TryParse(string quote, long seq, out string head, out long omitted, out string tail)
    {
        (head, omitted, tail) = (string.Empty, 0, string.Empty);
        var matches = MarkerPattern().Matches(quote);
        if (
            matches.Count != 1
            || !long.TryParse(
                matches[0].Groups["seq"].ValueSpan,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var markerSeq
            )
            || markerSeq != seq
            || !long.TryParse(
                matches[0].Groups["omitted"].ValueSpan,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out omitted
            )
        )
        {
            return false;
        }

        head = quote[..matches[0].Index];
        tail = quote[(matches[0].Index + matches[0].Length)..];

        // A trim always keeps a head and a tail and omits something; any other shape is not the form this class writes.
        return omitted >= 1 && head.Length > 0 && tail.Length > 0;
    }

    private static string Build(string text, long seq, int keep)
    {
        var head = (int)(keep * HeadShare);
        var (headEnd, tailStart) = Edges(text, head, text, text.Length - (keep - head));
        return string.Concat(text.AsSpan(0, headEnd), Marker(tailStart - headEnd, seq), text.AsSpan(tailStart));
    }

    /// <summary>The head's end and the tail's start, moved so neither splits a surrogate pair.</summary>
    private static (int HeadEnd, int TailStart) Edges(string head, int headEnd, string tail, int tailStart) =>
        (
            headEnd > 0 && char.IsHighSurrogate(head[headEnd - 1]) ? headEnd - 1 : headEnd,
            tailStart < tail.Length && char.IsLowSurrogate(tail[tailStart]) ? tailStart + 1 : tailStart
        );

    private static string Marker(long omitted, long seq) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"\n[… {omitted} chars omitted; full text: {RecallConversationToolProvider.ToolName} seq {seq}]\n"
        );

    [GeneratedRegex(
        @"\n\[… (?<omitted>\d+) chars omitted; full text: "
            + RecallConversationToolProvider.ToolName
            + @" seq (?<seq>\d+)\]\n",
        RegexOptions.CultureInvariant
    )]
    private static partial Regex MarkerPattern();
}
