using AchieveAi.LmDotnetTools.LmAgentInfra.Auth;
using AchieveAi.LmDotnetTools.LmAgentInfra.Controllers;
using AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using CodeReviewDaemon.Sample.Agents;
using CodeReviewDaemon.Sample.Auth;
using CodeReviewDaemon.Sample.Configuration;
using CodeReviewDaemon.Sample.Eval;
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

// ── Config profile selection ─────────────────────────────────────────────────────────────────────
// `--review <name>` selects the hosting environment so ASP.NET layers `appsettings.<name>.json` over
// the base config — e.g. `--review mcqdb` loads appsettings.mcqdb.json (ADO daemon), `--review
// achieveai` loads appsettings.achieveai.json (GitHub daemon). This is the single operator knob:
// every setting (repo/store/paths/ports/gateway) lives in that one profile file, so no launch env
// vars are required. Absent the flag, the environment resolves as usual (DOTNET_ENVIRONMENT/default).
var (reviewProfile, maxPrAgeDaysOverride, hostArgs) = ReviewProfileArgs.Extract(args);

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = hostArgs,
    EnvironmentName = reviewProfile, // null ⇒ default environment resolution (base appsettings only)
});

// A `--days N` / `--max-pr-age-days N` flag overrides the profile's CodeReviewDaemon:MaxPrAgeDays recency
// bound for this run. Injected as the last (highest-precedence) config source so it wins over appsettings,
// and BEFORE the section is bound below.
if (maxPrAgeDaysOverride is int maxPrAgeDaysFlag)
{
    builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
    {
        [$"{CodeReviewDaemonOptions.SectionName}:{nameof(CodeReviewDaemonOptions.MaxPrAgeDays)}"] =
            maxPrAgeDaysFlag.ToString(System.Globalization.CultureInfo.InvariantCulture),
    });
}

// ── Feature flags ────────────────────────────────────────────────────────────────────────────────
// Conservative defaults (collect-only, GitHub-only, repo allow-list empty); each flag is an explicit
// operator opt-in to a higher-blast-radius behavior. See CodeReviewDaemonOptions.
var daemonOptions =
    builder.Configuration.GetSection(CodeReviewDaemonOptions.SectionName).Get<CodeReviewDaemonOptions>()
    ?? new CodeReviewDaemonOptions();

// Refuse startup on a malformed EnabledRepos entry, naming it — encoding the segments consistently (issue
// #485) is not the same as validating them, and a bad entry otherwise polls the wrong repo or nothing at all.
PrPollTargetBuilder.ValidateEnabledRepos(daemonOptions);

builder.Services.AddSingleton(daemonOptions);

// The ADO org(s) whose legacy {org}.visualstudio.com submodule URLs the host-side git rewrites to
// dev.azure.com (so the modern ADO credential authenticates the fetch — see HostGitCredentialEnv). Derived
// from the configured ADO context and shared by every HostGitCommandRunner below; a GitHub-only daemon gets
// an empty set (no rewrite emitted).
var hostGitAdoOrgs = DeriveAdoOrgs(daemonOptions);

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
var daemonAppId = Environment.GetEnvironmentVariable("CRD_SANDBOX_APP_ID")
    ?? builder.Configuration["SandboxGateway:AppId"] ?? "codereview-daemon";
var daemonAppKey = Environment.GetEnvironmentVariable("CRD_SANDBOX_APP_KEY")
    ?? builder.Configuration["SandboxGateway:AppKey"];
var daemonKeyMissing = string.IsNullOrWhiteSpace(daemonAppKey);
if (!daemonKeyMissing)
{
    SandboxCredential.ValidateKeyOrThrow(daemonAppId, daemonAppKey!);
}
var daemonCredential = new SandboxCredential(daemonAppId, daemonAppKey ?? string.Empty);

// The already-running sandbox gateway's base URL, resolved once (env overrides config, then the
// 3000 default). Threaded into every gateway consumer so a profile can set SandboxGateway:BaseUrl
// and nothing needs CRD_SANDBOX_GATEWAY in env.
var gatewayBaseUrl = Environment.GetEnvironmentVariable("CRD_SANDBOX_GATEWAY")
    ?? builder.Configuration["SandboxGateway:BaseUrl"] ?? "http://127.0.0.1:3000";

