using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AchieveAi.LmDotnetTools.LmAgentInfra.Auth;
using AchieveAi.LmDotnetTools.LmAgentInfra.Controllers;
using AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using AchieveAi.LmDotnetTools.LmWorkflow.Persistence;
using AchieveAi.LmDotnetTools.LmWorkflow.Runtime;
using CodeReviewDaemon.Sample.Agents;
using CodeReviewDaemon.Sample.Auth;
using CodeReviewDaemon.Sample.Configuration;
using CodeReviewDaemon.Sample.Eval;
using CodeReviewDaemon.Sample.Hosting;
using CodeReviewDaemon.Sample.Orchestration;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Workspace;
using CodeReviewDaemon.Sample.Workspace.Git;
using CodeReviewDaemon.Sample.Workspace.ReviewBot;
using CodeReviewDaemon.Sample.Workspace.Sandbox;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.Data.Sqlite;

// The untouched process argv, captured before any parsing/stripping below mutates `args` — used only for
// the `--setup-workspace` command's startup log line (plan §5: log sanitized argv). Nothing on this CLI
// surface (`--review`, `--days`, `--max-pr-age-days`, `--workflow-operation`, `--setup-workspace`) carries a
// secret value, so the original argv already IS the sanitized form; nothing here would ever be redacted.
var processArgv = args;

// ── One-time setup subcommand ────────────────────────────────────────────────────────────────────
// `CodeReviewDaemon reviewbot init --url <ReviewBotRepoUrl>` seeds/validates the ReviewBot repo and
// exits (plan §1). This runs BEFORE the web host is built so the long-running daemon and the setup
// path never share a process; the no-arg run used by the route-exposure test host is unaffected.
if (args is ["reviewbot", "init", ..])
{
    return await ReviewBotInitCommand.RunAsync(args);
}

// ── Config profile selection ─────────────────────────────────────────────────────────────────────
// `--review <name>` selects the hosting environment so ASP.NET layers `appsettings.<name>.json` over
// the base config — e.g. `--review mcqdb` loads appsettings.mcqdb.json (ADO daemon), `--review
// achieveai` loads appsettings.achieveai.json (GitHub daemon). This is the single operator knob:
// every setting (repo/store/paths/ports/gateway) lives in that one profile file, so no launch env
// vars are required. Absent the flag, the environment resolves as usual (DOTNET_ENVIRONMENT/default).
// Extract profile/recency options first so a scoped command can compose with `--review <profile>`.
var (reviewProfile, maxPrAgeDaysOverride, hostArgs) = ReviewProfileArgs.Extract(args);

string? workflowOperation = null;
var listCandidatePrs = false;
(string RepoKey, string PrId, string HeadSha, string BaseSha)? runPrRequest = null;
int? runPrConcurrency = null;
long? retainReviewNotesRunId = null;
(string RepoKey, string PrId)? redoArtifactBranchRequest = null;
var scopedCommand = ScopedCommandLine.Parse(hostArgs);
if (scopedCommand.Kind == ScopedCommandKind.Malformed)
{
    // Never fall through to ReviewProfileArgs.Extract/normal daemon startup on a fat-fingered scoped
    // command — that would silently hand the daemon an unrelated "review args" list and boot the poller
    // instead of telling the operator they mistyped `--run-pr`'s four arguments (security review round 1).
    Console.Error.WriteLine(scopedCommand.Error);
    return 64; // EX_USAGE
}
if (scopedCommand.Kind == ScopedCommandKind.WorkflowOperation)
{
    workflowOperation = scopedCommand.WorkflowOperation;
    hostArgs = [];
}
else if (scopedCommand.Kind == ScopedCommandKind.ListCandidatePrs)
{
    // Task #81, Command A — a bounded, read-only candidate listing over the configured allow-list.
    // Never creates a run, never touches a cursor: see ListCandidatePrsCommand.
    listCandidatePrs = true;
    hostArgs = [];
}
else if (scopedCommand.Kind == ScopedCommandKind.RunPr)
{
    // Task #81, Command B — an operator-approved, exact one-PR run. See RunSinglePrCommand.
    runPrRequest = (
        scopedCommand.RunPrRepoKey!,
        scopedCommand.RunPrId!,
        scopedCommand.RunPrHeadSha!,
        scopedCommand.RunPrBaseSha!
    );
    hostArgs = [];
}
else if (scopedCommand.Kind == ScopedCommandKind.RunPrStream)
{
    runPrConcurrency = scopedCommand.RunPrConcurrency;
    hostArgs = [];
}
else if (scopedCommand.Kind == ScopedCommandKind.ResetReviewRun)
{
    hostArgs = [];
}
else if (scopedCommand.Kind == ScopedCommandKind.RetainReviewNotes)
{
    retainReviewNotesRunId = scopedCommand.NotesRunId;
    hostArgs = [];
}
else if (scopedCommand.Kind == ScopedCommandKind.RedoArtifactBranch)
{
    // Task #82, requirement 4 — the explicit, operator-only redo. Never scheduled, never reached from the
    // review flow. Recognized by the SAME parser as the other scoped commands so a fat-fingered
    // `--redo-artifact-branch` is a usage error rather than a silent fall-through into daemon startup.
    redoArtifactBranchRequest = (scopedCommand.RedoRepoKey!, scopedCommand.RedoPrId!);
    hostArgs = [];
}
var isScopedCommand =
    workflowOperation is not null
    || listCandidatePrs
    || runPrRequest is not null
    || runPrConcurrency is not null
    || retainReviewNotesRunId is not null
    || scopedCommand.Kind == ScopedCommandKind.ResetReviewRun
    || redoArtifactBranchRequest is not null;

// Task #82, requirement 1 — the review-artifact branch push capability. Denied here, and there is no flag
// that changes that: the grant is minted by calling ReviewArtifactBranchCapability.Grant from the one-shot
// single-PR run command (task #81's --run-pr), using the repo/PR/head that command has just re-read and
// validated for the SECOND time, immediately before it admits the run. Every other entry point into this
// file — normal daemon startup, --list-candidate-prs, --workflow-operation, --redo-artifact-branch — leaves
// it at Denied, so retention refuses before it touches git on all of them.
ReviewArtifactBranchCapability ArtifactCapability() => ReviewArtifactBranchCapability.Current;

// `--setup-workspace` bootstraps every configured logical review slot through the SAME Gateway/session
// wiring a real review uses, then keeps serving (plan §5, Step 0) — a bare flag, composable with
// `--review <profile>`, so it is stripped here before profile-flag extraction rather than consuming args.
// It is NOT a ScopedCommandLine command: it composes with `--review <profile>` and it keeps serving, so it
// is stripped from args rather than claiming the whole argv the way the four one-shot commands do.
var setupWorkspaceRequested = args.Contains("--setup-workspace");
if (setupWorkspaceRequested)
{
    args = [.. args.Where(a => a != "--setup-workspace")];
}

var builder = WebApplication.CreateBuilder(
    new WebApplicationOptions
    {
        Args = hostArgs,
        EnvironmentName = reviewProfile, // null ⇒ default environment resolution (base appsettings only)
        ContentRootPath = isScopedCommand ? AppContext.BaseDirectory : null,
    }
);
if (isScopedCommand)
{
    // A scoped operation owns stdout exclusively; diagnostic logs stay on stderr.
    builder.Logging.ClearProviders();
    builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
}

// A `--days N` / `--max-pr-age-days N` flag overrides the profile's CodeReviewDaemon:MaxPrAgeDays recency
// bound for this run. Injected as the last (highest-precedence) config source so it wins over appsettings,
// and BEFORE the section is bound below.
if (maxPrAgeDaysOverride is int maxPrAgeDaysFlag)
{
    builder.Configuration.AddInMemoryCollection(
        new Dictionary<string, string?>
        {
            [$"{CodeReviewDaemonOptions.SectionName}:{nameof(CodeReviewDaemonOptions.MaxPrAgeDays)}"] =
                maxPrAgeDaysFlag.ToString(System.Globalization.CultureInfo.InvariantCulture),
        }
    );
}

// ── Feature flags ────────────────────────────────────────────────────────────────────────────────
// Conservative defaults (collect-only, GitHub-only, repo allow-list empty); each flag is an explicit
// operator opt-in to a higher-blast-radius behavior. See CodeReviewDaemonOptions.
var daemonOptions = CodeReviewDaemonOptions.FromConfiguration(
    builder.Configuration.GetSection(CodeReviewDaemonOptions.SectionName)
);

// Reject invalid workspace environment keys before any hosted conversation can be provisioned. Validation
// reports key names only; values are never logged or surfaced.
SandboxEnvRules.Validate(daemonOptions.WorkspaceEnv, "CodeReviewDaemon:WorkspaceEnv");

// Refuse startup on a malformed EnabledRepos entry, naming it — encoding the segments consistently (issue
// #485) is not the same as validating them, and a bad entry otherwise polls the wrong repo or nothing at all.
PrPollTargetBuilder.ValidateEnabledRepos(daemonOptions);

// `--setup-workspace` must never run in the same process as PR polling — see
// SetupWorkspaceCommand.EnsureCanRunWithoutPolling for why. Checked here, before the host is even built,
// so this fails fast instead of only surfacing later as pool contention once PrPollingService starts
// polling in app.StartAsync().
if (setupWorkspaceRequested)
{
    SetupWorkspaceCommand.EnsureCanRunWithoutPolling(daemonOptions);
}

builder.Services.AddSingleton(daemonOptions);

// The ADO org(s) whose legacy {org}.visualstudio.com submodule URLs the host-side git rewrites to
// dev.azure.com (so the modern ADO credential authenticates the fetch — see HostGitCredentialEnv). Derived
// from the configured ADO context and shared by every HostGitCommandRunner below; a GitHub-only daemon gets
// an empty set (no rewrite emitted).
var hostGitAdoOrgs = DeriveAdoOrgs(daemonOptions);

// Task #85 — the scoped authority HostGitCommandRunner consults before executing any `git push`. This is
// what closes the gap `artifactBranchCapability` above cannot: that capability governs whether
// WorkflowArtifactOperations.RetainArtifactsAsync PROCEEDS to call git at all, but the shared host git
// boundary itself used to consult only the flat CodeReviewDaemonOptions.EnableGitPush boolean — so a
// capability minted below could clear the retention layer's own check while this runner still rejected (or,
// under a permissive profile, accepted ANY push) regardless of which branch was actually named. Constructed
// once here and shared BY REFERENCE with every HostGitCommandRunner that needs it, so a grant minted later
// (in onIdentityRevalidated below, and in RedoReviewArtifactBranchCommand) is visible to a runner that was
// already constructed. EnableGitPush is now only this object's all-or-nothing escape hatch, not a second
// gate layered on top of the scoped grants.
var hostGitPushAuthorization = new HostGitPushAuthorization(daemonOptions.EnableGitPush);

