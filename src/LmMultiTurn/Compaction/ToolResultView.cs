using System.Globalization;
using AchieveAi.LmDotnetTools.LmCore.Messages;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.Compaction;

/// <summary>How tool results are shown in the execution view; the defaults change nothing.</summary>
internal sealed record ToolResultViewOptions
{
    /// <summary>The longest result text shown whole; a longer one is shown as head + marker + tail.</summary>
    public int CapChars { get; init; } = int.MaxValue;

    /// <summary>Results with <c>Seq</c> at or below this are shown as a placeholder; 0 clears nothing.</summary>
    public long ClearedThroughSeq { get; init; }

    /// <summary>The tool the marker and placeholder point the model at.</summary>
    public string RecallToolName { get; init; } = RecallConversationToolProvider.ToolName;

    /// <summary>
    ///     Results with <c>Seq</c> at or below this (and above the clear watermark) are trimmed harder: to
    ///     <see cref="TightenedPartsPerMillion" /> of the length <see cref="CapChars" /> would show. 0 tightens nothing.
    /// </summary>
    public long TightenedThroughSeq { get; init; }

    /// <summary>The share of a tightened result's shown length kept, in millionths.</summary>
    public int TightenedPartsPerMillion { get; init; } = PartsPerMillion;

    /// <summary>The least a tightened result keeps, in characters (never more than <see cref="CapChars" />).</summary>
    public int TightenedFloorChars { get; init; }

    /// <summary>True when no transform can change a message.</summary>
    public bool IsIdentity => CapChars == int.MaxValue && ClearedThroughSeq <= 0 && TightenedThroughSeq <= 0;

    /// <summary>One whole, in <see cref="TightenedPartsPerMillion" /> units.</summary>
    public const int PartsPerMillion = 1_000_000;

    /// <summary>The character cap a result of <paramref name="length" /> chars at <paramref name="seq" /> is shown within.</summary>
    public int CapFor(long seq, int length)
    {
        if (seq > TightenedThroughSeq || seq <= ClearedThroughSeq)
        {
            return CapChars;
        }

        var shown = Math.Min(length, CapChars);
        var kept = (int)((long)shown * TightenedPartsPerMillion / PartsPerMillion);
        return Math.Min(CapChars, Math.Max(TightenedFloorChars, kept));
    }
}

/// <summary>
///     The two view-only transforms of a tool result (compaction phase 1): <b>clear</b> — a result at or below
///     the persisted clear watermark becomes a short placeholder naming its seq and size — and <b>trim</b> — a
///     result longer than the cap keeps its head and tail around a marker naming the elided size and the
///     recall call that reads it. A result the fit check had to shrink further is trimmed below the cap
///     (<see cref="ToolResultViewOptions.TightenedThroughSeq" />). All are pure functions of the row, its seq and the
///     options, so replaying
///     the store rebuilds the same bytes and the prompt cache stays warm between turns. The canonical row is
///     never edited; deferred placeholders and multi-modal results are never rewritten.
/// </summary>
internal static class ToolResultView
{
    /// <summary>Share of the text budget kept from the start of a trimmed result; the rest comes from its end.</summary>
    private const double HeadShare = 0.7;

    /// <summary>The message as the view shows it: the same instance when nothing applies.</summary>
    public static IMessage Apply(IMessage message, long seq, ToolResultViewOptions options)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(options);
        if (options.IsIdentity)
        {
            return message;
        }

