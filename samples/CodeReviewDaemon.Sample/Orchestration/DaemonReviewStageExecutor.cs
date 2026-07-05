using System.Text.Json;
using AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using CodeReviewDaemon.Sample.Agents;
using CodeReviewDaemon.Sample.Configuration;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Workspace;
using CodeReviewDaemon.Sample.Workspace.Git;
using CodeReviewDaemon.Sample.Workspace.ReviewBot;
using CodeReviewDaemon.Sample.Workspace.Sandbox;

namespace CodeReviewDaemon.Sample.Orchestration;

/// <summary>
/// The production <see cref="IReviewStageExecutor"/> (plan P4.4, §4–§15). It performs the work of each
/// stage and is the single consumer of the four agent/posting feature flags. It is <b>stateless across
/// stages</b>: it threads nothing through the run, persisting each stage's output as a
/// <see cref="ReviewArtifact"/> and re-reading what it needs from the store on the next stage, so a
/// crash resumes cleanly from the first incomplete stage.
/// <list type="bullet">
///   <item><see cref="ReviewStage.ContextReady"/> — fetch the PR diff in the sandbox, persist a
///   <c>review-context</c> artifact.</item>
///   <item><see cref="ReviewStage.Reviewed"/> — run the primary <see cref="ReviewAgent"/> and persist a
///   <c>review</c> artifact; if <c>EnableABVariants</c>, also run the collect-only
///   <see cref="VariantReviewer"/> B arm; if <c>EnableKnowledgeAgent</c>, write a Knowledge Base
///   entry.</item>
///   <item><see cref="ReviewStage.Judged"/> — if <c>EnableJudgeAgent</c>, grade the review with the
///   <see cref="JudgeAgent"/>.</item>
///   <item><see cref="ReviewStage.Posted"/> — post the review via <see cref="ReviewPoster"/> with
///   <c>LivePostingAuthorized = EnableCommentPosting</c> (collect-only by default).</item>
/// </list>
/// </summary>
internal sealed class DaemonReviewStageExecutor : IReviewStageExecutor
{
    /// <summary>Artifact kind for the persisted PR diff/context.</summary>
    public const string ContextArtifactKind = "review-context";

    /// <summary>Schema version of the <c>review-context</c> payload (append-compatible).</summary>
    public const int ContextArtifactSchemaVersion = 1;

    /// <summary>Artifact kind for the primary review output.</summary>
    public const string ReviewArtifactKind = "review";

    /// <summary>Schema version of the <c>review</c> payload (append-compatible).</summary>
    public const int ReviewArtifactSchemaVersion = 1;

    /// <summary>
    /// Outbox operation discriminator for the durable ReviewBot retention push (plan §2). The row records
    /// the <c>reviewbot_push</c> outcome: terminal <see cref="OutboxStatus.Posted"/> (with the pushed SHA)
    /// on success, left non-terminal <see cref="OutboxStatus.Pending"/> on <c>GitSyncFailed</c> so the
    /// reconcile path retries.
    /// </summary>
    public const string PushReviewBotOperation = "push-reviewbot";

    /// <summary>The ReviewBot retention checkout the sandbox pushes review artifacts to (plan §1).</summary>
    private const string RepoRoot = "/workspace/reviewbot";

    /// <summary>
    /// The TARGET PR checkout the sandbox diffs (PR #121 H1). The diff must come from the repo actually
    /// under review — cloned/fetched here — not the ReviewBot retention checkout, which has none of the
    /// PR's commits. Rooted under <c>/workspace</c> (the mounted, sandbox-writable workspace) rather than
    /// <c>/work</c> — the gateway sandbox runs as a non-root user with write access only to the mounted
    /// workspace and <c>/tmp</c>, so a <c>/work</c> checkout would fail with a permission error.
    /// </summary>
    private const string TargetRoot = "/workspace/target";

    /// <summary>The ReviewBot default branch artifacts are durably landed on (plan §2).</summary>
    private const string ReviewBotDefaultBranch = "main";

