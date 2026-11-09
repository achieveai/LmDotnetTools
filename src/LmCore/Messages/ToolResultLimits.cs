using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

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
///     <para>
///     A result that is a valid JSON document is never cut into a broken one (#713): it is replaced
///     by a small object naming the reason, the sizes and an escaped preview, which still parses and
///     still carries the marker in its <c>note</c> field.
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

    /// <summary>The <c>error</c> value of the JSON object that replaces a cut JSON document.</summary>
    public const string JsonTruncationError = "tool_result_truncated";

    private const string MarkerSeparator = "\n\n";

    // Non-ASCII and HTML characters stay readable: this text goes to a model, not into a web page.
    private static readonly JsonWriterOptions s_envelopeWriterOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

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

        bounded = truncated
            ? result with
            {
                Result = text!,
                ContentBlocks = blocks,
                IsTruncated = true,
                OriginalBytes = result.IsTruncated ? result.OriginalBytes : CountOriginalTextBytes(result),
            }
            : result;
        return truncated;
    }

    // Count only after truncation is needed, keeping ordinary results on the existing fast path.
    // Result and text blocks are separate fields even when their contents happen to match.
    internal static int CountOriginalTextBytes(ToolCallResult result)
    {
        long total = result.Result == null ? 0 : Encoding.UTF8.GetByteCount(result.Result);
        if (result.ContentBlocks != null)
        {
            foreach (var block in result.ContentBlocks)
            {
                if (block is TextToolResultBlock text)
                {
                    total += Encoding.UTF8.GetByteCount(text.Text);
                    if (total >= int.MaxValue)
                    {
                        return int.MaxValue;
                    }
                }
            }
        }

        return (int)total;
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

        bounded = Cut(text, MaxResultBytes, savedPath: null, notice: null);
        return true;
    }

    /// <summary>
    ///     Cuts <paramref name="text"/> to at most <paramref name="maxBytes"/> UTF-8 bytes, adding
    ///     <paramref name="notice"/> after the truncation marker. A valid JSON document becomes the
    ///     JSON object instead, with <paramref name="savedPath"/> in its <c>saved_to</c> field.
    /// </summary>
    internal static string Cut(string text, int maxBytes, string? savedPath, string? notice)
    {
        var total = Encoding.UTF8.GetByteCount(text);
        if (IsJsonDocument(text))
        {
            return BuildJsonEnvelope(text, total, maxBytes, savedPath, notice);
        }

        // Reserve room for the marker using the largest number it could carry (kept <= total).
        // MinResultBytes guarantees the budget stays positive.
        var suffix = notice == null ? string.Empty : "\n" + notice;
        var reserve = Encoding.UTF8.GetByteCount(FormatMarker(total, total)) + Encoding.UTF8.GetByteCount(suffix);
        var (keptChars, keptBytes) = MeasurePrefix(text, maxBytes - reserve);

        return string.Concat(text.AsSpan(0, keptChars), FormatMarker(keptBytes, total), suffix);
    }

    /// <summary>
    ///     Finds the longest prefix within <paramref name="budget"/> UTF-8 bytes, stopping on a
    ///     <see cref="Rune"/> boundary so a cut never splits a character.
    /// </summary>
    private static (int KeptChars, int KeptBytes) MeasurePrefix(string text, int budget)
    {
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

        return (keptChars, keptBytes);
    }

    /// <summary>
    ///     True when <paramref name="text"/> is one complete JSON document. Runs only on text that
    ///     is already over a limit, so ordinary results never pay for a parse.
    /// </summary>
    private static bool IsJsonDocument(string text)
    {
        var first = text.AsSpan().TrimStart(" \t\r\n");
        if (first.IsEmpty || first[0] is not ('{' or '[' or '"'))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            // ArgumentException: an unpaired surrogate cannot be transcoded, so this is not JSON.
            return false;
        }
    }

    /// <summary>
    ///     Builds the JSON object that replaces a cut JSON document, keeping as much preview as fits.
    /// </summary>
    /// <remarks>
    ///     The preview grows when escaped, so the first render can overshoot. Every byte removed from
    ///     the raw preview removes at least one escaped byte, so taking the overshoot off the budget
    ///     lands within the limit; the loop only guards the arithmetic.
    /// </remarks>
    private static string BuildJsonEnvelope(string text, int total, int maxBytes, string? savedPath, string? notice)
    {
        var overhead = RenderEnvelope(string.Empty, 0, total, savedPath, notice).Length;
        var budget = Math.Max(0, maxBytes - overhead);

        while (true)
        {
            var (keptChars, keptBytes) = MeasurePrefix(text, budget);

            // A lone surrogate has no JSON form, and the encoder would refuse it. Round-tripping
            // through UTF-8 gives U+FFFD, the same three bytes the measurement counted.
            var preview = Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(text[..keptChars]));
            var rendered = RenderEnvelope(preview, keptBytes, total, savedPath, notice);
            if (rendered.Length <= maxBytes || budget == 0)
            {
                return Encoding.UTF8.GetString(rendered);
            }

            budget = Math.Max(0, budget - (rendered.Length - maxBytes));
        }
    }

    private static byte[] RenderEnvelope(string preview, int keptBytes, int total, string? savedPath, string? notice)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, s_envelopeWriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("error", JsonTruncationError);
            writer.WriteNumber("kept_bytes", keptBytes);
            writer.WriteNumber("original_bytes", total);
            writer.WriteString("preview", preview);
            if (savedPath != null)
            {
                writer.WriteString("saved_to", savedPath);
            }

            var marker = FormatMarker(keptBytes, total)[MarkerSeparator.Length..];
            writer.WriteString("note", notice == null ? marker : marker + " " + notice);
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    private static string FormatMarker(int kept, int total) =>
        MarkerSeparator
        + TruncationMarkerPrefix
        + kept.ToString("N0", CultureInfo.InvariantCulture)
        + " of "
        + total.ToString("N0", CultureInfo.InvariantCulture)
        + " bytes]";
}