// ── OAuth auth-provider services ───────────────────────────────────────────────────────────────
// Shared with LmStreaming.Sample (see its Program.cs): the sandbox gateway calls back into the
// auth webhook to obtain a per-provider bearer/basic credential for an outbound request, and these
// providers mint it. The daemon reviews GitHub PRs by default; Azure DevOps is opt-in via
// CodeReviewDaemon:EnableAdoProvider.
var authOptions =
    builder.Configuration.GetSection(AuthOptions.SectionName).Get<AuthOptions>() ?? new AuthOptions();
builder.Services.AddSingleton(authOptions);

var oauthTokenDir = string.IsNullOrWhiteSpace(authOptions.TokenStoreDir)
    ? Path.Combine(AppContext.BaseDirectory, "oauth-tokens")
    : authOptions.TokenStoreDir;
builder.Services.AddSingleton<IOAuthTokenStore>(sp => new FileOAuthTokenStore(
    oauthTokenDir,
    sp.GetRequiredService<ILogger<FileOAuthTokenStore>>()));
builder.Services.AddSingleton(sp => new SessionSecretStore(
    Path.Combine(oauthTokenDir, "session-secrets"),
    sp.GetRequiredService<ILogger<SessionSecretStore>>()));

// Dual-register each provider (concrete + IOAuthTokenProvider alias-to-concrete) so the
// enumerable-consuming callers (AuthWebhookController, OAuthTokenHydrator) and any concrete-typed
// consumer share a single singleton instance per provider.
builder.Services.AddSingleton(sp => new GitHubOAuthProvider(
    authOptions.Github,
    sp.GetRequiredService<IOAuthTokenStore>(),
    new HttpClient(),
    sp.GetRequiredService<ILogger<GitHubOAuthProvider>>()));
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
    sp.GetRequiredService<ILogger<StorePolicyRefusalRecorder>>()));

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
    daemonOptions.Limits));
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
// Per-app workspace rooting (gateway ADR 0028): when the adopted gateway roots workspaces at
// WORKSPACE_BASE_PATH/<app-dir>/<workspace>, the daemon prepares its store + measures slot paths under that
// same <app-dir> (derived from AppId) so the app-dir-less workspace field it sends re-roots to the prepared
// store. Off by default (flat, pre-ADR-0028). sandboxGatewayOptions.WorkspaceBasePath stays the CONFIGURED
// base (the gateway's own WORKSPACE_BASE_PATH); only the daemon-side prep/relative base gains <app-dir>.
var effectiveWorkspaceBase = SandboxAppDir.EffectiveBase(
    sandboxGatewayOptions.WorkspaceBasePath, daemonAppId, daemonOptions.PerAppWorkspaceRooting);
builder.Services.AddSingleton(sp => new SandboxSessionRegistry(
    new SandboxGatewayLifetime(
        sandboxGatewayOptions,
        sp.GetRequiredService<ILogger<SandboxGatewayLifetime>>(),
        new HttpClient(new GatewayAuthHandler(daemonAppId, daemonKeyMissing ? null : daemonAppKey) { InnerHandler = new HttpClientHandler { AllowAutoRedirect = false } })),
    sandboxGatewayOptions,
    sp.GetRequiredService<ILogger<SandboxSessionRegistry>>(),
    // Bounds the gateway create/destroy calls (mirrors LmStreaming.Sample's registration); the handler
    // attaches the per-app bearer headers to every gateway REST call. Auto-redirect is disabled so a
    // cross-origin 3xx can never replay the X-Sbx-* credential headers to a redirect target.
    new HttpClient(new GatewayAuthHandler(daemonAppId, daemonKeyMissing ? null : daemonAppKey) { InnerHandler = new HttpClientHandler { AllowAutoRedirect = false } })
    {
        Timeout = TimeSpan.FromSeconds(30),
    },
    authOptions,
    sp.GetRequiredService<SessionSecretStore>()));
builder.Services.AddSingleton<ISandboxSessionSource>(sp =>
    new RegistrySessionSource(sp.GetRequiredService<SandboxSessionRegistry>()));
builder.Services.AddSingleton<IReviewSessionProvisioner>(sp => new ReviewSessionProvisioner(
    sp.GetRequiredService<ISandboxSessionSource>(),
    daemonOptions,
    sp.GetRequiredService<ILoggerFactory>(),
    daemonCredential,
    // The gateway's host workspace base: a pooled run mounts its leased slot at /workspace by expressing
    // the slot's host path relative to this base (ReviewSessionProvisioner.GetOrCreateForSlotAsync). The
    // pool root is defaulted under it below so the slot always resolves to a path inside the base. Under
    // per-app rooting this base already includes <app-dir> (see effectiveWorkspaceBase above).
    effectiveWorkspaceBase,
    gatewayBaseUrl));