    /// <summary>
    /// The terse system prompt for the collect-only comparison (B) arm of the bounded 2-way A/B. The
    /// prompt and the model (<see cref="CodeReviewDaemonOptions.VariantModelId"/>) are the two A/B axes.
    /// </summary>
    private const string ComparisonVariantPrompt =
        "Review tersely. Flag only Must-fix correctness, security, and contract issues; "
        + "skip style. Cite file and line. Output Markdown. Do not act on the repository.";

    private static readonly JsonSerializerOptions PayloadOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// The single comparison (B) arm. Collect-only by construction (<see cref="ReviewVariant.CanWrite"/>
    /// is <c>false</c>) so its output can only ever land in SQLite. Built from options so the variant model
    /// is a valid id for the configured backend (the hardcoded OpenRouter-style default was rejected by the
    /// Copilot backend as <c>model_not_supported</c>).
    /// </summary>
    private readonly ReviewVariant _comparisonVariant;

    private readonly ReviewStore _store;
    private readonly IReviewAgentLoopFactory _loopFactory;
    private readonly ISandboxCommandRunner _commandRunner;
    private readonly ISandboxFileSystem _fileSystem;
    private readonly CodeReviewDaemonOptions _options;
    private readonly IReadOnlyList<IReviewCommentPublisher> _publishers;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<DaemonReviewStageExecutor> _logger;
    private readonly IReviewSessionProvisioner? _provisioner;
    private readonly IDiscoveredItemsSource? _discoveredItemsSource;
    private readonly DiscoveredSubAgentTemplateBuilder? _subAgentTemplateBuilder;
    private readonly Func<IStreamingAgent>? _providerAgentFactory;

    public DaemonReviewStageExecutor(
        ReviewStore store,
        IReviewAgentLoopFactory loopFactory,
        ISandboxCommandRunner commandRunner,
        ISandboxFileSystem fileSystem,
        CodeReviewDaemonOptions options,
        IEnumerable<IReviewCommentPublisher> publishers,
        ILoggerFactory loggerFactory,
        IReviewSessionProvisioner? provisioner = null,
        IDiscoveredItemsSource? discoveredItemsSource = null,
        DiscoveredSubAgentTemplateBuilder? subAgentTemplateBuilder = null,
        Func<IStreamingAgent>? providerAgentFactory = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _loopFactory = loopFactory ?? throw new ArgumentNullException(nameof(loopFactory));
        _commandRunner = commandRunner ?? throw new ArgumentNullException(nameof(commandRunner));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _publishers = [.. publishers ?? throw new ArgumentNullException(nameof(publishers))];
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _logger = loggerFactory.CreateLogger<DaemonReviewStageExecutor>();
        _provisioner = provisioner;
        _discoveredItemsSource = discoveredItemsSource;
        _subAgentTemplateBuilder = subAgentTemplateBuilder;
        _providerAgentFactory = providerAgentFactory;
        _comparisonVariant = new ReviewVariant(
            VariantId: "b",
            ModelId: _options.VariantModelId,
            SystemPrompt: ComparisonVariantPrompt,
            CanWrite: false);
    }

    /// <summary>
    /// Resolves the runner/filesystem pair this run's checkout git and the review agent's MCP tools
    /// should share (design §4). Tool-assisted runs ask the per-run <see cref="IReviewSessionProvisioner"/>
    /// for the run's sandbox session; the diff-only default (or a host without a provisioner registered)
    /// keeps using the injected boot-lifetime pair exactly as before this change.
    /// </summary>
    private async Task<(ISandboxCommandRunner Runner, ISandboxFileSystem Fs)> ResolveSandboxAsync(
        ReviewRun run, CancellationToken cancellationToken)
    {
        if (!_options.EnableToolAssistedReview || _provisioner is null)
        {
            return (_commandRunner, _fileSystem);
        }

        var session = await _provisioner.GetOrCreateAsync(run, cancellationToken).ConfigureAwait(false);
        return (session.CommandRunner, session.FileSystem);
    }

