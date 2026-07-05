using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace AchieveAi.LmDotnetTools.LmSampleShared.Discovery;

/// <summary>
/// Result of parsing a sub-agent markdown file (the Claude-Code convention of a YAML
/// frontmatter block fenced by <c>---</c> at the top of a markdown file, followed by the
/// system-prompt body). Only the typed fields the loader actually maps to
/// <see cref="AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents.SubAgentTemplate"/> are surfaced;
/// unknown frontmatter keys are ignored at parse time.
/// </summary>
public sealed record ParsedSubAgent(
    string Name,
    string? Description,
    string? Model,
    IReadOnlyList<string>? Tools,
    string SystemPrompt
);

/// <summary>
/// Pure parser for a single sub-agent markdown document. Failures (missing fences,
/// malformed YAML, empty body) return <c>null</c> so callers can log and skip the file
/// rather than aborting a whole batch.
/// </summary>
/// <remarks>
/// The expected document shape is the same one the workspace gateway uses when it walks
/// <c>.claude/agents/*.md</c>:
/// <code>
/// ---
/// name: echo-agent
/// description: Echoes a marker for E2E discovery tests.
/// model: claude-sonnet-4-5
/// tools: [Read, Glob]
/// ---
/// You are the echo sub-agent. ...
/// </code>
/// </remarks>
public static class SubAgentMarkdownParser
{
    private const string FenceMarker = "---";

    private static readonly IDeserializer YamlDeserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    /// <summary>
    /// Parses <paramref name="markdown"/> into a <see cref="ParsedSubAgent"/>. Returns
    /// <c>null</c> when the document is missing a frontmatter block, the YAML is malformed,
    /// or the body is empty.
    /// </summary>
    /// <param name="markdown">Raw markdown content (frontmatter + body).</param>
    /// <param name="filenameStem">
    /// Filename without extension. Used as the <c>name</c> fallback when the frontmatter
    /// omits it (mirrors Claude Code's behaviour). When also empty, parsing fails.
    /// </param>
    public static ParsedSubAgent? Parse(string markdown, string filenameStem)
    {
        if (string.IsNullOrEmpty(markdown))
        {
            return null;
        }

        if (!TrySplitFrontmatter(markdown, out var yaml, out var body))
        {
            return null;
        }

        FrontmatterDto? dto;
        try
        {
            dto = YamlDeserializer.Deserialize<FrontmatterDto>(yaml);
        }
        catch (YamlDotNet.Core.YamlException)
        {
            return null;
        }

        var trimmedBody = body.Trim();
        if (trimmedBody.Length == 0)
        {
            return null;
        }

        var name = !string.IsNullOrWhiteSpace(dto?.Name)
            ? dto.Name.Trim()
            : (string.IsNullOrWhiteSpace(filenameStem) ? null : filenameStem.Trim());

        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var description = string.IsNullOrWhiteSpace(dto?.Description) ? null : dto.Description.Trim();
        var model = string.IsNullOrWhiteSpace(dto?.Model) ? null : dto.Model.Trim();
        var tools = NormalizeTools(dto?.Tools);

        return new ParsedSubAgent(name, description, model, tools, trimmedBody);
    }

    private static bool TrySplitFrontmatter(string markdown, out string yaml, out string body)
    {
        yaml = string.Empty;
        body = string.Empty;

        // Tolerate UTF-8 BOM + leading whitespace before the opening fence.
        var span = markdown.AsSpan().TrimStart('\uFEFF').TrimStart();
        if (!StartsWithLine(span, FenceMarker, out var afterOpen))
        {
            return false;
        }

        var closeIndex = afterOpen.IndexOf("\n" + FenceMarker, StringComparison.Ordinal);
        if (closeIndex < 0)
        {
            return false;
        }

        // Require the closing fence to terminate at a line boundary so that "----foo" mid-body
        // doesn't accidentally close the block.
        var afterFenceAt = closeIndex + 1 + FenceMarker.Length;
        if (afterFenceAt < afterOpen.Length)
        {
            var trailing = afterOpen[afterFenceAt];
            if (trailing != '\r' && trailing != '\n')
            {
                return false;
            }
        }

        yaml = afterOpen[..closeIndex].ToString();
        var bodySpan = afterOpen[afterFenceAt..];
        body = bodySpan.ToString();
        return true;
    }

    private static bool StartsWithLine(ReadOnlySpan<char> input, string marker, out ReadOnlySpan<char> rest)
    {
        rest = input;
        if (input.Length < marker.Length || !input[..marker.Length].SequenceEqual(marker))
        {
            return false;
        }

        var after = input[marker.Length..];
        if (after.Length == 0)
        {
            return false;
        }

        if (after[0] == '\r' && after.Length > 1 && after[1] == '\n')
        {
            rest = after[2..];
            return true;
        }

        if (after[0] == '\n')
        {
            rest = after[1..];
            return true;
        }

        return false;
    }

    private static IReadOnlyList<string>? NormalizeTools(IList<string>? raw)
    {
        if (raw is null)
        {
            return null;
        }

        var normalized = new List<string>(raw.Count);
        foreach (var t in raw)
        {
            if (!string.IsNullOrWhiteSpace(t))
            {
                normalized.Add(t.Trim());
            }
        }

        return normalized;
    }

    /// <summary>
    /// Mutable DTO used only as the YAML deserialisation target. Public so YamlDotNet's
    /// reflection can populate it; never exposed beyond <see cref="Parse"/>.
    /// </summary>
    public sealed class FrontmatterDto
    {
        public string? Name { get; set; }

        public string? Description { get; set; }

        public string? Model { get; set; }

        public List<string>? Tools { get; set; }
    }
}
