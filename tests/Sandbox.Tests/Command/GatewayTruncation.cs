using System.Text;
using AchieveAi.LmDotnetTools.Sandbox.Command;

namespace AchieveAi.LmDotnetTools.Sandbox.Tests.Command;

/// <summary>
/// Faithfully models the pinned gateway's <c>exec</c> output truncation: it keeps only the HEAD of the
/// text, up to <see cref="CommandArtifactLayout.GatewayOutputByteLimit"/> bytes AND
/// <see cref="CommandArtifactLayout.GatewayOutputLineLimit"/> lines, discarding the rest — exactly what
/// a real gateway does before the SDK ever sees the response.
/// </summary>
/// <remarks>
/// Test doubles apply this so a manifest sentinel line that would overflow the limit is genuinely cut
/// (which breaks its base64 and fails the SDK's parse/verify) rather than being delivered whole by an
/// unrealistically "unlimited" fake. Both caps keep the head, so applying them in either order yields a
/// prefix bounded by the tighter of the two — the same observable result as the real gateway.
/// </remarks>
internal static class GatewayTruncation
{
    public static string Apply(string text) =>
        Apply(text, CommandArtifactLayout.GatewayOutputByteLimit, CommandArtifactLayout.GatewayOutputLineLimit);

    public static string Apply(string text, int byteLimit, int lineLimit)
    {
        var lineCapped = CapLines(text, lineLimit);
        return CapBytes(lineCapped, byteLimit);
    }

    /// <summary>Keeps at most <paramref name="lineLimit"/> leading lines (newline-delimited), preserving the original separators.</summary>
    private static string CapLines(string text, int lineLimit)
    {
        if (lineLimit <= 0)
        {
            return string.Empty;
        }

        var newlines = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n')
            {
                continue;
            }

            newlines++;
            if (newlines == lineLimit)
            {
                // Keep the first lineLimit lines including this terminating newline.
                return text[..(i + 1)];
            }
        }

        return text;
    }

    /// <summary>Keeps the largest leading prefix whose UTF-8 encoding is at most <paramref name="byteLimit"/> bytes, never splitting a character.</summary>
    private static string CapBytes(string text, int byteLimit)
    {
        if (byteLimit <= 0)
        {
            return string.Empty;
        }

        if (Encoding.UTF8.GetByteCount(text) <= byteLimit)
        {
            return text;
        }

        var bytes = 0;
        var end = 0;
        var enumerator = System.Globalization.StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext())
        {
            var element = (string)enumerator.Current;
            var elementBytes = Encoding.UTF8.GetByteCount(element);
            if (bytes + elementBytes > byteLimit)
            {
                break;
            }

            bytes += elementBytes;
            end += element.Length;
        }

        return text[..end];
    }
}
