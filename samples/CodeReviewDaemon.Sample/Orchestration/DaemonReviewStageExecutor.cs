using System.Collections.Concurrent;
using System.Globalization;
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
///   <see cref="VariantReviewer"/> B arm. Knowledge extraction no longer runs per-review — it runs at
///   PR-close from the <see cref="PrLifecycleSweeper"/> (Layer-2, design §1).</item>
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

    /// <summary>The cross-repo <c>AchieveAiReviews</c> store superproject checkout (the reviewed repo lives
    /// under <c>{StoreRoot}/repos/&lt;Repo&gt;</c> beside the shared <c>Contracts/</c> layer). Only used on
    /// the tool-assisted store path; the single-repo path clones straight into <see cref="TargetRoot"/>.</summary>
    private const string StoreRoot = "/workspace/store";

    /// <summary>The container mount point the leased pool slot is exposed at (design §4.1): the slot's
    /// <c>store/</c> child is <see cref="StoreRoot"/> and its <c>scratch/</c> child is a sibling outside the
    /// git tree. The daemon's host-side git operates on the slot's HOST paths; the review agent's MCP tools
    /// address these container paths — they are the ones recorded on the context artifact + tool context.</summary>
    private const string SandboxWorkspaceRoot = "/workspace";

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
    private readonly HostRetentionWorkspace? _hostRetention;
    private readonly SandboxCredential _credential;
    private readonly ReviewSlotWorkspace? _slotWorkspace;

    /// <summary>
    /// The per-run pooled lease, populated by <see cref="FetchContextAsync"/> when the pooled
    /// scoped-writable path handled a run and consumed by <see cref="ReviewAsync"/> (scoped tool context)
    /// and <see cref="PostAsync"/> (commit-notes + slot return). Held in memory because a leased slot is a
    /// host-process resource, not persisted state; the stages of a run execute in-process and serially, so
    /// a resume after a crash simply finds no lease and degrades to the read-only / host-retention path.
    /// </summary>
    private readonly ConcurrentDictionary<long, LeasedReview> _leasedReviews = new();

    /// <summary>Host lifetime, used to stop the daemon when a session lacks code-reviewer skill/agent
    /// support and <see cref="CodeReviewDaemonOptions.RequireSkillSupport"/> is set (fail-fast, not degrade).</summary>
    private readonly Microsoft.Extensions.Hosting.IHostApplicationLifetime? _appLifetime;

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
        Func<IStreamingAgent>? providerAgentFactory = null,
        HostRetentionWorkspace? hostRetention = null,
        SandboxCredential credential = default,
        ReviewSlotWorkspace? slotWorkspace = null,
        Microsoft.Extensions.Hosting.IHostApplicationLifetime? appLifetime = null)
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
        _hostRetention = hostRetention;
        _credential = credential;
        _slotWorkspace = slotWorkspace;
        _appLifetime = appLifetime;
        _comparisonVariant = new ReviewVariant(
            VariantId: "b",
            ModelId: _options.VariantModelId,
            SystemPrompt: ComparisonVariantPrompt,
            CanWrite: false);
    }

    /// <summary>Thrown by <see cref="BuildToolContextAsync"/> when a session lacks code-reviewer skill/agent
    /// support and <see cref="CodeReviewDaemonOptions.RequireSkillSupport"/> is set — aborts the review
    /// (rather than degrading) and is deliberately let through the degrade-catch.</summary>
    private sealed class SkillSupportUnavailableException(string sessionId)
        : InvalidOperationException(
            $"Sandbox session '{sessionId}' has no code-reviewer skill/agent support; review aborted (RequireSkillSupport).");

    /// <summary>
    /// Resolves the runner/filesystem pair this run's checkout git and the review agent's MCP tools
    /// should share (design §4). Tool-assisted runs ask the per-run <see cref="IReviewSessionProvisioner"/>
    /// for the run's sandbox session; the diff-only default (or a host without a provisioner registered)
    /// keeps using the injected boot-lifetime pair exactly as before this change. A <c>null</c> session
    /// (the host-dir disk guard declined to provision one, Task 18) degrades the same way — the checkout
    /// git runs against the boot-lifetime pair rather than failing the stage (design §7).
    /// </summary>
    private async Task<(ISandboxCommandRunner Runner, ISandboxFileSystem Fs)> ResolveSandboxAsync(
        ReviewRun run, CancellationToken cancellationToken)
    {
        if (!_options.EnableToolAssistedReview || _provisioner is null)
        {
            return (_commandRunner, _fileSystem);
        }

        var session = await _provisioner.GetOrCreateAsync(run, cancellationToken).ConfigureAwait(false);
        return session is null ? (_commandRunner, _fileSystem) : (session.CommandRunner, session.FileSystem);
    }

    /// <summary>
    /// Builds the per-run tool context for the primary review, or returns null to degrade to diff-only.
    /// Capability gaps (unreachable session, gateway down, or the host-dir disk guard declining to
    /// provision, Task 18) log and degrade — they never fail the stage (design §7). When the session
    /// resolves, sub-agent discovery is a further, independent degrade tier: a discovery/mapping failure
    /// (or nothing discovered) leaves <c>SubAgentOptions</c> null — a skill-only tool context — rather than
    /// dropping all the way back to diff-only.
    /// </summary>
    private async Task<ReviewToolContext?> BuildToolContextAsync(ReviewRun run, CancellationToken cancellationToken)
    {
        if (!_options.EnableToolAssistedReview || _provisioner is null)
        {
            return null;
        }

        try
        {
            // A pooled run mounts the agent session OVER the leased slot (so /workspace == the slot and
            // /workspace/store is real); every other tool-assisted run keeps the per-run mount. The lease
            // was recorded by TryPooledFetchContextAsync in the ContextReady stage.
            var session = _leasedReviews.TryGetValue(run.Id, out var lease)
                ? await _provisioner.GetOrCreateForSlotAsync(run, lease.Slot, cancellationToken).ConfigureAwait(false)
                : await _provisioner.GetOrCreateAsync(run, cancellationToken).ConfigureAwait(false);
            if (session is null)
            {
                _logger.LogInformation(
                    "Run {RunId}: no sandbox session provisioned (disk guard); degrading to diff-only.", run.Id);
                return null;
            }

            // Scoped-writable reviewer (Layer 1): when this run leased a pooled slot and reviewer-writes are
            // enabled, hand the agent scoped Write/Edit/Bash + the (container) notes/scratch roots the writes
            // are bounded to. Absent a pooled lease the reviewer stays hard read-only exactly as before.
            var writeScope = ResolvePooledWriteScope(run);

            var subAgentOptions = await BuildSubAgentOptionsAsync(run, session.SessionId, cancellationToken)
                .ConfigureAwait(false);

            // Fail-fast (RequireSkillSupport): a session with NO code-reviewer sub-agents can't support a
            // proper review, so abort rather than posting a degraded skill-only one — and stop the daemon so
            // the operator fixes the sandbox/plugin setup instead of it silently churning out weak reviews.
            if (_options.RequireSkillSupport && subAgentOptions is null)
            {
                _logger.LogCritical(
                    "Run {RunId}: sandbox session {SessionId} has no code-reviewer sub-agent support; Revobot "
                        + "will not review without proper skills/agents. Aborting this review and stopping the "
                        + "daemon (RequireSkillSupport=true).",
                    run.Id, session.SessionId);
                _appLifetime?.StopApplication();
                throw new SkillSupportUnavailableException(session.SessionId);
            }

            return new ReviewToolContext(
                GatewayBaseUrl: Environment.GetEnvironmentVariable("CRD_SANDBOX_GATEWAY") ?? "http://127.0.0.1:3000",
                SessionId: session.SessionId,
                ReadOnlyToolAllowList: _options.ReadOnlyToolAllowList,
                SubAgentOptions: subAgentOptions,
                Credential: _credential,
                EnableReviewerWrites: writeScope.Enabled,
                WritableToolAllowList: writeScope.WritableAllow,
                NotesDir: writeScope.NotesDir,
                ScratchDir: writeScope.ScratchDir);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not SkillSupportUnavailableException)
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
            _logger.LogInformation(
                "Run {RunId}: sub-agent discovery deps not wired (itemsSource={ItemsSource}, builder={Builder}, "
                    + "agentFactory={AgentFactory}); skill-only review.",
                run.Id,
                _discoveredItemsSource is not null,
                _subAgentTemplateBuilder is not null,
                _providerAgentFactory is not null);
            return null;
        }

        try
        {
            var discovered = await _discoveredItemsSource
                .ListDiscoveredAsync(sessionId, cancellationToken)
                .ConfigureAwait(false);
            var subagentCount = discovered.Count(d => string.Equals(d.Kind, "subagent", StringComparison.Ordinal));
            _logger.LogInformation(
                "Run {RunId}: gateway /discovered returned {Total} item(s) for session {SessionId} ({Subagents} subagent(s)); "
                    + "kinds=[{Kinds}].",
                run.Id,
                discovered.Count,
                sessionId,
                subagentCount,
                string.Join(",", discovered.Select(d => d.Kind).Distinct()));
            var templates = _subAgentTemplateBuilder.Build(discovered, _options.SubAgentMarketplaces, _providerAgentFactory);
            if (templates.Count > 0)
            {
                return new SubAgentOptions { Templates = templates };
            }

            _logger.LogInformation(
                "Run {RunId}: no sub-agents discovered from marketplace(s) [{Marketplaces}]; skill-only review.",
                run.Id,
                _options.SubAgentMarketplaces.Count > 0 ? string.Join(",", _options.SubAgentMarketplaces) : "(all)");
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
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

    /// <summary>
    /// Returns the pooled slot leased for <paramref name="runId"/> (if any) and forgets the lease,
    /// idempotently. The atomic <see cref="ConcurrentDictionary{TKey,TValue}.TryRemove(TKey, out TValue)"/>
    /// guards against a double-return: whichever of this method or the Posted stage removes the entry first
    /// returns the slot, and the other is a no-op. Called from the orchestrator's terminal <c>finally</c> so
    /// a run that ends without reaching Posted (PR-not-open short-circuit or a stage exception) still returns
    /// its slot instead of leaking pool capacity.
    /// </summary>
    public async Task ReleaseReviewLeaseAsync(long runId, CancellationToken cancellationToken)
    {
        if (_slotWorkspace is not null && _leasedReviews.TryRemove(runId, out var lease))
        {
            await _slotWorkspace.Pool.ReturnAsync(lease.Slot, CancellationToken.None).ConfigureAwait(false);
            _logger.LogInformation("Run {RunId}: returned pooled slot {Index} on the terminal path.", runId, lease.Slot.Index);
        }
    }

    private async Task FetchContextAsync(ReviewRun run, CancellationToken cancellationToken)
    {
        var (repo, provider) = ResolveRepo(run);

        // Pooled scoped-writable path (Layer 1): lease a warm slot, prepare it host-side (branch reuse
        // carries prior notes), diff the prepared submodule host-side, and persist the context. When the
        // reviewed repo is not a submodule of the store — or the pooled path isn't wired — this returns
        // false and the existing per-run/diff-only checkout below runs unchanged (degrade intact, §7).
        if (UsePooledReview
            && await TryPooledFetchContextAsync(run, repo, provider, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var (runner, fileSystem) = await ResolveSandboxAsync(run, cancellationToken).ConfigureAwait(false);
        var git = new GitRunner(runner);

        // Resolve the checkout: prefer the cross-repo AchieveAiReviews store (the reviewed repo as a
        // submodule under repos/<Repo> beside the shared Contracts/ layer) when configured and applicable;
        // otherwise the single-repo /workspace/target checkout. The per-run OperationPolicy scopes every
        // fetch to the reviewed repo + the store's Contracts/ + gated siblings, so an off-allow-list
        // submodule (e.g. an unrelated sibling for a fork/public PR) is refused rather than fetched.
        var storeSubmodules = BuildStoreSubmoduleAllowList(run, repo);
        var policy = DaemonOperationPolicy.BuildForRun(
            repo, _options.ReviewBotRepoUrl, allowWriteOperations: false, allowedSubmodules: storeSubmodules);

        var layout = await EnsureCheckoutAsync(git, fileSystem, policy, repo, provider, run, cancellationToken)
            .ConfigureAwait(false);

        // Diff the reviewed repo — base...head — from wherever it was checked out, and persist the bounded
        // context artifact alongside the head file manifest (so the agent can Read files by exact path).
        var diff = await git
            .RunAsync(["-C", layout.TargetDir, "diff", $"{run.BaseSha}...{run.HeadSha}"], layout.TargetDir, cancellationToken)
            .ConfigureAwait(false);
        if (!diff.Succeeded)
        {
            throw new InvalidOperationException(
                $"Fetching the diff for run {run.Id} failed (exit {diff.ExitCode}): {diff.Stderr}");
        }

        var boundedDiff = _options.Limits.CapArtifactPayload(diff.Stdout);
        var fileManifest = await BuildFileManifestAsync(git, layout.TargetDir, cancellationToken).ConfigureAwait(false);

        _ = _store.AddArtifact(new ReviewArtifact
        {
            ReviewRunId = run.Id,
            ArtifactSchemaVersion = ContextArtifactSchemaVersion,
            ArtifactKind = ContextArtifactKind,
            Provider = provider,
            Payload = JsonSerializer.Serialize(new ContextArtifactPayload(
                run.PrId, run.BaseSha, run.HeadSha, boundedDiff, fileManifest, layout.TargetDir, layout.StoreRoot)),
        });

        _logger.LogInformation(
            "Run {RunId}: persisted {Kind} ({Length} char diff, {Files} manifest files) from {TargetDir} (store={Store}).",
            run.Id, ContextArtifactKind, boundedDiff.Length, ManifestFileCount(fileManifest),
            layout.TargetDir, layout.StoreRoot ?? "(single-repo)");
    }

    /// <summary>Whether the pooled scoped-writable review path is wired and enabled: tool-assisted +
    /// reviewer-writes on, a pool wired (Program.cs), and a resolved store to clone into the slots. When
    /// off, <see cref="FetchContextAsync"/> uses the existing per-run/diff-only checkout unchanged.</summary>
    private bool UsePooledReview =>
        _options.EnableToolAssistedReview
        && _options.EnableReviewerWrites
        && _slotWorkspace is not null
        && !string.IsNullOrWhiteSpace(_options.ResolvedStoreUrl);

    /// <summary>
    /// The pooled ContextReady phase: lease a warm slot, prepare it host-side (fetch, reuse-or-create the
    /// PR's persistent notes branch, advance the reviewed submodule to the PR head, wipe scratch), diff the
    /// prepared submodule host-side, and persist the context artifact carrying the <b>container</b> paths
    /// the review agent's tools address. Returns <c>true</c> when it handled the run (the lease is carried
    /// forward on <see cref="_leasedReviews"/> for the review + commit-notes + return), or <c>false</c> when
    /// the reviewed repo is not a submodule of the store so the caller falls back to the per-run checkout.
    /// The slot is always returned on any decline/failure so a transient error can never leak pool capacity;
    /// a genuine prep/diff failure surfaces (throws) so the stage retries with no partial artifact (§8).
    /// </summary>
    private async Task<bool> TryPooledFetchContextAsync(
        ReviewRun run, RepoIdentity repo, string provider, CancellationToken cancellationToken)
    {
        var storeUrl = _options.ResolvedStoreUrl!;
        var hostGit = new GitRunner(_slotWorkspace!.HostRunner);
        var hostFileSystem = _slotWorkspace.HostFileSystem;

        var slot = await _slotWorkspace.Pool.LeaseAsync(cancellationToken).ConfigureAwait(false);
        var handedOff = false;
        try
        {
            var submoduleRelPath = await ResolveStoreSubmodulePathAsync(hostFileSystem, slot.StorePath, repo, provider)
                .ConfigureAwait(false);
            if (submoduleRelPath is null)
            {
                _logger.LogInformation(
                    "Run {RunId}: {Repo} is not a submodule of the pooled store; using the per-run checkout.",
                    run.Id, repo.NormalizedKey);
                return false;
            }

            var branch = BuildNotesBranchName(hostGit, hostFileSystem, repo, run);
            var notesRelPath = BuildNotesRelPath(repo, run.PrId);
            var policy = DaemonOperationPolicy.BuildForRun(
                repo, _options.ReviewBotRepoUrl, allowWriteOperations: false,
                allowedSubmodules: BuildStoreSubmoduleAllowList(run, repo));

            var prepared = await _slotWorkspace.Preparer.PrepareAsync(
                    slot, run, storeUrl, submoduleRelPath, branch, ReviewBotDefaultBranch, notesRelPath, policy,
                    cancellationToken)
                .ConfigureAwait(false);

            // Diff + manifest run HOST-side against the prepared submodule working tree (privileged daemon
            // git), never in the sandbox the agent shares.
            var diff = await hostGit
                .RunAsync(["-C", prepared.TargetDir, "diff", $"{run.BaseSha}...{run.HeadSha}"], prepared.TargetDir, cancellationToken)
                .ConfigureAwait(false);
            if (!diff.Succeeded)
            {
                throw new InvalidOperationException(
                    $"Fetching the diff for run {run.Id} failed (exit {diff.ExitCode}): {diff.Stderr}");
            }

            var boundedDiff = _options.Limits.CapArtifactPayload(diff.Stdout);
            var fileManifest = await BuildFileManifestAsync(hostGit, prepared.TargetDir, cancellationToken).ConfigureAwait(false);

            // Container paths the agent's MCP tools address (the slot is mounted at /workspace) — these, not
            // the host paths the daemon git used, are what the review input + tool context reference.
            var targetDirSandbox = PosixJoin(StoreRoot, submoduleRelPath);
            var notesDirSandbox = PosixJoin(StoreRoot, notesRelPath);
            var scratchDirSandbox = $"{SandboxWorkspaceRoot}/{_options.ScratchDirName}";

            _ = _store.AddArtifact(new ReviewArtifact
            {
                ReviewRunId = run.Id,
                ArtifactSchemaVersion = ContextArtifactSchemaVersion,
                ArtifactKind = ContextArtifactKind,
                Provider = provider,
                Payload = JsonSerializer.Serialize(new ContextArtifactPayload(
                    run.PrId, run.BaseSha, run.HeadSha, boundedDiff, fileManifest, targetDirSandbox, StoreRoot)),
            });

            // Record the lease so the review + commit-notes stages can find it, guarding against silently
            // overwriting a lease already held for this run id. ContextReady runs once per run, so an
            // existing entry means a prior slot was never returned; overwriting it would orphan that slot.
            // Fail the stage instead (handedOff stays false, so this slot is returned by the finally below)
            // and let the orchestrator's terminal finally return the stale one — the stage then retries clean.
            if (!_leasedReviews.TryAdd(
                run.Id,
                new LeasedReview(slot, prepared, notesRelPath, branch, notesDirSandbox, scratchDirSandbox)))
            {
                throw new InvalidOperationException(
                    $"Run {run.Id} already holds a pooled review lease; refusing to overwrite it (would leak a slot).");
            }

            handedOff = true;

            _logger.LogInformation(
                "Run {RunId}: pooled slot {Index} prepared on branch '{Branch}' ({Length} char diff, {Files} "
                    + "manifest files) from {TargetDir}.",
                run.Id, slot.Index, branch, boundedDiff.Length, ManifestFileCount(fileManifest), prepared.TargetDir);
            return true;
        }
        finally
        {
            // Return the slot on decline (not-a-submodule) or failure (exception). On success the lease owns
            // it until PostAsync returns it, so it is NOT returned here (handedOff).
            if (!handedOff)
            {
                await _slotWorkspace.Pool.ReturnAsync(slot, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Resolves the reviewed repo's submodule path under the leased slot's store clone by parsing
    /// its <c>.gitmodules</c> (mirrors <see cref="TryStoreCheckoutAsync"/>'s pairing), or <c>null</c> when
    /// the store declares no submodule for the reviewed repo — the signal to fall back to the per-run
    /// checkout.</summary>
    private async Task<string?> ResolveStoreSubmodulePathAsync(
        ISandboxFileSystem fileSystem, string storeRoot, RepoIdentity repo, string provider)
    {
        var gitmodules = await fileSystem
            .ReadFileAsync(PosixJoin(storeRoot, ".gitmodules"), CancellationToken.None)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(gitmodules))
        {
            return null;
        }

        var targetUrl = GitRemoteUrl.Parse(TargetRemoteUrl(repo, provider));
        var entry = GitModulesParser.Parse(gitmodules)
            .FirstOrDefault(e => SubmoduleTargetsRepo(e.Url, targetUrl));
        return entry?.Path;
    }

    /// <summary>The PR's persistent notes branch name (<c>review/{repo}-{pr}</c>) — resolved
    /// through <see cref="ReviewBranchManager.BuildReviewBranchName(ReviewBotPublishRequest)"/> so the preparer,
    /// the commit-notes step, and the sweeper all name the branch identically.</summary>
    private string BuildNotesBranchName(
        GitRunner hostGit, ISandboxFileSystem hostFileSystem, RepoIdentity repo, ReviewRun run) =>
        new ReviewBranchManager(hostGit, hostFileSystem, _loggerFactory.CreateLogger<ReviewBranchManager>())
            .BuildReviewBranchName(BuildNotesRequest(repo, run, []));

    /// <summary>The PR's persistent notes directory under the store (<c>PRs/{repo}-{pr}</c>,
    /// design §4.3 D3 — one accumulating dir per PR, keyed by PR number for the PR's lifetime).</summary>
    private static string BuildNotesRelPath(RepoIdentity repo, string prId) =>
        $"PRs/{ReviewBotRepoManagerSlug(repo)}-{prId}";

    private static ReviewBotPublishRequest BuildNotesRequest(
        RepoIdentity repo, ReviewRun run, IReadOnlyList<ReviewArtifactFile> files) =>
        new(
            repo,
            PrNumber: int.Parse(run.PrId, CultureInfo.InvariantCulture),
            HeadSha: run.HeadSha,
            DefaultBranch: ReviewBotDefaultBranch,
            Files: files);

    /// <summary>The scoped-write config for the run's review agent: the writable tool allow-list + the
    /// container notes/scratch roots when this run leased a pooled slot and reviewer-writes are on, else
    /// read-only (no writable tools). Only a pooled lease supplies concrete write roots, so a run that fell
    /// back to the per-run checkout stays read-only.</summary>
    private (bool Enabled, IReadOnlyList<string>? WritableAllow, string? NotesDir, string? ScratchDir) ResolvePooledWriteScope(
        ReviewRun run) =>
        _options.EnableReviewerWrites && _leasedReviews.TryGetValue(run.Id, out var lease)
            ? (true, _options.WritableToolAllowList, lease.NotesDirSandbox, lease.ScratchDirSandbox)
            : (false, null, null, null);

    /// <summary>Where a run's code was checked out. <see cref="ReviewRoot"/> is what the review agent reads
    /// from (the cross-repo store root when in store mode — so Contracts/ and sibling repos are visible —
    /// else the single-repo checkout). <see cref="TargetDir"/> is the reviewed repo itself (the submodule
    /// working tree in store mode, else the same as ReviewRoot); the diff, head checkout, and file manifest
    /// are all taken there. <see cref="StoreRoot"/> is non-null only in cross-repo store mode.</summary>
    private sealed record CheckoutLayout(string ReviewRoot, string TargetDir, string? StoreRoot);

    /// <summary>The in-memory carry between the stages of a pooled run: the leased <see cref="Slot"/> (to
    /// return on the terminal stage), the host-side <see cref="Prepared"/> checkout (its <c>StoreRoot</c> is
    /// where commit-notes stages the PR dir), the PR notes <see cref="NotesRelPath"/> (the commit gate's
    /// scoped stage path + branch derivation), the <see cref="Branch"/> the notes persist on, and the
    /// container <see cref="NotesDirSandbox"/>/<see cref="ScratchDirSandbox"/> the scoped review agent
    /// writes to.</summary>
    private sealed record LeasedReview(
        ReviewSlot Slot,
        PreparedCheckout Prepared,
        string NotesRelPath,
        string Branch,
        string NotesDirSandbox,
        string ScratchDirSandbox);

    /// <summary>
    /// Resolves the run's checkout. When a cross-repo store is configured (<see
    /// cref="CodeReviewDaemonOptions.ResolvedStoreUrl"/>) and the reviewed repo is one of its submodules,
    /// clones the store, initializes that submodule (the allow-list denies the rest), and reviews from the
    /// store root. Otherwise clones the reviewed repo directly into <see cref="TargetRoot"/>. Either way the
    /// reviewed repo's working tree is moved to the PR head so Read/Grep/Glob and the manifest reflect the
    /// proposed code.
    /// </summary>
    private async Task<CheckoutLayout> EnsureCheckoutAsync(
        GitRunner git,
        ISandboxFileSystem fileSystem,
        OperationPolicy policy,
        RepoIdentity repo,
        string provider,
        ReviewRun run,
        CancellationToken cancellationToken)
    {
        var storeUrl = _options.ResolvedStoreUrl;
        if (_options.EnableToolAssistedReview && !string.IsNullOrWhiteSpace(storeUrl))
        {
            var storeLayout = await TryStoreCheckoutAsync(
                    git, fileSystem, policy, repo, provider, storeUrl, run, cancellationToken)
                .ConfigureAwait(false);
            if (storeLayout is not null)
            {
                return storeLayout;
            }

            _logger.LogInformation(
                "Run {RunId}: {Repo} is not a submodule of the cross-repo store; using the single-repo checkout.",
                run.Id, repo.NormalizedKey);
        }

        // Single-repo checkout: clone the reviewed repo directly, move it to the PR head, init its own
        // allow-listed submodules.
        var targetRemote = TargetRemoteUrl(repo, provider);
        await CloneIfMissingAsync(git, targetRemote, TargetRoot, run, cancellationToken).ConfigureAwait(false);
        await FetchAndCheckoutHeadAsync(git, TargetRoot, run, cancellationToken).ConfigureAwait(false);
        _ = await InitAllowListedSubmodulesAsync(
                git, fileSystem, policy, provider, TargetRoot, GitRemoteUrl.Parse(targetRemote), run, cancellationToken)
            .ConfigureAwait(false);
        return new CheckoutLayout(ReviewRoot: TargetRoot, TargetDir: TargetRoot, StoreRoot: null);
    }

    /// <summary>
    /// Attempts the cross-repo store checkout: clone the store, find the reviewed repo among its submodules,
    /// and (if present) initialize that submodule and move it to the PR head. Returns the store layout on
    /// success, or <c>null</c> when the store declares no submodule for the reviewed repo (or that submodule
    /// was denied by the allow-list) so the caller falls back to the single-repo checkout.
    /// </summary>
    private async Task<CheckoutLayout?> TryStoreCheckoutAsync(
        GitRunner git,
        ISandboxFileSystem fileSystem,
        OperationPolicy policy,
        RepoIdentity repo,
        string provider,
        string storeUrl,
        ReviewRun run,
        CancellationToken cancellationToken)
    {
        await CloneIfMissingAsync(git, storeUrl, StoreRoot, run, cancellationToken).ConfigureAwait(false);

        var gitmodules = await fileSystem
            .ReadFileAsync(PosixJoin(StoreRoot, ".gitmodules"), cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(gitmodules))
        {
            return null;
        }

        var targetUrl = GitRemoteUrl.Parse(TargetRemoteUrl(repo, provider));
        var entry = GitModulesParser.Parse(gitmodules)
            .FirstOrDefault(e => SubmoduleTargetsRepo(e.Url, targetUrl));
        if (entry is null)
        {
            return null;
        }

        // Initialize the store's allow-listed submodules (the reviewed repo + any gated siblings); the
        // allow-list denies everything else, so an unrelated sibling is never fetched.
        var outcome = await InitAllowListedSubmodulesAsync(
                git, fileSystem, policy, provider, StoreRoot, GitRemoteUrl.Parse(storeUrl), run, cancellationToken)
            .ConfigureAwait(false);

        if (!outcome.InitializedPaths.Contains(entry.Path))
        {
            _logger.LogWarning(
                "Run {RunId}: reviewed submodule '{Path}' was not initialized (denied by the allow-list?); "
                    + "falling back to the single-repo checkout.",
                run.Id, entry.Path);
            return null;
        }

        var targetDir = PosixJoin(StoreRoot, entry.Path);
        await FetchAndCheckoutHeadAsync(git, targetDir, run, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation(
            "Run {RunId}: reviewing {Repo} as store submodule '{Path}' under {StoreRoot}.",
            run.Id, repo.NormalizedKey, entry.Path, StoreRoot);
        return new CheckoutLayout(ReviewRoot: StoreRoot, TargetDir: targetDir, StoreRoot: StoreRoot);
    }

    /// <summary>Clones <paramref name="remote"/> into <paramref name="dir"/> unless it is already a work
    /// tree there. A failed clone surfaces (throws) so the stage retries.</summary>
    private static async Task CloneIfMissingAsync(
        GitRunner git, string remote, string dir, ReviewRun run, CancellationToken cancellationToken)
    {
        var probe = await git
            .RunAsync(["-C", dir, "rev-parse", "--is-inside-work-tree"], dir, cancellationToken)
            .ConfigureAwait(false);
        if (probe.Succeeded)
        {
            return;
        }

        var clone = await git
            .RunAsync(["clone", remote, dir], workingDirectory: null, cancellationToken)
            .ConfigureAwait(false);
        if (!clone.Succeeded)
        {
            throw new InvalidOperationException(
                $"Cloning '{remote}' for run {run.Id} failed (exit {clone.ExitCode}): {clone.Stderr}");
        }
    }

    /// <summary>
    /// Fetches the exact base + head commits (a fork/branch commit may not be reachable from the default
    /// fetch) and checks out the PR head (detached) into <paramref name="dir"/> so the review agent's
    /// Read/Grep/Glob and the file manifest reflect the code the PR PROPOSES, not the clone's default branch.
    /// Hooks are neutralized on every GitRunner call, so checking out untrusted PR content is no more
    /// dangerous than the clone that already fetched it.
    /// </summary>
    private static async Task FetchAndCheckoutHeadAsync(
        GitRunner git, string dir, ReviewRun run, CancellationToken cancellationToken)
    {
        var fetch = await git
            .RunAsync(["-C", dir, "fetch", "origin", run.BaseSha, run.HeadSha], dir, cancellationToken)
            .ConfigureAwait(false);
        if (!fetch.Succeeded)
        {
            throw new InvalidOperationException(
                $"Fetching the PR commits for run {run.Id} failed (exit {fetch.ExitCode}): {fetch.Stderr}");
        }

        var checkout = await git
            .RunAsync(["-C", dir, "checkout", "--force", run.HeadSha], dir, cancellationToken)
            .ConfigureAwait(false);
        if (!checkout.Succeeded)
        {
            throw new InvalidOperationException(
                $"Checking out the PR head for run {run.Id} failed (exit {checkout.ExitCode}): {checkout.Stderr}");
        }
    }

    /// <summary>Selectively (and recursively) initializes the allow-listed submodules under
    /// <paramref name="root"/>, logging each refusal, and returns the walk outcome (initialized paths +
    /// refusals). A denied submodule is absent and reported, never a hard failure.</summary>
    private async Task<SubmoduleInitOutcome> InitAllowListedSubmodulesAsync(
        GitRunner git,
        ISandboxFileSystem fileSystem,
        OperationPolicy policy,
        string provider,
        string root,
        GitRemoteUrl rootRemote,
        ReviewRun run,
        CancellationToken cancellationToken)
    {
        var initializer = new SubmoduleInitializer(
            git, fileSystem, policy, provider, _loggerFactory.CreateLogger<SubmoduleInitializer>());
        var outcome = await initializer.InitializeAsync(root, rootRemote, cancellationToken).ConfigureAwait(false);
        foreach (var denied in outcome.Denied)
        {
            _logger.LogWarning(
                "Run {RunId}: submodule '{Path}' ({Url}) was not initialized: {Reason}",
                run.Id, denied.Path, denied.Url, denied.Reason);
        }

        return outcome;
    }

    /// <summary>Whether a store <c>.gitmodules</c> entry's URL points at the reviewed repo (host + owner/name
    /// match, ignoring a trailing <c>.git</c>), so the store submodule can be paired with the run.</summary>
    private static bool SubmoduleTargetsRepo(string submoduleUrl, GitRemoteUrl targetUrl)
    {
        var url = GitRemoteUrl.Parse(submoduleUrl);
        return string.Equals(url.Host, targetUrl.Host, StringComparison.OrdinalIgnoreCase)
            && string.Equals(
                url.RepoPath.TrimEnd('/'), targetUrl.RepoPath.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
    }

    private static string PosixJoin(string root, string relative) => $"{root.TrimEnd('/')}/{relative.Trim('/')}";

    /// <summary>
    /// Lists the target checkout's tracked files (<c>git ls-files</c>) as a newline-joined, bounded
    /// manifest the review agent can consult to Read files by exact path. Best-effort: a failed listing
    /// logs and yields an empty manifest rather than failing the stage — the diff is the essential
    /// artifact and the manifest is only a grounding aid.
    /// </summary>
    private async Task<string> BuildFileManifestAsync(GitRunner git, string targetDir, CancellationToken cancellationToken)
    {
        var lsFiles = await git
            .RunAsync(["-C", targetDir, "ls-files"], targetDir, cancellationToken)
            .ConfigureAwait(false);
        if (!lsFiles.Succeeded)
        {
            _logger.LogWarning(
                "Target file manifest unavailable (git ls-files exit {ExitCode}): {Stderr}",
                lsFiles.ExitCode, lsFiles.Stderr);
            return string.Empty;
        }

        return _options.Limits.CapArtifactPayload(lsFiles.Stdout.Trim());
    }

    private static int ManifestFileCount(string manifest) =>
        string.IsNullOrWhiteSpace(manifest)
            ? 0
            : manifest.Count(c => c == '\n') + 1;

    /// <summary>
    /// Builds the per-run submodule allow-list for the cross-repo <c>AchieveAiReviews</c> store checkout
    /// (Task 16). The reviewed repo itself and the shared, low-sensitivity <c>Contracts/</c> layer are
    /// always permitted; a configured sibling repo (<see cref="CodeReviewDaemonOptions.CrossRepoSiblings"/>)
    /// is added only when <see cref="AllowsCrossRepoCoLocation"/> confirms this run is same-trust-domain
    /// (Task 17, design §6 Risk B) — an untrusted (fork/public/unknown-trust) PR never gets a sibling
    /// co-located beside it, so a prompt-injected agent has nothing extra to read and exfiltrate via the
    /// posted review. Returns empty for the diff-only path, which never walks any submodule.
    /// </summary>
    internal IReadOnlyList<SubmoduleAllowRule> BuildStoreSubmoduleAllowList(ReviewRun run, RepoIdentity repo)
    {
        if (!_options.EnableToolAssistedReview)
        {
            return [];
        }

        // The reviewed repo's own submodule + the shared Contracts layer are always allow-listed. The host
        // and repo-path shape are provider-specific — GitHub is /{owner}/{repo} on github.com, Azure DevOps
        // is /{org}/{project}/_git/{repo} on dev.azure.com — mirroring TargetRemoteUrl so the rule matches the
        // exact URL SubmoduleTargetsRepo resolves.
        var isAdo = string.Equals(repo.Provider, "azure-devops", StringComparison.OrdinalIgnoreCase)
            || string.Equals(repo.Provider, "ado", StringComparison.OrdinalIgnoreCase);
        var host = isAdo ? "dev.azure.com" : "github.com";
        string RepoPath(string name) =>
            isAdo ? $"/{repo.OrgOrOwner}/{repo.Project}/_git/{name}" : $"/{repo.OrgOrOwner}/{name}";

        var rules = new List<SubmoduleAllowRule>
        {
            new(host, RepoPath(repo.RepoName)),
            new(host, RepoPath("Contracts")),
        };

        if (AllowsCrossRepoCoLocation(run, repo))
        {
            foreach (var sibling in _options.CrossRepoSiblings)
            {
                // GitHub siblings are configured as owner/repo (absolute path); ADO siblings resolve under
                // the same org/project as the reviewed repo.
                rules.Add(new SubmoduleAllowRule(host, isAdo ? RepoPath(sibling) : $"/{sibling}"));
            }
        }

        return rules;
    }

    /// <summary>
    /// The confidentiality gate (Task 17, design §6 Risk B): whether a sibling private submodule may be
    /// co-located beside the run's checkout. <c>true</c> only when this run is positively established as
    /// same-trust-domain — the PR head is NOT from a fork AND the target repo is private (same-org
    /// private→private). A fork PR or a public target could carry a prompt-injected diff that reads the
    /// sibling repo and surfaces it in the review the daemon posts, so those get target + Contracts/ only.
    /// Fails closed: <see cref="ReviewRun.IsForkPr"/> and <see cref="ReviewRun.IsTargetRepoPublic"/> both
    /// default to <c>true</c>, so a run whose trust signal was never positively populated is denied
    /// co-location exactly like a confirmed fork/public PR — never a permissive default.
    /// </summary>
    internal bool AllowsCrossRepoCoLocation(ReviewRun run, RepoIdentity repo) => !run.IsForkPr && !run.IsTargetRepoPublic;

    /// <summary>Builds the HTTPS clone URL for the target repo from its identity + provider.</summary>
    private static string TargetRemoteUrl(RepoIdentity repo, string provider) =>
        string.Equals(provider, "ado", StringComparison.Ordinal)
            ? $"https://dev.azure.com/{repo.OrgOrOwner}/{repo.Project}/_git/{repo.RepoName}"
            : $"https://github.com/{repo.OrgOrOwner}/{repo.RepoName}.git";

    private async Task ReviewAsync(ReviewRun run, CancellationToken cancellationToken)
    {
        var (repo, provider) = ResolveRepo(run);
        var context = ReadContext(run.Id);
        var reviewInput = BuildReviewInput(run, repo, context.Diff, context.FileManifest);
        reviewInput = await PrependPriorKnowledgeAsync(reviewInput, context.StoreRoot, cancellationToken)
            .ConfigureAwait(false);

        // Primary review — collected and persisted; never posts here (the Posted stage owns posting).
        await RunPrimaryReviewAsync(run, provider, reviewInput, context.CheckoutRoot, context.StoreRoot, cancellationToken)
            .ConfigureAwait(false);

        if (_options.EnableABVariants)
        {
            await RunVariantArmAsync(run, provider, reviewInput, context.CheckoutRoot, context.StoreRoot, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Builds the review prompt's templated workspace-layout variables (design: prompt migration). The
    /// <c>notes_dir</c>/<c>has_notes</c> pair is derived from <paramref name="toolContext"/> — the SAME
    /// <see cref="ReviewToolContext.NotesDir"/> that scopes the agent's Write/Edit/Bash tools
    /// (<see cref="ResolvePooledWriteScope"/>) — never a parallel recomputation, so the prompt can never
    /// tell the agent to write somewhere its tools don't actually allow.
    /// <para>
    /// The re-review variables (<paramref name="prevHeadSha"/>, <paramref name="reviewRound"/>,
    /// <paramref name="priorNotesFiles"/>) are looked up/listed by the caller (<see cref="RunPrimaryReviewAsync"/>
    /// via <see cref="ComputeRereviewContextAsync"/>) so this builder stays a pure value mapper with no
    /// store/file-system access of its own.
    /// </para>
    /// </summary>
    private static Dictionary<string, object> BuildPromptVariables(
        string botName,
        string? checkoutRoot,
        string? storeRoot,
        ReviewToolContext? toolContext,
        string headSha,
        string? prevHeadSha,
        int reviewRound,
        IReadOnlyList<string> priorNotesFiles)
    {
        var notesDir = toolContext?.NotesDir;
        return new Dictionary<string, object>
        {
            // The daemon prepends "[BotName]" to the POSTED comment; injecting it here too lets the review
            // BODY self-identify with the same name instead of a name the model invents ad-hoc.
            ["bot_name"] = botName,
            ["checkout_root"] = checkoutRoot ?? TargetRoot,
            ["has_store"] = !string.IsNullOrWhiteSpace(storeRoot),
            ["store_root"] = storeRoot ?? string.Empty,
            ["has_notes"] = !string.IsNullOrWhiteSpace(notesDir),
            ["notes_dir"] = notesDir ?? string.Empty,
            ["is_rereview"] = !string.IsNullOrWhiteSpace(prevHeadSha),
            ["prev_commit"] = prevHeadSha ?? string.Empty,
            ["new_commit"] = headSha,
            ["review_round"] = reviewRound.ToString("D2", CultureInfo.InvariantCulture),
            ["has_prior_files"] = priorNotesFiles.Count > 0,
            ["prior_files"] = string.Join('\n', priorNotesFiles),
        };
    }

    /// <summary>
    /// Computes this run's re-review context: the previously-reviewed head (from
    /// <see cref="ReviewStore.GetPriorReviewSummary"/>, PRIMARY-variant completed rounds only), the round
    /// number this review is (<c>prior count + 1</c>), and — when a notes dir is mounted — the reviewer's
    /// own prior <c>PR_Context_*.md</c>/<c>PR_Findings_*.md</c> files so it can read its earlier work
    /// instead of re-collecting context. Shared by <see cref="RunPrimaryReviewAsync"/> and
    /// <see cref="RunVariantArmAsync"/> so both arms are told the same round/commit facts without either
    /// duplicating the store query or the file listing.
    /// </summary>
    private async Task<(string? PrevHeadSha, int ReviewRound, IReadOnlyList<string> PriorNotesFiles)> ComputeRereviewContextAsync(
        ReviewRun run, string? notesDir, CancellationToken cancellationToken)
    {
        var summary = _store.GetPriorReviewSummary(run.RepoId, run.PrId, run.Id);
        var reviewRound = summary.PriorReviewCount + 1;

        if (string.IsNullOrWhiteSpace(notesDir))
        {
            return (summary.PrevHeadSha, reviewRound, []);
        }

        IReadOnlyList<string> entries;
        try
        {
            entries = await _fileSystem.ListFilesAsync(notesDir, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Listing the notes dir goes through the boot-lifetime sandbox session, which can 404/hiccup;
            // design §6 says re-review context must never fail the review, so degrade to no prior files.
            _logger.LogWarning(ex, "Listing prior notes files in '{NotesDir}' failed; proceeding without them.", notesDir);
            return (summary.PrevHeadSha, reviewRound, []);
        }

        IReadOnlyList<string> priorFiles =
        [
            .. entries
                .Where(IsPriorNotesFile)
                .OrderBy(name => name, StringComparer.Ordinal)
                .Select(name => PosixJoin(notesDir, name)),
        ];

        return (summary.PrevHeadSha, reviewRound, priorFiles);
    }

    /// <summary>Matches this run's own <c>PR_Context_NN.md</c>/<c>PR_Findings_NN.md</c> notes files (design:
    /// prompt migration write convention) among a notes dir's listed entries.</summary>
    private static bool IsPriorNotesFile(string name) =>
        (name.StartsWith("PR_Context_", StringComparison.Ordinal) || name.StartsWith("PR_Findings_", StringComparison.Ordinal))
        && name.EndsWith(".md", StringComparison.Ordinal);

    /// <summary>
    /// Best-effort prepends the store's Knowledge Base table of contents to the review input so the review
    /// agent starts with the durable knowledge distilled from past PRs (design §3). Only a cross-repo
    /// store-mode run carries a Knowledge Base — it lives at the store root (<c>&lt;StoreRoot&gt;/KnowledgeBase/</c>),
    /// so the single-repo path (null <paramref name="storeRoot"/>) is unchanged. A missing <c>_toc.md</c> —
    /// the common case before any knowledge has been extracted — silently leaves the input untouched (it must
    /// never fail the review, design §6); the review prompt still directs the agent to consult the KB itself.
    /// </summary>
    private async Task<string> PrependPriorKnowledgeAsync(
        string reviewInput, string? storeRoot, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(storeRoot))
        {
            return reviewInput;
        }

        var tocPath = PosixJoin(storeRoot, "KnowledgeBase/_toc.md");
        string? toc;
        try
        {
            toc = await _fileSystem.ReadFileAsync(tocPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A missing _toc.md returns null (handled below); but the read itself can THROW — e.g. the
            // boot-lifetime sandbox session 404s ("Session not found") or the gateway hiccups. Design §6
            // says the KB prepend must NEVER fail the review, so degrade to "no prior knowledge" and go on.
            _logger.LogWarning(ex, "Reading KnowledgeBase/_toc.md failed; proceeding without prior knowledge.");
            return reviewInput;
        }

        if (string.IsNullOrWhiteSpace(toc))
        {
            return reviewInput;
        }

        _logger.LogInformation("Prepending KnowledgeBase/_toc.md ({Length} chars) to the review input.", toc.Length);
        return $"## Prior knowledge (KnowledgeBase/_toc.md)\n\n{toc}\n\n{reviewInput}";
    }

    private async Task RunPrimaryReviewAsync(
        ReviewRun run,
        string provider,
        string reviewInput,
        string? checkoutRoot,
        string? storeRoot,
        CancellationToken cancellationToken)
    {
        var toolContext = await BuildToolContextAsync(run, cancellationToken).ConfigureAwait(false);
        var (prevHeadSha, reviewRound, priorNotesFiles) = await ComputeRereviewContextAsync(
            run, toolContext?.NotesDir, cancellationToken).ConfigureAwait(false);
        var variables = BuildPromptVariables(
            _options.BotName, checkoutRoot, storeRoot, toolContext, run.HeadSha, prevHeadSha, reviewRound, priorNotesFiles);
        var profile = DaemonAgentFactory.CreateReviewProfile(variables);
        // A tool-assisted review must actually CALL Read/Grep/Glob/Skill to ground its findings in the
        // checkout. At the diff-only "low" effort the model shortcuts to a diff-only answer (and even
        // fabricates a "no files found / couldn't read the repo" caveat) rather than doing the multi-step
        // tool calls, so the tool-assisted path uses the higher ToolAssistedReasoningEffort.
        var effort = toolContext is not null ? _options.ToolAssistedReasoningEffort : null;
        await using var loop = _loopFactory.Create(
            profile, run.ModelId, ThreadId(run, run.VariantId), reasoningEffort: effort, toolContext: toolContext);
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
    }

    private async Task RunVariantArmAsync(
        ReviewRun run,
        string provider,
        string reviewInput,
        string? checkoutRoot,
        string? storeRoot,
        CancellationToken cancellationToken)
    {
        // The comparison arm never gets a tool context (it always runs diff-only), so it has no notes dir
        // and no prior-files listing — but it is still told the same round/commit facts as the primary.
        var (prevHeadSha, reviewRound, _) = await ComputeRereviewContextAsync(run, notesDir: null, cancellationToken)
            .ConfigureAwait(false);
        var variables = BuildPromptVariables(
            _options.BotName, checkoutRoot, storeRoot, toolContext: null, run.HeadSha, prevHeadSha, reviewRound, []);
        var profile = DaemonAgentFactory.CreateVariantProfile(_comparisonVariant, variables);
        await using var loop = _loopFactory.Create(
            profile, _comparisonVariant.ModelId, ThreadId(run, _comparisonVariant.VariantId), _options.VariantReasoningEffort);
        var reviewer = new VariantReviewer(loop, _store, _loggerFactory.CreateLogger<VariantReviewer>());
        _ = await reviewer.ReviewAsync(run.Id, provider, _comparisonVariant, reviewInput, cancellationToken)
            .ConfigureAwait(false);
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

        // A review that produced NO content must post NOTHING. A "_No review content was produced._"
        // placeholder would go through the full idempotency path and leave a marked comment on the provider;
        // the backstop scan (ReviewPoster.FindPostedCommentAsync) later ADOPTS that placeholder and
        // permanently suppresses a real review of the same head_sha — e.g. a re-run on a model that does
        // produce content. So skip both the post and the retention when the review is empty. The run still
        // reaches its terminal stage below (and its review_run row prevents re-review), and the slot/session
        // are still freed, so nothing is leaked or looped.
        var reviewText = ReadReviewText(run.Id);
        var hasContent = !string.IsNullOrWhiteSpace(reviewText);
        if (hasContent)
        {
            // The POSTED comment is prefixed with "[BotName]" so a reader knows the content was authored by
            // the bot on behalf of whichever OAuth app/person's credential actually posted it — the retained
            // artifact (CommitPooledNotesAsync / PublishToReviewBotAsync below) keeps the raw, unprefixed body.
            var postedBody = $"[{_options.BotName}]\n\n{reviewText}";

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
                postedBody,
                LivePostingAuthorized: _options.EnableCommentPosting);

            _ = await poster.PostReviewAsync(request, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            _logger.LogWarning(
                "Run {RunId}: review produced no content; not posting a comment or claiming the head's dedup "
                    + "slot (a placeholder would block a later real review of the same commit).",
                run.Id);
        }

        // Retention (design §4.4, the commit gate) — only when there is content to retain. A run that leased a
        // pooled slot commits its notes onto the slot's store checkout scoped to ONLY the PR notes dir, then
        // returns the slot; every other run uses the host ReviewBot retention checkout. The slot is ALWAYS
        // returned (finally) and the session ALWAYS torn down (below), so an empty review still frees its
        // resources; the atomic TryRemove guards against a double-return.
        if (_slotWorkspace is not null && _leasedReviews.TryRemove(run.Id, out var lease))
        {
            try
            {
                if (hasContent)
                {
                    await CommitPooledNotesAsync(run, repo, provider, reviewText, lease, cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                await _slotWorkspace.Pool.ReturnAsync(lease.Slot, CancellationToken.None).ConfigureAwait(false);
            }
        }
        else if (hasContent)
        {
            // Durably persist the primary review's artifacts to the ReviewBot repo (AC#6, plan §2). This is
            // the only path that writes to the ReviewBot remote; the collect-only B variant never reaches it.
            await PublishToReviewBotAsync(run, repo, provider, reviewText, cancellationToken).ConfigureAwait(false);
        }

        // Terminal-stage cleanup (Task 18, design §7): tear down the run's per-run sandbox session (and
        // its host workspace dir, best-effort) now that the run has reached its terminal stage. The
        // diff-only path never provisioned a session, so there is nothing to consult.
        if (_options.EnableToolAssistedReview && _provisioner is not null)
        {
            await _provisioner.DestroyAsync(run, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The pooled commit gate (design §4.4/§5.4): commits the review body into the PR's persistent notes
    /// dir on the slot's store checkout, staging <b>only</b> <c>PRs/&lt;pr&gt;/…</c> — never the moved code
    /// submodule pointer, never scratch — and pushes the notes branch (kept for later re-reviews; merged or
    /// deleted only by the PR-lifecycle sweep). Records the <c>reviewbot_push</c> outcome in the outbox
    /// exactly like <see cref="PublishToReviewBotAsync"/>: terminal <see cref="OutboxStatus.Posted"/> with
    /// the pushed SHA on success, left non-terminal on <see cref="ReviewBotPublishOutcome.GitSyncFailed"/>.
    /// </summary>
    private async Task CommitPooledNotesAsync(
        ReviewRun run, RepoIdentity repo, string provider, string reviewBody, LeasedReview lease,
        CancellationToken cancellationToken)
    {
        var hostGit = new GitRunner(_slotWorkspace!.HostRunner);
        var manager = new ReviewBranchManager(
            hostGit, _slotWorkspace.HostFileSystem, _loggerFactory.CreateLogger<ReviewBranchManager>());

        // The review file lives directly inside the accumulating per-PR notes dir (design §4.3 D3); only
        // that dir is staged, so nothing the agent wrote elsewhere (code, scratch) can reach the commit.
        var reviewFile = $"{lease.NotesRelPath}/review.md";
        var reqFiles = new[] { new ReviewArtifactFile(reviewFile, reviewBody) };
        var request = BuildNotesRequest(repo, run, reqFiles);

        var result = await manager
            .CommitNotesAsync(lease.Prepared.StoreRoot, request, cancellationToken, stagePaths: [lease.NotesRelPath])
            .ConfigureAwait(false);

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
                "Run {RunId}: pooled notes pushed {Sha} onto branch '{Branch}' (kept for later re-reviews).",
                run.Id, result.PushedSha, result.ReviewBranch);
        }
        else
        {
            _logger.LogWarning(
                "Run {RunId}: pooled notes failed to push; branch '{Branch}' kept for reconcile.",
                run.Id, result.ReviewBranch);
        }
    }

    /// <summary>
    /// Commits the primary review's notes onto its (persistent) review branch for the primary review
    /// when a ReviewBot repo is configured, then records the <c>reviewbot_push</c> outcome in the
    /// outbox: terminal <see cref="OutboxStatus.Posted"/> (carrying the pushed SHA) on success, or left
    /// non-terminal on a <see cref="ReviewBotPublishOutcome.GitSyncFailed"/> so the reconcile path
    /// retries. The review branch is always kept here — it accumulates notes across re-reviews and is
    /// only merged-or-deleted by a later PR-close step, not by this per-review commit. Retention is
    /// skipped (and nothing is pushed) when <c>ReviewBotRepoUrl</c> is unset — the inert default.
    /// </summary>
    private async Task PublishToReviewBotAsync(
        ReviewRun run, RepoIdentity repo, string provider, string reviewBody, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.ReviewBotRepoUrl))
        {
            return;
        }

        // Retention must run against the HOST-side workspace when one is configured (design §6 Risk A) —
        // the push happens with the write credential in the daemon process, never in the read-only sandbox
        // the review agent shares.
        var retention = _hostRetention;
        var git = new GitRunner(retention?.Git ?? _commandRunner);
        var fileSystem = retention?.FileSystem ?? _fileSystem;
        var repoRoot = retention?.RepoRoot ?? RepoRoot;

        // PR #121 H3: clone (or reuse) the configured ReviewBot remote and validate its skeleton before
        // pushing. The daemon must not assume the checkout exists/is well-formed — a missing remote gives
        // a classified clone diagnosis, a malformed skeleton fails fast rather than pushing into a corrupt
        // repo.
        await EnsureReviewBotCheckoutAsync(git, fileSystem, repoRoot, run, cancellationToken).ConfigureAwait(false);

        var manager = new ReviewBranchManager(
            git,
            fileSystem,
            _loggerFactory.CreateLogger<ReviewBranchManager>());

        // Only the PRs/... artifact is supplied explicitly; the manager's `git add -A` still captures any
        // other tracked changes in the checkout.
        var prArtifactPath =
            $"PRs/{ReviewBotRepoManagerSlug(repo)}-{run.PrId}/review.md";
        var request = new ReviewBotPublishRequest(
            repo,
            PrNumber: int.Parse(run.PrId, System.Globalization.CultureInfo.InvariantCulture),
            HeadSha: run.HeadSha,
            DefaultBranch: ReviewBotDefaultBranch,
            Files: [new ReviewArtifactFile(prArtifactPath, reviewBody)]);

        var result = await manager.CommitNotesAsync(repoRoot, request, cancellationToken).ConfigureAwait(false);

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
                "Run {RunId}: ReviewBot notes pushed {Sha} onto review branch '{Branch}' (kept for later re-reviews).",
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
    /// Clones (or reuses) the configured ReviewBot remote into <paramref name="repoRoot"/> and validates its
    /// skeleton before any push (PR #121 H3). A failed clone surfaces a classified diagnosis; a malformed
    /// skeleton fails fast rather than pushing into a corrupt repo. A freshly-cloned empty repo is seeded.
    /// </summary>
    private async Task EnsureReviewBotCheckoutAsync(
        GitRunner git, ISandboxFileSystem fileSystem, string repoRoot, ReviewRun run, CancellationToken cancellationToken)
    {
        var cloneFailure = await ReviewBotCheckout
            .EnsureCheckoutAsync(
                git, _options.ReviewBotRepoUrl!, repoRoot,
                _loggerFactory.CreateLogger("reviewbot-checkout"), cancellationToken)
            .ConfigureAwait(false);
        if (cloneFailure is not null)
        {
            throw new InvalidOperationException(
                $"Run {run.Id}: ReviewBot checkout failed ({cloneFailure.Kind}): {cloneFailure.Message}");
        }

        var initializer = new ReviewBotInitializer(
            git, fileSystem, _loggerFactory.CreateLogger<ReviewBotInitializer>());
        var init = await initializer
            .InitializeAsync(repoRoot, ReviewBotDefaultBranch, cancellationToken)
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

    /// <summary>Slugs the target repo name into a single ReviewBot path segment (mirrors the branch slug).</summary>
    private static string ReviewBotRepoManagerSlug(RepoIdentity repo) => SlugSegment(repo.RepoName);

    private static string SlugSegment(string value)
    {
        var chars = value
            .ToLowerInvariant()
            .Select(static c => char.IsLetterOrDigit(c) || c is '.' or '_' ? c : '-');
        return new string([.. chars]).Trim('-');
    }

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

    private ContextArtifactPayload ReadContext(long reviewRunId) =>
        ReadArtifactPayload<ContextArtifactPayload>(reviewRunId, ContextArtifactKind);

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

    private static string BuildReviewInput(
        ReviewRun run, RepoIdentity repo, string diff, string? fileManifest)
    {
        var input = $"Review pull request {repo.DisplayName}#{run.PrId} (head {run.HeadSha}).\n\nDiff:\n{diff}";
        if (string.IsNullOrWhiteSpace(fileManifest))
        {
            return input;
        }

        // The checkout root / store layout are now templated into the review agent's SYSTEM PROMPT (the
        // "Workspace layout" section, see DaemonAgentFactory.CreateReviewProfile) rather than duplicated
        // here — this only needs to carry the file manifest so the agent can Read files by exact path.
        return input + "\n\nTracked files in the reviewed repository (Read any of these by exact path):\n" + fileManifest;
    }

    private static string ThreadId(ReviewRun run, string variant) => $"review-run-{run.Id}-{variant}";
}

/// <summary>The persisted PR diff/context (kind <c>review-context</c>). <see cref="FileManifest"/> is the
/// newline-joined tracked-file list of the head checkout (bounded), appended so the review agent can Read
/// files by exact path; <see cref="CheckoutRoot"/> is the absolute dir the reviewed repo is checked out in
/// (the manifest paths are relative to it), and <see cref="StoreRoot"/> is the cross-repo store root when the
/// reviewed repo was checked out as a store submodule (else null). All are null/empty on older artifacts.</summary>
internal sealed record ContextArtifactPayload(
    string PrId,
    string BaseSha,
    string HeadSha,
    string Diff,
    string? FileManifest = null,
    string? CheckoutRoot = null,
    string? StoreRoot = null);

/// <summary>The persisted primary review output (kind <c>review</c>).</summary>
internal sealed record ReviewArtifactPayload(string ReviewText, string? RunId, string VariantId);

/// <summary>
/// The host-side pooled-review dependencies (Layer 1), non-null in <see cref="DaemonReviewStageExecutor"/>
/// only when the pooled scoped-writable path is wired in Program.cs; the diff-only and per-run-session
/// paths leave it null and behave exactly as before. <see cref="HostRunner"/>/<see cref="HostFileSystem"/>
/// are the daemon-process (privileged, write-credentialled) git+fs the pooled diff and commit-notes run
/// through — never the sandbox the review agent shares (design §4.7).
/// </summary>
internal sealed record ReviewSlotWorkspace(
    IReviewSlotPool Pool,
    IReviewSlotPreparer Preparer,
    ISandboxCommandRunner HostRunner,
    ISandboxFileSystem HostFileSystem);

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