    /// <summary>
    /// Builds the per-run tool context for the primary review, or returns null to degrade to diff-only.
    /// Capability gaps (unreachable session, gateway down) log a warning and degrade — they never fail the
    /// stage (design §7). When the session resolves, sub-agent discovery is a further, independent degrade
    /// tier: a discovery/mapping failure (or nothing discovered) leaves <c>SubAgentOptions</c> null — a
    /// skill-only tool context — rather than dropping all the way back to diff-only.
    /// </summary>
    private async Task<ReviewToolContext?> BuildToolContextAsync(ReviewRun run, CancellationToken cancellationToken)
    {
        if (!_options.EnableToolAssistedReview || _provisioner is null)
        {
            return null;
        }

        try
        {
            var session = await _provisioner.GetOrCreateAsync(run, cancellationToken).ConfigureAwait(false);
            return new ReviewToolContext(
                GatewayBaseUrl: Environment.GetEnvironmentVariable("CRD_SANDBOX_GATEWAY") ?? "http://127.0.0.1:3000",
                SessionId: session.SessionId,
                ReadOnlyToolAllowList: _options.ReadOnlyToolAllowList,
                SubAgentOptions: await BuildSubAgentOptionsAsync(run, session.SessionId, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex, "Run {RunId}: tool-assisted review unavailable; degrading to diff-only.", run.Id);
            return null;
        }
    }

    /// <summary>
    /// Discovers <c>code-reviewer:*</c> sub-agents in the resolved session and maps them to
    /// <see cref="SubAgentTemplate"/>s (Task 11). Only attempted when all three sub-agent dependencies were
    /// supplied (they default to null, so hosts/tests that don't wire discovery keep today's skill-only
    /// tool context unchanged). Never throws — a discovery or mapping failure degrades to null (skill-only).
    /// </summary>
    private async Task<SubAgentOptions?> BuildSubAgentOptionsAsync(
        ReviewRun run, string sessionId, CancellationToken cancellationToken)
    {
        if (_discoveredItemsSource is null || _subAgentTemplateBuilder is null || _providerAgentFactory is null)
        {
            return null;
        }

        try
        {
            var discovered = await _discoveredItemsSource
                .ListDiscoveredAsync(sessionId, cancellationToken)
                .ConfigureAwait(false);
            var templates = _subAgentTemplateBuilder.Build(discovered, "code-reviewer", _providerAgentFactory);
            if (templates.Count > 0)
            {
                return new SubAgentOptions { Templates = templates };
            }

            _logger.LogInformation(
                "Run {RunId}: no code-reviewer sub-agents discovered; skill-only review.", run.Id);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Run {RunId}: sub-agent discovery failed; skill-only review.", run.Id);
            return null;
        }
    }

    public Task ExecuteStageAsync(ReviewStage stage, ReviewRun run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);

        return stage switch
        {
            ReviewStage.ContextReady => FetchContextAsync(run, cancellationToken),
            ReviewStage.Reviewed => ReviewAsync(run, cancellationToken),
            ReviewStage.Judged => JudgeAsync(run, cancellationToken),
            ReviewStage.Posted => PostAsync(run, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(
                nameof(stage), stage, "The executor only performs the post-Discovered stages."),
        };
    }

    private async Task FetchContextAsync(ReviewRun run, CancellationToken cancellationToken)
    {
        var (repo, provider) = ResolveRepo(run);
        var (runner, fileSystem) = await ResolveSandboxAsync(run, cancellationToken).ConfigureAwait(false);
        var git = new GitRunner(runner);

        // 1. Clone (or reuse) the TARGET repo into its own checkout (PR #121 H1). The per-run
        // OperationPolicy scopes the fetch to exactly this repo + the ReviewBot remote; submodule init
        // runs under it too, so an off-allow-list submodule is refused rather than fetched.
        var policy = DaemonOperationPolicy.BuildForRun(repo, _options.ReviewBotRepoUrl);
        var targetRemote = TargetRemoteUrl(repo, provider);
        await EnsureTargetCheckoutAsync(git, targetRemote, run, cancellationToken).ConfigureAwait(false);

        // 2. Selectively (and recursively) initialize allow-listed submodules in the target checkout.
        var submoduleInitializer = new SubmoduleInitializer(
            git,
            fileSystem,
            policy,
            provider,
            _loggerFactory.CreateLogger<SubmoduleInitializer>());
        var submoduleOutcome = await submoduleInitializer
            .InitializeAsync(TargetRoot, GitRemoteUrl.Parse(targetRemote), cancellationToken)
            .ConfigureAwait(false);
        foreach (var denied in submoduleOutcome.Denied)
        {
            _logger.LogWarning(
                "Run {RunId}: submodule '{Path}' ({Url}) was not initialized: {Reason}",
                run.Id, denied.Path, denied.Url, denied.Reason);
        }

        // 3. Diff the TARGET checkout — base...head — and persist the bounded context artifact.
        var diff = await git
            .RunAsync(["-C", TargetRoot, "diff", $"{run.BaseSha}...{run.HeadSha}"], TargetRoot, cancellationToken)
            .ConfigureAwait(false);
        if (!diff.Succeeded)
        {
            throw new InvalidOperationException(
                $"Fetching the diff for run {run.Id} failed (exit {diff.ExitCode}): {diff.Stderr}");
        }

        var boundedDiff = _options.Limits.CapArtifactPayload(diff.Stdout);

        _ = _store.AddArtifact(new ReviewArtifact
        {
            ReviewRunId = run.Id,
            ArtifactSchemaVersion = ContextArtifactSchemaVersion,
            ArtifactKind = ContextArtifactKind,
            Provider = provider,
            Payload = JsonSerializer.Serialize(
                new ContextArtifactPayload(run.PrId, run.BaseSha, run.HeadSha, boundedDiff)),
        });

        _logger.LogInformation("Run {RunId}: persisted {Kind} ({Length} char diff).",
            run.Id, ContextArtifactKind, boundedDiff.Length);
    }

    /// <summary>
    /// Clones the target repo into <see cref="TargetRoot"/> (or reuses an existing checkout), then
    /// fetches the PR's base + head commits so the <c>base...head</c> diff is resolvable. A failed clone
    /// or fetch surfaces (throws) so the stage retries.
    /// </summary>
    private async Task EnsureTargetCheckoutAsync(
        GitRunner git, string targetRemote, ReviewRun run, CancellationToken cancellationToken)
    {
        var probe = await git
            .RunAsync(["rev-parse", "--is-inside-work-tree"], TargetRoot, cancellationToken)
            .ConfigureAwait(false);
        if (!probe.Succeeded)
        {
            var clone = await git
                .RunAsync(["clone", targetRemote, TargetRoot], workingDirectory: null, cancellationToken)
                .ConfigureAwait(false);
            if (!clone.Succeeded)
            {
                throw new InvalidOperationException(
                    $"Cloning the target repo for run {run.Id} failed (exit {clone.ExitCode}): {clone.Stderr}");
            }
        }

        // Fetch the exact base + head commits the PR identity references (a fork/branch commit may not be
        // reachable from the default fetch); the diff below relies on both being present.
        var fetch = await git
            .RunAsync(["-C", TargetRoot, "fetch", "origin", run.BaseSha, run.HeadSha], TargetRoot, cancellationToken)
            .ConfigureAwait(false);
        if (!fetch.Succeeded)
        {
            throw new InvalidOperationException(
                $"Fetching the PR commits for run {run.Id} failed (exit {fetch.ExitCode}): {fetch.Stderr}");
        }
    }

    /// <summary>Builds the HTTPS clone URL for the target repo from its identity + provider.</summary>
    private static string TargetRemoteUrl(RepoIdentity repo, string provider) =>
        string.Equals(provider, "ado", StringComparison.Ordinal)
            ? $"https://dev.azure.com/{repo.OrgOrOwner}/{repo.Project}/_git/{repo.RepoName}"
            : $"https://github.com/{repo.OrgOrOwner}/{repo.RepoName}.git";

    private async Task ReviewAsync(ReviewRun run, CancellationToken cancellationToken)
    {
        var (repo, provider) = ResolveRepo(run);
        var reviewInput = BuildReviewInput(run, repo, ReadContextDiff(run.Id));

        // Primary review — collected and persisted; never posts here (the Posted stage owns posting).
        var reviewText = await RunPrimaryReviewAsync(run, provider, reviewInput, cancellationToken)
            .ConfigureAwait(false);

        if (_options.EnableABVariants)
        {
            await RunVariantArmAsync(run, provider, reviewInput, cancellationToken).ConfigureAwait(false);
        }

        if (_options.EnableKnowledgeAgent)
        {
            await RunKnowledgeArmAsync(run, repo, reviewInput, reviewText, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<string> RunPrimaryReviewAsync(
        ReviewRun run, string provider, string reviewInput, CancellationToken cancellationToken)
    {
        var toolContext = await BuildToolContextAsync(run, cancellationToken).ConfigureAwait(false);
        var profile = DaemonAgentFactory.CreateReviewProfile();
        await using var loop = _loopFactory.Create(
            profile, run.ModelId, ThreadId(run, run.VariantId), reasoningEffort: null, toolContext: toolContext);
        var agent = new ReviewAgent(loop, _loggerFactory.CreateLogger<ReviewAgent>());
        var result = await agent.ReviewAsync(reviewInput, cancellationToken).ConfigureAwait(false);

        _ = _store.AddArtifact(new ReviewArtifact
        {
            ReviewRunId = run.Id,
            ArtifactSchemaVersion = ReviewArtifactSchemaVersion,
            ArtifactKind = ReviewArtifactKind,
            Provider = provider,
            Payload = JsonSerializer.Serialize(
                new ReviewArtifactPayload(result.ReviewText, result.RunId, run.VariantId)),
        });

        return result.ReviewText;
    }

    private async Task RunVariantArmAsync(
        ReviewRun run, string provider, string reviewInput, CancellationToken cancellationToken)
    {
        var profile = DaemonAgentFactory.CreateVariantProfile(_comparisonVariant);
        await using var loop = _loopFactory.Create(
            profile, _comparisonVariant.ModelId, ThreadId(run, _comparisonVariant.VariantId), _options.VariantReasoningEffort);
        var reviewer = new VariantReviewer(loop, _store, _loggerFactory.CreateLogger<VariantReviewer>());
        _ = await reviewer.ReviewAsync(run.Id, provider, _comparisonVariant, reviewInput, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task RunKnowledgeArmAsync(
        ReviewRun run, RepoIdentity repo, string reviewInput, string reviewText, CancellationToken cancellationToken)
    {
        var profile = DaemonAgentFactory.CreateKnowledgeProfile();
        await using var loop = _loopFactory.Create(profile, run.ModelId, ThreadId(run, DaemonAgentFactory.KnowledgeProfileId));
        var agent = new KnowledgeAgent(loop, _fileSystem, _loggerFactory.CreateLogger<KnowledgeAgent>());

        var title = $"{repo.DisplayName} PR {run.PrId}";
        var knowledgeInput = $"{reviewInput}\n\n## Review\n{reviewText}";
        _ = await agent.WriteEntryAsync(RepoRoot, title, knowledgeInput, cancellationToken).ConfigureAwait(false);
    }

    private async Task JudgeAsync(ReviewRun run, CancellationToken cancellationToken)
    {
        if (!_options.EnableJudgeAgent)
        {
            return;
        }

        var (_, provider) = ResolveRepo(run);
        var reviewText = ReadReviewText(run.Id);

        var profile = DaemonAgentFactory.CreateJudgeProfile();
        await using var loop = _loopFactory.Create(profile, run.ModelId, ThreadId(run, DaemonAgentFactory.JudgeProfileId));
        var judge = new JudgeAgent(loop, _store, _loggerFactory.CreateLogger<JudgeAgent>());

        var judgingInput = $"Grade this code review:\n\n{reviewText}";
        _ = await judge.JudgeAsync(
            new JudgeRequest(run.Id, provider, run.VariantId, judgingInput), cancellationToken).ConfigureAwait(false);
    }

    private async Task PostAsync(ReviewRun run, CancellationToken cancellationToken)
    {
        var (repo, provider) = ResolveRepo(run);
        var publisher = _publishers.FirstOrDefault(p =>
            string.Equals(p.Provider, provider, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"No review-comment publisher registered for provider '{provider}'.");

        var poster = new ReviewPoster(publisher, _store, _loggerFactory.CreateLogger<ReviewPoster>());

        // ReviewPoster requires a non-blank body even on the collect-only path (it still builds the
        // idempotency key and records the outbox row), so fall back when the agent produced nothing.
        var reviewText = ReadReviewText(run.Id);
        var body = string.IsNullOrWhiteSpace(reviewText) ? "_No review content was produced._" : reviewText;

        var key = new IdempotencyKeyComponents(
            Provider: provider,
            OrgOrOwner: repo.OrgOrOwner,
            Project: repo.Project,
            // RepoStableId must be non-blank and ':'-free; fall back to the colon-free normalized key.
            RepoStableId: string.IsNullOrWhiteSpace(repo.RepoStableId) ? repo.NormalizedKey : repo.RepoStableId,
            PrId: run.PrId,
            Operation: ReviewPoster.PostReviewCommentOperation,
            ArtifactKind: ReviewArtifactKind,
            ArtifactSubject: "summary",
            // Scope the key to the reviewed COMMIT. head_sha is stable across re-polls and — unlike the PR
            // updated_at — is not mutated by posting the comment, so a re-poll of the same commit resolves
            // to the same key and the backstop scan recognizes the already-posted comment (no duplicate).
            HeadSha: run.HeadSha,
            VariantId: run.VariantId);

        var request = new PostReviewRequest(
            run.Id,
            key,
            new ReviewCommentTarget(repo, run.PrId),
            body,
            LivePostingAuthorized: _options.EnableCommentPosting);

        _ = await poster.PostReviewAsync(request, cancellationToken).ConfigureAwait(false);

        // Durably persist the primary review's artifacts to the ReviewBot repo (AC#6, plan §2). This is
        // the only path that writes to the ReviewBot remote; the collect-only B variant never reaches it.
        await PublishToReviewBotAsync(run, repo, provider, body, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs the §2 durable one-commit retention sequence for the primary review when a ReviewBot repo is
    /// configured, then records the <c>reviewbot_push</c> outcome in the outbox: terminal
    /// <see cref="OutboxStatus.Posted"/> (carrying the pushed SHA) on success, or left non-terminal on a
    /// <see cref="ReviewBotPublishOutcome.GitSyncFailed"/> so the reconcile path retries. Retention is
    /// skipped (and nothing is pushed) when <c>ReviewBotRepoUrl</c> is unset — the inert default.
    /// </summary>
    private async Task PublishToReviewBotAsync(
        ReviewRun run, RepoIdentity repo, string provider, string reviewBody, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.ReviewBotRepoUrl))
        {
            return;
        }

        var git = new GitRunner(_commandRunner);

        // PR #121 H3: clone (or reuse) the configured ReviewBot remote and validate its skeleton before
        // pushing. The daemon must not assume the checkout exists/is well-formed — a missing remote gives
        // a classified clone diagnosis, a malformed skeleton fails fast rather than pushing into a corrupt
        // repo.
        await EnsureReviewBotCheckoutAsync(git, run, cancellationToken).ConfigureAwait(false);

        var manager = new ReviewBotRepoManager(
            git,
            _fileSystem,
            provider,
            _loggerFactory.CreateLogger<ReviewBotRepoManager>());

        // Only the PRs/... artifact is supplied explicitly; any KnowledgeBase/... entry the Knowledge arm
        // wrote into the checkout earlier is committed by the manager's `git add -A` (plan §2 step 2/3).
        var prArtifactPath =
            $"PRs/{provider}/{ReviewBotRepoManagerSlug(repo)}/{run.PrId}-{ShortSha(run.HeadSha)}/review.md";
        var request = new ReviewBotPublishRequest(
            repo,
            PrNumber: int.Parse(run.PrId, System.Globalization.CultureInfo.InvariantCulture),
            HeadSha: run.HeadSha,
            DefaultBranch: ReviewBotDefaultBranch,
            Files: [new ReviewArtifactFile(prArtifactPath, reviewBody)]);

        var result = await manager.PublishAsync(RepoRoot, request, cancellationToken).ConfigureAwait(false);

        var outbox = _store.EnqueueOutbox(new OutboxEntry
        {
            IdempotencyKey = BuildPushKey(run, repo, provider),
            Provider = provider,
            ReviewRunId = run.Id,
            Operation = PushReviewBotOperation,
            ArtifactKind = ReviewArtifactKind,
            Status = OutboxStatus.Pending,
        });

        if (result.Outcome == ReviewBotPublishOutcome.Pushed)
        {
            _ = _store.TryTransitionOutbox(outbox.Id, outbox.Status, OutboxStatus.Posted, result.PushedSha);
            _logger.LogInformation(
                "Run {RunId}: ReviewBot retention pushed {Sha}; review branch '{Branch}' deleted.",
                run.Id, result.PushedSha, result.ReviewBranch);
        }
        else
        {
            // GitSyncFailed — leave the outbox row non-terminal (Pending) so reconcile retries. The
            // manager kept the review branch, so no artifacts are lost.
            _logger.LogWarning(
                "Run {RunId}: ReviewBot retention failed to push; review branch '{Branch}' kept for reconcile.",
                run.Id, result.ReviewBranch);
        }
    }

    /// <summary>
    /// Clones (or reuses) the configured ReviewBot remote into <see cref="RepoRoot"/> and validates its
    /// skeleton before any push (PR #121 H3). A failed clone surfaces a classified diagnosis; a malformed
    /// skeleton fails fast rather than pushing into a corrupt repo. A freshly-cloned empty repo is seeded.
    /// </summary>
    private async Task EnsureReviewBotCheckoutAsync(GitRunner git, ReviewRun run, CancellationToken cancellationToken)
    {
        var cloneFailure = await ReviewBotCheckout
            .EnsureCheckoutAsync(
                git, _options.ReviewBotRepoUrl!, RepoRoot,
                _loggerFactory.CreateLogger("reviewbot-checkout"), cancellationToken)
            .ConfigureAwait(false);
        if (cloneFailure is not null)
        {
            throw new InvalidOperationException(
                $"Run {run.Id}: ReviewBot checkout failed ({cloneFailure.Kind}): {cloneFailure.Message}");
        }

        var initializer = new ReviewBotInitializer(
            git, _fileSystem, _loggerFactory.CreateLogger<ReviewBotInitializer>());
        var init = await initializer
            .InitializeAsync(RepoRoot, ReviewBotDefaultBranch, cancellationToken)
            .ConfigureAwait(false);
        if (init.Outcome == ReviewBotInitOutcome.Malformed)
        {
            throw new InvalidOperationException(
                $"Run {run.Id}: ReviewBot checkout is malformed; missing required path(s): "
                + string.Join(", ", init.MissingPaths));
        }
    }

    /// <summary>The push retention idempotency key: a single push per (run, primary variant).</summary>
    private string BuildPushKey(ReviewRun run, RepoIdentity repo, string provider) =>
        IdempotencyKey.Build(new IdempotencyKeyComponents(
            Provider: provider,
            OrgOrOwner: repo.OrgOrOwner,
            Project: repo.Project,
            RepoStableId: string.IsNullOrWhiteSpace(repo.RepoStableId) ? repo.NormalizedKey : repo.RepoStableId,
            PrId: run.PrId,
            Operation: PushReviewBotOperation,
            ArtifactKind: ReviewArtifactKind,
            ArtifactSubject: "retention",
            HeadSha: run.HeadSha,
            VariantId: run.VariantId));

    /// <summary>Slugs the target identity into a single ReviewBot path segment (mirrors the branch slug).</summary>
    private static string ReviewBotRepoManagerSlug(RepoIdentity repo)
    {
        var parts = new[] { repo.OrgOrOwner, repo.Project, repo.RepoName }
            .Where(static p => !string.IsNullOrEmpty(p))
            .Select(static p => SlugSegment(p!));
        return string.Join('-', parts);
    }

    private static string SlugSegment(string value)
    {
        var chars = value
            .ToLowerInvariant()
            .Select(static c => char.IsLetterOrDigit(c) || c is '.' or '_' ? c : '-');
        return new string([.. chars]).Trim('-');
    }

    private static string ShortSha(string sha) => sha.Length <= 8 ? sha : sha[..8];

    /// <summary>
    /// Resolves the run's repo and the publisher/artifact provider string. <c>RepoIdentity.Provider</c>
    /// is the storage namespace (<c>github</c> / <c>azure-devops</c>); the publisher/poll-target
    /// namespace is <c>github</c> / <c>ado</c>, so Azure DevOps is mapped here once.
    /// </summary>
    private (RepoIdentity Repo, string Provider) ResolveRepo(ReviewRun run)
    {
        var repo = _store.GetRepo(run.RepoId)
            ?? throw new InvalidOperationException($"Repo {run.RepoId} not found for run {run.Id}.");
        var provider = string.Equals(repo.Provider, "azure-devops", StringComparison.Ordinal) ? "ado" : repo.Provider;
        return (repo, provider);
    }

    private string ReadContextDiff(long reviewRunId) =>
        ReadArtifactPayload<ContextArtifactPayload>(reviewRunId, ContextArtifactKind).Diff;

    private string ReadReviewText(long reviewRunId) =>
        ReadArtifactPayload<ReviewArtifactPayload>(reviewRunId, ReviewArtifactKind).ReviewText;

    private T ReadArtifactPayload<T>(long reviewRunId, string kind)
    {
        var artifact = _store.GetArtifacts(reviewRunId)
            .LastOrDefault(a => string.Equals(a.ArtifactKind, kind, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"No '{kind}' artifact for run {reviewRunId}.");

        return JsonSerializer.Deserialize<T>(artifact.Payload, PayloadOptions)
            ?? throw new InvalidOperationException($"The '{kind}' artifact for run {reviewRunId} did not deserialize.");
    }

    private static string BuildReviewInput(ReviewRun run, RepoIdentity repo, string diff) =>
        $"Review pull request {repo.DisplayName}#{run.PrId} (head {run.HeadSha}).\n\nDiff:\n{diff}";

    private static string ThreadId(ReviewRun run, string variant) => $"review-run-{run.Id}-{variant}";
}

/// <summary>The persisted PR diff/context (kind <c>review-context</c>).</summary>
internal sealed record ContextArtifactPayload(string PrId, string BaseSha, string HeadSha, string Diff);

/// <summary>The persisted primary review output (kind <c>review</c>).</summary>
internal sealed record ReviewArtifactPayload(string ReviewText, string? RunId, string VariantId);

/// <summary>
/// The one discovery operation <see cref="DaemonReviewStageExecutor"/> needs from the registry to build
/// sub-agent templates (Task 11/12). Implemented by
/// <see cref="AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox.SandboxSessionRegistry"/> via the
/// <c>RegistryDiscoverySource</c> adapter (registered in Program.cs) and by a fake in tests — mirrors the
/// narrow <see cref="ISandboxSessionSource"/> seam already used for session provisioning, so the executor
/// stays verifiable against a fake without a live gateway.
/// </summary>
internal interface IDiscoveredItemsSource
{
    Task<IReadOnlyList<SandboxSessionRegistry.DiscoveredItem>> ListDiscoveredAsync(string sessionId, CancellationToken ct);
}

