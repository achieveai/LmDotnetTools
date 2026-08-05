namespace CodeReviewDaemon.Sample.Workspace.Sandbox;

/// <summary>
/// Reads and writes files inside the sandbox working tree. This is a deliberately tiny companion to
/// <see cref="ISandboxCommandRunner"/>: the daemon writes review artifacts (PRs/, KnowledgeBase/) into
/// the ReviewBot checkout before committing, and reads <c>.gitmodules</c> while walking submodules.
/// Keeping it an interface lets the deterministic orchestration (<c>ReviewBotRepoManager</c>,
/// <c>SubmoduleInitializer</c>) be verified against an in-memory fake with no live gateway.
/// </summary>
internal interface ISandboxFileSystem
{
    /// <summary>
    /// Reads a UTF-8 text file, refusing any file larger than <paramref name="maxBytes"/>. See
    /// <see cref="SandboxFileRead"/> for how "missing" and "refused" are told apart.
    /// <para>
    /// There is no unbounded overload, and that is the point. Every reader here is a caller with a
    /// KILOBYTE-scale budget for what it will actually use — a 16 KiB listing, a 32 KiB guidance file, a
    /// ranked digest — and every one of them used to enforce that budget AFTER the whole file was in
    /// memory. That bounds parsing cost; it bounds nothing about ingestion, which is the cost that
    /// matters when the file is written by an agent or arrives on a PR head. An unbounded sibling would
    /// let each new call site inherit the defect instead of choosing a ceiling.
    /// </para>
    /// </summary>
    Task<SandboxFileRead> ReadFileAsync(string path, long maxBytes, CancellationToken cancellationToken);

    /// <summary>Writes UTF-8 text, creating parent directories as needed (overwrites if present).</summary>
    Task WriteFileAsync(string path, string content, CancellationToken cancellationToken);

    /// <summary>
    /// Lists the entry names directly under <paramref name="directory"/> (non-recursive; names only,
    /// not full paths), or an empty list when the directory does not exist. The single consumer is the
    /// Knowledge Base table-of-contents regeneration, which enumerates <c>KnowledgeBase/</c> to rebuild
    /// <c>_toc.md</c> from the entries actually present.
    /// </summary>
    Task<IReadOnlyList<string>> ListFilesAsync(string directory, CancellationToken cancellationToken);
}

/// <summary>
/// The outcome of a bounded read: the file's text, or the reason there is none.
/// <para>
/// A VALUE rather than an exception, because the reason has to survive the caller. The knowledge readers
/// wrap their reads in a catch that degrades any failure to "no prior knowledge", so a cap that threw would
/// arrive as silence — and silence is exactly what the review prompt teaches means "this repository has no
/// Knowledge Base". The refusal must be something a caller has to destructure to ignore.
/// </para>
/// <para>
/// <see cref="TooLarge"/> and never a prefix. The gateway's files API has no range read, so a truncating
/// contract would be implementable host-side and not sandbox-side — the route asymmetry that lets the two
/// halves of this daemon drift apart. Refusal is symmetric, and a half-read file is worse than none: the
/// last JSONL record, Markdown link or code fence is cut mid-way and nothing downstream can tell.
/// </para>
/// </summary>
/// <param name="Content">The file's text, or <c>null</c> when it is missing or was refused.</param>
/// <param name="TooLarge">Whether the file exists but exceeded the caller's byte ceiling.</param>
internal readonly record struct SandboxFileRead(string? Content, bool TooLarge)
{
    /// <summary>The file does not exist. Not an error, and not a refusal.</summary>
    public static SandboxFileRead Missing => new(null, false);

    /// <summary>The file exists and is larger than the caller agreed to read.</summary>
    public static SandboxFileRead Refused => new(null, true);

    /// <summary>The file, read whole.</summary>
    public static SandboxFileRead Of(string content) => new(content, false);

    /// <summary>
    /// Whether the file is there at all — true for a refusal, because a refusal is a statement about a file
    /// that EXISTS. A presence check written as <c>Content is not null</c> would answer "no" for an
    /// over-size file and quietly re-seed a store, or re-clone a checkout, over the top of one.
    /// </summary>
    public bool Exists => Content is not null || TooLarge;
}

/// <summary>
/// The byte ceilings the daemon reads with, named once so a call site picks a ceiling rather than inventing
/// one. These bound INGESTION and are deliberately far above the kilobyte-scale presentation budgets
/// downstream of them (<c>MaxExistingListingChars</c>, the digest's character budget) — those decide how much
/// of a legitimate file reaches a prompt, these decide how much of a pathological file reaches memory. Set so
/// that no plausible real file is ever refused: a 5,000-entry <c>_index.jsonl</c> at ~300 bytes a record is
/// ~1.5 MB.
/// </summary>
internal static class SandboxReadLimits
{
    /// <summary>
    /// A Knowledge Base LISTING (<c>_index.jsonl</c>, <c>_toc.md</c>) — the one file that legitimately grows
    /// with the whole store, so it gets the most generous ceiling of the three.
    /// </summary>
    public const long KnowledgeListingBytes = 8L * 1024 * 1024;

    /// <summary>
    /// A single Knowledge Base entry — one lesson, written by the extraction agent. Nothing legitimate here
    /// approaches a megabyte; the entries are prose with frontmatter.
    /// </summary>
    public const long KnowledgeEntryBytes = 1L * 1024 * 1024;

    /// <summary>
    /// A repository control or guidance file (<c>.gitmodules</c>, <c>CLAUDE.md</c>, <c>AGENTS.md</c>, the
    /// ReviewBot config, the ownership marker). The guidance files are read from the PR HEAD, which is
    /// attacker-controllable on a public repository, so this ceiling is the one that has to hold against
    /// input nobody in this process wrote.
    /// </summary>
    public const long RepositoryFileBytes = 1L * 1024 * 1024;
}