        switch (message)
        {
            case ToolCallResultMessage single
                when Shape(
                    single.Result,
                    single.ToolCallId,
                    single.IsDeferred,
                    single.ContentBlocks is { Count: > 0 },
                    seq,
                    options
                )
                    is { } shaped:
                return single with { Result = shaped };
            case ToolsCallResultMessage many:
                var changed = false;
                var results = many
                    .ToolCallResults.Select(r =>
                    {
                        if (
                            Shape(r.Result, r.ToolCallId, r.IsDeferred, r.ContentBlocks is { Count: > 0 }, seq, options)
                            is not { } text
                        )
                        {
                            return r;
                        }

                        changed = true;
                        return r with { Result = text };
                    })
                    .ToList();
                return changed ? many with { ToolCallResults = [.. results] } : message;
            default:
                return message;
        }
    }

    /// <summary>
    ///     The clear watermark that keeps the <paramref name="keepTurns" /> most recent tool turns whole: the
    ///     seq just before the first row of the oldest kept turn, or 0 when there are no older turns. A tool
    ///     turn is the call and result rows of one generation; an unstamped row is its own turn.
    /// </summary>
    public static long ClearedThroughSeq(IReadOnlyList<SequencedMessage> rows, int keepTurns)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentOutOfRangeException.ThrowIfLessThan(keepTurns, 1);

        var turnStarts = new List<long>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (row.IsCheckpointRow || !IsToolRow(row.Message))
            {
                continue;
            }

            var key = row.Message.GenerationId ?? ("seq:" + row.Seq.ToString(CultureInfo.InvariantCulture));
            if (seen.Add(key))
            {
                turnStarts.Add(row.Seq);
            }
        }

        return turnStarts.Count <= keepTurns ? 0 : turnStarts[^keepTurns] - 1;
    }

    private static bool IsToolRow(IMessage message) =>
        message is ToolCallMessage or ToolCallResultMessage or ToolsCallResultMessage or ICanGetToolCalls;

    private static string? Shape(
        string? text,
        string? toolCallId,
        bool deferred,
        bool multiModal,
        long seq,
        ToolResultViewOptions options
    )
    {
        if (text is null || deferred || multiModal)
        {
            return null;
        }

        if (seq <= options.ClearedThroughSeq)
        {
            var placeholder = Placeholder(text.Length, toolCallId, seq, options.RecallToolName);
            return placeholder.Length < text.Length ? placeholder : null;
        }

        var cap = options.CapFor(seq, text.Length);
        return text.Length > cap ? Trim(text, toolCallId, cap, options.RecallToolName) : null;
    }

    private static string Placeholder(int chars, string? toolCallId, long seq, string recall)
    {
        var id = toolCallId is null ? string.Empty : ", tool_call_id " + toolCallId;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"[Tool result cleared from the context to save space: {chars} characters, seq {seq}{id}. Read it with {recall}(seq={seq}) if it is still needed.]"
        );
    }

    private static string Marker(int elided, int total, int offset, string? toolCallId, string recall)
    {
        var how = toolCallId is null
            ? "The full result is kept in the conversation."
            : string.Create(
                CultureInfo.InvariantCulture,
                $"Read them with {recall}(tool_call_id=\"{toolCallId}\", offset={offset})."
            );
        return string.Create(
            CultureInfo.InvariantCulture,
            $"\n\n[… {elided} of {total} characters elided from this tool result to fit the context window. {how}]\n\n"
        );
    }

    private static string Trim(string text, string? toolCallId, int capChars, string recall)
    {
        // Size the marker with the widest numbers it can carry, so the finished text never exceeds the cap.
        var reserve = Marker(text.Length, text.Length, text.Length, toolCallId, recall).Length;
        var budget = Math.Max(0, capChars - reserve);
        var head = (int)(budget * HeadShare);
        var tail = budget - head;

        // Never split a surrogate pair at either edge.
        if (head > 0 && char.IsHighSurrogate(text[head - 1]))
        {
            head--;
        }

        var tailStart = text.Length - tail;
        if (tail > 0 && char.IsLowSurrogate(text[tailStart]))
        {
            tailStart++;
        }

        return string.Concat(
            text.AsSpan(0, head),
            Marker(tailStart - head, text.Length, head, toolCallId, recall),
            text.AsSpan(tailStart)
        );
    }
}
