using CodeReviewDaemon.Sample.Workspace.Sandbox;

namespace CodeReviewDaemon.Sample.Agents;

/// <summary>
/// Rebuilds the Knowledge Base's two DERIVED listings — <c>_index.jsonl</c> and <c>_toc.md</c> — from the
/// entry files actually present on disk. Both are pure functions of the entries' frontmatter (see
/// <see cref="KnowledgeIndex.RenderIndex"/> and <see cref="KnowledgeTableOfContents.Render"/>), so they are
/// always cheaper to recompute than to reconcile, and there is never a reason to accept one side of a
/// conflict on them.
/// <para>
/// It lives outside <see cref="KnowledgeAgent"/> because it has two callers with nothing else in common:
/// the extraction pass, which regenerates after writing an entry, and the notes-branch merge in
/// <see cref="Workspace.Git.ReviewBranchManager"/>, which regenerates AFTER the merge lands so the listings
/// describe the merged tree rather than whichever side the merge strategy happened to pick. The second
/// caller has no agent and must not need one.
/// </para>
/// </summary>
internal sealed class KnowledgeIndexRegenerator
{
    /// <summary>The Knowledge Base directory, relative to the repository root.</summary>
    internal const string KnowledgeBaseDirectory = "KnowledgeBase";

    /// <summary>The human-readable table of contents, regenerated from the entries present.</summary>
    internal const string TocFileName = "_toc.md";

    /// <summary>The machine-queryable entry index, regenerated from the entries present.</summary>
    internal const string IndexFileName = "_index.jsonl";

    private readonly ISandboxFileSystem _fileSystem;
    private readonly ILogger _logger;

