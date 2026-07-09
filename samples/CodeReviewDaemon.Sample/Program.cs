using AchieveAi.LmDotnetTools.LmAgentInfra.Auth;
using AchieveAi.LmDotnetTools.LmAgentInfra.Controllers;
using AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using CodeReviewDaemon.Sample.Agents;
using CodeReviewDaemon.Sample.Auth;
using CodeReviewDaemon.Sample.Configuration;
using CodeReviewDaemon.Sample.Hosting;
using CodeReviewDaemon.Sample.Orchestration;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Workspace;
using CodeReviewDaemon.Sample.Workspace.Git;
using CodeReviewDaemon.Sample.Workspace.ReviewBot;
using CodeReviewDaemon.Sample.Workspace.Sandbox;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.Data.Sqlite;

// ── One-time setup subcommand ────────────────────────────────────────────────────────────────────
// `CodeReviewDaemon reviewbot init --url <ReviewBotRepoUrl>` seeds/validates the ReviewBot repo and
// exits (plan §1). This runs BEFORE the web host is built so the long-running daemon and the setup
// path never share a process; the no-arg run used by the route-exposure test host is unaffected.
if (args is ["reviewbot", "init", ..])
{
    return await ReviewBotInitCommand.RunAsync(args);
}

var builder = WebApplication.CreateBuilder(args);

// ── Feature flags ────────────────────────────────────────────────────────────────────────────────
// Conservative defaults (collect-only, GitHub-only, repo allow-list empty); each flag is an explicit
// operator opt-in to a higher-blast-radius behavior. See CodeReviewDaemonOptions.
var daemonOptions =
    builder.Configuration.GetSection(CodeReviewDaemonOptions.SectionName).Get<CodeReviewDaemonOptions>()
    ?? new CodeReviewDaemonOptions();
builder.Services.AddSingleton(daemonOptions);

// ── Sandbox gateway per-app identity (ADR 0029) ─────────────────────────────────────────────────
// The daemon authenticates to the sandbox gateway under its OWN app identity — distinct from
// LmStreaming.Sample's default ("lmstreaming-sample") — so the shared SandboxSessionRegistry's
// default credential (derived from sandboxGatewayOptions.AppId/AppKey below) and the daemon's two
// direct /mcp transports (SandboxOrchestrator, LiveReviewAgentLoopFactory) all stamp the SAME
// X-Sbx-App-Id/X-Sbx-App-Key headers. A present-but-invalid key fails fast at boot (redacted); an
// absent key is the keyless AUTH_ENFORCE=off dev path, logged once as a warning after the host is
// built (never blocking startup, and never logging the key itself).
var daemonAppId = Environment.GetEnvironmentVariable("CRD_SANDBOX_APP_ID") ?? "codereview-daemon";
var daemonAppKey = Environment.GetEnvironmentVariable("CRD_SANDBOX_APP_KEY");
var daemonKeyMissing = string.IsNullOrWhiteSpace(daemonAppKey);
if (!daemonKeyMissing)
{
    SandboxCredential.ValidateKeyOrThrow(daemonAppId, daemonAppKey!);
}
var daemonCredential = new SandboxCredential(daemonAppId, daemonAppKey ?? string.Empty);

// ── OAuth auth-provider services ───────────────────────────────────────────────────────────────
// Shared with LmStreaming.Sample (see its Program.cs): the sandbox gateway calls back into the
// auth webhook to obtain a per-provider bearer/basic credential for an outbound request, and these
// providers mint it. The daemon reviews GitHub PRs by default; Azure DevOps is opt-in via
// CodeReviewDaemon:EnableAdoProvider.
var authOptions =
    builder.Configuration.GetSection(AuthOptions.SectionName).Get<AuthOptions>() ?? new AuthOptions();
builder.Services.AddSingleton(authOptions);
builder.Services.AddSingleton<AuthSharedSecret>();

var oauthTokenDir = string.IsNullOrWhiteSpace(authOptions.TokenStoreDir)
    ? Path.Combine(AppContext.BaseDirectory, "oauth-tokens")
    : authOptions.TokenStoreDir;