// Sub-agent discovery (Task 12): the executor asks for `code-reviewer:*` sub-agents through the same
// narrow-adapter pattern as ISandboxSessionSource above, so it never depends on the registry's full
// surface directly.
builder.Services.AddSingleton<IDiscoveredItemsSource>(sp =>
    new RegistryDiscoverySource(sp.GetRequiredService<SandboxSessionRegistry>()));

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
        + "Set CodeReviewDaemon:UseS2SReviewAgent=true and configure LmStreamingBaseUrl.");
}

if (string.IsNullOrWhiteSpace(daemonOptions.LmStreamingBaseUrl))
{
    throw new InvalidOperationException(
        "UseS2SReviewAgent is on but LmStreamingBaseUrl is not configured; set it to the LmStreaming review "
        + "host base URL (e.g. http://localhost:5051).");
}

// JudgeModelId is a PER-CALL model id and S2S provision carries no model field
// (ProvisionConversationRequest is {WorkspaceId, ProviderId, ModeId}), so S2SReviewAgentLoopFactory
// discards it — the same hazard appsettings.s2s.json documents for KnowledgeModelId. Accepting it here
// would read as "the judge runs on opus" while the judge kept running on whatever LmStreamingProviderId
// resolves: the review's own model. That is not a silent no-op, it is a silent REVERSAL of the setting's
// entire purpose, so refuse it at boot rather than let the operator believe the bias is gone.
if (!string.IsNullOrWhiteSpace(daemonOptions.JudgeModelId))
{
    throw new InvalidOperationException(
        "CodeReviewDaemon:JudgeModelId cannot be honoured while UseS2SReviewAgent is on: provision carries "
        + "no model field, so the review host resolves the judge's model from LmStreamingProviderId exactly "
        + "as it does the review's. Unset JudgeModelId (the judge then grades on the review's own model, "
        + "which the judge stage warns about and the judge artifact records), or run the judge against a "
        + "review host whose provider resolves a different model.");
}
if (string.IsNullOrWhiteSpace(effectiveWorkspaceBase))
{
    throw new InvalidOperationException(
        "UseS2SReviewAgent is on but no workspace base path is configured "
        + "(SandboxGateway:WorkspaceBasePath / CRD_WORKSPACE_BASE_PATH); the S2S review preparer clones the "
        + "PR checkout under it, and the review host must mount the same base.");
}

// Normalize to a host-root base with a trailing slash so the client's relative paths ("api/workspaces",
// "api/conversations") resolve correctly against HttpClient.BaseAddress.
var lmStreamingBaseUri = new Uri(
    daemonOptions.LmStreamingBaseUrl.TrimEnd('/') + "/",
    UriKind.Absolute);

// Outbound S2S client over the review host. The raw HttpClient is intentionally long-lived (mirrors the
// gateway HttpClients above); it forwards the daemon's own gateway identity (X-Sbx-App-*) so the sandbox
// the review provisions is attributed to codereview-daemon, and the X-S2S-Auth secret (never logged).
builder.Services.AddSingleton(sp => new LmStreamingS2SClient(
    new HttpClient { BaseAddress = lmStreamingBaseUri },
    daemonOptions.LmStreamingS2SSecret,
    daemonAppId,
    daemonKeyMissing ? null : daemonAppKey));

// Completion-source seam for the recursive review completion barrier: reads the review host's versioned
// recursive sub-agent tree over the same S2S client. IMPORTANT — the review host (LmStreaming.Sample) must
// be deployed with the recursive `?recursive=true` endpoint BEFORE the daemon barrier is enabled.
//
// The same object is also the daemon's read half of the agent directory (IReviewAgentTranscriptSource):
// once the barrier hands back a settled roster, the notes artifacts fetch each named agent's transcript to
// write per-reviewer findings files. Registered concrete-first so both interfaces share ONE instance —
// two factory registrations would silently give the barrier and the artifact builder separate objects.
builder.Services.AddSingleton(sp =>
    new S2SReviewSubAgentCompletionSource(sp.GetRequiredService<LmStreamingS2SClient>()));
builder.Services.AddSingleton<IReviewSubAgentCompletionSource>(sp =>
    sp.GetRequiredService<S2SReviewSubAgentCompletionSource>());
builder.Services.AddSingleton<IReviewAgentTranscriptSource>(sp =>
    sp.GetRequiredService<S2SReviewSubAgentCompletionSource>());