// Opt-in structured JSONL logging: when CodeReviewDaemon:LogFilePath is set, add a Serilog file sink
// alongside the console logger so the daemon's own logs are DuckDB-queryable. Unset ⇒ console-only.
if (!string.IsNullOrWhiteSpace(daemonOptions.LogFilePath))
{
    DaemonLogging.AddJsonlFileSink(builder.Logging, daemonOptions.LogFilePath);
}

// ── Sandbox gateway per-app identity (ADR 0029) ─────────────────────────────────────────────────
// The daemon authenticates to the sandbox gateway under its OWN app identity — distinct from
// LmStreaming.Sample's default ("lmstreaming-sample") — so the shared SandboxSessionRegistry's
// default credential (derived from sandboxGatewayOptions.AppId/AppKey below), the typed SandboxClient
// the daemon's SandboxSessionAdapter binds per session, and the S2S client's X-Sbx-App-Id/X-Sbx-App-Key
// headers all stamp the SAME
// X-Sbx-App-Id/X-Sbx-App-Key headers. A present-but-invalid key fails fast at boot (redacted); an
// absent key is the keyless AUTH_ENFORCE=off dev path, logged once as a warning after the host is
// built (never blocking startup, and never logging the key itself).
var daemonAppId =
    Environment.GetEnvironmentVariable("CRD_SANDBOX_APP_ID")
    ?? builder.Configuration["SandboxGateway:AppId"]
    ?? "codereview-daemon";
var daemonAppKey =
    Environment.GetEnvironmentVariable("CRD_SANDBOX_APP_KEY") ?? builder.Configuration["SandboxGateway:AppKey"];
var daemonKeyMissing = string.IsNullOrWhiteSpace(daemonAppKey);
if (!daemonKeyMissing)
{
    SandboxCredential.ValidateKeyOrThrow(daemonAppId, daemonAppKey!);
}
var daemonCredential = new SandboxCredential(daemonAppId, daemonAppKey ?? string.Empty);

// The already-running sandbox gateway's base URL, resolved once (env overrides config, then the
// 3000 default). Threaded into every gateway consumer so a profile can set SandboxGateway:BaseUrl
// and nothing needs CRD_SANDBOX_GATEWAY in env.
var gatewayBaseUrl =
    Environment.GetEnvironmentVariable("CRD_SANDBOX_GATEWAY")
    ?? builder.Configuration["SandboxGateway:BaseUrl"]
    ?? "http://127.0.0.1:3000";

// ── OAuth auth-provider services ───────────────────────────────────────────────────────────────
// Shared with LmStreaming.Sample (see its Program.cs): the sandbox gateway calls back into the
// auth webhook to obtain a per-provider bearer/basic credential for an outbound request, and these
// providers mint it. The daemon reviews GitHub PRs by default; Azure DevOps is opt-in via
// CodeReviewDaemon:EnableAdoProvider.
var authOptions = builder.Configuration.GetSection(AuthOptions.SectionName).Get<AuthOptions>() ?? new AuthOptions();
builder.Services.AddSingleton(authOptions);

var oauthTokenDir = string.IsNullOrWhiteSpace(authOptions.TokenStoreDir)
    ? Path.Combine(AppContext.BaseDirectory, "oauth-tokens")
    : authOptions.TokenStoreDir;
builder.Services.AddSingleton<IOAuthTokenStore>(sp => new FileOAuthTokenStore(
    oauthTokenDir,
    sp.GetRequiredService<ILogger<FileOAuthTokenStore>>()
));
builder.Services.AddSingleton(sp => new SessionSecretStore(
    Path.Combine(oauthTokenDir, "session-secrets"),
    sp.GetRequiredService<ILogger<SessionSecretStore>>()
));

// Dual-register each provider (concrete + IOAuthTokenProvider alias-to-concrete) so the
// enumerable-consuming callers (AuthWebhookController, OAuthTokenHydrator) and any concrete-typed
// consumer share a single singleton instance per provider.
builder.Services.AddSingleton(sp => new GitHubOAuthProvider(
    authOptions.Github,
    sp.GetRequiredService<IOAuthTokenStore>(),
    new HttpClient(),
    sp.GetRequiredService<ILogger<GitHubOAuthProvider>>()
));
builder.Services.AddSingleton<IOAuthTokenProvider>(sp => sp.GetRequiredService<GitHubOAuthProvider>());

// Startup diagnostics: log the GitHub Copilot model catalog the daemon's credential can see (raw catalog
// + the routable subset usable as ReviewModelId) so an operator can discover valid model ids on boot.
// Best-effort and bounded — a discovery failure is logged and never blocks startup.
builder.Services.AddHostedService<CodeReviewDaemon.Sample.Diagnostics.CopilotModelCatalogLogger>();