builder.Services.AddSingleton<IOAuthTokenStore>(sp => new FileOAuthTokenStore(
    oauthTokenDir,
    sp.GetRequiredService<ILogger<FileOAuthTokenStore>>()));

// Dual-register each provider (concrete + IOAuthTokenProvider alias-to-concrete) so the
// enumerable-consuming callers (AuthWebhookController, OAuthTokenHydrator) and any concrete-typed
// consumer share a single singleton instance per provider.
builder.Services.AddSingleton(sp => new GitHubOAuthProvider(
    authOptions.Github,
    sp.GetRequiredService<IOAuthTokenStore>(),
    new HttpClient(),
    sp.GetRequiredService<ILogger<GitHubOAuthProvider>>()));
builder.Services.AddSingleton<IOAuthTokenProvider>(sp => sp.GetRequiredService<GitHubOAuthProvider>());

// Azure DevOps is opt-in: when EnableAdoProvider is off (default) the provider is never registered,
// so an "ado" webhook call resolves no provider and is denied as unknown — the daemon stays GitHub-only.
if (daemonOptions.EnableAdoProvider)
{
    builder.Services.AddSingleton(sp => new AdoOAuthProvider(
        authOptions.Ado,
        Path.Combine(oauthTokenDir, "msal-ado.bin"),
        sp.GetRequiredService<ILogger<AdoOAuthProvider>>()));
    builder.Services.AddSingleton<IOAuthTokenProvider>(sp => sp.GetRequiredService<AdoOAuthProvider>());
}

// Restore persisted sign-in state at startup so token injection reflects a prior console sign-in.
builder.Services.AddHostedService<OAuthTokenHydrator>();

// Auth-resolution policy. The daemon is unattended, so its notifier only logs the lifecycle (no
// browser to prompt) and its policy fails fast: a not-signed-in webhook call raises an operator
// "auth required" signal and denies immediately, rather than holding the call open for an
// interactive sign-in that no one is present to complete.
builder.Services.AddSingleton<IAuthEventNotifier, DaemonAuthEventNotifier>();
builder.Services.AddSingleton<IAuthResolutionPolicy, FailFastDaemonAuthPolicy>();
// The daemon has no chat sessions/threads to forward to (that's LmStreaming.Sample-only); the
// shared AuthWebhookController still requires an IAuthWebhookForwarder, so wire the no-op.
builder.Services.AddSingleton<IAuthWebhookForwarder, NoOpAuthWebhookForwarder>();

// Gateway callback authentication. The real sandbox gateway authenticates its auth-webhook calls with a
// single shared secret in the `Authorization` header (crates/mcp-gateway/.../proxy_policy/auth_webhook.rs
// sends ONLY `Authorization: {gateway_auth}` — no body signature, timestamp, or delivery id). The shared
// AuthWebhookController already verifies that secret (AuthSharedSecret, constant-time) and injects tokens
// only toward each provider's own hosts. The plan §9 HMAC/timestamp/replay middleware was built for a
// Stripe-style signing gateway this one is NOT: it hard-required X-Sandbox-Signature/Timestamp/Delivery-Id
// and so rejected EVERY real callback as MissingHeaders (proven live — clone 403, "Rejected ... MissingHeaders"),
// breaking all authenticated git (private clone, ReviewBot push). It is therefore not wired; the shared
// secret carried in Authorization is the gateway↔webhook boundary. (WebhookVerification* + DeliveryReplayCache
// are retained unwired for a future signing gateway.)

// ── Orchestration: store, sandbox, agents, providers, poller ─────────────────────────────────────
// The daemon's orchestration source of truth (plan §6–§14). The store migrates SQLite at construction,
// so the path is test-isolated via CodeReviewDaemon:DatabasePath; the default lives beside the binary.
var databasePath = string.IsNullOrWhiteSpace(daemonOptions.DatabasePath)
    ? Path.Combine(AppContext.BaseDirectory, "review.db")
    : daemonOptions.DatabasePath;
var dbConnectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();
// Singleton: ReviewStore wraps one SqliteConnection. Its single accessor is the serial PrPollingService
// loop (each PR is orchestrated to completion before the next), so concurrent use never arises today.
// Any future fan-out (parallel arms, a second poller) MUST serialize access before sharing this store.
builder.Services.AddSingleton(_ => new ReviewStore(dbConnectionString));

