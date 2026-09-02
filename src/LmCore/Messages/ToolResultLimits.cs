using System.Globalization;
using System.Text;

namespace AchieveAi.LmDotnetTools.LmCore.Messages;

/// <summary>
///     Size bound applied to tool results at the point they are produced, before they enter
///     conversation history or a provider request.
/// </summary>
/// <remarks>
///     <para>
///     Providers cap the size of a single tool-result field (the OpenAI Responses API rejects a
///     <c>function_call_output.output</c> above 10,485,760 bytes with HTTP 400, which fails the
///     whole turn). Bounding at production time — rather than only at serialization — keeps the
///     oversized payload out of persisted history, where it would otherwise re-fail every later
///     turn that replays it.
///     </para>
///     <para>
///     Limits are measured in UTF-8 <b>bytes</b>, not <see cref="string.Length"/> chars. A
///     truncated result keeps a verbatim prefix and ends with an explicit marker that is both
///     model-readable and greppable:
///     <c>[tool result truncated: kept 4,194,304 of 15,231,668 bytes]</c>. The affected
///     <see cref="ToolCallResult"/> is also flagged via <see cref="ToolCallResult.IsTruncated"/>.
///     </para>
/// </remarks>
public sealed record ToolResultLimits
{
    /// <summary>
    ///     Leading text of the truncation marker. Stable so UIs, logs and tests can detect it.
    /// </summary>
    public const string TruncationMarkerPrefix = "[tool result truncated: kept ";

    /// <summary>
    ///     Smallest <see cref="MaxResultBytes"/> accepted. The bound must leave room for the
    ///     truncation marker itself — at most 68 bytes when both counts are <see cref="int.MaxValue"/>
    ///     (<c>"\n\n[tool result truncated: kept 2,147,483,647 of 2,147,483,647 bytes]"</c>) — plus a
    ///     non-empty verbatim prefix, so that every accepted cap yields output of at most
    ///     <see cref="MaxResultBytes"/> bytes. 256 keeps the marker under a third of the smallest result.
    /// </summary>
    public const int MinResultBytes = 256;

    private const string MarkerSeparator = "\n\n";

    private readonly int _maxResultBytes = 4 * 1024 * 1024;

    /// <summary>
    ///     Default bound: 4 MiB. Well under the smallest known provider field limit
    ///     (10,485,760 bytes) while still large enough that ordinary tool output never hits it.
    /// </summary>
    public static ToolResultLimits Default { get; } = new();

    /// <summary>
    ///     Disables bounding. Only appropriate when the caller enforces a limit of its own.
    /// </summary>
    public static ToolResultLimits Unbounded { get; } = new() { MaxResultBytes = int.MaxValue };

    /// <summary>
    ///     Maximum UTF-8 byte length of <see cref="ToolCallResult.Result"/> and of each
    ///     <see cref="TextToolResultBlock.Text"/>, including the truncation marker.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    ///     The value is below <see cref="MinResultBytes"/>. A cap that small could not hold the
    ///     marker, so the "at most <see cref="MaxResultBytes"/> bytes" guarantee would not hold.
    /// </exception>
    public int MaxResultBytes
    {
        get => _maxResultBytes;
        init
        {
            if (value < MinResultBytes)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(MaxResultBytes),
                    value,
                    $"MaxResultBytes must be at least {MinResultBytes} bytes so the truncation marker always fits."
                );
            }

            _maxResultBytes = value;
        }
    }

    /// <summary>
    ///     Bounds every text field of <paramref name="result"/>, flagging the returned struct when
    ///     anything was cut. Returns <paramref name="result"/> itself when nothing exceeds the limit.
    /// </summary>
    public ToolCallResult Apply(ToolCallResult result) => TryApply(result, out var bounded) ? bounded : result;

    /// <summary>
    ///     Bounds every text field of <paramref name="result"/>.
    /// </summary>
    /// <returns>
    ///     <c>true</c> when something was cut and <paramref name="bounded"/> holds the flagged copy;
    ///     <c>false</c> when nothing exceeds the limit (and <paramref name="bounded"/> is
    ///     <paramref name="result"/> unchanged, even if it was already flagged by an earlier pass).
    /// </returns>
    public bool TryApply(ToolCallResult result, out ToolCallResult bounded)
    {
        var truncated = false;

        var text = result.Result;
        if (text != null && TryBoundText(text, out var boundedText))
        {
            text = boundedText;
            truncated = true;
        }

        var blocks = result.ContentBlocks;
        if (blocks != null && blocks.Count > 0)
        {
            List<ToolResultContentBlock>? boundedBlocks = null;
            for (var i = 0; i < blocks.Count; i++)
            {
                if (blocks[i] is TextToolResultBlock textBlock && TryBoundText(textBlock.Text, out var boundedBlock))
                {
                    boundedBlocks ??= [.. blocks];
                    boundedBlocks[i] = textBlock with { Text = boundedBlock };
                    truncated = true;
                }
            }

            if (boundedBlocks != null)
            {
                blocks = boundedBlocks;
            }
        }

        bounded = truncated ? result with { Result = text!, ContentBlocks = blocks, IsTruncated = true } : result;
        return truncated;
    }

    /// <summary>
    ///     Returns <paramref name="text"/> unchanged when it fits, otherwise a bounded copy that
    ///     keeps a verbatim prefix and ends with the truncation marker.
    /// </summary>
    public string BoundText(string text) => TryBoundText(text, out var bounded) ? bounded : text;

    /// <summary>
    ///     Bounds <paramref name="text"/> to <see cref="MaxResultBytes"/> UTF-8 bytes.
    /// </summary>
    /// <remarks>
    ///     Walks the input by <see cref="Rune"/> and stops before the first one that would overrun
    ///     the budget, so the cut never lands inside a surrogate pair or a multi-byte sequence and
    ///     the input is never re-encoded into a byte array. A lone surrogate enumerates as
    ///     U+FFFD — three bytes, which is exactly what the UTF-8 encoder emits for it — so the byte
    ///     accounting matches what a provider will receive.
    /// </remarks>
    /// <returns><c>true</c> when the text was cut and <paramref name="bounded"/> holds the result.</returns>
    public bool TryBoundText(string text, out string bounded)
    {
        ArgumentNullException.ThrowIfNull(text);

        // Chars are a lower bound on UTF-8 bytes, so this cheap check skips the byte count for
        // the overwhelmingly common small result.
        if (text.Length <= MaxResultBytes / 4 || Encoding.UTF8.GetByteCount(text) <= MaxResultBytes)
        {
            bounded = text;
            return false;
        }

        var total = Encoding.UTF8.GetByteCount(text);

        // Reserve room for the marker using the largest number it could carry (kept <= total).
        // MinResultBytes guarantees the budget stays positive.
        var reserve = Encoding.UTF8.GetByteCount(FormatMarker(total, total));
        var budget = MaxResultBytes - reserve;

        var keptBytes = 0;
        var keptChars = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            var runeBytes = rune.Utf8SequenceLength;
            if (keptBytes + runeBytes > budget)
            {
                break;
            }

            keptBytes += runeBytes;
            keptChars += rune.Utf16SequenceLength;
        }

        bounded = string.Concat(text.AsSpan(0, keptChars), FormatMarker(keptBytes, total));
        return true;
    }

    private static string FormatMarker(int kept, int total) =>
        MarkerSeparator
        + TruncationMarkerPrefix
        + kept.ToString("N0", CultureInfo.InvariantCulture)
        + " of "
        + total.ToString("N0", CultureInfo.InvariantCulture)
        + " bytes]";
}
