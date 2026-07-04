using AchieveAi.LmDotnetTools.LmAgentInfra.Auth;
using AchieveAi.LmDotnetTools.LmAgentInfra.Controllers;
using CodeReviewDaemon.Sample.Agents;
using CodeReviewDaemon.Sample.Auth;
using CodeReviewDaemon.Sample.Configuration;
using CodeReviewDaemon.Sample.Hosting;
using CodeReviewDaemon.Sample.Orchestration;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Workspace;
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
builder.Services.AddSingleton<ISandboxCommandRunner>(sp => new SandboxOrchestrator(
    Environment.GetEnvironmentVariable("CRD_SANDBOX_GATEWAY") ?? "http://127.0.0.1:8080",
    Environment.GetEnvironmentVariable("CRD_SANDBOX_SESSION") ?? Guid.NewGuid().ToString("N"),
    sp.GetRequiredService<ILogger<SandboxOrchestrator>>(),
    daemonOptions.Limits));
builder.Services.AddSingleton<ISandboxFileSystem>(sp =>
    new SandboxFileSystem(sp.GetRequiredService<ISandboxCommandRunner>()));

// The live agent loop (OpenAI-compatible MultiTurnAgentLoop). Dead-by-default + lazy per run.
builder.Services.AddSingleton<IReviewAgentLoopFactory, LiveReviewAgentLoopFactory>();

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

// The stage executor (consumer of the four agent/posting flags) and the orchestrator that sequences it.
builder.Services.AddSingleton<IReviewStageExecutor, DaemonReviewStageExecutor>();
builder.Services.AddSingleton<PrOrchestrator>();

// The PR-watching loop. Registering a BackgroundService adds NO route, so the host's mapped routes stay
// exactly the one webhook below. With the allow-list empty (default) it has no targets and is inert.
builder.Services.AddHostedService(sp => new PrPollingService(
    PrPollTargetBuilder.Build(daemonOptions, sp.GetRequiredService<ILogger<PrPollingService>>()),
    sp.GetServices<IPrProvider>(),
    sp.GetRequiredService<ReviewStore>(),
    sp.GetRequiredService<PrOrchestrator>(),
    sp.GetRequiredService<ILogger<PrPollingService>>()));

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

// The gateway↔webhook boundary is the shared secret the shared AuthWebhookController verifies (see the
// gateway-callback note above). The plan §9 HMAC middleware is intentionally NOT wired — the real gateway
// does not sign its callbacks, so requiring a signature rejected every real callback.
app.MapControllers();

app.Run();

return 0;

/// <summary>Exposed for the route-exposure test host (WebApplicationFactory&lt;Program&gt;).</summary>
public partial class Program;