// Sandbox: all deterministic git/fs work runs in the gateway, driven as a direct MCP client. The
// connection is lazy (opened on first command), so registering it does no work at boot and the daemon
// stays inert until a repo is allow-listed. The gateway base URL / session come from the environment.
// Per-app bearer identity for the sandbox gateway (ADR 0029): sent as X-Sbx-App-Id/X-Sbx-App-Key on every
// gateway request when a key is configured, so an AUTH_ENFORCE gateway authenticates the daemon and scopes
// its sessions to it. No key configured → no bearer headers (works unchanged against an unenforced gateway).
builder.Services.AddSingleton<ISandboxCommandRunner>(sp => new SandboxOrchestrator(
    Environment.GetEnvironmentVariable("CRD_SANDBOX_GATEWAY") ?? "http://127.0.0.1:8080",
    Environment.GetEnvironmentVariable("CRD_SANDBOX_SESSION") ?? Guid.NewGuid().ToString("N"),
    sp.GetRequiredService<ILogger<SandboxOrchestrator>>(),
    daemonCredential,
    daemonOptions.Limits));
builder.Services.AddSingleton<ISandboxFileSystem>(sp =>
    new SandboxFileSystem(sp.GetRequiredService<ISandboxCommandRunner>()));

// Per-run session provisioning (tool-assisted path, Task 7). The diff-only path above talks to the
// gateway directly via a boot-lifetime SandboxOrchestrator; EnableToolAssistedReview instead provisions
// one sandbox session per run (design §4) so the checkout git and the review agent's MCP tools share a
// container. The registry needs a SandboxGatewayLifetime purely to resolve/probe the gateway base URL —
// it is not registered as a hosted service (AutoSpawn stays false, mirroring the SandboxOrchestrator
// registration above: the daemon assumes an already-running gateway and never spawns one itself).
var sandboxGatewayOptions = new SandboxGatewayOptions
{
    BaseUrl = Environment.GetEnvironmentVariable("CRD_SANDBOX_GATEWAY") ?? "http://127.0.0.1:3000",
    AutoSpawn = false,
    Marketplaces = string.Join(",", daemonOptions.Marketplaces),
    // Host base directory the (already-running) gateway maps to its container WORKSPACE_BASE_PATH. The
    // per-run provisioner mounts a distinct leaf (review-run-{id}) under this base, so it MUST be set or
    // session-create fails with "no workspace base path is configured". Sourced from env/config so it
    // tracks whatever the adopted gateway actually mounts (e.g. B:/sandbox-workspaces/workspaces).
    WorkspaceBasePath = Environment.GetEnvironmentVariable("CRD_WORKSPACE_BASE_PATH")
        ?? builder.Configuration["SandboxGateway:WorkspaceBasePath"],
    // Per-app bearer identity (ADR 0029) — the daemon's own identity, distinct from LmStreaming.Sample so
    // its sandbox sessions are scoped to their own app tree under an AUTH_ENFORCE gateway, and so the
    // registry's default credential (used to stamp its own REST/MCP calls) matches the two direct /mcp
    // transports rather than defaulting to "lmstreaming-sample".
    AppId = daemonAppId,
    AppKey = daemonKeyMissing ? null : daemonAppKey,
};
builder.Services.AddSingleton(sp => new SandboxSessionRegistry(
    new SandboxGatewayLifetime(
        sandboxGatewayOptions,
        sp.GetRequiredService<ILogger<SandboxGatewayLifetime>>(),
        new HttpClient(new GatewayAuthHandler(daemonAppId, daemonKeyMissing ? null : daemonAppKey) { InnerHandler = new HttpClientHandler() })),
    sandboxGatewayOptions,
    sp.GetRequiredService<ILogger<SandboxSessionRegistry>>(),
    // Bounds the gateway create/destroy calls (mirrors LmStreaming.Sample's registration); the handler
    // attaches the per-app bearer headers to every gateway REST call.
    new HttpClient(new GatewayAuthHandler(daemonAppId, daemonKeyMissing ? null : daemonAppKey) { InnerHandler = new HttpClientHandler() })
    {
        Timeout = TimeSpan.FromSeconds(30),
    },
    authOptions,
    sp.GetRequiredService<AuthSharedSecret>()));