// Azure DevOps is opt-in: when EnableAdoProvider is off (default) the provider is never registered,
// so an "ado" webhook call resolves no provider and is denied as unknown — the daemon stays GitHub-only.
if (daemonOptions.EnableAdoProvider)
{
    builder.Services.AddSingleton(sp => new AdoOAuthProvider(
        authOptions.Ado,
        Path.Combine(oauthTokenDir, "msal-ado.bin"),
        sp.GetRequiredService<ILogger<AdoOAuthProvider>>()
    ));
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
// per-session secret in the `Authorization` header (crates/mcp-gateway/.../proxy_policy/auth_webhook.rs
// sends ONLY `Authorization: {gateway_auth}` — no body signature, timestamp, or delivery id). The shared
// AuthWebhookController already verifies that secret (SessionSecretStore, constant-time, keyed by the
// session id carried in the callback body) and injects tokens only toward each provider's own hosts. The
// plan §9 HMAC/timestamp/replay middleware was built for a Stripe-style signing gateway this one is NOT:
// it hard-required X-Sandbox-Signature/Timestamp/Delivery-Id and so rejected EVERY real callback as
// MissingHeaders (proven live — clone 403, "Rejected ... MissingHeaders"), breaking all authenticated git
// (private clone, ReviewBot push). It is therefore not wired; the per-session secret carried in
// Authorization is the gateway↔webhook boundary. (WebhookVerification* + DeliveryReplayCache are retained
// unwired for a future signing gateway.)

// ── Orchestration: store, sandbox, agents, providers, poller ─────────────────────────────────────
// The daemon's orchestration source of truth (plan §6–§14). The store migrates SQLite at construction,
// so the path is test-isolated via CodeReviewDaemon:DatabasePath; the default lives beside the binary.
var databasePath = string.IsNullOrWhiteSpace(daemonOptions.DatabasePath)
    ? Path.Combine(AppContext.BaseDirectory, "review.db")
    : daemonOptions.DatabasePath;
var dbConnectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();

// This daemon's SQLite/workspace deployment has one coordinator. An OS-held lease rejects a
// second host before polling; process exit releases it, so stale coordinators cannot keep writing.
// A scoped one-shot command (workflow operation, candidate listing, an operator-approved single-PR run,
// or an artifact-branch redo) never polls, so it never contends for this process-wide lease here. The two
// that DO need coordinator exclusion take the identical WorkflowCoordinatorLease themselves, around their
// own gates — --run-pr via RunPrCoordinatorLease.TryAcquire, --redo-artifact-branch inside
// RedoReviewArtifactBranchCommand — so "no daemon is running" is checked at the point the state is read
// rather than minutes earlier at startup. Taking it here too would self-deadlock both of them.
using var coordinatorLease = isScopedCommand ? null : WorkflowCoordinatorLease.Acquire(databasePath);

// Singleton: ReviewStore wraps one SqliteConnection. Its single accessor is still the serial
// PrPollingService loop (each PR is orchestrated to completion before the next), so concurrent use does
// not arise today — but the store now serializes access itself (every operation runs under an internal
// gate held across command-plus-reader), so a future fan-out (parallel arms, a second poller) can share
// this singleton without corrupting the connection. Isolation of the review WORKSPACES is separate and
// already in place: each concurrent review leases its own pooled slot.
builder.Services.AddSingleton(_ => new ReviewStore(dbConnectionString));

// The refusal ledger (#536). Every capability gate that DENIES something writes here, because the absence
// of a Posted row in review_outbox was never evidence that nothing was posted: a review sub-agent posting
// straight to the provider REST API over the sandbox egress proxy does not touch that table at all. A
// refusal that leaves no trace cannot be told apart from an attempt nobody made, and that is exactly the
// distinction an operator is trying to draw when they ask whether collect-only held.
builder.Services.AddSingleton<IPolicyRefusalRecorder>(sp => new StorePolicyRefusalRecorder(
    sp.GetRequiredService<ReviewStore>(),
    sp.GetRequiredService<ILogger<StorePolicyRefusalRecorder>>()
));

// Sandbox: all deterministic git/fs work runs in the gateway via the typed SandboxClient SDK, wrapped
// by SandboxSessionAdapter. The client is lazy (built on first command), so registering it does no work
// at boot and the daemon stays inert until a repo is allow-listed. The gateway base URL / session come
// from the environment.
// Per-app bearer identity for the sandbox gateway (ADR 0029): sent as X-Sbx-App-Id/X-Sbx-App-Key on every
// gateway request when a key is configured, so an AUTH_ENFORCE gateway authenticates the daemon and scopes
// its sessions to it. No key configured → no bearer headers (works unchanged against an unenforced gateway).
// One combined adapter serves BOTH ports over the typed SandboxClient SDK (issue #192): register it
// once and alias each interface to that single instance, so the runner and filesystem share one
// borrowed gateway session exactly as the old SandboxOrchestrator + SandboxFileSystem pair did.
// The gateway URL comes from the single `gatewayBaseUrl` resolved above (env → SandboxGateway:BaseUrl →
// :3000), NOT from a second env lookup here. This adapter used to resolve CRD_SANDBOX_GATEWAY itself with
// its own :8080 default, which made a profile-only SandboxGateway:BaseUrl invisible to it: every other
// gateway consumer talked to the configured gateway while this one talked to :8080 (issue #218 item 10).
builder.Services.AddSingleton(sp => new SandboxSessionAdapter(
    gatewayBaseUrl,
    Environment.GetEnvironmentVariable("CRD_SANDBOX_SESSION") ?? Guid.NewGuid().ToString("N"),
    sp.GetRequiredService<ILogger<SandboxSessionAdapter>>(),
    daemonCredential,
    daemonOptions.Limits
));
builder.Services.AddSingleton<ISandboxCommandRunner>(sp => sp.GetRequiredService<SandboxSessionAdapter>());
builder.Services.AddSingleton<ISandboxFileSystem>(sp => sp.GetRequiredService<SandboxSessionAdapter>());

// Per-run session provisioning (tool-assisted path, Task 7). The diff-only path above talks to the
// gateway via a boot-lifetime SandboxSessionAdapter; EnableToolAssistedReview instead provisions
// one sandbox session per run (design §4) so the checkout git and the review agent's MCP tools share a
// container. The registry needs a SandboxGatewayLifetime purely to resolve/probe the gateway base URL —
// it is not registered as a hosted service (AutoSpawn stays false, mirroring the SandboxSessionAdapter
// registration above: the daemon assumes an already-running gateway and never spawns one itself).
var sandboxGatewayOptions = new SandboxGatewayOptions
{
    BaseUrl = gatewayBaseUrl,
    AutoSpawn = false,
    Marketplaces = string.Join(",", daemonOptions.Marketplaces),
    // Gateway storage may be remote. The daemon identifies only its logical workspace leaf and never needs
    // the gateway host's workspace base path.
    WorkspaceBasePath = null,
    // Per-app bearer identity (ADR 0029) — the daemon's own identity, distinct from LmStreaming.Sample so
    // its sandbox sessions are scoped to their own app tree under an AUTH_ENFORCE gateway, and so the
    // registry's default credential (used to stamp its own REST/MCP calls) matches the two direct /mcp
    // transports rather than defaulting to "lmstreaming-sample".
    AppId = daemonAppId,
    AppKey = daemonKeyMissing ? null : daemonAppKey,
};

// NOTE: this no longer feeds a review-workspace host path — every review slot is a purely logical leaf the
// run's own Gateway session mounts (see the pooled workspace registration below), so there is no daemon-side
// prep/relative base left to re-root under an app dir.
builder.Services.AddSingleton(sp => new SandboxSessionRegistry(
    new SandboxGatewayLifetime(
        sandboxGatewayOptions,
        sp.GetRequiredService<ILogger<SandboxGatewayLifetime>>(),
        new HttpClient(
            new GatewayAuthHandler(daemonAppId, daemonKeyMissing ? null : daemonAppKey)
            {
                InnerHandler = new HttpClientHandler { AllowAutoRedirect = false },
            }
        )
    ),
    sandboxGatewayOptions,
    sp.GetRequiredService<ILogger<SandboxSessionRegistry>>(),
    // Bounds the gateway create/destroy calls (mirrors LmStreaming.Sample's registration); the handler
    // attaches the per-app bearer headers to every gateway REST call. Auto-redirect is disabled so a
    // cross-origin 3xx can never replay the X-Sbx-* credential headers to a redirect target.
    new HttpClient(
        new GatewayAuthHandler(daemonAppId, daemonKeyMissing ? null : daemonAppKey)
        {
            InnerHandler = new HttpClientHandler { AllowAutoRedirect = false },
        }
    )
    {
        Timeout = TimeSpan.FromSeconds(30),
    },
    authOptions,
    sp.GetRequiredService<SessionSecretStore>()
));
builder.Services.AddSingleton<ISandboxSessionSource>(sp => new RegistrySessionSource(
    sp.GetRequiredService<SandboxSessionRegistry>()
));
builder.Services.AddSingleton<IReviewSessionProvisioner>(sp => new ReviewSessionProvisioner(
    sp.GetRequiredService<ISandboxSessionSource>(),
    daemonOptions,
    sp.GetRequiredService<ILoggerFactory>(),
    daemonCredential,
    gatewayBaseUrl,
    daemonOptions.ReviewWorkspaceLeaf
));

// Sub-agent discovery (Task 12): the executor asks for `code-reviewer:*` sub-agents through the same
// narrow-adapter pattern as ISandboxSessionSource above, so it never depends on the registry's full
// surface directly.

// Optional conversation persistence: when ConversationStorePath is set, the S2S review host persists its
// full message history. The daemon retains the path for log auditing references.
IConversationStore? conversationStore = string.IsNullOrWhiteSpace(daemonOptions.ConversationStorePath)
    ? null
    : new FileConversationStore(daemonOptions.ConversationStorePath);

// The daemon drives all reviews over the S2S REST API against a running LmStreaming.Sample review host.
// UseS2SReviewAgent must be on — the in-process path has been removed.
if (!daemonOptions.UseS2SReviewAgent)
{
    throw new InvalidOperationException(
        "UseS2SReviewAgent must be true; the in-process review path (LiveReviewAgentLoopFactory) has been removed. "
            + "Set CodeReviewDaemon:UseS2SReviewAgent=true and configure LmStreamingBaseUrl."
    );
}

if (string.IsNullOrWhiteSpace(daemonOptions.LmStreamingBaseUrl))
{
    throw new InvalidOperationException(
        "UseS2SReviewAgent is on but LmStreamingBaseUrl is not configured; set it to the LmStreaming review "
            + "host base URL (e.g. http://localhost:5051)."
    );
}

// The authored model id is resolved by the review host when its parent conversation is provisioned.
// The review-workspace path itself no longer needs a host workspace base: every review slot is a purely
// logical leaf the run's own Gateway session mounts, never a host directory this process inspects.

// Normalize to a host-root base with a trailing slash so the client's relative paths ("api/workspaces",
// "api/conversations") resolve correctly against HttpClient.BaseAddress.
var lmStreamingBaseUri = new Uri(daemonOptions.LmStreamingBaseUrl.TrimEnd('/') + "/", UriKind.Absolute);

// The transport budget for every S2S call. Long-LIVED is not long-TIMEOUT: the client below is a
// singleton, but an HttpClient with no explicit Timeout takes .NET's 100-second default, and
// `POST api/conversations/{id}/messages` blocks on the review host until it has built the agent and
// provisioned its sandbox session — routinely minutes. The 100s stopwatch fired first and every review
// died as `TaskCanceledException: ... HttpClient.Timeout of 100 seconds elapsing`, leaving the run
// RetryPending while the host went on to finish a review nobody collected. Same shape as #735, one layer
// down: there the ceiling was a constructor default, here it is a transport default.
//
// The value is the operator's whole-stage budget, so the review's deadline is the stage budget rather
// than the transport's stopwatch. That is deliberately NOT a min-clamp of the #735 kind: this bounds ONE
// request while ReviewStageDeadlineMinutes bounds the whole stage (collect -> barrier -> synthesize), and
// a single request is always a strict sub-interval of the stage it runs in. So this bound cannot bind
// before the stage bound on any healthy path — it can only truncate a request that has already outlived
// the entire stage that contains it.
//
// Timeout.InfiniteTimeSpan was the alternative, carrying the deadline solely on the CancellationToken.
// Rejected on the evidence: no caller on this path attaches a deadline to that token (the stage budget is
// enforced as an absolute DateTimeOffset inside S2SReviewAgent.PollToTerminalAsync, and that check only
// runs BETWEEN requests). An infinite transport timeout would therefore hang a stalled review host's
// request forever and wedge the poller, which is strictly worse than the bug being fixed. A finite bound
// keeps a hung host recoverable within one budget.
//
// Math.Max keeps the span positive: HttpClient.Timeout rejects zero/negative, so a misconfigured 0 would
// otherwise fail startup with an opaque ArgumentOutOfRangeException from a DI factory.
var lmStreamingS2STimeout = TimeSpan.FromMinutes(Math.Max(1, daemonOptions.ReviewStageDeadlineMinutes));

// Outbound S2S client over the review host. The raw HttpClient is intentionally long-lived (mirrors the
// gateway HttpClients above); it forwards the daemon's own gateway identity (X-Sbx-App-*) so the sandbox
// the review provisions is attributed to codereview-daemon, and the X-S2S-Auth secret (never logged).
builder.Services.AddSingleton(sp => new LmStreamingS2SClient(
    new HttpClient { BaseAddress = lmStreamingBaseUri, Timeout = lmStreamingS2STimeout },
    daemonOptions.LmStreamingS2SSecret,
    daemonAppId,
    daemonKeyMissing ? null : daemonAppKey
));

// Completion-source seam for the recursive review completion barrier: reads the review host's versioned
// recursive sub-agent tree over the same S2S client. IMPORTANT — the review host (LmStreaming.Sample) must
// be deployed with the recursive `?recursive=true` endpoint BEFORE the daemon barrier is enabled.
//
// The same object is also the daemon's read half of the agent directory (IReviewAgentTranscriptSource):
// once the barrier hands back a settled roster, the notes artifacts fetch each named agent's transcript to
// write per-reviewer findings files. Registered concrete-first so both interfaces share ONE instance —
// two factory registrations would silently give the barrier and the artifact builder separate objects.
builder.Services.AddSingleton(sp => new S2SReviewSubAgentCompletionSource(
    sp.GetRequiredService<LmStreamingS2SClient>()
));
builder.Services.AddSingleton<IReviewSubAgentCompletionSource>(sp =>
    sp.GetRequiredService<S2SReviewSubAgentCompletionSource>()
);
builder.Services.AddSingleton<IReviewAgentTranscriptSource>(sp =>
    sp.GetRequiredService<S2SReviewSubAgentCompletionSource>()
);

// Names a leased Layer-1 review slot to LmStreaming as an S2S workspace. Runs no git of its own — the slot
// was already prepared through the run's own Gateway session (see the pooled workspace registration below).
builder.Services.AddSingleton(sp => new S2SReviewWorkspacePreparer(
    sp.GetRequiredService<LmStreamingS2SClient>(),
    daemonOptions.LmStreamingReviewMarketplace,
    sp.GetRequiredService<ILogger<S2SReviewWorkspacePreparer>>(),
    daemonOptions.ReviewWorkspaceLeaf
));

// Gateway prerequisite probe (RequireSkillSupport). The review runs in a conversation the review host
// provisions, so the equivalent check reads the gateway's marketplace catalog directly.
builder.Services.AddSingleton<IGatewaySkillProbe>(sp => new GatewaySkillProbe(
    gatewayBaseUrl,
    daemonCredential,
    sp.GetRequiredService<ILogger<GatewaySkillProbe>>()
));

// Deep-link retention ceiling. Every posted comment carries ?threadId=, so the hosted conversation must
// OUTLIVE its review — but not forever. When a window is configured, each minted conversation is recorded
// in the ledger and discarded once it has aged past it.
var deepLinkRetention =
    daemonOptions.DeepLinkRetentionHours > 0
        ? TimeSpan.FromHours(daemonOptions.DeepLinkRetentionHours)
        : (TimeSpan?)null;

if (deepLinkRetention is { } retentionWindow)
{
    builder.Services.AddSingleton(sp => new DeepLinkRetentionSweeper(
        sp.GetRequiredService<ReviewStore>(),
        sp.GetRequiredService<LmStreamingS2SClient>(),
        retentionWindow,
        sp.GetRequiredService<ILogger<DeepLinkRetentionSweeper>>()
    ));
}

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
    sp.GetRequiredService<ILogger<GitHubPrProvider>>(),
    // The per-poll coverage bounds (issue #537). Both were declared-but-unread before this: the provider
    // carried its own private const, so an operator raising MaxPagesPerPoll changed nothing.
    daemonOptions.MaxPagesPerPoll,
    daemonOptions.MaxPrsPerPage
));

