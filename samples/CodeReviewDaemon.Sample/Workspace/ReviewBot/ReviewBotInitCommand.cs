using CodeReviewDaemon.Sample.Workspace.Git;
using CodeReviewDaemon.Sample.Workspace.Sandbox;

namespace CodeReviewDaemon.Sample.Workspace.ReviewBot;

/// <summary>
/// The checked-in, human-run setup subcommand: <c>CodeReviewDaemon reviewbot init --url &lt;url&gt;</c>
/// (plan §1). This is the thin integration glue around the unit-tested <see cref="ReviewBotInitializer"/>:
/// it clones the ReviewBot repo into the sandbox (or reuses an existing checkout), runs the idempotent
/// seed/validate, and maps the outcome to a process exit code — <c>0</c> when the repo is created or
/// already well-formed, non-zero when it is malformed or the sandbox/clone step fails. All decision
/// logic lives in <see cref="ReviewBotInitializer"/> + <see cref="SandboxFileSystem"/>, which are
/// verifiable against fakes; only the live sandbox connection and clone live here.
/// </summary>
internal static class ReviewBotInitCommand
{
    private const string DefaultBranch = "main";
    private const string DefaultWorkdir = "/work/reviewbot";

    /// <summary>Runs <c>reviewbot init</c>. Expects <paramref name="args"/> to start with <c>reviewbot init</c>.</summary>
    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);

        using var loggerFactory = LoggerFactory.Create(builder =>
            builder.AddSimpleConsole(options => options.SingleLine = true));
        var logger = loggerFactory.CreateLogger("reviewbot-init");

        var url = GetOption(args, "--url");
        if (string.IsNullOrWhiteSpace(url))
        {
            logger.LogError("reviewbot init requires --url <ReviewBotRepoUrl>.");
            return 64; // EX_USAGE
        }

        var gateway =
            GetOption(args, "--gateway") ?? Environment.GetEnvironmentVariable("CRD_SANDBOX_GATEWAY");
        if (string.IsNullOrWhiteSpace(gateway))
        {
            logger.LogError(
                "reviewbot init requires a sandbox gateway (--gateway <baseUrl> or CRD_SANDBOX_GATEWAY).");
            return 64;
        }

        var sessionId = GetOption(args, "--session") ?? Guid.NewGuid().ToString("N");
        var branch = GetOption(args, "--branch") ?? DefaultBranch;
        var workdir = GetOption(args, "--workdir") ?? DefaultWorkdir;

        await using var sandbox = new SandboxOrchestrator(
            gateway,
            sessionId,
            loggerFactory.CreateLogger<SandboxOrchestrator>());
        var git = new GitRunner(sandbox);
        var fileSystem = new SandboxFileSystem(sandbox);

        if (!await EnsureCheckoutAsync(git, url, workdir, logger, cancellationToken).ConfigureAwait(false))
        {
            return 70; // EX_SOFTWARE — clone/connect failed.
        }

        var initializer = new ReviewBotInitializer(
            git,
            fileSystem,
            loggerFactory.CreateLogger<ReviewBotInitializer>());
        var result = await initializer
            .InitializeAsync(workdir, branch, cancellationToken)
            .ConfigureAwait(false);

        if (result.Outcome == ReviewBotInitOutcome.Created)
        {
            logger.LogInformation("ReviewBot repository seeded at {Workdir}.", workdir);
            return 0;
        }

        if (result.Outcome == ReviewBotInitOutcome.AlreadySeeded)
        {
            logger.LogInformation("ReviewBot repository already initialized; nothing to do.");
            return 0;
        }

        logger.LogError(
            "ReviewBot repository is malformed. Missing: {Missing}",
            string.Join(", ", result.MissingPaths));
        return 65; // EX_DATAERR
    }

    /// <summary>Clones <paramref name="url"/> into <paramref name="workdir"/>, or reuses an existing checkout.</summary>
    private static async Task<bool> EnsureCheckoutAsync(
        GitRunner git,
        string url,
        string workdir,
        ILogger logger,
        CancellationToken cancellationToken
    )
    {
        var probe = await git
            .RunAsync(["rev-parse", "--is-inside-work-tree"], workdir, cancellationToken)
            .ConfigureAwait(false);
        if (probe.Succeeded)
        {
            logger.LogInformation("Reusing existing ReviewBot checkout at {Workdir}.", workdir);
            return true;
        }

        var clone = await git
            .RunAsync(["clone", url, workdir], workingDirectory: null, cancellationToken)
            .ConfigureAwait(false);
        if (!clone.Succeeded)
        {
            logger.LogError(
                "git clone of ReviewBot repo failed (exit {ExitCode}): {Stderr}",
                clone.ExitCode,
                clone.Stderr);
            return false;
        }

        return true;
    }

    private static string? GetOption(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.Ordinal))
            {
                return args[i + 1];
            }
        }

        return null;
    }
}
