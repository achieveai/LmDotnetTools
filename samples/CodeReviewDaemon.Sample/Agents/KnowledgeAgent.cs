using System.Text;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using CodeReviewDaemon.Sample.Workspace.Sandbox;

namespace CodeReviewDaemon.Sample.Agents;

/// <summary>
/// Distills review knowledge into the durable Knowledge Base. Two entry points share the collect-only
/// agent drive and the <see cref="ISandboxFileSystem"/> IO: the legacy per-review
/// <see cref="KnowledgeAgent.WriteEntryAsync"/> (flat <c>KnowledgeBase/{slug}.md</c> + <c>_toc.md</c>),
/// and the at-close <see cref="KnowledgeAgent.TryExtractAsync"/> (design §1/§2) which gates on durable,
/// generalizable knowledge, writes a <b>layered</b> <c>KnowledgeBase/&lt;scope&gt;/&lt;slug&gt;.md</c> entry
/// with daemon-injected frontmatter (create-or-update), then regenerates both the queryable
/// <c>_index.jsonl</c> and <c>_toc.md</c> from the entries actually present. Committing/pushing the
/// checkout is the repo manager's job, so this agent stays verifiable against an in-memory fake.
/// </summary>
internal sealed class KnowledgeAgent
{
    private const string KnowledgeBaseDirectory = "KnowledgeBase";
    private const string TocFileName = "_toc.md";
    private const string IndexFileName = "_index.jsonl";

    /// <summary>The gate sentinel: the extraction agent replies with this when nothing durable is worth writing.</summary>
    private const string NoKnowledgeSentinel = "NO_KNOWLEDGE";

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

    /// <summary>
    /// The at-close extraction pass (design §1/§2). Drives one collect-only run over the extraction agent,
    /// giving it the PR's accumulated <paramref name="notesInput"/> plus the existing index/ToC so it can
    /// update a related entry rather than duplicate it. The agent applies the <b>durable-knowledge gate</b>:
    /// when it replies with the <see cref="NoKnowledgeSentinel"/> (the common case — "not every PR
    /// contributes"), this returns <c>null</c> and writes nothing. Otherwise it parses the agent's header
    /// markers (<c>## SCOPE/TITLE/TAGS/UPDATES</c>), resolves create-vs-update against
    /// <paramref name="repoRoot"/>, injects daemon-owned YAML frontmatter deterministically
    /// (<c>updated</c> = <paramref name="todayUtc"/>, <c>sourcePrs</c> merges the existing set with
    /// <paramref name="sourcePrRef"/>), writes the layered entry, then regenerates <c>_index.jsonl</c> and
    /// <c>_toc.md</c> from the entries actually present.
    /// </summary>
    public async Task<KnowledgeWriteResult?> TryExtractAsync(
        string repoRoot,
        string notesInput,
        string sourcePrRef,
        string todayUtc,
        CancellationToken cancellationToken
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePrRef);
        ArgumentException.ThrowIfNullOrWhiteSpace(todayUtc);

        var knowledgeBaseDir = JoinPath(repoRoot, KnowledgeBaseDirectory);

        var extractionInput = await BuildExtractionInputAsync(knowledgeBaseDir, notesInput, cancellationToken)
            .ConfigureAwait(false);
        var collected = await AgentTextCollector
            .CollectAsync(_agent, extractionInput, cancellationToken)
            .ConfigureAwait(false);

        // Gate: an empty or NO_KNOWLEDGE reply means nothing durable — write nothing, leave the KB as-is.
        var text = collected.Text?.TrimStart() ?? string.Empty;
        if (text.Length == 0 || text.StartsWith(NoKnowledgeSentinel, StringComparison.Ordinal))
        {
            _logger.LogInformation(
                "Knowledge run {RunId} produced no durable knowledge (gate); Knowledge Base left unchanged.",
                collected.RunId
            );
            return null;
        }

        var parsed = ParseEntry(text);

        // Resolve the target entry: an ## UPDATES marker pointing at an existing file wins; otherwise the
        // scope+slug path (which is a create, or an in-place update when that file already exists).
        var targetRelPath = await ResolveTargetRelPathAsync(knowledgeBaseDir, parsed, cancellationToken)
            .ConfigureAwait(false);
        if (targetRelPath is null)
        {
            _logger.LogWarning(
                "Knowledge run {RunId} emitted no usable SCOPE/TITLE markers; nothing written.",
                collected.RunId
            );
            return null;
        }