// LLM-selected publications use the host publisher with scoped authorization and durable receipts.
builder.Services.AddSingleton<IReviewCommentPublisher>(sp => new GitHubReviewCommentPublisher(
    sp.GetRequiredService<PolicyEnforcedHttpClientFactory>().Create("github"),
    sp.GetRequiredService<GitHubOAuthProvider>(),
    sp.GetRequiredService<ILogger<GitHubReviewCommentPublisher>>()
));

// Issue #647 — what the PR was ASKED to do, read from GitHub's issue-linking graph (the GitHub analog of
// the ADO work-item context reader registered below). Registered unconditionally alongside the other
// GitHub services; the reader itself returns Unavailable for a non-GitHub repo.
builder.Services.AddSingleton(sp => new GitHubIssueContextReader(
    sp.GetRequiredService<PolicyEnforcedHttpClientFactory>(),
    sp.GetRequiredService<ReviewStore>(),
    sp.GetRequiredService<GitHubOAuthProvider>(),
    sp.GetRequiredService<ILogger<GitHubIssueContextReader>>()
));

if (daemonOptions.EnableAdoProvider)
{
    builder.Services.AddSingleton<IPrProvider>(sp => new AdoPrProvider(
        sp.GetRequiredService<PolicyEnforcedHttpClientFactory>().Create("ado"),
        sp.GetRequiredService<AdoOAuthProvider>(),
        sp.GetRequiredService<ILogger<AdoPrProvider>>(),
        // Same per-poll coverage bounds as GitHub above (issue #537). On ADO these matter most: with no
        // $top the endpoint returns its own default of 101 and no continuation token, so the page loop
        // could never iterate however high MaxPagesPerPoll was set.
        daemonOptions.MaxPagesPerPoll,
        daemonOptions.MaxPrsPerPage
    ));

    // Resolve the concrete OAuth provider for this scoped publication adapter.
    builder.Services.AddSingleton<IReviewCommentPublisher>(sp => new AdoReviewCommentPublisher(
        sp.GetRequiredService<PolicyEnforcedHttpClientFactory>().Create("ado"),
        sp.GetRequiredService<AdoOAuthProvider>(),
        sp.GetRequiredService<ILogger<AdoReviewCommentPublisher>>()
    ));

    // Linked work items are carried into the prepared workflow context as untrusted evidence.
    builder.Services.AddSingleton(sp => new AdoWorkItemContextReader(
        sp.GetRequiredService<PolicyEnforcedHttpClientFactory>().Create("ado"),
        sp.GetRequiredService<AdoOAuthProvider>(),
        sp.GetRequiredService<ILogger<AdoWorkItemContextReader>>()
    ));
}

// Host-side git authenticates to every OAuth provider the daemon is signed in to — GitHub for github.com
// clones, Azure DevOps for dev.azure.com clones — so a private ADO store/submodule checkout gets a
// credential just like GitHub. HostGitCommandRunner asks this source per git command; a provider that is
// not signed in throws and is skipped, so the GitHub-only daemon and the dedicated ADO daemon each inject
// exactly the credentials their store/target needs. Tokens never touch argv or on-disk git config.
// Collects the ADO org(s) the daemon is configured against so the host git can key a legacy→modern
// url.<base>.insteadOf rewrite per org (insteadOf cannot extract the org generically). Sources: the
// 3-segment {org}/{project}/{repo} EnabledRepos entries and the resolved cross-repo store URL. GitHub-only
// config (2-segment entries, github.com store) yields an empty set.
static IReadOnlyList<string> DeriveAdoOrgs(CodeReviewDaemonOptions options)
{
    var orgs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    foreach (var entry in options.EnabledRepos)
    {
        var segments = (entry ?? string.Empty).Split(
            '/',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
        );
        if (segments.Length == 3)
        {
            orgs.Add(segments[0]); // {org}/{project}/{repo}
        }
    }

    foreach (var url in new[] { options.ResolvedStoreUrl, options.CrossRepoStoreUrl })
    {
        if (AdoOrgFromUrl(url) is { } org)
        {
            orgs.Add(org);
        }
    }

    return [.. orgs];
}

// Extracts the ADO org from an HTTPS store URL in either shape: the leading path segment of a modern
// dev.azure.com/{org}/... URL, or the host label of a legacy {org}.visualstudio.com URL. Anything else
// (non-HTTPS, non-ADO host) yields null.
static string? AdoOrgFromUrl(string? url)
{
    if (string.IsNullOrWhiteSpace(url))
    {
        return null;
    }

    var parsed = GitRemoteUrl.Parse(url);
    if (parsed.Kind != GitUrlKind.Https)
    {
        return null;
    }

    const string legacySuffix = ".visualstudio.com";
    if (parsed.Host.EndsWith(legacySuffix, StringComparison.OrdinalIgnoreCase))
    {
        var org = parsed.Host[..^legacySuffix.Length];
        return org.Length > 0 && !org.Contains('.', StringComparison.Ordinal) ? org : null;
    }

    if (string.Equals(parsed.Host, "dev.azure.com", StringComparison.OrdinalIgnoreCase))
    {
        var first = parsed.RepoPath.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return string.IsNullOrEmpty(first) ? null : first;
    }

    return null;
}

static Func<CancellationToken, Task<IReadOnlyList<GitProviderToken>>> BuildHostGitCredentialsSource(IServiceProvider sp)
{
    var providers = sp.GetServices<IOAuthTokenProvider>().ToList();
    return async ct =>
    {
        var tokens = new List<GitProviderToken>(providers.Count);
        foreach (var provider in providers)
        {
            try
            {
                var token = await provider.GetAccessTokenAsync(ct: ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(token.Value))
                {
                    tokens.Add(new GitProviderToken(provider.ProviderId, token.Value));
                }
            }
            catch (InvalidOperationException)
            {
                // Provider not signed in / not configured — skip; its host's clones stay unauthenticated.
            }
        }

        return tokens;
    };
}

// HOST-side retention workspace (Task 15, design §6 Risk A): the ReviewBot retention push and the KB
// entry it carries must run OUTSIDE the sandbox the untrusted review agent shares, with the write
// credential injected only into this host-process git runner. Registered only when a ReviewBot repo is
// configured. Retention uses the host credential and a separate checkout.
// This reuses the existing GitHub credential (a
// dedicated write-scoped credential is a documented fast-follow, not introduced here).
if (!string.IsNullOrWhiteSpace(daemonOptions.ResolvedStoreUrl))
{
    builder.Services.AddSingleton(sp =>
    {
        var runner = new HostGitCommandRunner(
            BuildHostGitCredentialsSource(sp),
            sp.GetRequiredService<ILogger<HostGitCommandRunner>>(),
            hostGitAdoOrgs,
            pushAuthorization: hostGitPushAuthorization
        );
        return new HostRetentionWorkspace(
            runner,
            new HostFileSystem(),
            HostRetentionWorkspace.ResolveRoot(daemonOptions.WorkspaceHostRoot, daemonOptions.ResolvedStoreUrl)
        );
    });
}

// ── Shared review workspace + repository worktree slot pool ────────────────────────────────────────
// The Gateway mounts exactly one workspace (`ReviewWorkspaceLeaf`). A slot is a linked worktree path
// inside that mount: `.worktrees/<Repo>-<N>`. All deterministic Git/file access goes through the shared
// Gateway session and uses an explicit slot-relative cwd; the daemon never resolves a Gateway host path.
{
    builder.Services.AddSingleton(sp =>
    {
        var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
        var repositoryNames = daemonOptions.EnabledRepos.Select(RepositoryNameRules.FromEnabledRepo).ToArray();

        var pool = new ReviewSlotPool(
            repositoryNames,
            daemonOptions.ReviewPoolSize,
            loggerFactory.CreateLogger<ReviewSlotPool>(),
            daemonOptions.ReviewPoolSizesByRepository
        );
        return new ReviewSlotWorkspace(
            pool,
            AutoDiscardCompletedSlotOnAdmission: daemonOptions.AutoDiscardCompletedSlotOnAdmission
        );
    });
}