builder.Services.AddSingleton<ISandboxSessionSource>(sp =>
    new RegistrySessionSource(sp.GetRequiredService<SandboxSessionRegistry>()));
builder.Services.AddSingleton<IReviewSessionProvisioner>(sp => new ReviewSessionProvisioner(
    sp.GetRequiredService<ISandboxSessionSource>(),
    daemonOptions,
    sp.GetRequiredService<ILoggerFactory>(),
    daemonCredential,
    // The gateway's host workspace base: a pooled run mounts its leased slot at /workspace by expressing
    // the slot's host path relative to this base (ReviewSessionProvisioner.GetOrCreateForSlotAsync). The
    // pool root is defaulted under it below so the slot always resolves to a path inside the base.
    sandboxGatewayOptions.WorkspaceBasePath));

// Sub-agent discovery (Task 12): the executor asks for `code-reviewer:*` sub-agents through the same
// narrow-adapter pattern as ISandboxSessionSource above, so it never depends on the registry's full
// surface directly. DiscoveredSubAgentTemplateBuilder is pure (no gateway calls), so it is registered
// as-is.
builder.Services.AddSingleton<IDiscoveredItemsSource>(sp =>
    new RegistryDiscoverySource(sp.GetRequiredService<SandboxSessionRegistry>()));
builder.Services.AddSingleton<DiscoveredSubAgentTemplateBuilder>();

// The live agent loop (OpenAI-compatible MultiTurnAgentLoop). Dead-by-default + lazy per run. Registered
// by its concrete type too (rather than only the IReviewAgentLoopFactory interface) so the executor can
// also resolve its SharedAgentFactory (Task 12) — the same Copilot-backed agent every review loop and any
// discovered sub-agent are driven by — without standing up a second provider agent.
builder.Services.AddSingleton<LiveReviewAgentLoopFactory>();
builder.Services.AddSingleton<IReviewAgentLoopFactory>(sp => sp.GetRequiredService<LiveReviewAgentLoopFactory>());
builder.Services.AddSingleton<Func<IStreamingAgent>>(sp =>
    sp.GetRequiredService<LiveReviewAgentLoopFactory>().SharedAgentFactory);

// PR read providers + comment publishers. GitHub is always registered; ADO is opt-in (mirrors the
// OAuth provider registration above). Each resolves the matching concrete OAuth provider for its token.
// Their HttpClient flows through the OperationPolicyHandler (plan §4 / PR #121 H2): every outbound
// provider-API call is classified into a SandboxOperation and validated against the per-run policy of
// each ALLOW-LISTED repo (host + method + repo route), and a denied op is both egress-blocked AND
// credential-withheld — so reviewing untrusted PR code can never coax the daemon into an off-repo,
// off-scope, or wrong-method request.
builder.Services.AddSingleton<PolicyEnforcedHttpClientFactory>();
builder.Services.AddSingleton<IPrProvider>(sp => new GitHubPrProvider(
    sp.GetRequiredService<PolicyEnforcedHttpClientFactory>().Create("github"),
    sp.GetRequiredService<GitHubOAuthProvider>(),
    sp.GetRequiredService<ILogger<GitHubPrProvider>>()));
builder.Services.AddSingleton<IReviewCommentPublisher>(sp => new GitHubReviewCommentPublisher(
    sp.GetRequiredService<PolicyEnforcedHttpClientFactory>().Create("github"),
    sp.GetRequiredService<GitHubOAuthProvider>(),
    sp.GetRequiredService<ILogger<GitHubReviewCommentPublisher>>()));

if (daemonOptions.EnableAdoProvider)
{
    builder.Services.AddSingleton<IPrProvider>(sp => new AdoPrProvider(
        sp.GetRequiredService<PolicyEnforcedHttpClientFactory>().Create("ado"),
        sp.GetRequiredService<AdoOAuthProvider>(),
        sp.GetRequiredService<ILogger<AdoPrProvider>>()));
    builder.Services.AddSingleton<IReviewCommentPublisher>(sp => new AdoReviewCommentPublisher(
        sp.GetRequiredService<PolicyEnforcedHttpClientFactory>().Create("ado"),
        sp.GetRequiredService<AdoOAuthProvider>(),
        sp.GetRequiredService<ILogger<AdoReviewCommentPublisher>>()));
}

