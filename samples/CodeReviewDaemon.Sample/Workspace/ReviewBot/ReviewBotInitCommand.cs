using AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;
using CodeReviewDaemon.Sample.Workspace.Git;
using CodeReviewDaemon.Sample.Workspace.Sandbox;

namespace CodeReviewDaemon.Sample.Workspace.ReviewBot;

/// <summary>
/// The checked-in, human-run setup subcommand: <c>CodeReviewDaemon reviewbot init --url &lt;url&gt;</c>
/// (plan §1). This is the thin integration glue around the unit-tested <see cref="ReviewBotInitializer"/>:
/// it clones the <b>pre-created</b> ReviewBot remote into the sandbox (or reuses an existing checkout),
/// runs the idempotent seed/validate, and maps the outcome to a process exit code — <c>0</c> when the
/// repo is created or already well-formed, non-zero otherwise. The command does <b>not</b> create a
/// provider repository: the ReviewBot remote must be created out-of-band and the bot identity granted
/// access first. When the clone fails it returns a precise, classified exit code + message
/// (<see cref="CloneFailureClassifier"/>) — not-found vs permission vs bad-credential vs transient —
/// rather than a generic failure. All decision logic lives in <see cref="ReviewBotInitializer"/>,
/// <see cref="CloneFailureClassifier"/>, and <see cref="SandboxSessionAdapter"/>, which are verifiable
/// against fakes; only the live sandbox connection and clone live here.
/// </summary>
internal static class ReviewBotInitCommand
{
    private const string DefaultBranch = "main";
    private const string DefaultWorkdir = "/workspace/reviewbot";

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

        // Sandbox gateway per-app identity (ADR 0029) — mirrors Program.cs's daemon identity so this
        // one-time setup command authenticates to the gateway as the same app the long-running daemon
        // does. A present-but-invalid key fails fast (redacted); an absent key is the keyless
        // AUTH_ENFORCE=off dev path, logged once and never blocking the command.
        var appId = Environment.GetEnvironmentVariable("CRD_SANDBOX_APP_ID") ?? "codereview-daemon";
        var appKey = Environment.GetEnvironmentVariable("CRD_SANDBOX_APP_KEY");
        if (string.IsNullOrWhiteSpace(appKey))
        {
            logger.LogWarning(
                "CRD_SANDBOX_APP_KEY is not set; connecting to the sandbox gateway as app '{AppId}' with "
                    + "no key (keyless AUTH_ENFORCE=off dev path).",
                appId);
        }
        else
        {
            try
            {
                SandboxCredential.ValidateKeyOrThrow(appId, appKey);
            }
            catch (ArgumentException ex)
            {
                logger.LogError("{Message}", ex.Message);
                return 64; // EX_USAGE
            }
        }

        var credential = new SandboxCredential(appId, appKey ?? string.Empty);

        await using var sandbox = new SandboxSessionAdapter(
            gateway,
            sessionId,
            loggerFactory.CreateLogger<SandboxSessionAdapter>(),
            credential);
        var git = new GitRunner(sandbox);
        ISandboxFileSystem fileSystem = sandbox;

        var cloneFailure = await ReviewBotCheckout
            .EnsureCheckoutAsync(git, url, workdir, logger, cancellationToken)
            .ConfigureAwait(false);
        if (cloneFailure is not null)
        {
            logger.LogError("{Message}", cloneFailure.Message);
            return cloneFailure.ExitCode;
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
