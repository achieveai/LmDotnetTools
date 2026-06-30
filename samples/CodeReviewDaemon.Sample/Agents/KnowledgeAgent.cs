using System.Text;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using CodeReviewDaemon.Sample.Workspace.Sandbox;

namespace CodeReviewDaemon.Sample.Agents;

/// <summary>
/// Distills a review into a durable Knowledge Base entry (plan §13). It drives one collect-only run
/// over an <see cref="IMultiTurnAgent"/> to produce the entry's Markdown, writes it to
/// <c>KnowledgeBase/{slug}.md</c> in the ReviewBot checkout, then <b>regenerates</b> <c>_toc.md</c>
/// from the entries actually present so the table of contents always reflects the directory. All file
/// IO goes through <see cref="ISandboxFileSystem"/>; committing/pushing the checkout is the repo
/// manager's job, so this agent stays verifiable against an in-memory fake.
/// </summary>
internal sealed class KnowledgeAgent
{
    private const string KnowledgeBaseDirectory = "KnowledgeBase";
    private const string TocFileName = "_toc.md";

    private readonly IMultiTurnAgent _agent;
    private readonly ISandboxFileSystem _fileSystem;
    private readonly ILogger<KnowledgeAgent> _logger;

    public KnowledgeAgent(IMultiTurnAgent agent, ISandboxFileSystem fileSystem, ILogger<KnowledgeAgent> logger)
    {
        _agent = agent ?? throw new ArgumentNullException(nameof(agent));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Generates a Knowledge Base entry titled <paramref name="title"/> from
    /// <paramref name="knowledgeInput"/>, writes it under <paramref name="repoRoot"/>, and rebuilds the
    /// table of contents. Returns the entry's file name and the agent run id that produced it.
    /// </summary>
    public async Task<KnowledgeWriteResult> WriteEntryAsync(
        string repoRoot,
        string title,
        string knowledgeInput,
        CancellationToken cancellationToken
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        var collected = await AgentTextCollector
            .CollectAsync(_agent, knowledgeInput, cancellationToken)
            .ConfigureAwait(false);

        var entryFileName = Slugify(title) + ".md";
        var knowledgeBaseDir = JoinPath(repoRoot, KnowledgeBaseDirectory);

        // Prefix the title heading when the model didn't open with one, so every entry is well-formed
        // and the regenerated ToC can derive a link label from the entry's first heading.
        var entryBody = collected.Text.TrimStart().StartsWith('#')
            ? collected.Text
            : $"# {title}\n\n{collected.Text}";

        await _fileSystem
            .WriteFileAsync(JoinPath(knowledgeBaseDir, entryFileName), entryBody, cancellationToken)
            .ConfigureAwait(false);

        await RegenerateTocAsync(knowledgeBaseDir, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Knowledge run {RunId} wrote entry '{Entry}' and regenerated the table of contents.",
            collected.RunId,
            entryFileName
        );

        return new KnowledgeWriteResult(entryFileName, collected.RunId);
    }

    private async Task RegenerateTocAsync(string knowledgeBaseDir, CancellationToken cancellationToken)
    {
        var names = await _fileSystem.ListFilesAsync(knowledgeBaseDir, cancellationToken).ConfigureAwait(false);

        var entries = new List<KnowledgeEntry>();
        foreach (var name in names)
        {
            // The ToC lists Markdown entries only; the ToC itself and non-entry markers are excluded.
            if (!name.EndsWith(".md", StringComparison.Ordinal) || string.Equals(name, TocFileName, StringComparison.Ordinal))
            {
                continue;
            }

            var content = await _fileSystem
                .ReadFileAsync(JoinPath(knowledgeBaseDir, name), cancellationToken)
                .ConfigureAwait(false);
            entries.Add(new KnowledgeEntry(name, FirstHeading(content) ?? name));
        }

        var toc = KnowledgeTableOfContents.Render(entries);
        await _fileSystem
            .WriteFileAsync(JoinPath(knowledgeBaseDir, TocFileName), toc, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Returns the text of the first top-level <c>#</c> heading, or <c>null</c> if there is none.</summary>
    private static string? FirstHeading(string? content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return null;
        }

        foreach (var line in content.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("# ", StringComparison.Ordinal))
            {
                return trimmed[2..].Trim();
            }
        }

        return null;
    }

    /// <summary>
    /// Lowercases <paramref name="title"/> and collapses every run of non-alphanumeric characters to a
    /// single hyphen, trimming leading/trailing hyphens — a deterministic, filesystem-safe file stem.
    /// </summary>
    private static string Slugify(string title)
    {
        var builder = new StringBuilder(title.Length);
        var pendingHyphen = false;
        foreach (var ch in title)
        {
            if (char.IsLetterOrDigit(ch))
            {
                if (pendingHyphen && builder.Length > 0)
                {
                    _ = builder.Append('-');
                }

                _ = builder.Append(char.ToLowerInvariant(ch));
                pendingHyphen = false;
            }
            else
            {
                pendingHyphen = true;
            }
        }

        var slug = builder.ToString();
        return slug.Length == 0 ? "entry" : slug;
    }

    private static string JoinPath(string root, string relative) =>
        $"{root.TrimEnd('/')}/{relative.TrimStart('/')}";
}

/// <summary>The Knowledge Base entry that was written and the agent run id that produced it.</summary>
internal sealed record KnowledgeWriteResult(string EntryFileName, string? RunId);
