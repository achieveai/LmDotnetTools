using System.Text;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmLifecycle;
using AchieveAi.LmDotnetTools.LmLifecycle.Payloads;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.Lifecycle;

/// <summary>
/// One <c>&lt;context-discovery&gt;</c> block together with the provenance that describes it: which
/// discovery produced it, where it came from, whether the model saw all of it, and whether it
/// entered the conversation at boot or mid-session.
/// </summary>
/// <remarks>
/// <para>
/// <b>One grammar, two directions.</b> The wrapper tag is written when a context file is rendered
/// and read back when a provider request is inspected on its way out. Both live here so there is a
/// single definition of the tag: a change to how it is written cannot silently stop it being
/// recognized, and boot-time and mid-session rendering stay byte-identical by construction rather
/// than by two implementations agreeing.
/// </para>
/// <para>
/// <b>Why provenance is recovered from the request rather than carried alongside it.</b> A block
/// reaches the model by two very different routes — concatenated into the system prompt at boot, or
/// wrapped in a notification message mid-session — and the first of those is a bare string with
/// nowhere to hang metadata. Reading the provenance back out of what is actually being sent is the
/// only description that cannot drift from the request: it reports the context the model received,
/// not the context something intended to send.
/// </para>
/// <para>
/// <b>What is not recoverable.</b> The tag carries the path and the truncation flag and nothing
/// else, so a scanned block is attributed to <see cref="ContextFileKind"/> — the only discovery kind
/// that renders this tag. The dedup target (which agent the discovery was delivered to) is likewise
/// absent, and does not need to be: an event is attributed to the run that carried the block, and a
/// run belongs to exactly one agent.
/// </para>
/// </remarks>
public sealed record RenderedContextBlock
{
    /// <summary>
    /// The discovery kind that renders this wrapper: a repository instruction file such as
    /// <c>CLAUDE.md</c> or <c>AGENTS.md</c>, delivered by the sandbox gateway.
    /// </summary>
    public const string ContextFileKind = "context_file";

    private const string OpenTagName = "<context-discovery";
    private const string CloseTag = "</context-discovery>";
    private const string PathAttribute = "path=\"";
    private const string TruncatedAttribute = "truncated=\"true\"";

    /// <summary>What kind of discovery produced this block. See <see cref="ContextFileKind"/>.</summary>
    public required string DiscoveryKind { get; init; }

    /// <summary>A short display name — the final path segment for a file-backed source.</summary>
    public required string Name { get; init; }

    /// <summary>
    /// The source's location with separators normalized to forward slashes, so the same file
    /// produces the same value whichever host rendered it.
    /// </summary>
    /// <remarks>
    /// Not rewritten relative to a workspace root: the loop that reads a request back does not know
    /// where the workspace begins, and inventing a root would make two hosts disagree about the same
    /// file. Absent only when the block carried no path at all.
    /// </remarks>
    public string? NormalizedPath { get; init; }

    /// <summary>
    /// The value that decides two blocks describe the same source. Blocks sharing it are the same
    /// context arriving twice, and only the first is reported.
    /// </summary>
    public required string DedupIdentity { get; init; }

    /// <summary>The block exactly as it appears in the request, wrapper tag included.</summary>
    public required string Text { get; init; }

    /// <summary>The block's length in UTF-8 bytes, after any truncation the renderer applied.</summary>
    public required long RenderedByteCount { get; init; }

    /// <summary>
    /// Whether the source was cut short to fit. When <see langword="true"/> the model saw less than
    /// the file contains.
    /// </summary>
    public bool WasTruncated { get; init; }

    /// <summary>
    /// When this block entered the conversation. See <see cref="LifecycleContextPhases"/>.
    /// </summary>
    public required string Phase { get; init; }

    /// <summary>
    /// Renders a discovered context file into the wrapper tag, and describes what was rendered.
    /// </summary>
    /// <param name="path">Where the file came from, as the discovery reported it.</param>
    /// <param name="content">The file body, already truncated if it needed to be.</param>
    /// <param name="truncated">Whether <paramref name="content"/> is shorter than the file.</param>
    /// <param name="phase">
    /// Whether this is a boot seed or a mid-session delivery. See <see cref="LifecycleContextPhases"/>.
    /// </param>
    /// <param name="discoveryKind">What produced the discovery. Defaults to <see cref="ContextFileKind"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="content"/> is <see langword="null"/>.</exception>
    public static RenderedContextBlock Create(
        string path,
        string content,
        bool truncated,
        string phase,
        string discoveryKind = ContextFileKind)
    {
        ArgumentNullException.ThrowIfNull(content);

        var text = Render(path, content, truncated);
        return FromParts(path, text, truncated, phase, discoveryKind);
    }