// Host-side workspace preparer: clones the PR checkout under the shared WORKSPACE_BASE_PATH and ensures
// the LmStreaming workspace points at that leaf. Uses the SAME host-backed GitRunner the pooled path uses
// (privileged, credentialed, never the sandbox runner).
builder.Services.AddSingleton(sp => new S2SReviewWorkspacePreparer(
    sp.GetRequiredService<LmStreamingS2SClient>(),
    new GitRunner(new HostGitCommandRunner(
        BuildHostGitCredentialsSource(sp),
        sp.GetRequiredService<ILogger<HostGitCommandRunner>>(),
        hostGitAdoOrgs)),
    effectiveWorkspaceBase!,
    daemonOptions.LmStreamingReviewMarketplace,
    sp.GetRequiredService<ILogger<S2SReviewWorkspacePreparer>>()));

// Gateway prerequisite probe (RequireSkillSupport). The review runs in a conversation the review host
// provisions, so the equivalent check reads the gateway's marketplace catalog directly.
builder.Services.AddSingleton<IGatewaySkillProbe>(sp => new GatewaySkillProbe(
    gatewayBaseUrl,
    daemonCredential,
    sp.GetRequiredService<ILogger<GatewaySkillProbe>>()));

// Deep-link retention ceiling. Every posted comment carries ?threadId=, so the hosted conversation must
// OUTLIVE its review — but not forever. When a window is configured, each minted conversation is recorded
// in the ledger and discarded once it has aged past it.
var deepLinkRetention = daemonOptions.DeepLinkRetentionHours > 0
    ? TimeSpan.FromHours(daemonOptions.DeepLinkRetentionHours)
    : (TimeSpan?)null;

if (deepLinkRetention is { } retentionWindow)
{
    builder.Services.AddSingleton(sp => new DeepLinkRetentionSweeper(
        sp.GetRequiredService<ReviewStore>(),
        sp.GetRequiredService<LmStreamingS2SClient>(),
        retentionWindow,
        sp.GetRequiredService<ILogger<DeepLinkRetentionSweeper>>()));
}

builder.Services.AddSingleton<IReviewAgentLoopFactory>(sp =>
{
    var store = sp.GetRequiredService<ReviewStore>();
    return new S2SReviewAgentLoopFactory(
        sp.GetRequiredService<LmStreamingS2SClient>(),
        daemonOptions,
        sp.GetRequiredService<ILoggerFactory>(),
        onConversationMinted: deepLinkRetention is null
            ? null
            : (threadId, title) => store.RecordDeepLinkConversation(threadId, title));
});


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
    daemonOptions.MaxPrsPerPage));