// Merged PRs enter the authored workflow; abandoned PRs retain the existing branch cleanup.
if (!string.IsNullOrWhiteSpace(daemonOptions.ResolvedStoreUrl))
{
    builder.Services.AddSingleton(sp =>
    {
        var store = sp.GetRequiredService<ReviewStore>();
        var retention = sp.GetRequiredService<HostRetentionWorkspace>();
        var git = new GitRunner(retention.Git);
        var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
        var logger = loggerFactory.CreateLogger<PrLifecycleSweeper>();
        var providers = sp.GetServices<IPrProvider>().ToList();
        var targets = PrPollTargetBuilder.Build(daemonOptions, logger);
        var warnedOrphans = new HashSet<string>(StringComparer.Ordinal);
        var index = new KnowledgeIndexRegenerator(
            retention.FileSystem,
            loggerFactory.CreateLogger<KnowledgeIndexRegenerator>()
        );
        var manager = new ReviewBranchManager(
            git,
            retention.FileSystem,
            loggerFactory.CreateLogger<ReviewBranchManager>(),
            (root, token) => index.RegenerateAsync(root.TrimEnd('/', '\\') + "/KnowledgeBase", token)
        );
        return new PrLifecycleSweeper(
            async ct =>
            {
                await using var repositoryLease = await HostRetentionWorkspace.AcquireRepositoryLockAsync(
                    retention.RepoRoot,
                    ct
                );
                var retentionPreparer = new ReviewSlotPreparer(git, retention.FileSystem);
                await retentionPreparer.EnsureStoreAsync(retention.RepoRoot, daemonOptions.ResolvedStoreUrl, ct);
                var rows = await store.ListReviewedPrsAsync(ct);
                IReadOnlyList<ReviewedPr> reviewed =
                [
                    .. rows.Select(PrLifecycleSweepSeam.MapReviewedPr).OfType<ReviewedPr>(),
                ];
                var branches = await ListRemoteReviewBranchesAsync(git, retention.RepoRoot, logger, ct);
                return OrphanBranchReconciler.Reconcile(reviewed, branches, targets, logger, warnedOrphans);
            },
            (pr, ct) => PrLifecycleSweepSeam.ResolveLifecycleAsync(providers, pr, ct),
            manager,
            retention.RepoRoot,
            logger,
            store,
            sp.GetRequiredService<PrOrchestrator>(),
            getCurrentHeadShaAsync: (pr, ct) =>
                providers
                    .Single(provider =>
                        string.Equals(provider.Provider, pr.Provider, StringComparison.OrdinalIgnoreCase)
                    )
                    .GetCurrentHeadShaAsync(pr.Repo, pr.PrId, ct)
        );
    });
}
static async Task<IReadOnlyList<string>> ListRemoteReviewBranchesAsync(
    GitRunner git,
    string repoRoot,
    ILogger logger,
    CancellationToken cancellationToken
)
{
    var result = await git.RunAsync(
            ["-C", repoRoot, "ls-remote", "--heads", "origin", "review/*"],
            repoRoot,
            cancellationToken
        )
        .ConfigureAwait(false);
    if (!result.Succeeded)
    {
        logger.LogWarning(
            "PR-lifecycle sweep: listing review/* branches failed (exit {Exit}): {Err}; sweeping the DB set only.",
            result.ExitCode,
            result.Stderr
        );
        return [];
    }

    const string headsPrefix = "refs/heads/";
    var branches = new List<string>();
    foreach (
        var line in result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    )
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

// One authored workflow; the existing stores own admission, receipts and durable execution state.
var workflowPath = Path.GetFullPath(daemonOptions.WorkflowPath, builder.Environment.ContentRootPath);
var workflowStateRoot = Path.GetFullPath(daemonOptions.WorkflowStateDirectory ?? (databasePath + ".workflow"));
var workflowRunRoot = Path.Combine(workflowStateRoot, "runs");
builder.Services.AddSingleton<IWorkflowStore>(_ => new FileWorkflowStore(Path.Combine(workflowStateRoot, "snapshots")));
builder.Services.AddSingleton<WorkflowPublicationScopes>();
builder.Services.AddSingleton(sp => new WorkflowPublicationGateway(
    builder.Configuration["WorkflowPublication:SharedSecret"] ?? string.Empty,
    sp.GetRequiredService<WorkflowPublicationScopes>().ResolveAsync
));
builder.Services.AddSingleton(sp => new WorkflowContextReader(
    sp.GetRequiredService<ReviewStore>(),
    workflowRunRoot,
    daemonOptions.Limits.MaxArtifactPayloadChars,
    async (run, ct) =>
    {
        var repo =
            sp.GetRequiredService<ReviewStore>().GetRepo(run.RepoId)
            ?? throw new InvalidOperationException("Review repository is missing.");
        return RepoIdentity.ToPublisherNamespace(repo.Provider) switch
        {
            "github" => JsonSerializer.SerializeToNode(
                await sp.GetRequiredService<GitHubIssueContextReader>().ReadAsync(run.Id, ct)
            ),
            "ado" => JsonSerializer.SerializeToNode(
                await sp.GetRequiredService<AdoWorkItemContextReader>().ReadAsync(repo, run.PrId, ct)
            ),
            _ => null,
        };
    }
));
builder.Services.AddSingleton(sp => new WorkflowWorkspace(
    sp.GetRequiredService<ReviewStore>(),
    daemonOptions,
    sp.GetRequiredService<ReviewSlotWorkspace>(),
    sp.GetRequiredService<IReviewSessionProvisioner>(),
    sp.GetRequiredService<S2SReviewWorkspacePreparer>().AdoptSlotAsync,
    sp.GetRequiredService<ILoggerFactory>(),
    async (run, admission, session, ct) =>
    {
        var context = await sp.GetRequiredService<WorkflowContextReader>()
            .ReadAsync(run, admission, session, ct)
            .ConfigureAwait(false);
        context["Execution"] = new JsonObject
        {
            ["PublicationMode"] =
                daemonOptions.EnableCommentPosting && run.Mode == "post" && run.VariantId != "b"
                    ? "post"
                    : "collect_only",
        };
        var retention = sp.GetRequiredService<HostRetentionWorkspace>();
        await using var repositoryLease = await HostRetentionWorkspace.AcquireRepositoryLockAsync(
            retention.RepoRoot,
            ct
        );
        var retentionPreparer = new ReviewSlotPreparer(new GitRunner(retention.Git), retention.FileSystem);
        await retentionPreparer.EnsureStoreAsync(
            retention.RepoRoot,
            daemonOptions.ResolvedStoreUrl ?? throw new InvalidOperationException("Review store URL is required."),
            ct
        );
        return context;
    }
));
var scriptEnvironment = WorkflowScriptHost.BuildEnvironment(
    builder.Configuration,
    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["REVIEW_DAEMON_EXECUTABLE"] = typeof(Program).Assembly.Location,
        ["DOTNET_ENVIRONMENT"] = builder.Environment.EnvironmentName,
        ["CodeReviewDaemon__DatabasePath"] = Path.GetFullPath(databasePath),
        ["CodeReviewDaemon__WorkflowStateDirectory"] = workflowStateRoot,
        ["CodeReviewDaemon__WorkflowPath"] = workflowPath,
    }
);
builder.Services.AddSingleton(
    new WorkflowScriptInvoker(
        daemonOptions.WorkflowPythonExecutable,
        daemonOptions.WorkflowPowerShellExecutable,
        scriptEnvironment,
        daemonOptions.Limits.MaxArtifactPayloadChars
    )
);
var workflowGitGate = new SemaphoreSlim(1, 1);
builder.Services.AddSingleton(sp => new WorkflowOperationDispatcher(
    sp.GetRequiredService<ReviewStore>(),
    sp.GetRequiredService<WorkflowWorkspace>(),
    daemonOptions,
    workflowRunRoot,
    ArtifactCapability,
    (run, ct) =>
    {
        ct.ThrowIfCancellationRequested();
        var store = sp.GetRequiredService<ReviewStore>();
        var repo = store.GetRepo(run.RepoId) ?? throw new InvalidOperationException("Review repository is missing.");
        var retention = sp.GetRequiredService<HostRetentionWorkspace>();
        var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
        var index = new KnowledgeIndexRegenerator(
            retention.FileSystem,
            loggerFactory.CreateLogger<KnowledgeIndexRegenerator>()
        );
        var manager = new ReviewBranchManager(
            new GitRunner(retention.Git),
            retention.FileSystem,
            loggerFactory.CreateLogger<ReviewBranchManager>(),
            (root, token) => index.RegenerateAsync(root.TrimEnd('/', '\\') + "/KnowledgeBase", token)
        );
        return Task.FromResult(
            new WorkflowArtifactOperations(
                store,
                run,
                repo,
                retention.RepoRoot,
                "main",
                manager,
                workflowGitGate,
                ArtifactCapability(),
                new WorkflowKnowledgeEdits(
                    retention.RepoRoot,
                    repo,
                    retention.FileSystem,
                    loggerFactory.CreateLogger<WorkflowKnowledgeEdits>()
                ),
                token =>
                    ReviewSlotPreparer.VerifyStoreOriginAsync(
                        new GitRunner(retention.Git),
                        retention.RepoRoot,
                        daemonOptions.ResolvedStoreUrl
                            ?? throw new InvalidOperationException("Review store URL is required."),
                        token
                    )
            )
        );
    },
    async (run, ct) =>
    {
        var store = sp.GetRequiredService<ReviewStore>();
        var repo = store.GetRepo(run.RepoId) ?? throw new InvalidOperationException("Review repository is missing.");
        var providerId = RepoIdentity.ToPublisherNamespace(repo.Provider);
        var provider = sp.GetServices<IPrProvider>().Single(value => value.Provider == providerId);
        var before =
            await provider.GetPullRequestAsync(repo, run.PrId, ct)
            ?? throw new InvalidDataException("PR is unavailable for discussion capture.");
        if (before.HeadSha != run.HeadSha || before.BaseSha != run.BaseSha)
            throw new InvalidDataException("PR identity changed before discussion capture.");
        var readStartedAt = DateTimeOffset.UtcNow;
        var snapshot = await PrCommentContextReader.ReadAsync(
            sp.GetServices<IReviewCommentPublisher>().ToArray(),
            store,
            repo,
            providerId,
            run.RepoId,
            run.PrId,
            before,
            new Dictionary<string, string>(StringComparer.Ordinal),
            ct
        );
        snapshot["BodyNormalization"] =
            "Provider reader flattens line endings and caps each comment at 2000 characters; a trailing ellipsis may indicate truncation. Judge only the available evidence.";
        snapshot["ReadStartedAt"] = readStartedAt.ToString("O");
        var after = await provider.GetPullRequestAsync(repo, run.PrId, ct);
        if (after is null || after.HeadSha != before.HeadSha || after.BaseSha != before.BaseSha)
            throw new InvalidDataException("PR identity changed during discussion capture.");
        return snapshot;
    }
));
builder.Services.AddSingleton(sp => new ReviewWorkflowRunner(
    workflowPath,
    workflowRunRoot,
    sp.GetRequiredService<ReviewStore>(),
    sp.GetRequiredService<IWorkflowStore>(),
    sp.GetRequiredService<WorkflowWorkspace>(),
    (run, instanceId, directory, ct) =>
    {
        ct.ThrowIfCancellationRequested();
        IWorkflowTaskInvoker invoker = new ReviewWorkflowInvoker(
            run,
            instanceId,
            directory,
            Path.Combine(directory, "package"),
            sp.GetRequiredService<WorkflowScriptInvoker>(),
            sp.GetRequiredService<WorkflowOperationDispatcher>(),
            sp.GetRequiredService<LmStreamingS2SClient>(),
            sp.GetRequiredService<WorkflowWorkspace>(),
            sp.GetRequiredService<ReviewStore>(),
            daemonOptions,
            sp.GetRequiredService<WorkflowPublicationScopes>(),
            (activeRun, activeInstance, token) =>
            {
                token.ThrowIfCancellationRequested();
                var store = sp.GetRequiredService<ReviewStore>();
                var repo =
                    store.GetRepo(activeRun.RepoId)
                    ?? throw new InvalidOperationException("Review repository is missing.");
                var providerId = RepoIdentity.ToPublisherNamespace(repo.Provider);
                var provider = sp.GetServices<IPrProvider>().Single(p => p.Provider == providerId);
                var publisher = sp.GetServices<IReviewCommentPublisher>().Single(p => p.Provider == providerId);
                var diff = JsonNode.Parse(
                    store.TryGetLatestArtifact(activeRun.Id, "workflow-diff")?.Payload
                        ?? throw new InvalidOperationException("Review diff evidence is missing.")
                )!;
                if (diff["HeadSha"]?.GetValue<string>() != activeRun.HeadSha)
                    throw new InvalidOperationException("Review diff evidence belongs to another head.");
                var manifest = UnifiedDiffParser.Parse(
                    diff["Diff"]?.GetValue<string>(),
                    diff["BaseSha"]?.GetValue<string>(),
                    activeRun.HeadSha
                );
                var poster = new ReviewPoster(publisher, store, sp.GetRequiredService<ILogger<ReviewPoster>>());
                return Task.FromResult(
                    new ReviewPublicationTools(
                        activeRun,
                        repo,
                        activeInstance,
                        poster,
                        provider,
                        manifest,
                        daemonOptions.EnableCommentPosting && activeRun.Mode == "post" && activeRun.VariantId != "b",
                        () =>
                            JsonSerializer
                                .Deserialize<WorkflowWorkspaceAssignment>(
                                    store
                                        .TryGetLatestArtifact(activeRun.Id, WorkflowWorkspace.AssignmentArtifactKind)
                                        ?.Payload
                                        ?? "null"
                                )
                                ?.Active == true
                    )
                );
            },
            sp.GetRequiredService<IGatewaySkillProbe>(),
            sp.GetRequiredService<ILoggerFactory>()
        );
        return Task.FromResult(invoker);
    },
    (run, _) => new Dictionary<string, string> { ["review-parent"] = $"review-parent-{run.Id}" },
    invocationTimeout: TimeSpan.FromMinutes(Math.Max(1, daemonOptions.ReviewStageDeadlineMinutes))
));