        var targetPath = JoinPath(knowledgeBaseDir, targetRelPath);
        var existing = await _fileSystem.ReadFileAsync(targetPath, cancellationToken).ConfigureAwait(false);
        var existingMeta = existing is null ? null : KnowledgeIndex.ParseFrontmatter(targetRelPath, existing);

        var scope = ScopeSegment(targetRelPath) ?? parsed.Scope;
        var title = !string.IsNullOrWhiteSpace(parsed.Title)
            ? parsed.Title
            : existingMeta?.Title ?? SlugFromRelPath(targetRelPath);
        IReadOnlyList<string> tags = parsed.Tags.Count > 0 ? parsed.Tags : existingMeta?.Tags ?? [];
        var sourcePrs = MergeSourcePrs(existingMeta?.SourcePrs, sourcePrRef);

        var entryMarkdown = BuildEntry(title, tags, scope, sourcePrs, todayUtc, parsed.Body);
        await _fileSystem.WriteFileAsync(targetPath, entryMarkdown, cancellationToken).ConfigureAwait(false);

        await RegenerateIndexAndTocAsync(knowledgeBaseDir, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Knowledge run {RunId} wrote entry '{Entry}' and regenerated the index + table of contents.",
            collected.RunId,
            targetRelPath
        );

        return new KnowledgeWriteResult(targetRelPath, collected.RunId);
    }

    /// <summary>
    /// Assembles the extraction agent's input: the PR notes followed by the existing <c>_index.jsonl</c>
    /// and <c>_toc.md</c> (best-effort; missing files render as <c>(empty)</c>) so the agent can update a
    /// related entry instead of duplicating one.
    /// </summary>
    private async Task<string> BuildExtractionInputAsync(
        string knowledgeBaseDir,
        string notesInput,
        CancellationToken cancellationToken
    )
    {
        var index = await _fileSystem
            .ReadFileAsync(JoinPath(knowledgeBaseDir, IndexFileName), cancellationToken)
            .ConfigureAwait(false);
        var toc = await _fileSystem
            .ReadFileAsync(JoinPath(knowledgeBaseDir, TocFileName), cancellationToken)
            .ConfigureAwait(false);

        var builder = new StringBuilder();
        _ = builder.Append(notesInput ?? string.Empty);
        _ = builder.Append("\n\n## Existing Knowledge Base index (_index.jsonl)\n");
        _ = builder.Append(string.IsNullOrWhiteSpace(index) ? "(empty)" : index);
        _ = builder.Append("\n\n## Existing Knowledge Base table of contents (_toc.md)\n");
        _ = builder.Append(string.IsNullOrWhiteSpace(toc) ? "(empty)" : toc);
        return builder.ToString();
    }

    /// <summary>
    /// Resolves the KB-relative path the entry should be written to. An <c>## UPDATES</c> marker that names
    /// an existing entry is honored (update in place); otherwise the path is <c>&lt;scope&gt;/&lt;slug&gt;.md</c>
    /// derived from the SCOPE + slugified TITLE — which is a create, or an in-place update when that file
    /// already exists. Returns <c>null</c> when the markers are too sparse to form a path (no UPDATES target
    /// and a missing scope/title).
    /// </summary>
    private async Task<string?> ResolveTargetRelPathAsync(
        string knowledgeBaseDir,
        ParsedEntry parsed,
        CancellationToken cancellationToken
    )
    {
        if (!string.IsNullOrWhiteSpace(parsed.Updates))
        {
            var updatesRel = parsed.Updates.Trim().TrimStart('/');
            var updatesContent = await _fileSystem
                .ReadFileAsync(JoinPath(knowledgeBaseDir, updatesRel), cancellationToken)
                .ConfigureAwait(false);
            if (updatesContent is not null)
            {
                return updatesRel;
            }
        }

        if (string.IsNullOrWhiteSpace(parsed.Scope) || string.IsNullOrWhiteSpace(parsed.Title))
        {
            return null;
        }

        return $"{parsed.Scope.Trim().Trim('/')}/{Slugify(parsed.Title)}.md";
    }

    /// <summary>Merges <paramref name="existing"/> source-PR refs with <paramref name="sourcePrRef"/>, preserving order and de-duplicating.</summary>
    private static IReadOnlyList<string> MergeSourcePrs(IReadOnlyList<string>? existing, string sourcePrRef)
    {
        List<string> merged = existing is null ? [] : [.. existing];
        if (!merged.Contains(sourcePrRef, StringComparer.Ordinal))
        {
            merged.Add(sourcePrRef);
        }

        return merged;
    }

    /// <summary>
    /// Renders the entry: a leading <c>---</c>…<c>---</c> YAML frontmatter block the daemon owns, then the
    /// model's body. The lists are emitted <b>flow-style</b> (<c>tags: [a, b]</c>, <c>sourcePrs: ["x"]</c>)
    /// because that is the only shape <see cref="KnowledgeIndex.ParseFrontmatter"/> reads back — so the
    /// entry round-trips through the index parser.
    /// </summary>
    private static string BuildEntry(
        string title,
        IReadOnlyList<string> tags,
        string scope,
        IReadOnlyList<string> sourcePrs,
        string updated,
        string body
    )
    {
        var builder = new StringBuilder();
        _ = builder.Append("---\n");
        _ = builder.Append("title: ").Append(title).Append('\n');
        _ = builder.Append("tags: [").Append(string.Join(", ", tags)).Append("]\n");
        _ = builder.Append("scope: ").Append(scope).Append('\n');
        _ = builder.Append("sourcePrs: [").Append(string.Join(", ", sourcePrs.Select(Quote))).Append("]\n");
        _ = builder.Append("updated: ").Append(updated).Append('\n');
        _ = builder.Append("---\n");

        var trimmedBody = body.Trim();
        if (trimmedBody.Length > 0)
        {
            _ = builder.Append('\n').Append(trimmedBody).Append('\n');
        }

        return builder.ToString();
    }

    private static string Quote(string value) => $"\"{value}\"";

    /// <summary>
    /// Parses the agent's header markers (<c>## SCOPE/TITLE/TAGS/UPDATES</c>, each on its own line at the
    /// top, blank lines allowed) and the entry body that follows the last marker. Unknown or absent markers
    /// leave their fields empty; the first substantive non-marker line begins the body.
    /// </summary>
    private static ParsedEntry ParseEntry(string text)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');

        string scope = string.Empty, title = string.Empty;
        string? updates = null;
        List<string> tags = [];
        var bodyStart = lines.Length;

        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.Length == 0)
            {
                continue; // Blank separator lines between markers (and before the body) are skipped.
            }

            var marker = TryParseMarker(trimmed);
            if (marker is null)
            {
                bodyStart = i; // First non-marker content line — the body begins here.
                break;
            }

            var (key, value) = marker.Value;
            switch (key)
            {
                case "SCOPE":
                    scope = value;
                    break;
                case "TITLE":
                    title = value;
                    break;
                case "TAGS":
                    tags = SplitTags(value);
                    break;
                case "UPDATES":
                    updates = value;
                    break;
                default:
                    break;
            }
        }

        var body = bodyStart < lines.Length ? string.Join("\n", lines[bodyStart..]).Trim() : string.Empty;
        return new ParsedEntry(scope, title, tags, updates, body);
    }

    /// <summary>Recognizes a <c>## KEY: value</c> header marker for the known keys, or returns <c>null</c>.</summary>
    private static (string Key, string Value)? TryParseMarker(string line)
    {
        if (!line.StartsWith("## ", StringComparison.Ordinal))
        {
            return null;
        }

        var rest = line[3..];
        var colon = rest.IndexOf(':', StringComparison.Ordinal);
        if (colon < 0)
        {
            return null;
        }

        var key = rest[..colon].Trim().ToUpperInvariant();
        if (key is not ("SCOPE" or "TITLE" or "TAGS" or "UPDATES"))
        {
            return null;
        }

        return (key, rest[(colon + 1)..].Trim());
    }

    private static List<string> SplitTags(string value)
    {
        List<string> tags = [];
        foreach (var part in value.Split(','))
        {
            var tag = part.Trim();
            if (tag.Length > 0)
            {
                tags.Add(tag);
            }
        }

        return tags;
    }

    /// <summary>
    /// Regenerates <c>_index.jsonl</c> and <c>_toc.md</c> from the layered entries actually present, so
    /// neither ever drifts from the directory. Walks each scope directory under
    /// <paramref name="knowledgeBaseDir"/>, parses each entry's frontmatter, and skips (with a log) any
    /// entry that has none — malformed frontmatter never aborts the regen (design §6).
    /// </summary>
    private async Task RegenerateIndexAndTocAsync(string knowledgeBaseDir, CancellationToken cancellationToken)
    {
        var metas = await CollectEntryMetasAsync(knowledgeBaseDir, cancellationToken).ConfigureAwait(false);

        var index = KnowledgeIndex.RenderIndex(metas);
        await _fileSystem
            .WriteFileAsync(JoinPath(knowledgeBaseDir, IndexFileName), index, cancellationToken)
            .ConfigureAwait(false);

        var tocEntries = metas.Select(meta => new KnowledgeEntry(meta.File, meta.Title)).ToList();
        var toc = KnowledgeTableOfContents.Render(tocEntries);
        await _fileSystem
            .WriteFileAsync(JoinPath(knowledgeBaseDir, TocFileName), toc, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<KnowledgeEntryMeta>> CollectEntryMetasAsync(
        string knowledgeBaseDir,
        CancellationToken cancellationToken
    )
    {
        var metas = new List<KnowledgeEntryMeta>();
        var children = await _fileSystem.ListFilesAsync(knowledgeBaseDir, cancellationToken).ConfigureAwait(false);

        foreach (var child in children)
        {
            if (IsBookkeeping(child))
            {
                continue;
            }

            if (child.EndsWith(".md", StringComparison.Ordinal))
            {
                // A legacy flat entry (no scope directory): included only if it carries frontmatter.
                await TryAddMetaAsync(metas, knowledgeBaseDir, child, cancellationToken).ConfigureAwait(false);
                continue;
            }

            // Otherwise a scope directory (system/, <repo>/): walk its Markdown entries.
            var scopeDir = JoinPath(knowledgeBaseDir, child);
            var names = await _fileSystem.ListFilesAsync(scopeDir, cancellationToken).ConfigureAwait(false);
            foreach (var name in names)
            {
                if (IsBookkeeping(name) || !name.EndsWith(".md", StringComparison.Ordinal))
                {
                    continue;
                }

                await TryAddMetaAsync(metas, knowledgeBaseDir, $"{child}/{name}", cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        return metas;
    }

    private async Task TryAddMetaAsync(
        List<KnowledgeEntryMeta> metas,
        string knowledgeBaseDir,
        string relFile,
        CancellationToken cancellationToken
    )
    {
        var content = await _fileSystem
            .ReadFileAsync(JoinPath(knowledgeBaseDir, relFile), cancellationToken)
            .ConfigureAwait(false);
        var meta = content is null ? null : KnowledgeIndex.ParseFrontmatter(relFile, content);
        if (meta is null)
        {
            _logger.LogDebug("Skipping Knowledge Base entry '{Entry}' with no parseable frontmatter during regen.", relFile);
            return;
        }

        metas.Add(meta);
    }

    /// <summary>True for the ToC/index bookkeeping files and dotfiles the entry walk must ignore.</summary>
    private static bool IsBookkeeping(string name) =>
        name.StartsWith('.')
        || string.Equals(name, TocFileName, StringComparison.Ordinal)
        || string.Equals(name, IndexFileName, StringComparison.Ordinal);

    /// <summary>The scope (first path segment) of <paramref name="relPath"/>, or <c>null</c> when it has none.</summary>
    private static string? ScopeSegment(string relPath)
    {
        var slash = relPath.IndexOf('/', StringComparison.Ordinal);
        return slash > 0 ? relPath[..slash] : null;
    }

    /// <summary>The file stem of <paramref name="relPath"/> (a last-resort title when the model omits one).</summary>
    private static string SlugFromRelPath(string relPath)
    {
        var name = relPath[(relPath.LastIndexOf('/') + 1)..];
        return name.EndsWith(".md", StringComparison.Ordinal) ? name[..^3] : name;
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

/// <summary>
/// The header markers the extraction agent emits — <c>Scope</c>, <c>Title</c>, <c>Tags</c>, an optional
/// <c>Updates</c> relpath — and the entry <c>Body</c> that follows them. The daemon turns these into the
/// deterministic frontmatter; the model never writes frontmatter itself.
/// </summary>
internal sealed record ParsedEntry(
    string Scope,
    string Title,
    IReadOnlyList<string> Tags,
    string? Updates,
    string Body
);
