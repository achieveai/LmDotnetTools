using System.Text;

namespace LmStreaming.Sample.Services;

/// <summary>
/// Pure formatter for sandbox-discovered context files (CLAUDE.md / AGENTS.md). Owns the exact
/// shape of the <c>&lt;context-discovery&gt;</c> wrapper tag used both at session boot — appended
/// to the system prompt for the initial workspace seed — and mid-session — emitted as a user
/// turn by <see cref="ContextDiscoveryInjector"/> when the gateway delivers a new file.
/// </summary>
/// <remarks>
/// The wrapper tag is parsed by the model, so its shape is a real contract: changing it requires
/// updating the formatter unit tests in lockstep. Sharing one formatter across the two code
/// paths keeps boot-time and mid-session rendering byte-identical, so a context file injected
/// after the first turn looks the same to the model as one seeded at boot.
/// </remarks>
public sealed class ContextDiscoveryFormatter
{
    /// <summary>
    /// Builds the block appended to the system prompt at session boot. Returns an empty string
    /// when <paramref name="content"/> is null or empty so the caller can concatenate
    /// unconditionally.
    /// </summary>
    public string BuildSystemPromptBlock(string path, string? content, bool truncated)
    {
        if (string.IsNullOrEmpty(content))
        {
            return string.Empty;
        }

        return Render(path, content, truncated);
    }

    /// <summary>
    /// Builds the user-turn message body injected into a live conversation when the gateway
    /// discovers a new context file mid-session. Same wrapper tag as
    /// <see cref="BuildSystemPromptBlock"/> so the model's rendering of "what counts as a
    /// discovered file" stays consistent across boot and mid-session deliveries.
    /// </summary>
    public string BuildInjectedMessage(string path, string content, bool truncated)
    {
        ArgumentException.ThrowIfNullOrEmpty(content);
        return Render(path, content, truncated);
    }

    private static string Render(string path, string content, bool truncated)
    {
        var sb = new StringBuilder(content.Length + 128);
        _ = sb.Append("<context-discovery path=\"").Append(EscapeAttribute(path ?? string.Empty)).Append('"');
        if (truncated)
        {
            _ = sb.Append(" truncated=\"true\"");
        }
        _ = sb.Append(">\n").Append(content);
        if (!content.EndsWith('\n'))
        {
            _ = sb.Append('\n');
        }
        _ = sb.Append("</context-discovery>");
        return sb.ToString();
    }

    private static string EscapeAttribute(string value)
    {
        // Path values come from the gateway and may contain characters that need XML-safe
        // escaping inside a quoted attribute. Keep the rule minimal — only the characters that
        // would actually break parsing.
        return value
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);
    }
}