builder.Services.AddSingleton<ReviewProgressReporter>();

// In-memory retry governance for the orchestrator: attempt-counting + exponential backoff + park-after-K,
// so a stuck ContextReady backs off (not the old ~30s hot-loop) and a genuinely stuck commit is parked +
// alerted. Not persisted — a restart resets it, so a restart retries parked runs.
builder.Services.AddSingleton(sp => new RetryGovernor(
    daemonOptions.MaxContextRetries,
    TimeSpan.FromSeconds(daemonOptions.RetryBackoffBaseSeconds),
    TimeSpan.FromSeconds(daemonOptions.RetryBackoffCapSeconds),
    () => DateTimeOffset.UtcNow,
    sp.GetRequiredService<ILogger<RetryGovernor>>()
));

// Announces a PERMANENT park on the pull request itself, so an author whose review went quiet learns why
// from the PR rather than from the daemon's log. Gated internally by EnableCommentPosting, like every other
// post.
builder.Services.AddSingleton<IReviewParkNotifier>(sp => new ReviewParkNotifier(
    sp.GetRequiredService<ReviewStore>(),
    sp.GetServices<IReviewCommentPublisher>(),
    daemonOptions,
    sp.GetRequiredService<ILoggerFactory>(),
    sp.GetServices<IPrProvider>()
));

// Registered by factory rather than by type because the durable retry budget is an int the container cannot
// resolve. The outer, DURABLE bound the governor above cannot supply: the governor's state is in memory and
// StrandedRunReconciler resets it on every resume, so it never reached its own bound (19 parks on 2026-08-28,
// zero from 08-29 onward, the transition exactly at the reconciler's first resume).
builder.Services.AddSingleton(sp => new PrOrchestrator(
    sp.GetRequiredService<ReviewStore>(),
    sp.GetRequiredService<ReviewWorkflowRunner>(),
    sp.GetRequiredService<ILogger<PrOrchestrator>>(),
    sp.GetRequiredService<ReviewProgressReporter>(),
    sp.GetRequiredService<RetryGovernor>(),
    daemonOptions.MaxDurableRetryAttempts,
    () => DateTimeOffset.UtcNow,
    sp.GetRequiredService<IReviewParkNotifier>()
));

// The route back for a run the poll can no longer reach. The poll only ever enumerates OPEN PRs inside its
// recency window, so a run left non-terminal when its PR merges, closes, or goes quiet is never retried by
// anything again. Registered unless the operator disables it by zeroing the grace period.
if (daemonOptions.StrandedRunGraceHours > 0)
{
    builder.Services.AddSingleton(sp =>
    {
        var store = sp.GetRequiredService<ReviewStore>();
        var providers = sp.GetServices<IPrProvider>().ToList();
        var orchestrator = sp.GetRequiredService<PrOrchestrator>();

        // #429: the retry-pending fast path. The rule that it must be strictly faster than the abandonment
        // window it rides beside is the reconciler's — it refuses a slower one at construction — so the
        // resolution lives there too rather than being restated here, where the two could drift apart and
        // only meet at host start. Zero switches the path off. Both listings share one pass and one resume
        // cap, so this knob widens WHEN a retry-pending run is picked up, never HOW MANY run at once.
        var grace = TimeSpan.FromHours(daemonOptions.StrandedRunGraceHours);
        var retryPendingGrace = StrandedRunReconciler.ResolveRetryPendingGrace(
            daemonOptions.StrandedRunRetryPendingGraceMinutes,
            grace
        );

        return new StrandedRunReconciler(
            listStrandedRuns: store.ListStrandedRuns,
            getPrLifecycleAsync: (row, ct) =>
                PrLifecycleSweepSeam.ResolveLifecycleAsync(
                    providers,
                    row.Repo,
                    RepoIdentity.ToPublisherNamespace(row.Repo.Provider),
                    row.Run.PrId,
                    ct
                ),
            resumeAsync: orchestrator.ReconcileAsync,
            updateRunState: store.UpdateReviewRunState,
            timeProvider: TimeProvider.System,
            grace: grace,
            scanLimit: daemonOptions.StrandedRunScanLimit,
            maxResumesPerPass: daemonOptions.StrandedRunMaxResumesPerSweep,
            logger: sp.GetRequiredService<ILogger<StrandedRunReconciler>>(),
            listRetryPendingRuns: retryPendingGrace > TimeSpan.Zero ? store.ListRetryPendingRuns : null,
            retryPendingGrace: retryPendingGrace
        );
    });
}

// ── eval corpus sweep (#400) ───────────────────────────────────────────────────────────────────
// The corpus reader, its persisted window and the consumer that joins the two, registered only when
// an operator has set a cadence. Until this existed, all three were complete, tested and constructed
// by nothing but their own tests — which reads as a shipped capability and is not one.
//
// The sweep contacts no model and writes no artifact: it reads the reviews recorded since it last
// ran, measures each one's citation surface, joins it to the grade the daemon's own judge recorded,
// and advances a cursor in poll_cursor. It rides the poller's maintenance tick, behind its own
// interval — that tick fires every thirty seconds, which is the right cadence for what it was built
// for and far too hot for a pass over a window of recorded history.
// Both refusals live in EvalSweepConfiguration, with the section and key named, rather than here:
// this is a top-level program, so a refusal written inline runs for the first time in a real
// deployment. They are also refusals EvalCorpusSweep's own guards cannot make — that one throws on
// `limit`, an argument no operator ever passed.
// Called unconditionally, so a configuration it refuses stops the host whether or not the sweep
// would have been registered.
if (EvalSweepConfiguration.Resolve(daemonOptions) is { } evalSweep)
{
    var (evalSweepInterval, evalSweepWindow) = evalSweep;

    builder.Services.AddSingleton(sp =>
    {
        var store = sp.GetRequiredService<ReviewStore>();
        var loggerFactory = sp.GetRequiredService<ILoggerFactory>();

        var sweep = new EvalCorpusSweep(
            // The read the grade lookup needs, and the whole of what this consumer wants from
            // persistence: judge rows only, filtered in SQL, so the review-context diff this pass
            // would discard is never materialised (#453). Memoised for the last run read, which is
            // all the memory a window's worth of contiguous candidates needs.
            EvalCorpusSweep.GradeArtifactReader(store),
            new DaemonCorpusReader(store, ModelFamilies.Of, loggerFactory.CreateLogger<DaemonCorpusReader>()),
            new EvalCorpusWatermark(store, loggerFactory.CreateLogger<EvalCorpusWatermark>()),
            evalSweepWindow,
            loggerFactory.CreateLogger<EvalCorpusSweep>()
        );

        return new EvalCorpusSweepSchedule(
            sweep.SweepOnceAsync,
            evalSweepInterval,
            TimeProvider.System,
            loggerFactory.CreateLogger<EvalCorpusSweepSchedule>()
        );
    });
}

// The PR-watching loop. Registering a BackgroundService adds NO route, so the host's mapped routes stay
// exactly the gateway callbacks below. With the allow-list empty (default) it has no targets and is inert.
// Workspace-validation profiles can keep their repository allow-list configured while disabling polling
// explicitly; this lets setup use the real repository identity without accidentally selecting or reviewing
// a PR.
//
// Never registered for --run-pr either (security review round 1: "configured polling disabled" must be
// structural, not just "the daemon happens not to call app.Run()") — an operator-approved exact run must
// never race the poller's own admission for the same PR/slot. The two gates are independent and both must
// pass: one is an operator configuration choice, the other is a property of the command being run.
if (daemonOptions.EnablePrPolling && runPrRequest is null && runPrConcurrency is null && retainReviewNotesRunId is null)
{
    builder.Services.AddHostedService(sp => new PrPollingService(
        PrPollTargetBuilder.Build(daemonOptions, sp.GetRequiredService<ILogger<PrPollingService>>()),
        sp.GetServices<IPrProvider>(),
        sp.GetRequiredService<ReviewStore>(),
        sp.GetRequiredService<PrOrchestrator>(),
        sp.GetRequiredService<ILogger<PrPollingService>>(),
        // Maintenance runs on the poller cadence: the PR-lifecycle sweep (registered by the pooled path), the
        // deep-link retention sweep (registered by the S2S path when a window is configured), the stranded-run
        // reconciler, and the eval corpus sweep (registered when a cadence is configured; it gates itself on
        // its own interval rather than running on every tick). Any of them may be absent, in which case the
        // poller keeps polling with whatever remains (design §4.5). The reconciler runs before the eval sweep
        // so it observes the state this cycle's polls left behind, and the eval sweep runs last because it
        // only reads: nothing downstream of it depends on when in the cycle it happened.
        sweepAsync: ComposeMaintenanceSweep(
            (
                "PR-lifecycle",
                sp.GetService<PrLifecycleSweeper>() is { } lifecycleSweeper ? lifecycleSweeper.SweepAsync : null
            ),
            (
                "deep-link retention",
                sp.GetService<DeepLinkRetentionSweeper>() is { } retentionSweeper ? retentionSweeper.SweepAsync : null
            ),
            (
                "stranded-run",
                sp.GetService<StrandedRunReconciler>() is { } strandedReconciler ? strandedReconciler.SweepAsync : null
            ),
            (
                "eval-corpus",
                sp.GetService<EvalCorpusSweepSchedule>() is { } evalCorpusSweep ? evalCorpusSweep.SweepAsync : null
            )
        ),
        // The reporter is passed so the startup no-change-on-a-first-review rate reaches the CONSOLE, which is
        // filtered to Warning for every category except this one. GetRequiredService, not GetService: a missing
        // registration must fail at startup rather than leave the standing check silently inert, which is the
        // exact failure mode — a control that is present and does nothing — this check exists to catch.
        progress: sp.GetRequiredService<ReviewProgressReporter>(),
        firstReviewLookbackDays: daemonOptions.FirstReviewSentinelLookbackDays,
        commentReaders: sp.GetServices<IReviewCommentPublisher>()
    ));
}

