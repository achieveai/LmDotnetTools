using CodeReviewDaemon.Sample.Workspace.Git;
using CodeReviewDaemon.Sample.Workspace.Sandbox;

namespace CodeReviewDaemon.Sample.Workspace.ReviewBot;

/// <summary>Terminal state of a <see cref="ReviewBotInitializer"/> run.</summary>
internal enum ReviewBotInitOutcome
{
    /// <summary>The checkout was empty and the skeleton was seeded + committed + pushed.</summary>
    Created,

    /// <summary>The checkout already contained a well-formed skeleton; nothing was changed.</summary>
    AlreadySeeded,

    /// <summary>The checkout was partially seeded; <see cref="ReviewBotInitResult.MissingPaths"/> lists the gaps.</summary>
    Malformed,
}

/// <summary>The result of <see cref="ReviewBotInitializer.InitializeAsync"/>.</summary>
/// <param name="Outcome">What the initializer did (or refused to do).</param>
/// <param name="MissingPaths">For <see cref="ReviewBotInitOutcome.Malformed"/>, the required paths that are absent.</param>
internal sealed record ReviewBotInitResult(
    ReviewBotInitOutcome Outcome,
    IReadOnlyList<string> MissingPaths);

/// <summary>
/// Implements the plan §1 idempotent ReviewBot setup. Setup is a one-time, human-run subcommand; this
/// class owns the decision: an <b>empty</b> checkout is seeded with the skeleton (default branch
/// <c>main</c>, <c>KnowledgeBase/_toc.md</c>, <c>KnowledgeBase/.gitkeep</c>, <c>PRs/.gitkeep</c>,
/// <c>README.md</c>) and pushed; a <b>fully-seeded</b> checkout is a no-op; a <b>partially-seeded</b>
/// checkout is reported as malformed (with exactly what is missing) and is never silently mutated, so a
/// corrupt repo surfaces to the operator rather than being papered over. Like the rest of the git
/// orchestration it runs through <see cref="GitRunner"/> + <see cref="ISandboxFileSystem"/>, so it is
/// verifiable against in-memory fakes.
/// </summary>
internal sealed class ReviewBotInitializer
{
    /// <summary>The skeleton files that define a well-formed ReviewBot repo (plan §1 seed).</summary>
    private static readonly IReadOnlyList<string> RequiredFiles =
    [
        "README.md",
        "PRs/.gitkeep",
        "KnowledgeBase/.gitkeep",
        "KnowledgeBase/_toc.md",
    ];

    private readonly GitRunner _git;
    private readonly ISandboxFileSystem _fileSystem;
    private readonly ILogger<ReviewBotInitializer> _logger;

    public ReviewBotInitializer(
        GitRunner git,
        ISandboxFileSystem fileSystem,
        ILogger<ReviewBotInitializer> logger
    )
    {
        _git = git ?? throw new ArgumentNullException(nameof(git));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Inspects the ReviewBot checkout at <paramref name="repoRoot"/> and seeds it onto
    /// <paramref name="defaultBranch"/> when empty, leaves it untouched when already well-formed, or
    /// reports it malformed when partially seeded.
    /// </summary>
    public async Task<ReviewBotInitResult> InitializeAsync(
        string repoRoot,
        string defaultBranch,
        CancellationToken cancellationToken
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultBranch);

        var present = new List<string>();
        foreach (var relativePath in RequiredFiles)
        {
            var content = await _fileSystem
                .ReadFileAsync(JoinPath(repoRoot, relativePath), cancellationToken)
                .ConfigureAwait(false);
            if (content is not null)
            {
                present.Add(relativePath);
            }
        }

        if (present.Count == RequiredFiles.Count)
        {
            _logger.LogInformation("ReviewBot repo at '{Root}' is already seeded; no changes.", repoRoot);
            return new ReviewBotInitResult(ReviewBotInitOutcome.AlreadySeeded, []);
        }

        if (present.Count > 0)
        {
            var missing = RequiredFiles.Where(r => !present.Contains(r)).ToList();
            _logger.LogError(
                "ReviewBot repo at '{Root}' is malformed; missing {Count} required path(s): {Missing}",
                repoRoot,
                missing.Count,
                string.Join(", ", missing));
            // Never mutate a partially-populated repo — surface the corruption to the operator.
            return new ReviewBotInitResult(ReviewBotInitOutcome.Malformed, missing);
        }

        await SeedAsync(repoRoot, defaultBranch, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Seeded ReviewBot skeleton at '{Root}' on branch '{Branch}'.", repoRoot, defaultBranch);
        return new ReviewBotInitResult(ReviewBotInitOutcome.Created, []);
    }

    private async Task SeedAsync(string repoRoot, string defaultBranch, CancellationToken cancellationToken)
    {
        await _git.RunAsync(["checkout", "-B", defaultBranch], repoRoot, cancellationToken)
            .ConfigureAwait(false);

        foreach (var (relativePath, content) in SeedFiles())
        {
            await _fileSystem
                .WriteFileAsync(JoinPath(repoRoot, relativePath), content, cancellationToken)
                .ConfigureAwait(false);
        }

        await _git.RunAsync(["add", "-A"], repoRoot, cancellationToken).ConfigureAwait(false);
        await _git.RunAsync(["commit", "-m", "Seed ReviewBot repository"], repoRoot, cancellationToken)
            .ConfigureAwait(false);
        await _git.RunAsync(["push", "-u", "origin", defaultBranch], repoRoot, cancellationToken)
            .ConfigureAwait(false);
    }

    private static IEnumerable<(string RelativePath, string Content)> SeedFiles()
    {
        yield return ("README.md", ReadmeContent);
        yield return ("KnowledgeBase/_toc.md", TocStub);
        yield return ("KnowledgeBase/.gitkeep", string.Empty);
        yield return ("PRs/.gitkeep", string.Empty);
    }

    private const string ReadmeContent =
        "# ReviewBot\n\n"
        + "Durable store for the Code-Review Daemon.\n\n"
        + "- `KnowledgeBase/` — accumulated review knowledge; `_toc.md` is the generated table of contents.\n"
        + "- `PRs/` — retained per-PR review artifacts (`{repo}-{pr}/`).\n\n"
        + "## Setup\n\n"
        + "This repository must be **created out-of-band** (the daemon does not create provider repos). "
        + "Create the empty remote, grant the bot identity access, then run `CodeReviewDaemon reviewbot "
        + "init --url <remote-url>` once to seed this skeleton. The long-running daemon only clones and "
        + "pushes; it fails fast if the remote is missing or the skeleton is malformed.\n";

    private const string TocStub = "# Knowledge Base\n\n_Table of contents (generated)._\n";

    private static string JoinPath(string root, string relative) =>
        $"{root.TrimEnd('/')}/{relative.TrimStart('/')}";
}