// HOST-side retention workspace (Task 15, design §6 Risk A): the ReviewBot retention push and the KB
// entry it carries must run OUTSIDE the sandbox the untrusted review agent shares, with the write
// credential injected only into this host-process git runner. Registered only when a ReviewBot repo is
// configured — otherwise DaemonReviewStageExecutor's null-fallback keeps writing through the sandbox
// runner exactly as it does today. This iteration reuses the single existing GitHub credential (a
// dedicated write-scoped credential is a documented fast-follow, not introduced here).
if (!string.IsNullOrWhiteSpace(daemonOptions.ReviewBotRepoUrl))
{
    builder.Services.AddSingleton(sp =>
    {
        var hostRoot = string.IsNullOrWhiteSpace(daemonOptions.WorkspaceHostRoot)
            ? Path.Combine(AppContext.BaseDirectory, "workspaces")
            : daemonOptions.WorkspaceHostRoot;
        var github = sp.GetRequiredService<GitHubOAuthProvider>();
        var runner = new HostGitCommandRunner(
            async ct => (await github.GetAccessTokenAsync(ct: ct).ConfigureAwait(false)).Value,
            sp.GetRequiredService<ILogger<HostGitCommandRunner>>());
        return new HostRetentionWorkspace(runner, new HostFileSystem(), Path.Combine(hostRoot, "reviewbot"));
    });
}