// Chains the optional maintenance sweeps into the poller's single seam, in the order they were introduced:
// the lifecycle sweep first, so its today's-semantics timing is unchanged by the sweeps landing behind it.
// The poller already wraps the whole seam in its own try/catch, so a throwing sweep skips the rest of THIS
// cycle and all of them are retried on the next one — harmless against a 24-hour ceiling checked every
// 30 seconds.
//
// Each sweep is NAMED, and a failure is rethrown carrying its name (#455 item 5). The poller's log line
// said "PR-lifecycle sweep failed" for whichever of the four threw, which was true when there was one and
// now sends an operator to the wrong component three times out of four. The name is attached here because
// this is the only place that knows which delegate is which. Wrapping is unconditional — a single
// registered sweep is named too — and cancellation is passed through untouched, so a clean shutdown is
// still told from a failure by type rather than logged as an error.
static Func<CancellationToken, Task>? ComposeMaintenanceSweep(
    params (string Name, Func<CancellationToken, Task>? Sweep)[] sweeps
)
{
    var present = sweeps.Where(s => s.Sweep is not null).ToArray();
    if (present.Length == 0)
    {
        return null;
    }

    return async ct =>
    {
        foreach (var (name, sweep) in present)
        {
            try
            {
                await sweep!(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new MaintenanceSweepException(name, ex);
            }
        }
    };
}

// ── HTTP surface ───────────────────────────────────────────────────────────────────────────────
// Gateway callbacks retain their existing shared-secret authentication. The workflow publication
// callback has a separate host credential and validates each active workflow invocation.
// Context discovery returns 200 so the callback never tears down the sandbox
// session. MVC discovery remains restricted to the two gateway controllers.
builder
    .Services.AddControllers()
    .ConfigureApplicationPartManager(apm =>
    {
        // AuthWebhookController lives in LmAgentInfra (a referenced library, not auto-discovered), and
        // DiscoveryController lives in this daemon assembly; add both parts explicitly (the daemon
        // assembly may already be present via default population — guard against a duplicate), then
        // filter discovery to those two controllers.
        apm.ApplicationParts.Add(new AssemblyPart(typeof(AuthWebhookController).Assembly));
        var daemonAssembly = typeof(CodeReviewDaemon.Sample.Controllers.DiscoveryController).Assembly;
        if (!apm.ApplicationParts.OfType<AssemblyPart>().Any(p => p.Assembly == daemonAssembly))
        {
            apm.ApplicationParts.Add(new AssemblyPart(daemonAssembly));
        }
        foreach (
            var existing in apm
                .FeatureProviders.OfType<Microsoft.AspNetCore.Mvc.Controllers.ControllerFeatureProvider>()
                .ToList()
        )
        {
            _ = apm.FeatureProviders.Remove(existing);
        }
        apm.FeatureProviders.Add(new DaemonControllerFeatureProvider());
    });

var app = builder.Build();
CodeReviewDaemonOptions.ValidateWorkflowConfiguration(
    builder.Configuration.GetSection(CodeReviewDaemonOptions.SectionName),
    warning => app.Logger.LogWarning("{WorkflowMigrationWarning}", warning)
);

// The gateway↔webhook boundary is the shared secret the shared AuthWebhookController verifies (see the
// gateway-callback note above). The plan §9 HMAC middleware is intentionally NOT wired — the real gateway
// does not sign its callbacks, so requiring a signature rejected every real callback.
//
// Routes are mapped here — before every early-return branch below, including --run-pr — because --run-pr
// starts this same host (via app.StartAsync()) to serve S2S callbacks for the duration of its one
// orchestrated run (security review rounds 1/2); the routes must already exist by the time that happens.
// Mapping them here is harmless for --workflow-operation/--list-candidate-prs too: neither ever reaches
// app.StartAsync()/app.Run(), so a mapped-but-never-served route does nothing.
app.MapControllers();
app.MapPost(
    "api/workflow/publication",
    async (HttpContext context, WorkflowPublicationGateway gateway) =>
    {
        if (!gateway.Authenticate(context.Request.Headers.Authorization.ToString()))
            return Results.Unauthorized();
        if (context.Request.ContentLength is > 131072)
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        try
        {
            var request = await context.Request.ReadFromJsonAsync<WorkflowPublicationRequest>(context.RequestAborted);
            if (request is null)
                return Results.BadRequest();
            return Results.Json(await gateway.InvokeAsync(request, context.RequestAborted));
        }
        catch (UnauthorizedAccessException)
        {
            return Results.Unauthorized();
        }
        catch (Exception error) when (error is JsonException or ArgumentException)
        {
            return Results.BadRequest(new { Error = "Invalid publication request." });
        }
        catch (InvalidOperationException)
        {
            return Results.Conflict(new { Error = "Publication could not be confirmed for this invocation." });
        }
    }
);

if (workflowOperation is not null)
{
    // Building resolves configuration and credentials; hosted polling never starts in this child.
    return await WorkflowScriptHost.RunAsync(
        workflowOperation,
        Console.In,
        Console.Out,
        Console.Error,
        app.Services.GetRequiredService<WorkflowOperationDispatcher>().DispatchAsync,
        CancellationToken.None
    );
}

// Command output is compact and machine-readable; enums render by name rather than ordinal so an
// operator (or a script) reading stdout never has to cross-reference ReviewRunAxes.cs.
var commandOutputOptions = new JsonSerializerOptions
{
    WriteIndented = true,
    Converters = { new JsonStringEnumConverter() },
};

// Task #82, requirement 4 — the explicit redo. It is reached ONLY from the operator-typed flag parsed at
// the top of this file: building the host resolves configuration, hosted polling never starts in this
// child (app.Run() below is never reached, so the registered PrPollingService is constructed by nothing
// and its loop never begins), and no other code path in the daemon calls RedoReviewArtifactBranchCommand
// at all. It is deletion-only: it removes a published artifact branch and, on a VERIFIED non-quarantined
// deletion, records a single-use ReviewRerunAuthorization — it never re-admits a run itself, and
// `artifactBranchCapability` is still Denied here, so nothing in this arm can push a new commit. Task #85:
// the command still authorizes exactly one thing at the host git boundary — a single guarded
// (compare-and-swap) DELETE of the one branch/SHA it just finished proving it owns — and only grants that
// after its own full refusal-gate sequence passes, from inside RunUnderLocksAsync itself, not from here.
//
// The process-wide `coordinatorLease` above is deliberately NOT taken for this command: the command
// acquires the identical WorkflowCoordinatorLease itself, around its own gates, so that "no daemon is
// running" is checked at the point the state is read rather than minutes earlier at startup. Taking it
// here too would self-deadlock.
if (redoArtifactBranchRequest is { } redoRequest)
{
    var redoLogger = app.Services.GetRequiredService<ILogger<Program>>();
    var redoTargets = PrPollTargetBuilder
        .Build(app.Services.GetRequiredService<CodeReviewDaemonOptions>(), redoLogger)
        .Where(target =>
            string.Equals(target.Repo.DisplayName, redoRequest.RepoKey, StringComparison.OrdinalIgnoreCase)
            || string.Equals(target.Repo.NormalizedKey, redoRequest.RepoKey, StringComparison.OrdinalIgnoreCase)
        )
        .Select(target => target.Repo)
        .DistinctBy(repo => repo.NormalizedKey, StringComparer.OrdinalIgnoreCase)
        .ToList();
    if (redoTargets.Count != 1)
    {
        // Outside the allow-list, or ambiguous. Either way there is no single repository to own a branch.
        redoLogger.LogError(
            "'{RepoKey}' does not resolve to exactly one allow-listed repository ({Count} matches).",
            redoRequest.RepoKey,
            redoTargets.Count
        );
        return 64;
    }
    var redoRetention = app.Services.GetRequiredService<HostRetentionWorkspace>();
    var redoLoggerFactory = app.Services.GetRequiredService<ILoggerFactory>();
    var redoResult = await RedoReviewArtifactBranchCommand.RunAsync(
        app.Services.GetRequiredService<ReviewStore>(),
        redoTargets[0],
        redoRequest.PrId,
        redoRetention.RepoRoot,
        databasePath,
        new ReviewBranchManager(
            new GitRunner(redoRetention.Git),
            redoRetention.FileSystem,
            redoLoggerFactory.CreateLogger<ReviewBranchManager>()
        ),
        hostGitPushAuthorization,
        redoLogger,
        TimeProvider.System,
        app.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping
    );
    Console.WriteLine(JsonSerializer.Serialize(redoResult, commandOutputOptions));
    return
        redoResult.Outcome is RedoReviewArtifactBranchOutcome.Deleted or RedoReviewArtifactBranchOutcome.AlreadyAbsent
        ? 0
        : 1;
}

if (listCandidatePrs)
{
    // Task #81, Command A. Building resolves configuration only; hosted polling never starts in this
    // child (app.Run() below is never reached), and this command takes no ReviewStore/PrOrchestrator
    // dependency at all — it cannot create a run, update a cursor, or allocate a slot.
    var candidates = await ListCandidatePrsCommand.RunAsync(
        app.Services.GetRequiredService<CodeReviewDaemonOptions>(),
        [.. app.Services.GetServices<IPrProvider>()],
        app.Services.GetRequiredService<ILogger<Program>>(),
        CancellationToken.None
    );
    Console.WriteLine(JsonSerializer.Serialize(candidates, commandOutputOptions));
    return 0;
}

// One-time, non-blocking notice for the keyless dev path (see the per-app identity block above) — never
// logs the key itself, since none was configured.
if (daemonKeyMissing)
{
    app.Logger.LogWarning(
        "CRD_SANDBOX_APP_KEY is not set; connecting to the sandbox gateway as app '{AppId}' with no key "
            + "(keyless AUTH_ENFORCE=off dev path). Set CRD_SANDBOX_APP_KEY for a gateway that enforces auth.",
        daemonAppId
    );
}

app.Logger.LogInformation(
    "Review daemon startup: profile {Profile}; URLs {Urls}; auth webhook base {WebhookBaseUrl}; "
        + "auth routes {GithubWebhookUrl}, {AdoWebhookUrl}; LmStreaming {LmStreamingBaseUrl}; "
        + "comment posting {CommentPosting}; Git retention {GitRetention}; PR polling {PrPolling}; "
        + "configured PR targets {EnabledRepoCount}; review slots {ReviewPoolSize}; "
        + "workspace environment keys [{WorkspaceEnvKeys}].",
    reviewProfile ?? builder.Environment.EnvironmentName,
    builder.Configuration["Urls"] ?? "(Kestrel defaults)",
    authOptions.Webhook.CallbackBaseUrl,
    $"{authOptions.Webhook.CallbackBaseUrl}/api/auth/webhook/github",
    $"{authOptions.Webhook.CallbackBaseUrl}/api/auth/webhook/ado",
    daemonOptions.LmStreamingBaseUrl ?? "(disabled)",
    daemonOptions.EnableCommentPosting ? "enabled" : "disabled",
    daemonOptions.EnableGitPush ? "enabled" : "disabled",
    daemonOptions.EnablePrPolling ? "enabled" : "disabled",
    daemonOptions.EnabledRepos.Count,
    daemonOptions.ReviewPoolSize,
    daemonOptions.WorkspaceEnv.Count > 0
        ? string.Join(", ", daemonOptions.WorkspaceEnv.Keys.Order(StringComparer.Ordinal))
        : "(none)"
);

if (daemonOptions.UseS2SReviewAgent)
{
    await app
        .Services.GetRequiredService<LmStreamingS2SClient>()
        .EnsureHostContractAsync(CancellationToken.None, requireSandboxEnv: daemonOptions.WorkspaceEnv.Count > 0)
        .ConfigureAwait(false);
    app.Logger.LogInformation(
        "LmStreaming review host preflight passed at {LmStreamingBaseUrl}; sandbox environment support required: {SandboxEnvRequired}.",
        daemonOptions.LmStreamingBaseUrl,
        daemonOptions.WorkspaceEnv.Count > 0
    );
}

async Task<RunSinglePrResult> AdmitApprovedAsync(RunSinglePrPrepareResult prepared, CancellationToken ct)
{
    using var capabilityScope = ReviewArtifactBranchCapability.EnterScope();
    using var pushScope = hostGitPushAuthorization.EnterScope();
    return await RunSinglePrCommand
        .AdmitAndRunAsync(
            prepared,
            app.Services.GetRequiredService<ReviewStore>(),
            app.Services.GetRequiredService<PrOrchestrator>(),
            ct,
            commentReaders: [.. app.Services.GetServices<IReviewCommentPublisher>()],
            onIdentityRevalidated: (target, descriptor) =>
            {
                var capability = ReviewArtifactBranchCapability.Grant(
                    target.Repo.DisplayName,
                    descriptor.PrId,
                    descriptor.HeadSha
                );
                ReviewArtifactBranchCapability.SetCurrent(capability);
                hostGitPushAuthorization.AuthorizePush(capability.AuthorizedBranch(target.Repo));
            },
            fresh: scopedCommand.Fresh,
            canStartFresh: app.Services.GetRequiredService<ReviewWorkflowRunner>().CanStartFreshAsync
        )
        .ConfigureAwait(false);
}

if (scopedCommand.ResetRunId is { } resetRunId)
{
    try
    {
        var result = await ResetReviewRunCommand
            .RunAsync(
                resetRunId,
                scopedCommand.ConfirmReset,
                databasePath,
                daemonOptions,
                app.Services.GetRequiredService<ReviewStore>(),
                app.Services.GetRequiredService<WorkflowWorkspace>(),
                app.Services.GetRequiredService<IWorkflowStore>(),
                app.Services.GetRequiredService<IReviewSessionProvisioner>(),
                app.Services.GetRequiredService<LmStreamingS2SClient>(),
                app.Services.GetRequiredService<ILoggerFactory>(),
                CancellationToken.None
            )
            .ConfigureAwait(false);
        Console.WriteLine(result.ToJsonString());
        return 0;
    }
    catch (Exception error) when (error is not OutOfMemoryException)
    {
        app.Logger.LogError("Scoped review reset refused: {Reason}", error.Message);
        return 1;
    }
}

if (retainReviewNotesRunId is { } notesRunId)
{
    if (daemonOptions.EnablePrPolling || daemonOptions.EnableCommentPosting || daemonOptions.EnableGitPush)
        throw new InvalidOperationException(
            "Supplemental retention requires all broad write and polling gates disabled."
        );
    using var lease = RunPrCoordinatorLease.TryAcquire(databasePath);
    if (!lease.IsAcquired)
        return 1;
    var store = app.Services.GetRequiredService<ReviewStore>();
    var run = store.GetReviewRun(notesRunId) ?? throw new InvalidOperationException("Review run is missing.");
    if (run.WorkflowStatus != WorkflowStatus.Completed)
        throw new InvalidOperationException("Review must finish before supplemental retention.");
    var repo = store.GetRepo(run.RepoId) ?? throw new InvalidOperationException("Review repository is missing.");
    await app.StartAsync().ConfigureAwait(false);
    try
    {
        var prepared = await RunSinglePrCommand
            .PrepareAsync(
                daemonOptions,
                [.. app.Services.GetServices<IPrProvider>()],
                app.Services.GetRequiredService<ILogger<Program>>(),
                repo.DisplayName,
                run.PrId,
                run.HeadSha,
                run.BaseSha ?? throw new InvalidOperationException("Review base is missing."),
                CancellationToken.None
            )
            .ConfigureAwait(false);
        if (!prepared.Accepted)
            throw new InvalidOperationException("Supplemental retention identity revalidation failed.");
        using var capabilityScope = ReviewArtifactBranchCapability.EnterScope();
        using var pushScope = hostGitPushAuthorization.EnterScope();
        var capability = ReviewArtifactBranchCapability.Grant(repo.DisplayName, run.PrId, run.HeadSha);
        ReviewArtifactBranchCapability.SetCurrent(capability);
        hostGitPushAuthorization.AuthorizePush(capability.AuthorizedBranch(repo));
        var retention = app.Services.GetRequiredService<HostRetentionWorkspace>();
        await using (
            var repositoryLease = await HostRetentionWorkspace.AcquireRepositoryLockAsync(
                retention.RepoRoot,
                CancellationToken.None
            )
        )
        {
            await new ReviewSlotPreparer(new GitRunner(retention.Git), retention.FileSystem)
                .EnsureStoreAsync(
                    retention.RepoRoot,
                    daemonOptions.ResolvedStoreUrl
                        ?? throw new InvalidOperationException("Review store URL is missing."),
                    CancellationToken.None
                )
                .ConfigureAwait(false);
        }
        var retained = await app
            .Services.GetRequiredService<WorkflowOperationDispatcher>()
            .RetainCompletedReviewNotesAsync(run, CancellationToken.None)
            .ConfigureAwait(false);
        Console.WriteLine(retained.ToJsonString());
        return 0;
    }
    finally
    {
        await app.StopAsync().ConfigureAwait(false);
    }
}

if (runPrConcurrency is { } concurrency)
{
    if (daemonOptions.EnablePrPolling || daemonOptions.EnableCommentPosting || daemonOptions.EnableGitPush)
    {
        Console.Error.WriteLine("run-pr-stream requires polling, comment posting, and blanket Git push disabled.");
        return 64;
    }
    return await RunPrOrchestration
        .ExecuteStreamAsync(
            Console.In,
            concurrency,
            () => RunPrCoordinatorLease.TryAcquire(databasePath),
            () => app.StartAsync(),
            async (request, ct) =>
            {
                var prepared = await RunSinglePrCommand
                    .PrepareAsync(
                        daemonOptions,
                        [.. app.Services.GetServices<IPrProvider>()],
                        app.Services.GetRequiredService<ILogger<Program>>(),
                        request.RepoKey,
                        request.PrId,
                        request.HeadSha,
                        request.BaseSha,
                        ct
                    )
                    .ConfigureAwait(false);
                return prepared.Accepted
                    ? await AdmitApprovedAsync(prepared, ct).ConfigureAwait(false)
                    : RunSinglePrResult.Rejected(prepared.RejectionReason!.Value);
            },
            value => Console.WriteLine(JsonSerializer.Serialize(value, commandOutputOptions)),
            () => app.StopAsync(),
            app.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping
        )
        .ConfigureAwait(false);
}

if (runPrRequest is { } approvedRun)
{
    // Task #81, Command B — round 4, items 2+3: the whole prepare → lease → host-start → admit+run →
    // host-stop sequence is delegated to RunPrOrchestration.ExecuteAsync so its ordering guarantees (a
    // rejected prepare never attempts a lease; a refused lease never starts the host; the host always stops,
    // even on a thrown exception or a graceful cancellation) are proven once, by composition tests, rather
    // than re-verified by reading this arm. The cancellation token is real: Ctrl+C / application-stopping,
    // not CancellationToken.None, so a long-running review can be interrupted and still stop the host
    // cleanly instead of leaving it running past process shutdown.
    var runPrCancellationToken = app.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping;

    var result = await RunPrOrchestration.ExecuteAsync(
        prepareAsync: ct =>
            RunSinglePrCommand.PrepareAsync(
                app.Services.GetRequiredService<CodeReviewDaemonOptions>(),
                [.. app.Services.GetServices<IPrProvider>()],
                app.Services.GetRequiredService<ILogger<Program>>(),
                approvedRun.RepoKey,
                approvedRun.PrId,
                approvedRun.HeadSha,
                approvedRun.BaseSha,
                ct
            ),
        acquireLease: () =>
        {
            // The daemon and --run-pr apply for the SAME OS-level lease (RunPrCoordinatorLease wraps the
            // identical WorkflowCoordinatorLease.Acquire the daemon uses): an operator must stop the running
            // daemon before a pilot run, or this fails closed here instead of risking two processes
            // allocating the same slot workspace directory.
            var lease = RunPrCoordinatorLease.TryAcquire(databasePath);
            if (!lease.IsAcquired)
            {
                app.Logger.LogWarning(
                    "run-pr could not acquire the workflow coordinator lease: {Reason}",
                    lease.FailureReason
                );
            }
            return lease;
        },
        // Only from here on is the host started: routes were already mapped above and PrPollingService is
        // not registered for this mode (see its hosted-service registration above), so this starts exactly
        // the surface --run-pr needs — nothing polls, nothing else serves.
        startHostAsync: () => app.StartAsync(),
        admitAndRunAsync: AdmitApprovedAsync,
        stopHostAsync: () => app.StopAsync(),
        runPrCancellationToken
    );

    Console.WriteLine(JsonSerializer.Serialize(result, commandOutputOptions));
    return result.Admitted ? 0 : 1;
}

if (setupWorkspaceRequested)
{
    // Ordering constraint (plan §5): the webhook must be listening before any authenticated Git touches
    // the gateway. `StartAsync` brings Kestrel up without blocking, exactly like `Run` would, but returns
    // control here so the bootstrap can run before we hand off to `WaitForShutdownAsync`.
    await app.StartAsync().ConfigureAwait(false);

    var slotWorkspace = app.Services.GetRequiredService<ReviewSlotWorkspace>();
    var sessionProvisioner = app.Services.GetRequiredService<IReviewSessionProvisioner>();
    var storeUrl =
        daemonOptions.ResolvedStoreUrl
        ?? throw new InvalidOperationException(
            "--setup-workspace requires a configured review store (CrossRepoStoreUrl or ReviewBotRepoUrl)."
        );

    await SetupWorkspaceCommand
        .RunAsync(
            slotWorkspace.Pool,
            sessionProvisioner,
            storeUrl,
            sanitizedArgv: processArgv,
            workingDirectory: Directory.GetCurrentDirectory(),
            app.Logger,
            CancellationToken.None,
            app.Services.GetRequiredService<ReviewStore>(),
            daemonOptions.Limits.CommandTimeout
        )
        .ConfigureAwait(false);

    await app.WaitForShutdownAsync().ConfigureAwait(false);
    return 0;
}

app.Run();

return 0;

/// <summary>Exposed for the route-exposure test host (WebApplicationFactory&lt;Program&gt;).</summary>
public partial class Program;