// GitHub review posting is host-side (like ADO below). Agent-owned posting via code-reviewer:post-pr-review was
// abandoned — the agent loaded the skill but never actually posted — so DaemonReviewStageExecutor.PostAsync posts
// GitHub reviews through this publisher. Registered unconditionally (every profile reviews GitHub by default).
builder.Services.AddSingleton<IReviewCommentPublisher>(sp => new GitHubReviewCommentPublisher(
    sp.GetRequiredService<PolicyEnforcedHttpClientFactory>().Create("github"),
    sp.GetRequiredService<GitHubOAuthProvider>(),
    sp.GetRequiredService<ILogger<GitHubReviewCommentPublisher>>()));

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
        daemonOptions.MaxPrsPerPage));

    // ADO review posting is host-side (like GitHub above): DaemonReviewStageExecutor.PostAsync posts ADO
    // reviews through this publisher. Resolve the CONCRETE AdoOAuthProvider, not IOAuthTokenProvider, which is
    // ambiguous (both GitHub and ADO providers register against it).
    builder.Services.AddSingleton<IReviewCommentPublisher>(sp => new AdoReviewCommentPublisher(
        sp.GetRequiredService<PolicyEnforcedHttpClientFactory>().Create("ado"),
        sp.GetRequiredService<AdoOAuthProvider>(),
        sp.GetRequiredService<ILogger<AdoReviewCommentPublisher>>()));
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
        var segments = (entry ?? string.Empty)
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
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
        var runner = new HostGitCommandRunner(
            BuildHostGitCredentialsSource(sp),
            sp.GetRequiredService<ILogger<HostGitCommandRunner>>(),
            hostGitAdoOrgs);
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
    // The pool root MUST resolve INSIDE effectiveWorkspaceBase so a leased slot's host path is expressible
    // relative to that base (the workspace field the gateway re-roots under <app-dir>). Under per-app rooting
    // the explicit ReviewPoolHostRoot's leaf (e.g. "review-pool-mcqdb") is re-based under <app-dir>; a flat
    // path would land outside the base and the slot mount would silently degrade to the per-run mount.
    var poolLeaf = string.IsNullOrWhiteSpace(daemonOptions.ReviewPoolHostRoot)
        ? "review-pool"
        : Path.GetFileName(daemonOptions.ReviewPoolHostRoot.TrimEnd('/', '\\'));
    var poolRoot = daemonOptions.PerAppWorkspaceRooting && !string.IsNullOrWhiteSpace(effectiveWorkspaceBase)
        ? $"{effectiveWorkspaceBase!.TrimEnd('/', '\\')}/{poolLeaf}"
        : !string.IsNullOrWhiteSpace(daemonOptions.ReviewPoolHostRoot)
            ? daemonOptions.ReviewPoolHostRoot
            : !string.IsNullOrWhiteSpace(effectiveWorkspaceBase)
                ? Path.Combine(effectiveWorkspaceBase, "review-pool")
                : Path.Combine(AppContext.BaseDirectory, "review-pool");

    var slotDirPrefix = "slot-";
    // S2S flattens the slot to ONE segment directly under the base. The gateway re-roots a workspace by a
    // multi-segment rel path just fine (that is the in-process layout, {base}/review-pool/slot-{i}), but the
    // S2S path names the same directory to LmStreaming as a workspace *directory*, and
    // FileWorkspaceStore.SanitizeDirectory strips '/' and '\\' — "review-pool/slot-0" would silently become
    // "review-poolslot-0", a different (empty) directory the agent would review as if it were the repo.
    // {base}/review-slot-{i} sits BESIDE the untouched review-pool/ used by the in-process profiles.
    if (daemonOptions.UseS2SReviewAgent && !string.IsNullOrWhiteSpace(effectiveWorkspaceBase))
    {
        poolRoot = effectiveWorkspaceBase!.TrimEnd('/', '\\');
        slotDirPrefix = "review-slot-";
    }

    // Host-side only (never mounted) on the in-process path; on S2S the knowledge-extraction arm mounts it as
    // its own workspace, so it must be a sanitize-stable single segment too — hence the distinct leaf name.
    var sweeperRepoRoot = daemonOptions.UseS2SReviewAgent
        ? Path.Combine(poolRoot, "review-sweeper-store")
        : Path.Combine(poolRoot, "sweeper-store");

    builder.Services.AddSingleton(sp =>
    {
        var hostRunner = new HostGitCommandRunner(
            BuildHostGitCredentialsSource(sp),
            sp.GetRequiredService<ILogger<HostGitCommandRunner>>(),
            hostGitAdoOrgs);
        var hostFileSystem = new HostFileSystem();
        var loggerFactory = sp.GetRequiredService<ILoggerFactory>();

        var pool = new ReviewSlotPool(
            daemonOptions.ReviewPoolSize,
            poolRoot,
            daemonOptions.ScratchDirName,
            loggerFactory.CreateLogger<ReviewSlotPool>(),
            slotDirPrefix);

        // A slot directory whose name does not survive LmStreaming's workspace-directory sanitizer unchanged
        // would be mounted as a DIFFERENT, empty directory — a failure that looks like success: the agent finds
        // no repo, reports nothing wrong, and the review passes vacuously. Fail at startup instead.
        if (daemonOptions.UseS2SReviewAgent)
        {
            for (var i = 0; i < daemonOptions.ReviewPoolSize; i++)
            {
                var slotDir = pool.SlotDirectoryName(i);
                var sanitized = S2SReviewWorkspacePreparer.SanitizeLeaf(slotDir);
                if (!string.Equals(slotDir, sanitized, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Review slot directory '{slotDir}' is not stable under LmStreaming's workspace-directory "
                            + $"sanitizer (it becomes '{sanitized}'), so the hosted conversation would be mounted on "
                            + "a different, empty directory. Choose a slot prefix of lowercase letters, digits and "
                            + "dashes only.");
                }
            }
        }

        // The store is a GitHub superproject (AchieveAiReviews); its submodule URLs resolve under "github".
        var hostPreparer = new ReviewSlotPreparer(new GitRunner(hostRunner), hostFileSystem, "github", loggerFactory);
        return new ReviewSlotWorkspace(
            pool,
            hostPreparer,
            (session, provider) => new ReviewSlotPreparer(
                new GitRunner(session.CommandRunner),
                session.FileSystem,
                provider,
                loggerFactory,
                requireSdkOwnershipMarker: true),
            hostRunner,
            hostFileSystem);
    });

    builder.Services.AddSingleton(sp =>
    {
        var slots = sp.GetRequiredService<ReviewSlotWorkspace>();
        var store = sp.GetRequiredService<ReviewStore>();
        var providers = sp.GetServices<IPrProvider>().ToList();
        var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
        var hostGit = new GitRunner(slots.HostRunner);
        // The sweeper is the ONLY caller that merges the notes branch into the default branch, so it is the
        // only ReviewBranchManager that needs to rebuild the Knowledge Base's derived listings afterwards —
        // the review-time managers commit onto the notes branch and merge nothing. See
        // ReviewBranchManager.RebuildDerivedKnowledgeAsync for why a merge commit can un-index entries it
        // kept (issue #218 item 6).
        var listingRegenerator = new KnowledgeIndexRegenerator(
            slots.HostFileSystem, loggerFactory.CreateLogger<KnowledgeIndexRegenerator>());
        var branchManager = new ReviewBranchManager(
            hostGit,
            slots.HostFileSystem,
            loggerFactory.CreateLogger<ReviewBranchManager>(),
            (repoRoot, ct) => listingRegenerator.RegenerateAsync(
                $"{repoRoot.TrimEnd('/')}/{KnowledgeIndexRegenerator.KnowledgeBaseDirectory}", ct));
        var sweepLogger = loggerFactory.CreateLogger("pr-lifecycle-sweep");
        // The configured repos (full identity + provider) let the sweep resolve an orphaned review/* branch —
        // whose new-scheme name carries only the repo slug + PR number — back to a pollable PR.
        var sweepPollTargets = PrPollTargetBuilder.Build(daemonOptions, sweepLogger);

        // At-close extraction (Layer-2, design §1). Wired when EnableKnowledgeAgent or
        // EnableReviewFeedbackAgent is set: on a merged PR, read the PR's accumulated notes off its notes
        // branch once, run the enabled gated extractions over the host store checkout, and let the sweeper's
        // subsequent MergeToDefaultAsync carry the new/updated KnowledgeBase/ writes into the default branch.
        // Both unset → null → the sweep is unchanged.
        var loopFactory = sp.GetRequiredService<IReviewAgentLoopFactory>();
        // Non-null only on the S2S path. The extraction loop there is a HOSTED conversation, and the S2S
        // factory refuses to open one without a workspace — which is why this arm has been a silent no-op on
        // S2S (the factory threw and KnowledgeExtractionCommitter's catch-all swallowed it). Naming the
        // sweeper's own store checkout as a workspace both fixes that and grounds the extraction: the agent
        // can read the existing KnowledgeBase/ before deciding whether this PR taught anything durable.
        var s2sPreparer = sp.GetService<S2SReviewWorkspacePreparer>();
        var sweeperLeaf = Path.GetFileName(sweeperRepoRoot.TrimEnd('/', '\\'));
        Func<ReviewedPr, CancellationToken, Task<KnowledgeExtractionOutcome>>? extractKnowledgeAsync = null;
        if (daemonOptions.EnableKnowledgeAgent || daemonOptions.EnableReviewFeedbackAgent)
        {
            // The committer wraps the gated extraction with the git plumbing that carries its write into the
            // default branch: check the notes branch out, run extraction, and — only when it wrote an entry —
            // commit + push KnowledgeBase/ onto that branch so MergeToDefaultAsync fast-forwards it into main.
            var committer = new KnowledgeExtractionCommitter(
                hostGit, sweeperRepoRoot, loggerFactory.CreateLogger<KnowledgeExtractionCommitter>());
            // KnowledgeModelId (empty ⇒ null ⇒ inherit ReviewModelId) lets the extraction passes run on a
            // dedicated model, e.g. claude-opus-4.8, independent of the gpt-* dispatcher.
            var knowledgeModelId = string.IsNullOrWhiteSpace(daemonOptions.KnowledgeModelId)
                ? null
                : daemonOptions.KnowledgeModelId;
            var extractionLogger = loggerFactory.CreateLogger("at-close-extraction");

            // Idempotent: reuses the workspace pointing at this leaf across every extraction, and across both
            // passes of one PR. Non-null only on the S2S path, where the factory refuses to open a hosted
            // conversation without a workspace.
            async Task<PreparedReviewWorkspace?> EnsureExtractionWorkspaceAsync(ReviewedPr pr, CancellationToken ct)
            {
                if (s2sPreparer is null)
                {
                    return null;
                }

                var workspaceId = await s2sPreparer
                    .EnsureWorkspaceForLeafAsync(sweeperLeaf, "Knowledge extraction store", ct)
                    .ConfigureAwait(false);
                return new PreparedReviewWorkspace(sweeperLeaf, workspaceId, sweeperRepoRoot, pr.PrId);
            }

            async Task<KnowledgeExtractionResult> ExtractCuratedKnowledgeAsync(
                ReviewedPr pr, string notesInput, string sourcePrRef, string todayUtc, CancellationToken ct)
            {
                var workspace = await EnsureExtractionWorkspaceAsync(pr, ct).ConfigureAwait(false);
                await using var loop = loopFactory.Create(
                    DaemonAgentFactory.CreateKnowledgeExtractionProfile(),
                    modelId: knowledgeModelId,
                    threadId: $"knowledge-extract-{pr.Provider}-{pr.PrId}",
                    reviewWorkspace: workspace);
                var agent = new KnowledgeAgent(
                    loop, slots.HostFileSystem, loggerFactory.CreateLogger<KnowledgeAgent>());
                return await agent.TryExtractAsync(sweeperRepoRoot, notesInput, sourcePrRef, todayUtc, ct)
                    .ConfigureAwait(false);
            }

            // Per-developer feedback: the same notes, read for what this PR's AUTHOR keeps getting wrong. Runs
            // on its own conversation so neither pass sees the other's reply — the curated-knowledge prompt
            // forbids naming people and this one is entirely about one person.
            async Task<KnowledgeExtractionResult> ExtractReviewFeedbackAsync(
                ReviewedPr pr, string notesInput, string sourcePrRef, string todayUtc, CancellationToken ct)
            {
                var workspace = await EnsureExtractionWorkspaceAsync(pr, ct).ConfigureAwait(false);
                await using var loop = loopFactory.Create(
                    DaemonAgentFactory.CreateReviewFeedbackExtractionProfile(),
                    modelId: knowledgeModelId,
                    threadId: $"feedback-extract-{pr.Provider}-{pr.PrId}",
                    reviewWorkspace: workspace);
                var agent = new ReviewFeedbackAgent(
                    loop, slots.HostFileSystem, loggerFactory.CreateLogger<ReviewFeedbackAgent>());
                return await agent
                    .TryExtractAsync(sweeperRepoRoot, pr.Author, notesInput, sourcePrRef, todayUtc, ct)
                    .ConfigureAwait(false);
            }

            extractKnowledgeAsync = (pr, ct) =>
            {
                // sourcePrRef is a stable, human-readable id for the source PR; todayUtc is daemon-supplied
                // (deterministic — never the model) and stamped into the entry's `updated` frontmatter.
                var sourcePrRef = $"{pr.Provider}/{pr.Repo.NormalizedKey}/{pr.PrId}";
                return committer.RunAsync(pr.Branch, sourcePrRef, async innerCt =>
                {
                    // Both passes read the SAME notes and write under KnowledgeBase/ on the same notes branch,
                    // so they share one committer run: one checkout, one commit, one push.
                    var notesInput = await ReadPrNotesFromBranchAsync(hostGit, sweeperRepoRoot, pr.Branch, innerCt)
                        .ConfigureAwait(false);
                    var todayUtc = DateTime.UtcNow.ToString(
                        "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

                    var knowledge = daemonOptions.EnableKnowledgeAgent
                        ? await ExtractCuratedKnowledgeAsync(pr, notesInput, sourcePrRef, todayUtc, innerCt)
                            .ConfigureAwait(false)
                        : KnowledgeExtractionResult.Declined(null);
                    var feedback = daemonOptions.EnableReviewFeedbackAgent
                        ? await ExtractReviewFeedbackAsync(pr, notesInput, sourcePrRef, todayUtc, innerCt)
                            .ConfigureAwait(false)
                        : KnowledgeExtractionResult.Declined(null);

                    // Wrote > Failed > Declined, and a write is committed even when the other pass failed —
                    // see AtCloseExtractionSeam.Combine for why holding the commit back would be worse.
                    var combined = AtCloseExtractionSeam.Combine(knowledge, feedback);
                    if (combined.DroppedPass is { } dropped)
                    {
                        extractionLogger.LogWarning(
                            "At-close extraction for {SourcePr}: the {Pass} pass failed while the other wrote; "
                                + "committing the write and dropping the failed pass for this PR.",
                            sourcePrRef,
                            dropped);
                    }

                    return combined.Result;
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

        // Persists across sweeps so a stray review/* branch that maps to no configured repo (e.g. a leftover
        // pushed into this store by another daemon) is warned about ONCE, not on every sweep — one such branch
        // otherwise floods the log (163x in a single mcqdb run).
        var warnedOrphanBranches = new HashSet<string>(StringComparer.Ordinal);

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
                return OrphanBranchReconciler.Reconcile(
                    reviewed, reviewBranches, sweepPollTargets, sweepLogger, warnedOrphanBranches);
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
    ActivatorUtilities.CreateInstance<DaemonReviewStageExecutor>(sp, daemonCredential, gatewayBaseUrl));
builder.Services.AddSingleton<ReviewProgressReporter>();
// In-memory retry governance for the orchestrator: attempt-counting + exponential backoff + park-after-K,
// so a stuck ContextReady backs off (not the old ~30s hot-loop) and a genuinely stuck commit is parked +
// alerted. Not persisted — a restart resets it, so a restart retries parked runs.
builder.Services.AddSingleton(sp => new RetryGovernor(
    daemonOptions.MaxContextRetries,
    TimeSpan.FromSeconds(daemonOptions.RetryBackoffBaseSeconds),
    TimeSpan.FromSeconds(daemonOptions.RetryBackoffCapSeconds),
    () => DateTimeOffset.UtcNow,
    sp.GetRequiredService<ILogger<RetryGovernor>>()));
builder.Services.AddSingleton<PrOrchestrator>();
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
            daemonOptions.StrandedRunRetryPendingGraceMinutes, grace);

        return new StrandedRunReconciler(
            listStrandedRuns: store.ListStrandedRuns,
            getPrLifecycleAsync: (row, ct) => PrLifecycleSweepSeam.ResolveLifecycleAsync(
                providers,
                row.Repo,
                PrLifecycleSweepSeam.MapProviderNamespace(row.Repo.Provider),
                row.Run.PrId,
                ct),
            resumeAsync: orchestrator.ReconcileAsync,
            updateRunState: store.UpdateReviewRunState,
            timeProvider: TimeProvider.System,
            grace: grace,
            scanLimit: daemonOptions.StrandedRunScanLimit,
            maxResumesPerPass: daemonOptions.StrandedRunMaxResumesPerSweep,
            logger: sp.GetRequiredService<ILogger<StrandedRunReconciler>>(),
            listRetryPendingRuns: retryPendingGrace > TimeSpan.Zero ? store.ListRetryPendingRuns : null,
            retryPendingGrace: retryPendingGrace);
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
            new DaemonCorpusReader(
                store,
                ModelFamilies.Of,
                loggerFactory.CreateLogger<DaemonCorpusReader>()),
            new EvalCorpusWatermark(store, loggerFactory.CreateLogger<EvalCorpusWatermark>()),
            evalSweepWindow,
            loggerFactory.CreateLogger<EvalCorpusSweep>());

        return new EvalCorpusSweepSchedule(
            sweep.SweepOnceAsync,
            evalSweepInterval,
            TimeProvider.System,
            loggerFactory.CreateLogger<EvalCorpusSweepSchedule>());
    });
}

// The PR-watching loop. Registering a BackgroundService adds NO route, so the host's mapped routes stay
// exactly the one webhook below. With the allow-list empty (default) it has no targets and is inert.
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
        ("PR-lifecycle", sp.GetService<PrLifecycleSweeper>() is { } lifecycleSweeper ? lifecycleSweeper.SweepAsync : null),
        ("deep-link retention", sp.GetService<DeepLinkRetentionSweeper>() is { } retentionSweeper ? retentionSweeper.SweepAsync : null),
        ("stranded-run", sp.GetService<StrandedRunReconciler>() is { } strandedReconciler ? strandedReconciler.SweepAsync : null),
        ("eval-corpus", sp.GetService<EvalCorpusSweepSchedule>() is { } evalCorpusSweep ? evalCorpusSweep.SweepAsync : null))));

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
    params (string Name, Func<CancellationToken, Task>? Sweep)[] sweeps)
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
// The daemon exposes exactly TWO routes, both gateway callbacks authenticated by the same shared
// secret: POST /api/auth/webhook/{provider} (post-auth callback) and POST /api/discovery/context_discovery
// (context-discovery callback — returns 200 accept-and-ignore so a non-2xx never tears down the sandbox
// session). MVC discovery is filtered to exactly those two controllers so no other route can leak in.
builder.Services
    .AddControllers()
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
        foreach (var existing in apm.FeatureProviders.OfType<Microsoft.AspNetCore.Mvc.Controllers.ControllerFeatureProvider>().ToList())
        {
            _ = apm.FeatureProviders.Remove(existing);
        }
        apm.FeatureProviders.Add(new DaemonControllerFeatureProvider());
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