    /// <summary>
    /// Recovers every context block a rendered string carries, in the order they appear.
    /// </summary>
    /// <param name="text">The rendered text to read — a system prompt, a message body, a prompt.</param>
    /// <param name="phase">
    /// How blocks found here entered the conversation. See <see cref="LifecycleContextPhases"/>.
    /// </param>
    /// <param name="discoveryKind">What produced them. Defaults to <see cref="ContextFileKind"/>.</param>
    /// <returns>The blocks found, or an empty list when there are none.</returns>
    /// <remarks>
    /// Deliberately forgiving: an unterminated or malformed wrapper ends the scan and yields
    /// whatever was already recognized, because a mangled tag is a reason to report less context
    /// rather than to fail a request that is otherwise fine to send.
    /// </remarks>
    public static IReadOnlyList<RenderedContextBlock> Scan(
        string? text,
        string phase,
        string discoveryKind = ContextFileKind)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains(OpenTagName, StringComparison.Ordinal))
        {
            return [];
        }

        List<RenderedContextBlock>? found = null;
        var cursor = 0;

        while (cursor < text.Length)
        {
            var open = text.IndexOf(OpenTagName, cursor, StringComparison.Ordinal);
            if (open < 0)
            {
                break;
            }

            var afterName = open + OpenTagName.Length;

            // "<context-discoveryX" is a different tag that merely starts the same way. Only a
            // separator or the end of the open tag makes this ours.
            if (afterName >= text.Length || (text[afterName] != ' ' && text[afterName] != '>'))
            {
                cursor = afterName;
                continue;
            }

            // Attribute values have their '>' escaped by the renderer, so the first one closes the
            // open tag.
            var openEnd = text.IndexOf('>', afterName);
            if (openEnd < 0)
            {
                break;
            }

            var close = text.IndexOf(CloseTag, openEnd + 1, StringComparison.Ordinal);
            if (close < 0)
            {
                break;
            }

            var end = close + CloseTag.Length;
            var attributes = text[afterName..openEnd];

            (found ??= []).Add(
                FromParts(
                    ReadPathAttribute(attributes),
                    text[open..end],
                    attributes.Contains(TruncatedAttribute, StringComparison.Ordinal),
                    phase,
                    discoveryKind));

            cursor = end;
        }

        return found ?? (IReadOnlyList<RenderedContextBlock>)[];
    }

    /// <summary>
    /// Recovers every context block a provider request carries, in request order.
    /// </summary>
    /// <param name="messages">The request as it will be dispatched.</param>
    /// <returns>The blocks found, or an empty list when there are none.</returns>
    /// <remarks>
    /// A block found in a system message is a boot seed and everything else is a mid-session
    /// delivery — which is what the two routes into a request actually are, so the phase is read off
    /// the request rather than remembered separately and hoped to still be true.
    /// </remarks>
    public static IReadOnlyList<RenderedContextBlock> ScanRequest(IEnumerable<IMessage>? messages)
    {
        if (messages == null)
        {
            return [];
        }

        List<RenderedContextBlock>? found = null;

        foreach (var message in messages)
        {
            if (message is not ICanGetText textual)
            {
                continue;
            }

            var phase = message.Role == Role.System
                ? LifecycleContextPhases.Boot
                : LifecycleContextPhases.MidSession;

            var blocks = Scan(textual.GetText(), phase);
            if (blocks.Count > 0)
            {
                (found ??= []).AddRange(blocks);
            }
        }

        return found ?? (IReadOnlyList<RenderedContextBlock>)[];
    }

    /// <summary>Projects this block onto the lifecycle wire shape.</summary>
    public LifecycleContextSource ToLifecycleSource() =>
        new()
        {
            DiscoveryKind = DiscoveryKind,
            Name = Name,
            NormalizedPath = NormalizedPath,
            DedupIdentity = DedupIdentity,
            RenderedByteCount = RenderedByteCount,
            WasTruncated = WasTruncated,
            Phase = Phase,
        };

    private static RenderedContextBlock FromParts(
        string? path,
        string text,
        bool truncated,
        string phase,
        string discoveryKind)
    {
        var normalizedPath = NormalizePath(path);
        return new RenderedContextBlock
        {
            DiscoveryKind = discoveryKind,
            Name = DisplayName(normalizedPath),
            NormalizedPath = normalizedPath,
            DedupIdentity = $"{discoveryKind}:{normalizedPath ?? string.Empty}",
            Text = text,
            RenderedByteCount = Encoding.UTF8.GetByteCount(text),
            WasTruncated = truncated,
            Phase = phase,
        };
    }

    private static string Render(string path, string content, bool truncated)
    {
        var sb = new StringBuilder(content.Length + 128);
        _ = sb.Append(OpenTagName).Append(" path=\"").Append(EscapeAttribute(path ?? string.Empty)).Append('"');
        if (truncated)
        {
            _ = sb.Append(' ').Append(TruncatedAttribute);
        }

        _ = sb.Append(">\n").Append(content);
        if (!content.EndsWith('\n'))
        {
            _ = sb.Append('\n');
        }

        _ = sb.Append(CloseTag);
        return sb.ToString();
    }

    private static string? ReadPathAttribute(string attributes)
    {
        var start = attributes.IndexOf(PathAttribute, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        start += PathAttribute.Length;
        var end = attributes.IndexOf('"', start);
        return end < 0 ? null : UnescapeAttribute(attributes[start..end]);
    }

    private static string EscapeAttribute(string value) =>
        // Path values come from the gateway and may contain characters that need XML-safe escaping
        // inside a quoted attribute. Keep the rule minimal — only the characters that would actually
        // break parsing. '&' goes first so the entities introduced below are not re-escaped.
        value
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);

    private static string UnescapeAttribute(string value) =>
        // The exact inverse: '&' goes last, or "&amp;quot;" — an escaped literal "&quot;" — would
        // come back as a quote character instead of the text the file's path actually contained.
        value
            .Replace("&quot;", "\"", StringComparison.Ordinal)
            .Replace("&lt;", "<", StringComparison.Ordinal)
            .Replace("&gt;", ">", StringComparison.Ordinal)
            .Replace("&amp;", "&", StringComparison.Ordinal);

    private static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        return path.Trim().Replace('\\', '/');
    }

    private static string DisplayName(string? normalizedPath)
    {
        if (string.IsNullOrEmpty(normalizedPath))
        {
            return string.Empty;
        }

        var lastSeparator = normalizedPath.LastIndexOf('/');
        return lastSeparator < 0 || lastSeparator == normalizedPath.Length - 1
            ? normalizedPath
            : normalizedPath[(lastSeparator + 1)..];
    }
}