    public KnowledgeIndexRegenerator(ISandboxFileSystem fileSystem, ILogger logger)
    {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Regenerates <c>_index.jsonl</c> and <c>_toc.md</c> from the layered entries actually present, so
    /// neither ever drifts from the directory. Walks each scope directory under
    /// <paramref name="knowledgeBaseDir"/>, parses each entry's frontmatter, and skips (with a log) any
    /// entry that has none — malformed frontmatter never aborts the regen (design §6).
    /// <para>
    /// Returns <c>true</c> when either file's content actually CHANGED. The merge caller needs that answer
    /// to decide whether it has anything to commit: both renderers are deterministic and sorted, so an
    /// unchanged result is the normal outcome and must not produce an empty commit on every sweep.
    /// </para>
    /// </summary>
    public async Task<bool> RegenerateAsync(string knowledgeBaseDir, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(knowledgeBaseDir);

        var metas = await CollectEntryMetasAsync(knowledgeBaseDir, cancellationToken).ConfigureAwait(false);

        var index = KnowledgeIndex.RenderIndex(metas);

        // _toc.md link labels: the blank-title fallback to file path lives in
        // KnowledgeTableOfContents.RenderItems now (issue #259), so every caller gets it for free —
        // pass the raw Title through here.
        var tocEntries = metas.Select(meta => new KnowledgeEntry(meta.File, meta.Title)).ToList();
        var toc = KnowledgeTableOfContents.Render(tocEntries);

        var indexChanged = await WriteIfChangedAsync(knowledgeBaseDir, IndexFileName, index, cancellationToken)
            .ConfigureAwait(false);
        var tocChanged = await WriteIfChangedAsync(knowledgeBaseDir, TocFileName, toc, cancellationToken)
            .ConfigureAwait(false);

        return indexChanged || tocChanged;
    }

    /// <summary>
    /// Writes <paramref name="content"/> to <paramref name="fileName"/> and reports whether that changed
    /// anything. A read that fails or comes back over the listing ceiling counts as CHANGED: the point of
    /// the comparison is to avoid a pointless commit, and when the current content cannot be established
    /// the safe answer is the one that keeps the regenerated file.
    /// </summary>
    private async Task<bool> WriteIfChangedAsync(
        string knowledgeBaseDir,
        string fileName,
        string content,
        CancellationToken cancellationToken
    )
    {
        var path = JoinPath(knowledgeBaseDir, fileName);
        var read = await _fileSystem
            .ReadFileAsync(path, SandboxReadLimits.KnowledgeListingBytes, cancellationToken)
            .ConfigureAwait(false);
        var changed = read.TooLarge || !string.Equals(read.Content, content, StringComparison.Ordinal);

        await _fileSystem.WriteFileAsync(path, content, cancellationToken).ConfigureAwait(false);

        return changed;
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
            if (IsBookkeeping(child) || IsDevelopersDirectory(child))
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
        var read = await _fileSystem
            .ReadFileAsync(
                JoinPath(knowledgeBaseDir, relFile),
                SandboxReadLimits.KnowledgeEntryBytes,
                cancellationToken
            )
            .ConfigureAwait(false);
        if (read.TooLarge)
        {
            // LISTED, not skipped. This regen REPLACES both listings, and the reviewer reads _toc.md as the
            // set of entries that exist — so dropping an unreadable entry here does not merely fail to index
            // it, it deletes the only route anything has to a file still sitting in the store. Listed under a
            // path-derived title with no metadata: honest about what is unknown, and the link still resolves.
            _logger.LogWarning(
                "Knowledge Base entry '{Entry}' exceeds the {Limit}-byte read limit; listing it without "
                    + "frontmatter rather than dropping it from the regenerated index and table of contents.",
                relFile,
                SandboxReadLimits.KnowledgeEntryBytes
            );
            metas.Add(
                new KnowledgeEntryMeta(
                    relFile,
                    $"{SlugFromRelPath(relFile)} — too large to index; frontmatter unread",
                    [],
                    ScopeSegment(relFile) ?? string.Empty,
                    [],
                    string.Empty
                )
            );
            return;
        }

        var content = read.Content;
        var meta = content is null ? null : KnowledgeIndex.ParseFrontmatter(relFile, content);
        if (meta is null)
        {
            _logger.LogDebug(
                "Skipping Knowledge Base entry '{Entry}' with no parseable frontmatter during regen.",
                relFile
            );
            return;
        }

        metas.Add(meta);
    }

    /// <summary>True for the ToC/index bookkeeping files and dotfiles the entry walk must ignore.</summary>
    internal static bool IsBookkeeping(string name) =>
        name.StartsWith('.')
        || string.Equals(name, TocFileName, StringComparison.Ordinal)
        || string.Equals(name, IndexFileName, StringComparison.Ordinal);

    /// <summary>
    /// True for the reserved per-developer review-feedback directory
    /// (<see cref="ReviewFeedbackAgent.DevelopersDirectory"/>). Those records are about ONE person and are
    /// delivered by targeted injection into that person's own PRs; letting them into <c>_index.jsonl</c> /
    /// <c>_toc.md</c> would put every developer's record into every reviewer's context and spend the shared
    /// retrieval budget on it. Matched case-insensitively because a case-insensitive checkout (Windows)
    /// collapses <c>Developers/</c> onto <c>developers/</c>.
    /// </summary>
    internal static bool IsDevelopersDirectory(string name) =>
        string.Equals(name, ReviewFeedbackAgent.DevelopersDirectory, StringComparison.OrdinalIgnoreCase);

    /// <summary>The scope (first path segment) of <paramref name="relPath"/>, or <c>null</c> when it has none.</summary>
    internal static string? ScopeSegment(string relPath)
    {
        var slash = relPath.IndexOf('/', StringComparison.Ordinal);
        return slash > 0 ? relPath[..slash] : null;
    }

    /// <summary>The file stem of <paramref name="relPath"/> (a last-resort title when the model omits one).</summary>
    internal static string SlugFromRelPath(string relPath)
    {
        var name = relPath[(relPath.LastIndexOf('/') + 1)..];
        return name.EndsWith(".md", StringComparison.Ordinal) ? name[..^3] : name;
    }

    /// <summary>Joins a Knowledge Base root and a forward-slash relative path into one sandbox path.</summary>
    internal static string JoinPath(string root, string relative) => $"{root.TrimEnd('/')}/{relative.TrimStart('/')}";
}