// ── Pooled scoped-writable review workspace + PR-lifecycle sweep (Layer 1) ─────────────────────────
// Wired only when the pooled path is enabled (tool-assisted + reviewer-writes) AND a store is resolved.
// Otherwise DaemonReviewStageExecutor's null-fallback keeps the per-run/diff-only checkout and no sweeper
// runs. The pool, preparer, and sweeper share ONE host-side git runner (privileged, with the write
// credential) — never the sandbox the untrusted review agent shares (design §4.7).
if (daemonOptions.EnableToolAssistedReview
    && daemonOptions.EnableReviewerWrites
    && !string.IsNullOrWhiteSpace(daemonOptions.ResolvedStoreUrl))
{
    string storeUrl = daemonOptions.ResolvedStoreUrl;
    // The pool root MUST sit under the gateway's WorkspaceBasePath so a leased slot can be mounted at
    // /workspace (ReviewSessionProvisioner.GetOrCreateForSlotAsync expresses the slot relative to that
    // base). An explicit ReviewPoolHostRoot override wins; otherwise default under WorkspaceBasePath when
    // it is configured, falling back to a dir beside the binary only when no base path is set (the slot
    // mount then degrades to the per-run mount, which is still correct — just not slot-backed).
    var poolRoot = !string.IsNullOrWhiteSpace(daemonOptions.ReviewPoolHostRoot)
        ? daemonOptions.ReviewPoolHostRoot
        : !string.IsNullOrWhiteSpace(sandboxGatewayOptions.WorkspaceBasePath)
            ? Path.Combine(sandboxGatewayOptions.WorkspaceBasePath, "review-pool")
            : Path.Combine(AppContext.BaseDirectory, "review-pool");

    builder.Services.AddSingleton(sp =>
    {
        var github = sp.GetRequiredService<GitHubOAuthProvider>();
        var hostRunner = new HostGitCommandRunner(
            async ct => (await github.GetAccessTokenAsync(ct: ct).ConfigureAwait(false)).Value,
            sp.GetRequiredService<ILogger<HostGitCommandRunner>>());
        var hostFileSystem = new HostFileSystem();
        var loggerFactory = sp.GetRequiredService<ILoggerFactory>();

        var pool = new ReviewSlotPool(
            daemonOptions.ReviewPoolSize,
            poolRoot,
            daemonOptions.ScratchDirName,
            async (slot, ct) =>
            {
                var clone = await new GitRunner(hostRunner)
                    .RunAsync(["clone", storeUrl, slot.StorePath], workingDirectory: null, ct)
                    .ConfigureAwait(false);
                if (!clone.Succeeded)
                {
                    throw new InvalidOperationException(
                        $"Cloning the review store into slot {slot.Index} failed (exit {clone.ExitCode}): {clone.Stderr}");
                }
            },
            loggerFactory.CreateLogger<ReviewSlotPool>());

        // The store is a GitHub superproject (AchieveAiReviews); its submodule URLs resolve under "github".
        var preparer = new ReviewSlotPreparer(new GitRunner(hostRunner), hostFileSystem, "github", loggerFactory);
        return new ReviewSlotWorkspace(pool, preparer, hostRunner, hostFileSystem);
    });

    builder.Services.AddSingleton(sp =>
    {
        var slots = sp.GetRequiredService<ReviewSlotWorkspace>();
        var store = sp.GetRequiredService<ReviewStore>();
        var providers = sp.GetServices<IPrProvider>().ToList();
        var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
        var hostGit = new GitRunner(slots.HostRunner);
        var sweeperRepoRoot = Path.Combine(poolRoot, "sweeper-store");
        var branchManager = new ReviewBranchManager(
            hostGit, slots.HostFileSystem, loggerFactory.CreateLogger<ReviewBranchManager>());
        var sweepLogger = loggerFactory.CreateLogger("pr-lifecycle-sweep");
        // The configured repos (full identity + provider) let the sweep resolve an orphaned review/* branch —
        // whose new-scheme name carries only the repo slug + PR number — back to a pollable PR.
        var sweepPollTargets = PrPollTargetBuilder.Build(daemonOptions, sweepLogger);

        // At-close knowledge extraction (Layer-2, design §1). Wired only when EnableKnowledgeAgent is set:
        // on a merged PR, read the PR's accumulated notes off its notes branch, run the gated extraction
        // over the host store checkout, and let the sweeper's subsequent MergeToDefaultAsync carry the
        // new/updated Knowledge Base entry into the default branch. Unset → null → the sweep is unchanged.
        var loopFactory = sp.GetRequiredService<IReviewAgentLoopFactory>();
        Func<ReviewedPr, CancellationToken, Task>? extractKnowledgeAsync = null;
        if (daemonOptions.EnableKnowledgeAgent)
        {
            // The committer wraps the gated extraction with the git plumbing that carries its write into the
            // default branch: check the notes branch out, run extraction, and — only when it wrote an entry —
            // commit + push KnowledgeBase/ onto that branch so MergeToDefaultAsync fast-forwards it into main.
            var committer = new KnowledgeExtractionCommitter(
                hostGit, sweeperRepoRoot, loggerFactory.CreateLogger<KnowledgeExtractionCommitter>());
            extractKnowledgeAsync = (pr, ct) =>
            {
                // sourcePrRef is a stable, human-readable id for the source PR; todayUtc is daemon-supplied
                // (deterministic — never the model) and stamped into the entry's `updated` frontmatter.
                var sourcePrRef = $"{pr.Provider}/{pr.Repo.NormalizedKey}/{pr.PrId}";
                return committer.RunAsync(pr.Branch, sourcePrRef, async innerCt =>
                {
                    var notesInput = await ReadPrNotesFromBranchAsync(hostGit, sweeperRepoRoot, pr.Branch, innerCt)
                        .ConfigureAwait(false);
                    var profile = DaemonAgentFactory.CreateKnowledgeExtractionProfile();
                    await using var loop = loopFactory.Create(
                        profile, modelId: null, threadId: $"knowledge-extract-{pr.Provider}-{pr.PrId}");
                    var agent = new KnowledgeAgent(
                        loop, slots.HostFileSystem, loggerFactory.CreateLogger<KnowledgeAgent>());
                    var todayUtc = DateTime.UtcNow.ToString(
                        "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
                    return await agent.TryExtractAsync(sweeperRepoRoot, notesInput, sourcePrRef, todayUtc, innerCt)
                        .ConfigureAwait(false);
                }, ct);
            };
        }

        // Lists the store's persistent review/* branches straight from origin (fresh each sweep) so orphaned
        // notes branches are reconciled regardless of this daemon's DB state. A failure degrades to the DB set.
        static async Task<IReadOnlyList<string>> ListRemoteReviewBranchesAsync(
            GitRunner git, string repoRoot, ILogger logger, CancellationToken cancellationToken)
        {
            var result = await git
                .RunAsync(["-C", repoRoot, "ls-remote", "--heads", "origin", "review/*"], repoRoot, cancellationToken)
                .ConfigureAwait(false);
            if (!result.Succeeded)
            {
                logger.LogWarning(
                    "PR-lifecycle sweep: listing review/* branches failed (exit {Exit}): {Err}; sweeping the DB set only.",
                    result.ExitCode, result.Stderr);
                return [];
            }

            const string headsPrefix = "refs/heads/";
            var branches = new List<string>();
            foreach (var line in result.Stdout.Split(
                '\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var tab = line.IndexOf('\t');
                var refName = tab >= 0 ? line[(tab + 1)..] : line;
                if (refName.StartsWith(headsPrefix, StringComparison.Ordinal))
                {
                    branches.Add(refName[headsPrefix.Length..]);
                }
            }

            return branches;
        }

        return new PrLifecycleSweeper(
            async ct =>
            {
                // Ensure a host store checkout exists to merge/delete branches in; a clone failure logs and
                // skips this sweep (retried next cycle) rather than aborting.
                var cloneFailure = await ReviewBotCheckout
                    .EnsureCheckoutAsync(hostGit, storeUrl, sweeperRepoRoot, sweepLogger, ct)
                    .ConfigureAwait(false);
                if (cloneFailure is not null)
                {
                    sweepLogger.LogWarning(
                        "PR-lifecycle sweep skipped: store checkout unavailable ({Kind}): {Message}",
                        cloneFailure.Kind, cloneFailure.Message);
                    return [];
                }

                var rows = await store.ListReviewedPrsAsync(ct).ConfigureAwait(false);
                IReadOnlyList<ReviewedPr> reviewed =
                    [.. rows.Select(PrLifecycleSweepSeam.MapReviewedPr).Where(pr => pr is not null).Select(pr => pr!)];

                // Reconcile against the store's actual review/* branches so a PR whose review row is absent
                // from this daemon's DB (fresh DB / churn) still has its notes branch resolved when it closes.
                var reviewBranches = await ListRemoteReviewBranchesAsync(hostGit, sweeperRepoRoot, sweepLogger, ct)
                    .ConfigureAwait(false);
                return OrphanBranchReconciler.Reconcile(reviewed, reviewBranches, sweepPollTargets, sweepLogger);
            },
            (pr, ct) => PrLifecycleSweepSeam.ResolveLifecycleAsync(providers, pr, ct),
            branchManager,
            sweeperRepoRoot,
            "main",
            daemonOptions.MergeNotesBranchOnClose,
            loggerFactory.CreateLogger<PrLifecycleSweeper>(),
            extractKnowledgeAsync);
    });

    // Reads a merged PR's accumulated review notes off its persistent notes branch (they live on
    // origin/<branch>, not yet on the sweeper checkout's default branch) and assembles them as the
    // knowledge-extraction input. The notes dir mirrors the branch slug (review/<p>/<slug>/<pr> ->
    // PRs/<p>/<slug>/<pr>). Best-effort: an unreadable/absent notes tree yields a short placeholder rather
    // than throwing — extraction must never block the lifecycle (design §6).
    static async Task<string> ReadPrNotesFromBranchAsync(
        GitRunner git, string repoRoot, string branch, CancellationToken ct)
    {
        _ = await git.RunAsync(["-C", repoRoot, "fetch", "origin"], repoRoot, ct).ConfigureAwait(false);

        var remoteRef = $"origin/{branch}";
        var notesRelPath = branch.StartsWith("review/", StringComparison.Ordinal)
            ? "PRs/" + branch["review/".Length..]
            : branch;

        var listed = await git
            .RunAsync(["-C", repoRoot, "ls-tree", "-r", "--name-only", remoteRef, "--", notesRelPath], repoRoot, ct)
            .ConfigureAwait(false);
        if (!listed.Succeeded || string.IsNullOrWhiteSpace(listed.Stdout))
        {
            return $"(no accumulated notes found under {notesRelPath})";
        }

        var builder = new System.Text.StringBuilder();
        foreach (var file in listed.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var show = await git.RunAsync(["-C", repoRoot, "show", $"{remoteRef}:{file}"], repoRoot, ct)
                .ConfigureAwait(false);
            if (show.Succeeded)
            {
                _ = builder.Append("## ").Append(file).Append('\n').Append(show.Stdout).Append("\n\n");
            }
        }

        var assembled = builder.ToString().Trim();
        return assembled.Length == 0 ? $"(no readable notes under {notesRelPath})" : assembled;
    }
}

// The stage executor (consumer of the four agent/posting flags) and the orchestrator that sequences it.
// SandboxCredential is a value type, so it cannot be a DI singleton; pass the daemon identity explicitly
// via ActivatorUtilities (its ctor param is the trailing, type-matched arg) while the rest resolves from DI.
builder.Services.AddSingleton<IReviewStageExecutor>(sp =>
    ActivatorUtilities.CreateInstance<DaemonReviewStageExecutor>(sp, daemonCredential));
builder.Services.AddSingleton<PrOrchestrator>();
// The PR-watching loop. Registering a BackgroundService adds NO route, so the host's mapped routes stay
// exactly the one webhook below. With the allow-list empty (default) it has no targets and is inert.
builder.Services.AddHostedService(sp => new PrPollingService(
    PrPollTargetBuilder.Build(daemonOptions, sp.GetRequiredService<ILogger<PrPollingService>>()),
    sp.GetServices<IPrProvider>(),
    sp.GetRequiredService<ReviewStore>(),
    sp.GetRequiredService<PrOrchestrator>(),
    sp.GetRequiredService<ILogger<PrPollingService>>(),
    // The PR-lifecycle sweep runs on the poller cadence when the pooled path registered a sweeper; the
    // GetService is null otherwise, so the poller keeps polling with no sweep (design §4.5).
    sweepAsync: sp.GetService<PrLifecycleSweeper>() is { } sweeper ? sweeper.SweepAsync : null));

// ── HTTP surface ───────────────────────────────────────────────────────────────────────────────
// The daemon exposes exactly ONE route: POST /api/auth/webhook/{provider} (the gateway's post-auth
// callback). MVC discovery is restricted to AuthWebhookController so no other route can leak in.
builder.Services
    .AddControllers()
    .ConfigureApplicationPartManager(apm =>
    {
        // AuthWebhookController lives in LmAgentInfra, a referenced library — not auto-discovered as
        // an application part — so add it explicitly, then filter discovery to that one controller.
        apm.ApplicationParts.Add(new AssemblyPart(typeof(AuthWebhookController).Assembly));
        foreach (var existing in apm.FeatureProviders.OfType<Microsoft.AspNetCore.Mvc.Controllers.ControllerFeatureProvider>().ToList())
        {
            _ = apm.FeatureProviders.Remove(existing);
        }
        apm.FeatureProviders.Add(new WebhookOnlyControllerFeatureProvider());
    });

var app = builder.Build();

// One-time, non-blocking notice for the keyless dev path (see the per-app identity block above) — never
// logs the key itself, since none was configured.
if (daemonKeyMissing)
{
    app.Logger.LogWarning(
        "CRD_SANDBOX_APP_KEY is not set; connecting to the sandbox gateway as app '{AppId}' with no key "
            + "(keyless AUTH_ENFORCE=off dev path). Set CRD_SANDBOX_APP_KEY for a gateway that enforces auth.",
        daemonAppId);
}

// The gateway↔webhook boundary is the shared secret the shared AuthWebhookController verifies (see the
// gateway-callback note above). The plan §9 HMAC middleware is intentionally NOT wired — the real gateway
// does not sign its callbacks, so requiring a signature rejected every real callback.
app.MapControllers();

app.Run();

return 0;

/// <summary>Exposed for the route-exposure test host (WebApplicationFactory&lt;Program&gt;).</summary>
public partial class Program;
