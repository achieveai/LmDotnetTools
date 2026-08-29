using System.Collections.Immutable;
using System.Net;
using System.Net.Sockets;
using System.Text;
using AchieveAi.LmDotnetTools.AnthropicProvider.Agents;
using AchieveAi.LmDotnetTools.AnthropicProvider.Models;
using AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Configuration;
using AchieveAi.LmDotnetTools.CodexSdkProvider.Configuration;
using AchieveAi.LmDotnetTools.CodexSdkProvider.Models;
using AchieveAi.LmDotnetTools.CopilotSdkProvider.Configuration;
using AchieveAi.LmDotnetTools.GithubCopilotProvider.Agents;
using AchieveAi.LmDotnetTools.GithubCopilotProvider.Auth;
using AchieveAi.LmDotnetTools.GithubCopilotProvider.Models;
using AchieveAi.LmDotnetTools.GithubCopilotProvider.Reasoning;
using AchieveAi.LmDotnetTools.LmAgentInfra;
using AchieveAi.LmDotnetTools.LmAgentInfra.Agents;
using AchieveAi.LmDotnetTools.LmAgentInfra.Auth;
using AchieveAi.LmDotnetTools.LmAgentInfra.Context;
using AchieveAi.LmDotnetTools.LmAgentInfra.Lifecycle;
using AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;
using AchieveAi.LmDotnetTools.LmCore.AgentRuntime;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmCore.Utils;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Collaboration;
using AchieveAi.LmDotnetTools.LmMultiTurn.Lifecycle;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence.Sqlite;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using AchieveAi.LmDotnetTools.LmMultiTurn.TodoBoard;
using AchieveAi.LmDotnetTools.LmStreaming.AspNetCore.Extensions;
using AchieveAi.LmDotnetTools.LmStreaming.Sample.Triggers;
using AchieveAi.LmDotnetTools.LmTestUtils;
using AchieveAi.LmDotnetTools.LmWorkflow;
using AchieveAi.LmDotnetTools.LmWorkflow.Runtime;
using AchieveAi.LmDotnetTools.LmWorkflow.Tools;
using AchieveAi.LmDotnetTools.McpMiddleware.Extensions;
using AchieveAi.LmDotnetTools.McpServer.AspNetCore.Extensions;
using AchieveAi.LmDotnetTools.Misc.Configuration;
using AchieveAi.LmDotnetTools.Misc.Utils;
using AchieveAi.LmDotnetTools.Misc.Web.Jina;
using AchieveAi.LmDotnetTools.OpenAIProvider.Agents;
using AchieveAi.LmDotnetTools.OpenAiResponsesProvider.Agents;
using AchieveAi.LmDotnetTools.OpenAiResponsesProvider.Models;
using LmStreaming.Sample.Auth;
using LmStreaming.Sample.Configuration;
using LmStreaming.Sample.Controllers;
using LmStreaming.Sample.Identity;
using LmStreaming.Sample.Models;
using LmStreaming.Sample.Persistence;
using LmStreaming.Sample.Services;
using LmStreaming.Sample.Services.Discovery;
using LmStreaming.Sample.Tools;
using LmStreaming.Sample.WebSocket;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelContextProtocol.Client;
using Serilog;
using Serilog.Enrichers.CallerInfo;
using Serilog.Events;
using Serilog.Exceptions;
using Serilog.Formatting.Compact;
using Vite.AspNetCore;

// Load .env file from workspace root (if it exists)
EnvironmentHelper.LoadEnvIfNeeded(FindEnvFile());

// Bootstrap Serilog for early logging (before host is built)
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    Log.Information("Starting LmStreaming.Sample application");

    var builder = WebApplication.CreateBuilder(args);

    // Bridge the operator-facing flat env var LMSTREAMING_S2S_INBOUND_SECRET into the section key the
    // InboundS2SAuth filter actually reads (Auth:S2SInboundSecret). The standard env-var provider only
    // maps the double-underscore form (Auth__S2SInboundSecret) into that section, so the documented
    // flat name would otherwise land in an unrelated key and silently leave the inbound guard DISABLED
    // (issue #153). Anything already bound to the section key (appsettings.json or Auth__S2SInboundSecret)
    // wins — this only fills the gap for the documented flat name.
    var s2sInboundSecretEnv = Environment.GetEnvironmentVariable("LMSTREAMING_S2S_INBOUND_SECRET");
    if (
        !string.IsNullOrWhiteSpace(s2sInboundSecretEnv)
        && string.IsNullOrWhiteSpace(builder.Configuration[InboundS2SAuthAttribute.SecretConfigKey])
    )
    {
        builder.Configuration[InboundS2SAuthAttribute.SecretConfigKey] = s2sInboundSecretEnv;
    }

    // Configure Serilog from appsettings.json with all enrichers
    _ = builder.Host.UseSerilog(
        (context, services, configuration) =>
        {
            var logPath = Path.Combine(AppContext.BaseDirectory, "logs", "lmstreaming-.jsonl");

            _ = configuration
                .ReadFrom.Configuration(context.Configuration)
                .ReadFrom.Services(services)
                .Enrich.FromLogContext()
                .Enrich.WithMachineName()
                .Enrich.WithThreadId()
                .Enrich.WithExceptionDetails()
                .Enrich.WithProperty("Application", "LmStreaming.Sample")
                // Add caller info: file path, line number, method name, namespace
                .Enrich.WithCallerInfo(
                    includeFileInfo: true,
                    assemblyPrefix: "AchieveAi.", // Match our assemblies
                    filePathDepth: 3
                ) // Include last 3 path segments
                // File sink with structured JSON (includes all enriched properties)
                .WriteTo.File(
                    new CompactJsonFormatter(),
                    logPath,
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 7,
                    shared: true
                )
                // Console sink with readable format
                .WriteTo.Console(
                    outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}{NewLine}    {Message:lj}{NewLine}{Exception}"
                );

            Log.Information("Serilog configured. Log file location: {LogPath}", logPath);
        }
    );

    // Add LmStreaming services
    _ = builder.Services.AddLmStreaming(options =>
    {
        options.WebSocketPath = "/ws";
        options.WriteIndentedJson = builder.Environment.IsDevelopment();

        // Bound from configuration, not left at the default (#346). The default is empty, which
        // admits no cross-origin caller - correct, and the right default - but until this line
        // existed an operator had no way to change it, so `LmStreaming:AllowedOrigins` was a
        // setting the deployment documentation named and the host ignored.
        options.AllowedOrigins =
            builder.Configuration.GetSection("LmStreaming:AllowedOrigins").Get<List<string>>() ?? [];
    });
    _ = builder
        .Services.AddOptions<AgentOutputTokenOptions>()
        .Bind(builder.Configuration.GetSection(AgentOutputTokenOptions.SectionName))
        .Validate(options => options.Validate().Succeeded, "Invalid AgentOutputTokens configuration.")
        .ValidateOnStart();
    _ = builder.Services.AddSingleton(sp => new AgentOutputTokenPolicy(
        sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<AgentOutputTokenOptions>>().Value
    ));

    // Service-to-service lifecycle observation and remote tool approval (ADR 0003 + ADR 0005). Both
    // are off unless the matching `Lifecycle:*:Enabled` flag is set, and this file carries no default
    // for either — an absent section reads as disabled.
    _ = builder.Services.AddLifecycleDelivery(builder.Configuration);
    _ = builder.Services.AddRemoteToolApproval(builder.Configuration);

    // AddLifecycleControlPlane is called unconditionally, and the disabled case is the one that needs
    // it. The SDK emits an ApplicationPartAttribute for every referenced assembly that references MVC,
    // so LmAgentInfra's controllers — the lifecycle ones included — are discovered here whether or not
    // this sample asked for them. With the flags off those two would be published and unconstructible,
    // because their dependencies are registered only when the flags are on. This removes them. The
    // sample's existing surface, api/auth/webhook and api/auth/egress-keys among it, is untouched:
    // this host supplied the application part, so the method leaves the rest of that assembly alone.
    _ = builder.Services.AddControllers().AddLifecycleControlPlane(builder.Configuration);
    _ = builder.Services.AddEndpointsApiExplorer();

    // P1 slice 1 (#301): the tenant registry, the principal pipeline and — only when an Entra app
    // registration is configured — the JWT bearer handler. Inert by default: with Identity:Enforce
    // false every request resolves to the development principal, which is what keeps the existing
    // surface working unchanged.
    _ = builder.Services.AddSampleIdentity(builder.Configuration);

    // The operator secret is set through a flat env var for the same reason the S2S inbound secret
    // is: the standard env-var provider maps only `Identity__OperatorSecret` into that section key,
    // and operators reach for the flat name.
    var operatorSecret = Environment.GetEnvironmentVariable(OperatorSecretAuthAttribute.SecretEnvironmentVariable);
    if (!string.IsNullOrWhiteSpace(operatorSecret))
    {
        _ = builder.Configuration.AddInMemoryCollection(
            new Dictionary<string, string?> { [OperatorSecretAuthAttribute.SecretConfigKey] = operatorSecret }
        );
    }

    // Raise the request-body ceiling so the file browser's multipart upload (WI #195) can carry a file of
    // exactly MaxFileBytes (64 MiB) plus a fixed 8 KiB framing allowance. The exact inclusive per-file cap
    // is enforced in FileBrowserController against both the declared and observed bytes.
    _ = builder.WebHost.ConfigureKestrel(o =>
        o.Limits.MaxRequestBodySize = LmStreaming.Sample.FileBrowser.FileBrowserLimits.MaxUploadRequestBytes
    );
    _ = builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o =>
        o.MultipartBodyLengthLimit = LmStreaming.Sample.FileBrowser.FileBrowserLimits.MaxUploadRequestBytes
    );

    // Add Vite services for frontend integration. The dev-server port is configurable via
    // VITE_DEV_PORT so an isolated instance can run alongside another without colliding on 5173
    // (the paired vite.config.ts reads the same env var). Defaults to 5173.
    var viteDevPort = ushort.TryParse(Environment.GetEnvironmentVariable("VITE_DEV_PORT"), out var parsedVitePort)
        ? parsedVitePort
        : (ushort)5173;
    _ = builder.Services.AddViteServices(options =>
    {
        options.Base = "/dist/";
        options.Server.AutoRun = ResolveViteAutoRun();
        options.Server.PackageDirectory = "ClientApp";
        options.Server.Port = viteDevPort;
    });

    var providerMode = Environment.GetEnvironmentVariable("LM_PROVIDER_MODE") ?? "test";
    var codexMcpPort = ResolveCodexMcpPort();
    Environment.SetEnvironmentVariable("CODEX_MCP_PORT_EFFECTIVE", codexMcpPort.ToString());

    // Provider registry — single source of truth for which providers the client can pick
    // and what the per-process default is. Read once at startup; shared via DI singleton.
    _ = builder.Services.AddSingleton<IFileSystemProbe, FileSystemProbe>();

    // Side-table so the read-only /subagents endpoint + sub-agent WebSocket can surface a conversation's
    // StartWorkflowAgent runs (isolated controller loops, owned by a per-conversation WorkflowManager the
    // agent loop can't reference) as center-pane tabs. Its durable index is capped by the configured
    // retention (AgentCollaboration:MaxPersistedHierarchyEntries) — the index never deletes a row a live
    // snapshot dropped, so the ceiling is what keeps a long-lived conversation's file from growing forever.
    _ = builder.Services.AddSingleton(sp => new WorkflowRunRegistry(
        Path.Combine(AppContext.BaseDirectory, "workflow-index"),
        sp.GetRequiredService<AgentCollaborationHostOptions>().MaxPersistedHierarchyEntries
    ));

    // Process-lifetime cache of the persisted Agent-tool child roster AgentHierarchyService's cold path
    // recovers per conversation (PRRT_kwDOOPysWM6V1mjj) — shared across every AgentHierarchyService
    // instance built for a request or a spawned agent's transcript tool, both of which construct that
    // service fresh rather than resolving it from DI. Bounded by the same retention knob as
    // WorkflowRunRegistry above (AgentCollaboration:MaxPersistedHierarchyEntries) — reusing it here keeps
    // this cache's own distinct-conversation ceiling configurable without adding a second knob for the
    // same "how many conversations should this process remember" question. See
    // SubAgentScanCoverageCache's own remarks for the owner-keyed invalidation and eviction policy.
    _ = builder.Services.AddSingleton(sp => new SubAgentScanCoverageCache(
        sp.GetRequiredService<AgentCollaborationHostOptions>().MaxPersistedHierarchyEntries
    ));

    // Per-root memory of the persisted DESCENDANT graph (issue #251) — a different question from the
    // direct-child roster above, and deliberately a different cache; see ConversationDescendantScanner's
    // remarks for why reusing SubAgentScanCoverageCache here would be a correctness bug.
    _ = builder.Services.AddSingleton(sp => new ConversationDescendantScanner(
        sp.GetRequiredService<IConversationStore>(),
        sp.GetRequiredService<ILogger<ConversationDescendantScanner>>(),
        sp.GetRequiredService<AgentCollaborationHostOptions>().MaxPersistedHierarchyEntries
    ));

    // Mirror every conversation into its own workspace as JSONL (issue #251). Always on: a conversation
    // with no workspace bound resolves no sandbox session, so its flush is a no-op and costs nothing.
    // The pool is reached through a lookup delegate rather than injected — the pool's own registration
    // resolves this singleton, so a direct dependency would close a DI construction cycle. The delegate
    // only runs when a subscription ends, long after both singletons exist.
    _ = builder.Services.AddSingleton(sp => new WorkspaceTranscriptMirror(
        threadId => sp.GetRequiredService<MultiTurnAgentPool>().TryGet(threadId, out var agent) ? agent : null,
        sp.GetRequiredService<IConversationStore>(),
        sp.GetRequiredService<IWorkspaceFileBrowser>(),
        sp.GetRequiredService<ConversationDescendantScanner>(),
        sp.GetRequiredService<ILoggerFactory>()
    ));

    // Mock provider host: eagerly-started in-process Kestrel app that the *-mock providers
    // point at. Singleton-as-IHostedService so it boots in Host.StartAsync; the registry
    // dependency below reads its IsRunning flag for availability gating.
    _ = builder.Services.AddSingleton<MockProviderHostLifetime>();
    _ = builder.Services.AddHostedService(sp => sp.GetRequiredService<MockProviderHostLifetime>());

    // Discover the GitHub Copilot model catalog once at startup and register the provider
    // registry from it. Discovery reuses the developer's existing Copilot/gh login; when no token
    // resolves or the /models call fails, DiscoverCopilotModels returns an empty list and the
    // registry simply exposes no Copilot models (the app still boots with the direct/CLI/test
    // providers). Resolved eagerly here (sync-over-async on the no-SynchronizationContext startup
    // path) so the registry — used on the request hot path — stays synchronous.
    _ = builder.Services.AddSingleton(sp =>
    {
        var probe = sp.GetRequiredService<IFileSystemProbe>();
        var mockHost = sp.GetRequiredService<MockProviderHostLifetime>();
        var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
        var copilotModels = DiscoverCopilotModels(loggerFactory);
        var anthropicCompatModels = AnthropicCompatProviders.DiscoverFromEnv(loggerFactory);
        return new ProviderRegistry(copilotModels, probe, mockHost, anthropicCompatModels);
    });

    // Register the FunctionRegistry with sample tools
    _ = builder.Services.AddSingleton(sp =>
    {
        var registry = new FunctionRegistry();
        _ = registry.AddFunctionsFromType(typeof(SampleTools));
        return registry;
    });

    // Sandbox MCP gateway integration (Workspace Agent mode). The gateway lifetime boots eagerly
    // as a hosted service but is non-fatal: when the gateway is not configured/available the app
    // still starts for all other modes; the hard failure surfaces only when Workspace Agent mode
    // actually tries to create a sandbox session.
    var sandboxOptions =
        builder.Configuration.GetSection(SandboxGatewayOptions.SectionName).Get<SandboxGatewayOptions>()
        ?? new SandboxGatewayOptions();

    // Fail-fast sandbox credential validation (issue #153 M1). A configured-but-malformed key (bad
    // base64 / too short) is almost certainly a copy-paste/config mistake, so surface it as an
    // actionable startup error instead of a confusing 401 on the first sandbox request later. When
    // no key is configured at all this is the keyless AUTH_ENFORCE=off dev path: warn once and fall
    // back to an id-only credential (the empty key is omitted from headers by AddSandboxAuthHeaders).
    SandboxCredential sandboxCredential;
    try
    {
        var resolvedSandboxCredential = SandboxCredential.FromOptions(sandboxOptions);
        if (resolvedSandboxCredential is null)
        {
            Log.Warning(
                "No sandbox app key configured (SandboxGateway:AppKey); requests to the sandbox "
                    + "gateway will be unauthenticated for app id '{AppId}' — AUTH_ENFORCE must be "
                    + "off on the gateway for this to work.",
                sandboxOptions.AppId
            );
            sandboxCredential = new SandboxCredential(sandboxOptions.AppId, sandboxOptions.AppKey ?? string.Empty);
        }
        else
        {
            sandboxCredential = resolvedSandboxCredential.Value;
        }
    }
    catch (ArgumentException ex)
    {
        throw new InvalidOperationException(
            $"Sandbox gateway app key is invalid for app '{sandboxOptions.AppId}' "
                + "(SandboxGateway:AppKey) — fix the configured key (or unset it to run keyless "
                + $"while AUTH_ENFORCE=off) and restart. Detail: {ex.Message}",
            ex
        );
    }

    if (sandboxOptions.ValidateCredentialOnStartup)
    {
        // TODO(#153 M6): optional startup credential probe. Once SandboxSessionRegistry is
        // constructed (below) and the gateway hosted service is up, best-effort create+destroy a
        // throwaway session under a dedicated workspace id to catch a rejected-but-well-formed key
        // at boot, logging success/failure NON-FATALLY. Deferred here: it needs the registry built
        // first (which itself depends on SandboxGatewayLifetime) plus a throwaway workspace id that
        // can't collide with the "default" workspace's session — more plumbing than fits at this
        // call site.
        Log.Information(
            "SandboxGateway:ValidateCredentialOnStartup is set, but the startup credential probe is "
                + "not yet wired (see TODO(#153 M6)); a rejected key still only surfaces on first use."
        );
    }

    _ = builder.Services.AddSingleton(sandboxOptions);
    var workspaceCatalogIdentity = GatewayWorkspaceCatalogIdentity.Create(sandboxOptions.BaseUrl, sandboxOptions.AppId);
    _ = builder.Services.AddSingleton(workspaceCatalogIdentity);

    // Every gateway-bound HttpClient gets the per-app bearer handler (ADR 0029) so its REST calls carry
    // X-Sbx-App-Id/X-Sbx-App-Key under an AUTH_ENFORCE gateway. No-op when no AppKey is configured, so an
    // unenforced gateway is unaffected.
    HttpClient GatewayHttpClient(TimeSpan? timeout = null)
    {
        // AllowAutoRedirect=false is a security precondition of the Sandbox SDK's borrowed-client
        // contract: the SDK authenticates with custom X-Sbx-* headers, which .NET's auto-redirect
        // logic would re-send to a redirect target (it only strips the standard Authorization header).
        // The per-credential SandboxClient instances the registry builds forward onto this shared
        // client, so disabling it here closes that leak for the whole gateway transport.
        var client = new HttpClient(
            new GatewayAuthHandler(sandboxOptions.AppId, sandboxOptions.AppKey)
            {
                InnerHandler = new HttpClientHandler { AllowAutoRedirect = false },
            }
        );
        if (timeout is { } t)
        {
            client.Timeout = t;
        }

        return client;
    }

    _ = builder.Services.AddSingleton(sp => new SandboxGatewayLifetime(
        sandboxOptions,
        sp.GetRequiredService<ILogger<SandboxGatewayLifetime>>(),
        GatewayHttpClient()
    ));
    _ = builder.Services.AddHostedService(sp => sp.GetRequiredService<SandboxGatewayLifetime>());

    // Read-only marketplace catalog proxy (GET /api/marketplaces). Best-effort: it never spawns the
    // gateway, so the controller degrades to 503 when it's offline. Registered as an interface so
    // tests/E2E swap in a fake.
    _ = builder.Services.AddSingleton<IMarketplaceCatalogClient>(sp => new MarketplaceCatalogClient(
        sandboxOptions,
        // The gateway is frequently offline; a short timeout fails fast instead of holding the
        // request for the default 100s while the catalog is a best-effort, read-only browse.
        GatewayHttpClient(TimeSpan.FromSeconds(10)),
        sp.GetRequiredService<ILogger<MarketplaceCatalogClient>>()
    ));
    _ = builder.Services.AddSingleton<WorkspaceCatalogCompatibilityService>();

    // OAuth auth-provider services (GitHub + Azure DevOps token injection for sandbox egress).
    var authOptions = builder.Configuration.GetSection(AuthOptions.SectionName).Get<AuthOptions>() ?? new AuthOptions();
    _ = builder.Services.AddSingleton(authOptions);
    var oauthTokenDir = string.IsNullOrWhiteSpace(authOptions.TokenStoreDir)
        ? Path.Combine(AppContext.BaseDirectory, "oauth-tokens")
        : authOptions.TokenStoreDir;
    _ = builder.Services.AddSingleton(sp => new SessionSecretStore(
        Path.Combine(oauthTokenDir, "session-secrets"),
        sp.GetRequiredService<ILogger<SessionSecretStore>>()
    ));
    _ = builder.Services.AddSingleton<IOAuthTokenStore>(sp => new FileOAuthTokenStore(
        oauthTokenDir,
        sp.GetRequiredService<ILogger<FileOAuthTokenStore>>()
    ));
    // Predefined egress keys (issue #210): a runtime-managed registry of custom-header / OAuth
    // credentials the gateway injects on egress to user-specified hosts (managed via the Egress Auth
    // dialog / EgressKeysController). Persists under the same gitignored token dir; a dedicated
    // HttpClient drives the OAuth token-endpoint mint/refresh calls.
    _ = builder.Services.AddSingleton(sp => new PredefinedKeyRegistry(
        oauthTokenDir,
        sp.GetRequiredService<IOAuthTokenStore>(),
        new HttpClient(),
        sp.GetRequiredService<ILoggerFactory>()
    ));
    // Dual-register each provider: the concrete type is what the per-provider controller
    // (AdoAuthController / GitHubAuthController) takes in its ctor, while the IOAuthTokenProvider
    // alias keeps the enumerable-consuming callers (AuthWebhookController, OAuthTokenHydrator)
    // working unchanged. Concrete-first registration + alias-to-concrete means there's exactly one
    // singleton instance per provider.
    _ = builder.Services.AddSingleton(sp => new GitHubOAuthProvider(
        authOptions.Github,
        sp.GetRequiredService<IOAuthTokenStore>(),
        new HttpClient(),
        sp.GetRequiredService<ILogger<GitHubOAuthProvider>>()
    ));
    _ = builder.Services.AddSingleton<IOAuthTokenProvider>(sp => sp.GetRequiredService<GitHubOAuthProvider>());

    _ = builder.Services.AddSingleton(sp => new AdoOAuthProvider(
        authOptions.Ado,
        Path.Combine(oauthTokenDir, "msal-ado.bin"),
        sp.GetRequiredService<ILogger<AdoOAuthProvider>>()
    ));
    _ = builder.Services.AddSingleton<IOAuthTokenProvider>(sp => sp.GetRequiredService<AdoOAuthProvider>());

    _ = builder.Services.AddSingleton(sp => new M365OAuthProvider(
        authOptions.M365,
        authOptions.Webhook.CallbackBaseUrl,
        Path.Combine(oauthTokenDir, "msal-m365.bin"),
        sp.GetRequiredService<ILogger<M365OAuthProvider>>()
    ));
    _ = builder.Services.AddSingleton<IOAuthTokenProvider>(sp => sp.GetRequiredService<M365OAuthProvider>());

    // Restore persisted sign-in state at startup so the status API/UI reflects a prior run's sign-in
    // (token injection always reads the store directly, but the surfaced status was in-memory only).
    _ = builder.Services.AddHostedService<OAuthTokenHydrator>();

    // Deferred auth: not-signed-in webhook calls are held while connected chat clients are
    // prompted (auth_required WebSocket frame) to sign in interactively. The webhook resolves the
    // hold through the deferred-interactive policy (the daemon swaps in a fail-fast policy instead).
    _ = builder.Services.AddSingleton<IAuthEventNotifier, WebSocketAuthEventNotifier>();
    _ = builder.Services.AddSingleton<PendingAuthCoordinator>();
    _ = builder.Services.AddSingleton<IAuthResolutionPolicy, DeferredInteractiveAuthPolicy>();

    // Auth-webhook forwarding: in addition to the WS-facing auth_required/completed/denied
    // broadcast above, forward the same lifecycle to whichever thread in the session registered a
    // webhook URL via ConversationsController.Provision (headless REST callers have no WebSocket to
    // listen on). Depends on SandboxSessionRegistry/IConversationStore, registered below/above.
    _ = builder.Services.AddSingleton<IAuthWebhookForwarder>(sp => new SandboxAuthWebhookForwarder(
        sp.GetRequiredService<SandboxSessionRegistry>(),
        sp.GetRequiredService<IConversationStore>(),
        new HttpClient { Timeout = TimeSpan.FromSeconds(3) },
        sp.GetRequiredService<ILogger<SandboxAuthWebhookForwarder>>()
    ));

    _ = builder.Services.AddSingleton(sp => new SandboxSessionRegistry(
        sp.GetRequiredService<SandboxGatewayLifetime>(),
        sandboxOptions,
        sp.GetRequiredService<ILogger<SandboxSessionRegistry>>(),
        // Bounds the gateway create/destroy calls; the create-POST runs sync-over-async on the
        // WebSocket request thread, so the 100s default could stall it indefinitely.
        GatewayHttpClient(TimeSpan.FromSeconds(30)),
        authOptions,
        sp.GetRequiredService<SessionSecretStore>(),
        sp.GetRequiredService<PredefinedKeyRegistry>(),
        // Same bundle instance the agent pool resolves (see the hostLifecycleServices note below), so
        // the registry's SandboxCreated events share the pool's producer epoch and sequence stream. A
        // second bundle here would mint a second epoch, and a subscriber would read the interleaving
        // as "the producer restarted" every time a session was created. Null unless a Lifecycle flag
        // is set, in which case the registry falls back to MultiTurnLifecycleServices.Disabled and
        // publishes nothing — the pre-#227 behavior.
        sp.GetService<MultiTurnLifecycleServices>(),
        // Re-reads the workspace when a session has to be RECREATED (gateway 404). The ref an agent
        // captured at build time can be arbitrarily stale; without this the recreate resurrects the
        // marketplaces/plugins the workspace had back then and the user's edit looks discarded.
        // Resolved lazily inside the callback because IWorkspaceStore is registered further down.
        async (workspaceId, ct) =>
        {
            var workspace = await sp.GetRequiredService<IWorkspaceStore>()
                .GetAsync(workspaceId, ct)
                .ConfigureAwait(false);
            return workspace is null ? null : BuildWorkspaceRef(workspaceId, workspace);
        }
    ));

    // The registry also implements the narrow file-browser surface the FileBrowserController depends on
    // (WI #195): non-creating session resolution + credentialed workspace file ops.
    _ = builder.Services.AddSingleton<IWorkspaceFileBrowser>(sp => sp.GetRequiredService<SandboxSessionRegistry>());

    _ = builder.Services.AddSingleton(sp =>
        SubAgentIntelligenceOptions.Load(
            builder.Configuration,
            sp.GetRequiredService<ILogger<SubAgentIntelligenceOptions>>()
        )
    );
    _ = builder.Services.AddSingleton<SubAgentModelResolver>();

    // Workspace sub-agent discovery. The loader asks the gateway what it has discovered in
    // the session's workspace (sub-agent markdown files under .claude/agents/, etc.) and maps
    // them into SubAgentTemplate so they show up as spawnable types in the Agent tool catalog.
    _ = builder.Services.AddSingleton(sp => new WorkspaceSubAgentLoader(
        sp.GetRequiredService<SandboxSessionRegistry>(),
        sp.GetRequiredService<ILogger<WorkspaceSubAgentLoader>>(),
        sp.GetRequiredService<SubAgentModelResolver>()
    ));

    // Marketplace sub-agent bridge. Maps the agents the UI's marketplace browser lists (the
    // gateway's read-only catalog) into spawnable templates, filling any gap left by workspace
    // file-discovery so a browsable marketplace agent is also a usable Agent tool subagent_type.
    _ = builder.Services.AddSingleton<MarketplaceSubAgentLoader>();

    // Sandbox context-file (CLAUDE.md / AGENTS.md) injection. The formatter owns the
    // <context-discovery> wrapper tag shared by the boot-time system prompt and the mid-session
    // user-turn injection; the injector wires gateway webhook deliveries into every live agent
    // thread bound to the same sandbox session.
    // Bind the routing options FIRST (default-off) so the injector resolves them from DI. Off ⇒
    // discovery behaves byte-identically to today; on ⇒ a context_file carrying an agent_id is
    // routed to the opening sub-agent (cf. #187/#198).
    var contextDiscoveryOptions =
        builder.Configuration.GetSection(ContextDiscoveryOptions.SectionName).Get<ContextDiscoveryOptions>()
        ?? new ContextDiscoveryOptions();
    _ = builder.Services.AddSingleton(contextDiscoveryOptions);
    _ = builder.Services.AddSingleton<ContextDiscoveryFormatter>();
    _ = builder.Services.AddSingleton<ContextDiscoveryInjector>();
    // Tracks received discovery webhooks per session for GET /api/diagnostics/context-discovery,
    // so an operator can confirm discoveries are actually arriving (vs. silently lost to an
    // unreachable callback host).
    _ = builder.Services.AddSingleton<ContextDiscoveryDiagnostics>();

    // Hierarchy-wide agent collaboration (#244). Whether it is ON is decided per chat mode at
    // conversation construction (see CollaborationDefaultsOnForMode). When collaboration resolves ON,
    // validate its limits here so a bad limit or unknown transcript mode fails this boot rather than the
    // first spawn of a later conversation. An explicit Enabled: false bypasses those unused limits; they
    // are validated on the boot where collaboration is enabled.
    var collaborationHostOptions =
        builder.Configuration.GetSection(AgentCollaborationHostOptions.SectionName).Get<AgentCollaborationHostOptions>()
        ?? new AgentCollaborationHostOptions();
    _ = collaborationHostOptions.ResolveForMode(defaultEnabled: true);
    _ = builder.Services.AddSingleton(collaborationHostOptions);

    // Codex MCP server: registered unconditionally but started lazily, so non-codex boots
    // don't pay the startup cost and so the codex provider stays selectable from the
    // dropdown regardless of LM_PROVIDER_MODE.
    _ = builder.Services.AddSingleton<IFunctionProvider>(
        new TypeFunctionProvider(typeof(SampleTools), providerName: "SampleTools")
    );
    _ = builder.Services.AddMcpFunctionProviderServerLazy(options =>
    {
        options.Port = codexMcpPort;
        options.IncludeStatefulFunctions = true;
    });
    _ = builder.Services.AddSingleton<CodexMcpServerLifetime>();

    // Register the FileConversationStore for conversation persistence (it also implements the
    // IRunLedgerStore run-status ledger, so register the same instance under both interfaces).
    var conversationsPath = Path.Combine(AppContext.BaseDirectory, "conversations");
    var conversationStore = new FileConversationStore(conversationsPath);
    _ = builder.Services.AddSingleton<IConversationStore>(conversationStore);
    _ = builder.Services.AddSingleton<IRunLedgerStore>(conversationStore);

    // #145: durable notify-wait persistence. Register ONLY the (non-disposable) INotifyWaitStore and
    // construct its SqliteConnectionFactory INLINE — deliberately NOT as a separately-tracked
    // singleton. ISqliteConnectionFactory is IAsyncDisposable-only; a container-tracked
    // IAsyncDisposable-only singleton makes ServiceProvider.Dispose() (synchronous) throw
    // "only implements IAsyncDisposable". The sample host is torn down synchronously in tests
    // (BrowserWebAppFactory calls IHost.Dispose()), so tracking the factory would regress every E2E
    // test that creates an agent. The factory OBJECT itself only holds SemaphoreSlims (no OS handle)
    // and is reclaimed by GC at process end; the real cost of never disposing it is that the
    // PROCESS-WIDE Microsoft.Data.Sqlite connection pool — not owned by this object — stays populated
    // with live WAL connections (this factory sets Pooling=true + Cache=Shared and runs
    // PRAGMA journal_mode=WAL), each an open OS file handle plus its -wal/-shm sidecars, until the
    // process exits or SqliteConnection.ClearAllPools() is called. Acceptable here ONLY because this
    // path is test-mode-gated and the tests clear the pool in their finally. FOLLOW-UP (#161): before
    // flipping the real-provider gate, register real async disposal (e.g.
    // IHostApplicationLifetime.ApplicationStopped -> SqliteConnection.ClearAllPools()) so the pooled
    // WAL handles are released deterministically instead of lingering to process exit.
    // Configurable path (default a sibling of the conversations/ folder) mirrors
    // CodeReviewDaemon.Sample's CodeReviewDaemon:DatabasePath so a WebApplicationFactory test can
    // UseSetting an isolated file.
    // NOTE (intentional): SqliteNotifyWaitStore self-initializes via the shared SqliteSchemaInitializer,
    // which also creates currently-unused sibling tables (messages/thread_metadata/run_ledger/
    // accepted_inputs) in this db file — accepted to reuse shared infra rather than fork a
    // notify-only initializer.
    // KNOWN LIMITATION (#145, accepted): notify_waits rows for threads that are never reopened after a
    // restart are never pruned (restore/cleanup only runs when that thread's loop is reconstructed).
    // Acceptable for a sample app; no pruning logic added.
    var notifyWaitDbPath = builder.Configuration["LmStreaming:NotifyWaitDbPath"];
    if (string.IsNullOrWhiteSpace(notifyWaitDbPath))
    {
        notifyWaitDbPath = Path.Combine(AppContext.BaseDirectory, "notify-waits.db");
    }
    _ = builder.Services.AddSingleton<INotifyWaitStore>(_ => new SqliteNotifyWaitStore(
        new SqliteConnectionFactory(notifyWaitDbPath)
    ));

    // The REST status resolver (ConversationsController dependency) reads the conversation store plus
    // its run ledger. Resolve the ledger from the registered IConversationStore so a test host that
    // swaps the store (see BrowserWebAppFactory) keeps both halves pointing at the same instance —
    // without this registration the controller cannot be activated and every REST endpoint 500s.
    _ = builder.Services.AddSingleton(sp =>
    {
        var store = sp.GetRequiredService<IConversationStore>();
        var ledger = store as IRunLedgerStore ?? sp.GetRequiredService<IRunLedgerStore>();
        return new ConversationStatusResolver(store, ledger);
    });

    // Register the FileChatModeStore for chat mode persistence
    var chatModesPath = Path.Combine(AppContext.BaseDirectory, "chat-modes");
    _ = builder.Services.AddSingleton<IChatModeStore>(new FileChatModeStore(chatModesPath));

    // Scope workspace metadata to the active gateway URL + process AppId. The ambiguous legacy
    // flat catalog is archived once and never assigned to the currently configured gateway.
    var workspaceCatalogRoot = Path.Combine(AppContext.BaseDirectory, "workspaces");
    var workspaceCatalogResolution = new GatewayWorkspaceCatalogResolver()
        .ResolveAsync(workspaceCatalogRoot, workspaceCatalogIdentity)
        .GetAwaiter()
        .GetResult();
    Log.Information(
        "Workspace catalog scoped to gateway {GatewayBaseUrl}, app {AppId}, key {CatalogKeyPrefix}, path {CatalogPath}; legacy archive {LegacyArchive}",
        workspaceCatalogIdentity.CanonicalBaseUrl,
        workspaceCatalogIdentity.AppId,
        workspaceCatalogIdentity.CatalogKey[..12],
        workspaceCatalogResolution.CatalogDirectory,
        workspaceCatalogResolution.LegacyArchivePath ?? "(none)"
    );
    var defaultWorkspaceLeaf = sandboxOptions.ResolveWorkspace().Leaf;
    _ = builder.Services.AddSingleton<IWorkspaceStore>(
        new FileWorkspaceStore(workspaceCatalogResolution.CatalogDirectory, defaultWorkspaceLeaf)
    );

    // Register built-in (server-side) tool definitions for the tools API. The list is
    // computed for the boot default so the global tools API stays stable; per-conversation
    // built-in tools are derived from the resolved provider id at agent-creation time.
    var builtInTools = GetBuiltInToolsForProvider(providerMode);
    var builtInToolDefinitions =
        builtInTools
            ?.OfType<AnthropicBuiltInTool>()
            .Select(t => new ToolDefinition { Name = t.Name, Description = $"Server-side {t.Name} tool ({t.Type})" })
            .ToList()
        ?? [];
    _ = builder.Services.AddSingleton<IReadOnlyList<ToolDefinition>>(builtInToolDefinitions);

    // The catalog the Modes editor lists. Assembled from the same sources the agent factory wires
    // from, so what a mode can select matches what a conversation actually gets. Registered here,
    // after the built-in definitions it depends on.
    _ = builder.Services.AddSingleton<ISandboxToolCatalogProbe, SandboxToolCatalogProbe>();
    _ = builder.Services.AddSingleton<IToolCatalog, ToolCatalog>();

    // Register the provider agent factory (multi-provider support via LM_PROVIDER_MODE env var)
    Log.Information("LM Provider Mode: {ProviderMode}", providerMode);

    // Test-mode DI seam: default implementation is behavior-preserving. E2E tests can
    // replace this via ConfigureTestServices to inject scripted SSE responders and
    // sub-agent templates without touching any real-provider code path.
    builder.Services.TryAddSingleton<ITestAgentBuilder, DefaultTestAgentBuilder>();
    builder.Services.TryAddSingleton(TimeProvider.System);

    // Provider id → IStreamingAgent. Receives the per-conversation provider id resolved
    // by the pool (request → persisted → default), so this factory does not know about
    // LM_PROVIDER_MODE — it just dispatches by id.
    _ = builder.Services.AddSingleton<Func<string, IStreamingAgent>>(sp =>
        providerId =>
        {
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();

            // Discovered Copilot models route by their transport (Anthropic Messages vs OpenAI
            // Responses); everything else is a fixed provider handled by the switch below.
            if (sp.GetRequiredService<ProviderRegistry>().TryGetCopilotModel(providerId, out var copilotModel))
            {
                return CreateCopilotModelAgent(copilotModel, loggerFactory);
            }

            // Discovered Anthropic-compatible provider-family models (e.g. DeepSeek) route through the
            // same AnthropicClient/AnthropicAgent pairing as the "anthropic" provider, but with the
            // family's own base URL/API key instead of fixed env vars.
            if (sp.GetRequiredService<ProviderRegistry>().TryGetAnthropicCompatModel(providerId, out var compatModel))
            {
                return CreateAnthropicCompatAgent(compatModel, loggerFactory);
            }

            return providerId.ToLowerInvariant() switch
            {
                "openai" => CreateOpenAiAgent(loggerFactory),
                "anthropic" => CreateAnthropicAgent(loggerFactory),
                "test-anthropic" => CreateAnthropicTestAgent(loggerFactory, sp.GetRequiredService<ITestAgentBuilder>()),
                "test" => CreateTestAgent(loggerFactory, sp.GetRequiredService<ITestAgentBuilder>()),
                _ => throw new ProviderUnavailableException(
                    providerId,
                    "no IStreamingAgent factory is registered for this provider"
                ),
            };
        }
    );

    // Read LlmQueryMcp config for books/question MCP servers
    var llmQueryMcpBaseUrl = builder.Configuration["LlmQueryMcp:BaseUrl"];
    var llmQueryMcpExamType = builder.Configuration["LlmQueryMcp:ExamType"] ?? "NeetPG";

    // Per-subscriber output-channel capacity for the pooled agents. Unset (the shipped default) keeps
    // MultiTurnAgentBase's own default of 1000, so production delivery is unchanged; a host may shrink
    // it to reproduce the slow-consumer drop path deterministically (a browser E2E scenario does
    // exactly that, since a full channel â€” not a timeout â€” is what evicts a lagging subscriber).
    const int defaultOutputChannelCapacity = 1000;
    var outputChannelCapacity =
        int.TryParse(builder.Configuration["LmStreaming:OutputChannelCapacity"], out var configuredCapacity)
        && configuredCapacity > 0
            ? configuredCapacity
            : defaultOutputChannelCapacity;

    // Register the MultiTurnAgentPool with provider- and mode-aware factory
    // Public-cost pricing (#328/#378). This host is where a cost is actually stamped — the review daemon
    // runs no loop of its own and drives every review into a conversation here — so the catalog the
    // UsageLedger resolves against has to be composed and registered by THIS process. See PricingCatalog
    // for the configuration shape and why no rates are shipped in the repository.
    _ = builder.Services.AddConfiguredPricing(builder.Configuration);

    _ = builder.Services.AddSingleton(sp =>
    {
        var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
        var functionRegistry = sp.GetRequiredService<FunctionRegistry>();
        var agentFactory = sp.GetRequiredService<Func<string, IStreamingAgent>>();
        var conversationStore = sp.GetRequiredService<IConversationStore>();
        // #145: the durable notify-wait store is a process singleton (like conversationStore); captured
        // here so the inner per-thread agent factory can attach it to TriggerOptions (see the Build(...)
        // call site below). ThreadId is per-thread via context.ThreadId inside that factory.
        var notifyWaitStore = sp.GetRequiredService<INotifyWaitStore>();
        var providerRegistry = sp.GetRequiredService<ProviderRegistry>();
        // Conversation-wide usage cost (#196): resolves an estimated public cost per model when a rate is
        // configured under "Pricing:Models". Empty by default, so flat-rate Copilot ids resolve to null
        // cost ("unavailable") — the correct state — while any model with a configured rate gets a cost.
        var pricingResolver = sp.GetRequiredService<IPricingResolver>();
        var codexLifetime = sp.GetRequiredService<CodexMcpServerLifetime>();
        var mockHostLifetime = sp.GetRequiredService<MockProviderHostLifetime>();
        var sandboxRegistryForCleanup = sp.GetRequiredService<SandboxSessionRegistry>();
        var workflowRunRegistry = sp.GetRequiredService<WorkflowRunRegistry>();
        var descendantScanner = sp.GetRequiredService<ConversationDescendantScanner>();
        // Workspace transcript mirror (#251). Captured here so the per-thread factory below can attach
        // each agent it builds; eviction is wired into the pool's ThreadRemoved handler further down.
        var transcriptMirror = sp.GetRequiredService<WorkspaceTranscriptMirror>();
        // Lifecycle observation / tool approval (#227). Resolved once for the process — the bundle's
        // sequence allocator owns the producer epoch, and loops that share it share that epoch, which
        // is what lets a subscriber tell "producer restarted" from "events were lost". Handed to the
        // pool, which puts it on every AgentCreationContext; the per-thread factory below reads it
        // from there rather than closing over it, so the pool stays the single distributor. Registered
        // only when a Lifecycle flag is set, so on a default configuration this is null and every loop
        // falls back to MultiTurnLifecycleServices.Disabled — the sample behaves exactly as before.
        var hostLifecycleServices = sp.GetService<MultiTurnLifecycleServices>();

        // Web tools (WebFetch/WebSearch) fallback provider. Built ONCE for the process (this factory
        // runs once for the singleton pool) and shared across conversations — the provider owns a
        // single app-lifetime HttpClient. Invalid configuration degrades gracefully: we log the
        // errors and leave the provider null so the sample still boots without web tools.
        var webToolsOptions = WebToolsOptions.FromEnvironment();
        var webToolsErrors = webToolsOptions.Validate();
        JinaWebProvider? jinaWebProvider = null;
        if (
            webToolsErrors.Count == 0
            && string.Equals(webToolsOptions.Backend, "jina", StringComparison.OrdinalIgnoreCase)
        )
        {
            jinaWebProvider = new JinaWebProvider(webToolsOptions, loggerFactory.CreateLogger<JinaWebProvider>());
        }
        else if (webToolsErrors.Count > 0)
        {
            loggerFactory
                .CreateLogger<Program>()
                .LogWarning("Web tools disabled (invalid configuration): {Errors}", string.Join("; ", webToolsErrors));
        }

        // Best-effort disposal of an agent's owned resources (MCP clients) on a construction failure.
        // Shared by the branch that builds them and by the mirror-attach wrapper below, so both failure
        // paths dispose identically instead of one of them quietly leaking.
        static void DisposeOwnedResources(IReadOnlyList<IAsyncDisposable>? ownedResources)
        {
            if (ownedResources == null)
            {
                return;
            }

            foreach (var resource in ownedResources)
            {
                try
                {
                    resource.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
                catch
                { /* ignore cleanup errors */
                }
            }
        }

        // Start mirroring every conversation's messages into its workspace (#251).
        //
        // THIS WRAPPER IS THE ATTACH POINT, and it is a wrapper rather than a call inside the factory
        // on purpose. The factory below has seven construction branches, each ending in its own
        // `return`; an attach placed in one of them is invisible to the other six. That is exactly how
        // the CLI-backed providers (codex, claude, copilot and their three mocks) shipped unmirrored —
        // they return before reaching the attach, so their workspace conversations produced neither a
        // root transcript nor descendant files even though those loops all support SubscribeAsync.
        // Wrapping makes it structural: there is now exactly one place a result can leave this factory,
        // so an eighth provider cannot repeat the bug.
        //
        // Attaching here still happens after construction and before the agent is handed to the pool,
        // so no turn boundary can be missed. A mode switch builds a replacement agent for the same
        // threadId and re-attaches through this same path; the mirror swaps the subscription and keeps
        // the writer.
        Func<MultiTurnAgentPool.AgentCreationContext, MultiTurnAgentPool.AgentCreationResult> AttachingToMirror(
            Func<MultiTurnAgentPool.AgentCreationContext, MultiTurnAgentPool.AgentCreationResult> build
        ) =>
            context =>
            {
                var result = build(context);
                try
                {
                    transcriptMirror.Attach(result.Agent);
                }
                catch
                {
                    // The construction branches dispose what they own when they throw; once one has
                    // returned, this wrapper is the only thing standing between a failed attach and a
                    // leaked MCP client, so it takes over that duty.
                    DisposeOwnedResources(result.OwnedResources);
                    throw;
                }

                return result;
            };

        var pool = new MultiTurnAgentPool(
            AttachingToMirror(context =>
            {
                var threadId = context.ThreadId;
                var mode = context.Mode;
                var lifecycleServices = context.LifecycleServices;
                // Anchor the model to the real current date. Injected once at the single mode entry
                // point so every derived system prompt (workspace suffix, medical context, etc.)
                // carries it. Without it, models fall back to a training-era date, distrust
                // correctly-dated web_search results as "future"/unreliable, and loop.
                mode = mode with
                {
                    SystemPrompt = SystemPromptAugmenter.PrependCurrentDate(mode.SystemPrompt, DateTimeOffset.UtcNow),
                };
                var providerId = context.ProviderId;
                var requestResponseDumpFileName = context.DumpFile;
                var workspaceId = context.WorkspaceId;
                // Per-caller sandbox identity (issue #153 M2). Null for the interactive UI — the
                // sandbox session and /mcp headers below fall back to the process default in that
                // case. Frozen for the pooled agent's lifetime; captured once here so every site in
                // this factory threads the same value.
                var callerCredential = context.CallerCredential;

                var isMedicalMode = mode.Id == SystemChatModes.MedicalKnowledgeModeId;
                var mcpBaseUrl = isMedicalMode ? llmQueryMcpBaseUrl : null;
                var normalizedProviderId = providerId.ToLowerInvariant();

                // What this mode is allowed to do, derived from its OWN tool selection rather than
                // from its id. The previous `mode.Id == WorkspaceAgentModeId` checks meant a COPY of
                // Workspace Agent - necessarily a different id - silently got no sandbox session, no
                // sandbox tools, no workflow launch tools and no collaboration surface. See
                // ModeCapabilities.
                //
                // A sandbox-backed mode resolves its session up front (sync-over-async, consistent
                // with the books wiring) and augments the system prompt with the workspace's absolute
                // host path - the local backend has no '/workspace' mount, so the model must use the
                // absolute path for the file tools. A mode with a PARTIAL sandbox allow-list (e.g.
                // Workflow Author's Read/Grep/Skill) gets the same session and a narrower tool slice
                // wired further below; this shared block establishes what they have in common.
                var caps = ModeCapabilities.Resolve(mode.EnabledCapabilityTools);
                // True when the mode takes the whole gateway surface rather than a named subset.
                // Only a full-surface mode can be served over the Copilot CLI transport, which
                // connects to /mcp directly and cannot apply a per-tool filter.
                var hasFullSandboxSurface = caps.NeedsSandbox && caps.SandboxToolAllowList is null;
                var sandboxRegistry = sp.GetRequiredService<SandboxSessionRegistry>();
                var sandboxLifetime = sp.GetRequiredService<SandboxGatewayLifetime>();
                SandboxSession? sandboxSession = null;
                // Staged for the pool to publish as part of a successful agent-entry commit (WI #195): the
                // ONLY authoritative "this conversation has a sandbox workspace" signal for the file browser.
                SandboxEstablishedBinding? stagedBinding = null;
                var effectiveMode = mode;
                if (caps.NeedsSandbox)
                {
                    // Only the middleware providers (OpenAI/Anthropic/test/...) and - for a mode that
                    // takes the FULL gateway surface - Copilot are wired to route tool calls to the
                    // sandbox gateway. Reject the CLI-only providers and mock variants up front instead
                    // of creating an unused sandbox session and an agent with no sandbox tools.
                    //
                    // Copilot is a special case, and the rule is about the SHAPE of the selection, not
                    // the mode's identity: its CLI transport connects to the raw /mcp with no per-tool
                    // filter, so it can serve a full-surface mode but cannot honour a named subset.
                    // Handing it the full surface for a mode that asked for Read/Grep/Skill would
                    // defeat the narrowing, and it cannot consume the filtered FunctionRegistry path
                    // either - so a partial-surface mode on Copilot would get NEITHER the tools it
                    // asked for NOR the workflow-authoring tools (its provider arm returns before those
                    // are wired). Reject it here rather than establishing a live session and appending
                    // a system-prompt suffix promising tools it can't have.
                    var copilotCannotNarrowSandbox = !hasFullSandboxSurface && normalizedProviderId is "copilot";
                    if (
                        normalizedProviderId is "codex" or "claude" or "codex-mock" or "claude-mock" or "copilot-mock"
                        || copilotCannotNarrowSandbox
                    )
                    {
                        throw new ProviderUnavailableException(
                            normalizedProviderId,
                            $"Mode '{mode.Name}' supports the OpenAI/Anthropic"
                                + (hasFullSandboxSurface ? " and Copilot providers" : " providers")
                                + "; this provider is not wired for the sandbox"
                                + (
                                    copilotCannotNarrowSandbox
                                        ? " when the mode selects only some workspace tools (Copilot "
                                            + "cannot filter the gateway's tool surface)."
                                        : "."
                                )
                        );
                    }

                    // Resolve the chosen workspace (null/empty → "default", identical to before)
                    // and mount its own directory. The store is resolved from the captured service
                    // provider; the context carries the workspace id the thread locked in.
                    var effectiveWorkspaceId = string.IsNullOrWhiteSpace(workspaceId)
                        ? SandboxSessionRegistry.DefaultWorkspaceId
                        : workspaceId;
                    var workspaceStore = sp.GetRequiredService<IWorkspaceStore>();
                    var workspace = workspaceStore.GetAsync(effectiveWorkspaceId).GetAwaiter().GetResult();
                    var workspaceRef = BuildWorkspaceRef(effectiveWorkspaceId, workspace);
                    if (workspace is not null)
                    {
                        try
                        {
                            sp.GetRequiredService<WorkspaceCatalogCompatibilityService>()
                                .ValidateForSessionAsync(workspace)
                                .GetAwaiter()
                                .GetResult();
                        }
                        catch (UnsupportedWorkspaceMarketplacesException ex)
                        {
                            throw new SandboxSessionUnavailableException(
                                effectiveWorkspaceId,
                                StatusCodes.Status400BadRequest,
                                ex.Message,
                                ex
                            );
                        }
                        catch (WorkspaceGatewayCatalogUnavailableException ex)
                        {
                            throw new SandboxSessionUnavailableException(
                                effectiveWorkspaceId,
                                StatusCodes.Status503ServiceUnavailable,
                                ex.Message,
                                ex
                            );
                        }
                    }

                    // Use the liveness-checked variant: the gateway evicts idle sessions on its own
                    // schedule, and reusing a cached-but-evicted handle silently strips the session's
                    // marketplace-provided tools (e.g. sandbox-Skill). This recreates the session on a
                    // gateway 404 so the agent always gets the full tool set without a process restart.
                    sandboxSession = sandboxRegistry
                        .GetOrCreateLiveSessionAsync(workspaceRef, credential: callerCredential)
                        .GetAwaiter()
                        .GetResult();
                    // The effective credential is the caller's or the process default (never null) — used for
                    // gateway calls. The THIRD arg preserves the original caller's provenance (null for the
                    // interactive UI) so the file-browser resolver can distinguish an interactive owner from
                    // an S2S caller even when the S2S caller reuses the default app id.
                    stagedBinding = new SandboxEstablishedBinding(
                        workspaceRef,
                        callerCredential ?? sandboxRegistry.DefaultCredential,
                        callerCredential,
                        sandboxSession.SessionId
                    );
                    // Register this agent's threadId against the session HERE — at the one boundary every
                    // sandbox-backed conversation passes through — rather than further down beside the
                    // subagent binding, which several provider branches (Copilot chief among them) return
                    // before ever reaching. Two things read this index and both are silently wrong when a
                    // thread is missing from it: the context-discovery webhook fans a context_file delivery
                    // out to the registered threads, and WorkspacePluginSelectionService.WaitForIdleAsync
                    // asks it which threads to check for an in-flight run — an unregistered thread reads as
                    // "idle", so a plugin-selection migration would tear down a session mid-turn.
                    // RegisterThread is idempotent, and mode-switch recreations preserve threadId by design
                    // (and don't fire the pool's ThreadRemoved event), so this registration survives them.
                    sandboxRegistry.RegisterThread(sandboxSession.SessionId, threadId);
                    // The suffix must name the tools this agent ACTUALLY has, or the model will
                    // confidently claim tools (Write/Edit/Bash/...) that do not exist for it. Derived
                    // from the mode's own allow-list rather than from its id, so a narrowed copy gets a
                    // narrowed suffix instead of Workspace Agent's promises.
                    var wsSuffix = BuildWorkspaceSuffix(sandboxSession.HostPath, caps.SandboxToolAllowList);

                    // Seed any context files (CLAUDE.md / AGENTS.md) the gateway has already
                    // discovered into the system prompt. Mid-session deliveries land via the
                    // webhook + injector; this fills the boot-time hole where the gateway has
                    // already scanned the workspace before the first turn is sent.
                    var contextSuffix = TryBuildRootContextSuffix(
                        sandboxRegistry,
                        sandboxSession,
                        sp.GetRequiredService<ContextDiscoveryFormatter>(),
                        loggerFactory.CreateLogger("LmStreaming.Sample.ContextDiscoverySeed")
                    );
                    if (!string.IsNullOrEmpty(contextSuffix))
                    {
                        wsSuffix += contextSuffix;
                    }

                    effectiveMode = mode with { SystemPrompt = mode.SystemPrompt + wsSuffix };
                }

                // *-mock providers reuse the same agent-loop helpers as their real counterparts;
                // the mock-host base URL is threaded into the SDK options as a per-spawn override
                // applied to the child CLI process's environment block.
                var mockBaseUrl = mockHostLifetime.BaseUrl;

                if (string.Equals(normalizedProviderId, "codex-mock", StringComparison.Ordinal))
                {
                    if (string.IsNullOrWhiteSpace(mockBaseUrl))
                    {
                        throw new ProviderUnavailableException(
                            "codex-mock",
                            "the in-process mock provider host is not running"
                        );
                    }

                    string codexEndpoint;
                    try
                    {
                        codexEndpoint = codexLifetime.EnsureStartedAsync().GetAwaiter().GetResult();
                    }
                    catch (Exception ex)
                    {
                        throw new ProviderUnavailableException(
                            "codex-mock",
                            $"MCP server failed to start: {ex.Message}",
                            ex
                        );
                    }

                    return new MultiTurnAgentPool.AgentCreationResult(
                        CreateCodexAgentLoop(
                            threadId,
                            mode,
                            functionRegistry,
                            requestResponseDumpFileName,
                            conversationStore,
                            loggerFactory,
                            codexEndpoint,
                            mcpBaseUrl,
                            llmQueryMcpExamType,
                            mockBaseUrlOverride: $"{mockBaseUrl}/v1",
                            mockApiKeyOverride: "mock-token",
                            lifecycleServices: lifecycleServices
                        )
                    );
                }

                if (string.Equals(normalizedProviderId, "claude-mock", StringComparison.Ordinal))
                {
                    if (string.IsNullOrWhiteSpace(mockBaseUrl))
                    {
                        throw new ProviderUnavailableException(
                            "claude-mock",
                            "the in-process mock provider host is not running"
                        );
                    }

                    // Claude Agent SDK CLI appends /v1/messages itself, so the configured
                    // value must NOT end in /v1 (issue #29). Strip defensively so config drift
                    // doesn't silently turn into 404s.
                    var claudeMockBaseUrl = BaseUrlNormalizer.StripV1Suffix(mockBaseUrl);
                    return new MultiTurnAgentPool.AgentCreationResult(
                        CreateClaudeAgentLoop(
                            threadId,
                            mode,
                            requestResponseDumpFileName,
                            conversationStore,
                            loggerFactory,
                            mcpBaseUrl,
                            llmQueryMcpExamType,
                            mockBaseUrlOverride: claudeMockBaseUrl,
                            mockAuthTokenOverride: "mock-token",
                            lifecycleServices: lifecycleServices
                        )
                    );
                }

                if (string.Equals(normalizedProviderId, "copilot-mock", StringComparison.Ordinal))
                {
                    return string.IsNullOrWhiteSpace(mockBaseUrl)
                        ? throw new ProviderUnavailableException(
                            "copilot-mock",
                            "the in-process mock provider host is not running"
                        )
                        : new MultiTurnAgentPool.AgentCreationResult(
                            CreateCopilotAgentLoop(
                                threadId,
                                mode,
                                functionRegistry,
                                requestResponseDumpFileName,
                                conversationStore,
                                loggerFactory,
                                mockBaseUrlOverride: $"{mockBaseUrl}/v1",
                                mockApiKeyOverride: "mock-token",
                                lifecycleServices: lifecycleServices
                            )
                        );
                }

                if (string.Equals(normalizedProviderId, "codex", StringComparison.Ordinal))
                {
                    string codexEndpoint;
                    try
                    {
                        // Lazy MCP startup — fires on first codex agent creation regardless of
                        // boot mode. Sync-over-async is acceptable: this happens at most once
                        // per process from the pool's per-thread creation lock.
                        codexEndpoint = codexLifetime.EnsureStartedAsync().GetAwaiter().GetResult();
                    }
                    catch (Exception ex)
                    {
                        throw new ProviderUnavailableException(
                            "codex",
                            $"MCP server failed to start: {ex.Message}",
                            ex
                        );
                    }

                    return new MultiTurnAgentPool.AgentCreationResult(
                        CreateCodexAgentLoop(
                            threadId,
                            mode,
                            functionRegistry,
                            requestResponseDumpFileName,
                            conversationStore,
                            loggerFactory,
                            codexEndpoint,
                            mcpBaseUrl,
                            llmQueryMcpExamType,
                            lifecycleServices: lifecycleServices
                        )
                    );
                }

                if (string.Equals(normalizedProviderId, "claude", StringComparison.Ordinal))
                {
                    return new MultiTurnAgentPool.AgentCreationResult(
                        CreateClaudeAgentLoop(
                            threadId,
                            mode,
                            requestResponseDumpFileName,
                            conversationStore,
                            loggerFactory,
                            mcpBaseUrl,
                            llmQueryMcpExamType,
                            lifecycleServices: lifecycleServices
                        )
                    );
                }

                if (string.Equals(normalizedProviderId, "copilot", StringComparison.Ordinal))
                {
                    // Keep hosted/Jina web tools conversation-local; the shared registry is a process
                    // singleton and must never retain an MCP client owned by one pooled agent.
                    var copilotRegistry = new FunctionRegistry();
                    var (copilotSharedContracts, copilotSharedHandlers) = functionRegistry.Build();
                    foreach (var contract in copilotSharedContracts)
                    {
                        if (copilotSharedHandlers.TryGetValue(contract.Name, out var handler))
                        {
                            _ = copilotRegistry.AddFunction(contract, handler, "SampleTools");
                        }
                    }

                    var cliHostedSearch = CopilotWebSearchRegistration.TryRegister(
                        copilotRegistry,
                        WebToolRegistrationPolicy.ResolveEnabledTools(mode.EnabledTools, mode.EnabledBuiltInTools),
                        s_copilotTokenProvider.Value,
                        s_copilotSession.Value,
                        new CopilotOptions(),
                        loggerFactory
                    );
                    try
                    {
                        _ = WebToolRegistrationPolicy.Apply(
                            copilotRegistry,
                            normalizedProviderId,
                            WebToolRegistrationPolicy.ResolveEnabledTools(mode.EnabledTools, mode.EnabledBuiltInTools),
                            jinaWebProvider,
                            webToolsOptions,
                            loggerFactory,
                            isCopilotBackedModel: true,
                            suppressWebSearch: cliHostedSearch.Registered
                        );

                        // Sandbox MCP header dict: X-Session-ID plus the app's sandbox auth headers.
                        // The caller's credential (S2S) wins over the process default so an S2S
                        // caller's /mcp tool calls carry its own identity; the interactive UI (null)
                        // falls back to the default (issue #153 M1/M2). Connect-time-frozen for the
                        // pooled agent by design — not re-evaluated per turn.
                        // Only a full-surface mode reaches here with a sandbox: a partial allow-list
                        // on Copilot was rejected above, because the CLI connects to /mcp directly and
                        // cannot filter the gateway's tool surface.
                        Dictionary<string, string>? sandboxMcpHeaders = null;
                        if (hasFullSandboxSurface)
                        {
                            sandboxMcpHeaders = new Dictionary<string, string>
                            {
                                ["X-Session-ID"] = sandboxSession!.SessionId,
                            };
                            AddSandboxAuthHeaders(sandboxMcpHeaders, callerCredential ?? sandboxCredential);
                        }

                        return new MultiTurnAgentPool.AgentCreationResult(
                            CreateCopilotAgentLoop(
                                threadId,
                                effectiveMode,
                                copilotRegistry,
                                requestResponseDumpFileName,
                                conversationStore,
                                loggerFactory,
                                extraMcpServers: hasFullSandboxSurface
                                    ? BuildHttpMcpServer(
                                        "sandbox",
                                        $"{sandboxLifetime.GatewayBaseUrl}/mcp",
                                        sandboxMcpHeaders!
                                    )
                                    : null,
                                workingDirectoryOverride: hasFullSandboxSurface ? sandboxSession!.HostPath : null,
                                lifecycleServices: lifecycleServices
                            ),
                            cliHostedSearch.Resource is null ? null : [cliHostedSearch.Resource]
                        );
                    }
                    catch
                    {
                        if (cliHostedSearch.Resource is not null)
                        {
                            try
                            {
                                cliHostedSearch.Resource.DisposeAsync().AsTask().GetAwaiter().GetResult();
                            }
                            catch
                            { /* ignore cleanup errors so the construction failure remains primary */
                            }
                        }

                        throw;
                    }
                }

                var providerAgent = agentFactory(normalizedProviderId);

                // Per-conversation tool registry: clone the shared (stateless) sample tools and
                // layer a FRESH TaskManager on top so every chat gets its own isolated todo list
                // (add-task / list-tasks / update-task / add-note / ...). Only the API-driven
                // middleware providers (OpenAI/Anthropic/test/test-anthropic) reach here — the CLI
                // providers (codex/claude/copilot and their *-mock variants) returned earlier and
                // ship their own task tracking, so they stay on the shared SampleTools-only registry.
                var conversationRegistry = new FunctionRegistry();
                var (sharedContracts, sharedHandlers) = functionRegistry.Build();
                foreach (var sharedContract in sharedContracts)
                {
                    if (sharedHandlers.TryGetValue(sharedContract.Name, out var sharedHandler))
                    {
                        _ = conversationRegistry.AddFunction(sharedContract, sharedHandler, "SampleTools");
                    }
                }
                // Held in a local, not constructed inline as an argument: the instance the tools close
                // over IS the conversation's board, and handed back on the AgentCreationResult below it
                // becomes the pool's read path (GET /todos). Constructed inline it was unreachable, and
                // the only way to see the board was to ask the agent to run list-tasks.
                //
                // Hydrated from the persisted projection when one exists (#590 review F-002): a pool
                // entry recreated after eviction, a provider/mode swap, or a restart must start from
                // the durable board, because a fresh empty manager's FIRST mutation would otherwise
                // persist a one-row board over it — the writer's empty-board guard no longer applies
                // once one row exists, and the projection's monotonic guard accepts it because the
                // fresh capture genuinely is newer. Sync-over-async matches the sandbox and books
                // wiring in this same factory.
                var persistedBoard = ConversationTodoProjection
                    .LoadAsync(conversationStore, threadId)
                    .GetAwaiter()
                    .GetResult();
                var taskManager = persistedBoard is { IsEmpty: false }
                    ? TaskManager.FromSnapshot(persistedBoard)
                    : new TaskManager();
                _ = conversationRegistry.AddFunctionsFromObject(taskManager, providerName: "TaskManager");

                // Clone the per-conversation registry per-agent to avoid mutation, filtering by mode
                var filteredRegistry = BuildModeFilteredRegistry(conversationRegistry, mode.EnabledTools);

                // Add LlmQuery book search MCP tools — only for medical knowledge mode
                // Track MCP clients for proper disposal alongside the agent
                var ownedResources = new List<IAsyncDisposable>();
                // StartWorkflowAgent + friends: each launch spins up an ISOLATED controller loop with
                // its own model and restricted tool surface, wired further below once the conversation
                // loop exists (so an async workflow's completion notification can reach it). A
                // deployment can switch the whole LmWorkflow surface off without a redeploy via
                // WORKSPACE_AGENT_LMWORKFLOW_ENABLED=false. That switch now applies to EVERY mode that
                // asks for these tools, not only Workspace Agent, so "LmWorkflow off" means off
                // everywhere rather than off in one mode.
                var workspaceWorkflowEnabled =
                    caps.StartWorkflowTools
                    && !string.Equals(
                        Environment.GetEnvironmentVariable("WORKSPACE_AGENT_LMWORKFLOW_ENABLED"),
                        "false",
                        StringComparison.OrdinalIgnoreCase
                    );
                if (!string.IsNullOrEmpty(mcpBaseUrl))
                {
                    var (_, mcpClients) = ConnectLlmQueryMcpClients(
                        filteredRegistry,
                        threadId,
                        mcpBaseUrl,
                        llmQueryMcpExamType,
                        loggerFactory
                    );
                    if (mcpClients.Count > 0)
                    {
                        ownedResources.AddRange(mcpClients.Cast<IAsyncDisposable>());
                    }
                }

                // Workflow authoring/mutation tools (SetWorkflow, GetWorkflow, SetCurrentNode,
                // SetState, SetNotes, AddNode, RemoveNode) run on THIS conversation loop, so the model
                // drives a workflow graph in place rather than handing it to an isolated controller.
                // Selected per mode; Workflow Author takes the whole family.
                if (caps.WorkflowAuthoringTools)
                {
                    var workflowRuntime = WorkflowRuntime.CreateNew(
                        logger: loggerFactory.CreateLogger<WorkflowRuntime>()
                    );
                    // Narrowed to the names the mode selected, so the editor's per-tool checkboxes
                    // mean what they say; null allow-list (workflow:*) passes the family through.
                    _ = filteredRegistry.AddProvider(
                        ScopeWorkflowProvider(new WorkflowToolProvider(workflowRuntime), caps)
                    );
                }

                if (caps.NeedsSandbox)
                {
                    // Expose the sandbox file/shell tools via the gateway's MCP endpoint, bound to this
                    // agent's sandbox session by the X-Session-ID header and the app's sandbox auth
                    // headers. The caller's credential (S2S) wins over the process default so an S2S
                    // caller's /mcp calls carry its own identity; the interactive UI (null) falls back
                    // to the default (issue #153 M1/M2). Connect-time-frozen for the pooled agent; not
                    // re-evaluated per turn.
                    var sandboxMcpHeaders = new Dictionary<string, string>
                    {
                        ["X-Session-ID"] = sandboxSession!.SessionId,
                    };
                    AddSandboxAuthHeaders(sandboxMcpHeaders, callerCredential ?? sandboxCredential);

                    // Tools are exposed under their NATURAL names (Bash, Edit, ...): the `sandbox:`
                    // prefix a mode stores is a SELECTION id and never reaches the model. The gateway is
                    // the sole MCP server here, so no collisions. SandboxToolHealth.Wrap collapses the
                    // "container has no sandbox user" Docker-exec failure class into a single actionable
                    // message so the model stops retrying it.
                    //
                    // A null allow-list means the mode took the whole surface (sandbox:*) and must keep
                    // taking it, including tools a marketplace plugin adds later; a non-null one is an
                    // explicit subset and goes through the filtering connector.
                    var sandboxClients = caps.SandboxToolAllowList is { } sandboxAllowList
                        ? ConnectFilteredHttpMcpClient(
                            filteredRegistry,
                            "sandbox",
                            $"{sandboxLifetime.GatewayBaseUrl}/mcp",
                            sandboxMcpHeaders,
                            loggerFactory,
                            toolNames: sandboxAllowList,
                            omitServerPrefix: true,
                            handlerDecorator: SandboxToolHealth.Wrap
                        )
                        : ConnectHttpMcpClient(
                            filteredRegistry,
                            "sandbox",
                            $"{sandboxLifetime.GatewayBaseUrl}/mcp",
                            sandboxMcpHeaders,
                            loggerFactory,
                            omitServerPrefix: true,
                            handlerDecorator: SandboxToolHealth.Wrap
                        );

                    if (sandboxClients.Count > 0)
                    {
                        ownedResources.AddRange(sandboxClients.Cast<IAsyncDisposable>());
                    }
                    else
                    {
                        // The sandbox MCP endpoint is unreachable. Booting anyway is intentional
                        // (best-effort demo), but the workspace suffix added above claims tools this
                        // agent does not have - rebuild the prompt from the original mode with an
                        // honest degraded-mode notice instead, so the model tells the user rather than
                        // hallucinating tool calls.
                        effectiveMode = mode with
                        {
                            SystemPrompt =
                                mode.SystemPrompt
                                + "\n\nIMPORTANT: The sandbox workspace is currently UNAVAILABLE (its MCP endpoint "
                                + "could not be reached), so NO file or shell tools exist in this conversation. "
                                + "Do not claim or attempt to use them. Tell the user the workspace is offline and "
                                + "that restarting the app (or the sandbox gateway) should restore it.",
                        };
                        loggerFactory
                            .CreateLogger<Program>()
                            .LogWarning(
                                "Mode {ModeName} is running WITHOUT sandbox tools for thread {ThreadId}; "
                                    + "the system prompt now reports degraded mode instead of claiming tools",
                                mode.Name,
                                threadId
                            );
                    }
                }

                // WebFetch/WebSearch fallback tools for providers without a native web capability.
                // Applied AFTER the MCP additions so collision detection sees the final per-conversation
                // tool set. Gated by the provider allow-list and the mode's EnabledTools (function-tool
                // list); the native built-in path (modeBuiltInAllowList, below) is governed separately
                // and left untouched.
                // Resolve the discovered Copilot model (if any) backing this provider once; its
                // transport and raw id drive the model-id, reasoning, and web-tool wiring below.
                var isCopilotBackedModel = providerRegistry.TryGetCopilotModel(
                    normalizedProviderId,
                    out var copilotModelInfo
                );
                // Same resolution for a discovered Anthropic-compatible provider-family model (e.g.
                // DeepSeek); its raw model id drives the model-id and web-tool wiring below.
                var isAnthropicCompatModel = providerRegistry.TryGetAnthropicCompatModel(
                    normalizedProviderId,
                    out var anthropicCompatModelInfo
                );

                var enabledWebTools = WebToolRegistrationPolicy.ResolveEnabledTools(
                    mode.EnabledTools,
                    mode.EnabledBuiltInTools
                );
                var hostedSearch = isCopilotBackedModel
                    ? CopilotWebSearchRegistration.TryRegister(
                        filteredRegistry,
                        enabledWebTools,
                        s_copilotTokenProvider.Value,
                        s_copilotSession.Value,
                        new CopilotOptions(),
                        loggerFactory
                    )
                    : new CopilotWebSearchRegistrationResult(false, null, string.Empty);
                if (hostedSearch.Resource is not null)
                {
                    ownedResources.Add(hostedSearch.Resource);
                }

                try
                {
                    var webToolStatuses = WebToolRegistrationPolicy.Apply(
                        filteredRegistry,
                        normalizedProviderId,
                        enabledWebTools,
                        jinaWebProvider,
                        webToolsOptions,
                        loggerFactory,
                        isCopilotBackedModel,
                        isAnthropicCompatModel,
                        suppressWebSearch: hostedSearch.Registered
                    );
                    if (webToolStatuses.Count > 0)
                    {
                        var webToolsLogger = loggerFactory.CreateLogger<Program>();
                        foreach (var status in webToolStatuses)
                        {
                            webToolsLogger.LogInformation("WebTools: {Status}", status);
                        }
                    }

                    // Discovered Copilot models use their raw model id verbatim; discovered
                    // Anthropic-compatible models likewise use their configured model name verbatim;
                    // fixed providers keep the curated per-provider id map.
                    var modelId =
                        isCopilotBackedModel ? copilotModelInfo.Id
                        : isAnthropicCompatModel ? anthropicCompatModelInfo.ModelName
                        : GetModelIdForProvider(normalizedProviderId);

                    // Built-in (server-side) tools are selected by the MODE — never injected per
                    // provider or via a per-mode override. A mode declares its server-side built-ins in
                    // EnabledBuiltInTools (e.g. Workspace Agent => ["web_search"], decoupled from its
                    // empty function-tool allow-list); when that is null we fall back to EnabledTools for
                    // backward compat (e.g. Research Assistant lists web_search there). A mode that
                    // enables nothing gets no built-ins. This keeps tool availability mode-driven and
                    // leaves each provider's core behavior unchanged — we never add a tool the active
                    // mode didn't ask for.
                    var allBuiltInTools = GetBuiltInToolsForProvider(normalizedProviderId);
                    var modeBuiltInAllowList = mode.EnabledBuiltInTools ?? mode.EnabledTools;
                    var filteredBuiltInTools = ModeToolFilter.FilterBuiltInTools(allBuiltInTools, modeBuiltInAllowList);

                    // Surface model reasoning (provider→Thinking/Reasoning mapping). Extracted to a
                    // testable helper so the per-provider wiring is regression-guarded; see
                    // ProgramReasoningExtraPropertiesTests. Discovered Copilot models map by transport,
                    // and adaptive-thinking models opt out of the classic thinking budget request.
                    var extraProperties = BuildReasoningExtraProperties(
                        normalizedProviderId,
                        isCopilotBackedModel ? copilotModelInfo.Transport : null,
                        isCopilotBackedModel && copilotModelInfo.SupportsAdaptiveThinking
                    );

                    // Sub-agent orchestration options. Only the middleware providers reach this
                    // path — the CLI providers (codex/claude/copilot and their *-mock variants)
                    // returned earlier and have no sub-agent hook, so they are out of scope. Mock
                    // providers (test/test-anthropic) go through the ITestAgentBuilder DI seam for
                    // their base catalog (scripted templates for E2E); real providers start from the
                    // shared built-ins. BOTH then get the same workspace + marketplace enrichment when
                    // a sandbox session is active, so the Agent tool advertises an identical catalog
                    // regardless of provider — and the mock-only instruction-chain tool_schema probe
                    // can validate the workspace-discovered/marketplace sub-agents. In all cases the
                    // Legacy AgentFactory still builds a fresh parent-backend agent. The
                    // characteristics-aware factory below can instead route a resolved Copilot model
                    // through its own transport, and safely falls back to the parent provider agent.
                    var isTestMode =
                        string.Equals(normalizedProviderId, "test", StringComparison.Ordinal)
                        || string.Equals(normalizedProviderId, "test-anthropic", StringComparison.Ordinal);
                    // Sync-over-async on the agent-creation factory delegate: there is no async
                    // seam exposed by the pool's agent factory contract, and this runs on the
                    // pool-creation path (no ASP.NET SynchronizationContext) so a .Result here
                    // cannot deadlock. The blocking call is HTTP only when a sandbox session is
                    // active; otherwise it completes synchronously.
                    IStreamingAgent subAgentFactory() => agentFactory(normalizedProviderId);

                    // Root collaboration for THIS conversation (#244), or null when it resolves to off
                    // for this chat mode. Every descendant (ordinary sub-agent, workflow controller,
                    // workflow delegate) receives THIS handle by reference, so there is exactly one
                    // directory and one ledger per conversation.
                    var rootCollaboration = CreateRootCollaboration(collaborationHostOptions, caps, threadId);

                    var characteristicsAgentFactory = new CharacteristicsAgentFactory(
                        providerRegistry,
                        providerAgent,
                        model => CreateCopilotModelAgent(model, loggerFactory),
                        loggerFactory.CreateLogger<CharacteristicsAgentFactory>(),
                        parentCopilotModel: isCopilotBackedModel ? copilotModelInfo : null,
                        // Parent-model-reuse fallback: a classic Copilot Anthropic parent (advertises no
                        // efforts, so Copilot shaping is empty) or a non-Copilot parent still passes its OWN
                        // reasoning (e.g. a classic Thinking budget) to an inherited-model sub-agent.
                        parentReasoningExtraProperties: extraProperties
                    ).Create;
                    var outputTokenPolicy = sp.GetRequiredService<AgentOutputTokenPolicy>();
                    // Gated on the mode's own selection: a mode that records an explicit capability
                    // list with no subagents: entry gets NO delegation tools. A legacy mode (null
                    // list) resolves to ModeCapabilities.LegacyDefaults, whose SubAgents is true, so
                    // every mode that predates capability selection keeps the Agent tool it has
                    // always had.
                    var subAgentOptions = !caps.SubAgents
                        ? null
                        : BuildSubAgentOptionsAsync(
                                isTestMode,
                                sp.GetRequiredService<ITestAgentBuilder>(),
                                loggerFactory,
                                subAgentFactory,
                                characteristicsAgentFactory,
                                sandboxSession,
                                sp.GetRequiredService<WorkspaceSubAgentLoader>(),
                                sp.GetRequiredService<MarketplaceSubAgentLoader>(),
                                sp.GetRequiredService<IWorkspaceStore>(),
                                loggerFactory.CreateLogger("LmStreaming.Sample.SubAgentCatalog")
                            )
                            .GetAwaiter()
                            .GetResult();

                    if (subAgentOptions is not null)
                    {
                        subAgentOptions = outputTokenPolicy.ApplyDelegated(subAgentOptions);
                    }

                    // Applied here, before the workflow/transcript blocks below add their own `with`
                    // clauses, so the narrowing cannot be lost to a later record copy.
                    subAgentOptions = ApplySubAgentToolNarrowing(subAgentOptions, caps);

                    // Route a spawn's modelIntelligence tier (the Agent tool's argument, or a workflow task's
                    // tier) to a concrete model via the host's tier ladder, climbing to the nearest higher
                    // configured tier when the requested one is unmapped. The library stays catalog-agnostic;
                    // the ladder lives in the sample. These conversation sub-agents take the CHARACTERISTICS
                    // path, which builds a transport-correct provider for the resolved model itself, so no
                    // TierAgentFactory is needed here (unlike the controller's plain-path delegates below).
                    if (subAgentOptions is not null)
                    {
                        var subAgentModelResolver = sp.GetRequiredService<SubAgentModelResolver>();
                        // The conversation's own default sub-agent model, set by the headless caller at
                        // provision (ProvisionConversationRequest.SubAgentModelId). This is what lets a
                        // caller run a cheap orchestrator and stronger workers; without it the caller can
                        // only choose the conversation's provider id, which every child then inherits.
                        // Read here, at the point of use, so the value cannot be accepted at provision and
                        // then quietly drive nothing — the state #529 found it in.
                        var conversationSubAgentModelId = ConversationSubAgentModel
                            .ReadAsync(sp.GetRequiredService<IConversationStore>(), threadId)
                            .GetAwaiter()
                            .GetResult();
                        subAgentOptions = subAgentOptions with
                        {
                            TierModelResolver = tier => subAgentModelResolver.ResolveClimbing(null, tier),
                            // Guard the Agent tool's free-form `model` override: validate it against the
                            // discovered Copilot catalog (drop unknown ids → inherit parent instead of a
                            // provider BadRequest) and surface the valid ids in the tool descriptor so the
                            // LLM picks a real one in the first place.
                            ModelOverrideValidator = subAgentModelResolver.IsKnownModel,
                            AvailableModelIds = subAgentModelResolver.AvailableModelIds,
                            DefaultSubAgentModelId = conversationSubAgentModelId,
                        };
                    }

                    // Ordinary conversation sub-agents (characteristics path) inherit the parent's thinking as
                    // an effort FLOOR (Option A: High) — applied only when the parent can itself think, so an
                    // inherited-model sub-agent reasons like the launching conversation instead of running
                    // un-nudged. A template that lowers its own Effort, pins a model, or tier-resolves one
                    // overrides the floor (SubAgentManager). The parent "can think" when it has classic
                    // reasoning metadata OR is an adaptive Copilot model (which reasons via output_config.effort
                    // and therefore carries no classic extraProperties). The plain-path counterpart
                    // (InheritedReasoning) is seeded on the controller's own options, not here.
                    var parentCanThink =
                        !extraProperties.IsEmpty || (isCopilotBackedModel && copilotModelInfo.SupportsAdaptiveThinking);
                    if (subAgentOptions is not null && parentCanThink)
                    {
                        subAgentOptions = subAgentOptions with { InheritedEffort = ReasoningEffort.High };
                    }

                    // When a sandbox session is active, share the catalog with the session
                    // registry so the context-discovery webhook can activate newly discovered
                    // subagents into the same source the loop is reading. Without a session there
                    // is no webhook path, so the loop falls back to wrapping the static templates
                    // in a private source inside its ctor.
                    MutableSubAgentTemplateSource? sharedSubAgentSource = null;
                    if (sandboxSession is not null && subAgentOptions is not null)
                    {
                        var binding = BindConversationSubAgents(
                            sp.GetRequiredService<SandboxSessionRegistry>(),
                            sandboxSession.SessionId,
                            threadId,
                            subAgentOptions.Templates,
                            subAgentFactory,
                            characteristicsAgentFactory
                        );
                        sharedSubAgentSource = binding.Source;
                    }

                    // Declared before construction so the trigger-options closure below can read the
                    // just-built loop's SubAgentManager: the loop consumes AdditionalRegistrations
                    // inside its own ctor, so a subagent-kind source can't be handed the manager
                    // directly — it resolves it lazily once the loop (and thus the manager) exists.
                    MultiTurnAgentLoop agent = null!;
                    // #145: attach the durable notify-wait store + this thread's id so notify-mode waits
                    // survive a process restart (TriggerRuntime restores/reconciles them on thread
                    // recovery). Set via a record `with` on Build's result so SampleTriggerRegistrations.Build
                    // keeps its trigger-source-only responsibility. Test-mode/mock-provider ONLY today — same
                    // gate as the triggerOptions pass-through at the loop ctor below; real-provider rollout is
                    // tracked in #161. (When !isTestMode the whole triggerOptions object is discarded there,
                    // so leaving the store null keeps it from being attached to a thrown-away options value.)
                    var triggerOptions = SampleTriggerRegistrations.Build(
                        sandboxEnabled: sandboxSession is not null,
                        subAgentManagerAccessor: () => agent?.SubAgentManager,
                        loggerFactory: loggerFactory
                    ) with
                    {
                        NotifyWaitStore = isTestMode ? notifyWaitStore : null,
                        ThreadId = isTestMode ? threadId : null,
                    };

                    // Deterministic mock-workflow testing: enable the workflow tool family for the SCRIPTED
                    // mock providers (test / test-anthropic) in default mode WITHOUT a sandbox. The workflow
                    // wiring below is sandbox-independent (it uses subAgentFactory / subAgentOptions / the
                    // late-bound agent — never sandboxSession), and delegates inherit the conversation's mock
                    // tools via the transparency snapshot. Scoped strictly to test providers, so a real-provider
                    // default conversation never gets an unsandboxed WorkflowManager.
                    if (!workspaceWorkflowEnabled && normalizedProviderId is "test" or "test-anthropic")
                    {
                        workspaceWorkflowEnabled = true;
                    }

                    // Wire the StartWorkflowAgent tool family onto the conversation registry (Workspace Agent
                    // mode). Declared before the loop ctor so the launch tools are registered before the
                    // sub-agent snapshot is taken; the completion notifier is late-bound to `agent` (assigned
                    // just below). This replaces #130's direct SetWorkflow/GetWorkflow wiring.
                    if (workspaceWorkflowEnabled)
                    {
                        // Q2: the controller runs on a single, FIXED, pre-configured model — a configured
                        // value if present, else the conversation's own default model. Never the caller's.
                        var configuredControllerModel = Environment.GetEnvironmentVariable("WORKFLOW_CONTROLLER_MODEL");
                        var controllerModelId = string.IsNullOrWhiteSpace(configuredControllerModel)
                            ? modelId
                            : configuredControllerModel;

                        // Q5: concurrent-workflow cap defaults to 8, overridable via config.
                        var maxConcurrentWorkflows =
                            int.TryParse(
                                Environment.GetEnvironmentVariable("WORKFLOW_MAX_CONCURRENT"),
                                out var configuredCap
                            )
                            && configuredCap >= 1
                                ? configuredCap
                                : 8;

                        // Build the controller delegate options for a given provider — so a StartWorkflowAgent
                        // run with a preferred provider spawns its delegates on THAT provider (agent factory
                        // bound to it). The fixed default below uses the conversation's provider.
                        SubAgentOptions BuildControllerOptions(string providerId)
                        {
                            // Share the launching conversation's ENRICHED catalog (built-ins + workspace-discovered
                            // + marketplace) with the controller so a workflow delegate can spawn the SAME
                            // subagent_types the primary agent and its sub-agents can — not just general-purpose/
                            // researcher (the bug: a delegate asking for a plugin type got "Unknown template …
                            // Available: general-purpose, researcher"). Prefer the LIVE shared source so mid-session
                            // context-discovery registrations flow into the controller too; fall back to the static
                            // enriched snapshot; both are supersets of the built-ins. With neither (no sandbox AND
                            // no sub-agent options) fall back to the built-ins-only catalog.
                            var enrichedCatalog = sharedSubAgentSource?.Templates ?? subAgentOptions?.Templates;
                            var controllerTemplates = enrichedCatalog is not null
                                ? BuiltInSubAgentTemplates.CreateWorkflowControllerTemplates(
                                    enrichedCatalog,
                                    () => agentFactory(providerId)
                                )
                                : BuiltInSubAgentTemplates.CreateWorkflowControllerTemplates(() =>
                                    agentFactory(providerId)
                                );

                            var opts = new SubAgentOptions
                            {
                                Templates = controllerTemplates,
                                MaxConcurrentSubAgents = BuiltInSubAgentTemplates.DefaultMaxConcurrentSubAgents,
                                // Structural transparency guard: keep the controller's own workflow-state/launch
                                // tools OUT of the snapshot its delegates inherit, so an inherit-all delegate
                                // template can never drive/mutate the workflow it is a task of. The transparent
                                // domain tools are merged in separately via ExternalInheritableTools (below).
                                NonInheritedToolNames =
                                [
                                    .. WorkflowToolProvider.AllToolNames,
                                    .. StartWorkflowToolProvider.ToolNames,
                                ],
                                // Controller delegates take the PLAIN path (CreateWorkflowControllerTemplates
                                // sets CharacteristicsAgentFactory = null) and run on the controller's own model,
                                // so they inherit its already-transport-shaped reasoning (Option A: High floor).
                                // Keyed off `providerId` — the run's provider — so a preferred-provider run's
                                // delegates think on that provider's transport. For Copilot, providerId == model id.
                                InheritedReasoning = BuildControllerReasoningExtraProperties(
                                    providerRegistry,
                                    providerId,
                                    providerId
                                ),
                                // Route a delegate's modelIntelligence tier (forwarded by the controller from the
                                // composed unit) to a concrete model via the same host tier ladder. Controller
                                // delegates take the PLAIN path, so a tier that resolves to a CROSS-transport model
                                // (e.g. a Responses model under an Anthropic-transport controller) needs a
                                // transport-correct provider built for THAT model — supplied via TierAgentFactory
                                // (the DI per-id agent factory) — instead of the controller's own transport. A
                                // no-tier or same-transport delegate is unaffected (it keeps InheritedReasoning).
                                TierModelResolver = tier =>
                                    sp.GetRequiredService<SubAgentModelResolver>().ResolveClimbing(null, tier),
                                TierAgentFactory = agentFactory,
                                // Guard the controller delegate's free-form `model` override: the plain path
                                // sends `defaultOptions.ModelId` straight to the controller's transport, so an
                                // invented/mis-filled id (e.g. "gpt-5", or the subagent_type "general-purpose")
                                // hard-fails with a provider BadRequest and burns a spawn + retries. Validate it
                                // against the discovered Copilot catalog (drop unknown → inherit the controller
                                // model) and list the valid ids in the tool descriptor so the controller stops
                                // inventing them.
                                ModelOverrideValidator = sp.GetRequiredService<SubAgentModelResolver>().IsKnownModel,
                                AvailableModelIds = sp.GetRequiredService<SubAgentModelResolver>().AvailableModelIds,
                            };

                            // Persist nested delegate transcripts (subagent-{agentId}) to the shared store so a
                            // nested workflow tab survives a page reload (live streaming works regardless).
                            return ApplyDefaultSubAgentStore(outputTokenPolicy.ApplyDelegated(opts), conversationStore);
                        }

                        var controllerSubAgentOptions = BuildControllerOptions(normalizedProviderId);

                        var workflowManager = new WorkflowManager(
                            controllerAgentFactory: subAgentFactory,
                            controllerSubAgentOptions: controllerSubAgentOptions,
                            completionNotifier: async (notify, notifyCt) =>
                            {
                                // Late-bound to `agent` (assigned just below). Re-injects the async workflow's
                                // completion as a NotifyMessage into the conversation. WorkflowManager wraps
                                // this call in its own try/catch, so a SendAsync on an already-disposed loop
                                // (conversation torn down before the workflow finished) is tolerated — logged,
                                // never fatal.
                                var conversation = agent;
                                if (conversation is not null)
                                {
                                    // The delivery itself lives in WorkflowCompletionNotifier rather than
                                    // here (#418). It is the accept path least likely to be looked for — a
                                    // workflow finishing long after the turn that started it, onto an idle
                                    // conversation — and a lambda in the composition root is a path no test
                                    // can reach. The pool is resolved lazily rather than captured: this
                                    // delegate is built INSIDE the pool's own agent factory, so a direct
                                    // dependency would close a DI construction cycle (same reason, and same
                                    // shape, as the transcript mirror's pool lookup above). It only runs once
                                    // a workflow has completed, long after both singletons exist.
                                    await WorkflowCompletionNotifier.DeliverAsync(
                                        sp.GetRequiredService<MultiTurnAgentPool>(),
                                        threadId,
                                        conversation,
                                        notify,
                                        notifyCt
                                    );
                                }
                            },
                            maxConcurrentWorkflows: maxConcurrentWorkflows,
                            controllerDefaultOptions: outputTokenPolicy.ApplyDelegated(
                                new GenerateReplyOptions
                                {
                                    ModelId = controllerModelId,
                                    // The controller loop inherits the parent's reasoning (Option A: fixed High
                                    // floor), shaped for its OWN model so the orchestrator thinks instead of
                                    // running un-nudged. A per-run preferred-model override reshapes this in
                                    // WorkflowManager.StartAsync via the profile's ControllerReasoningExtraProperties.
                                    ExtraProperties = BuildControllerReasoningExtraProperties(
                                        providerRegistry,
                                        controllerModelId,
                                        normalizedProviderId
                                    ),
                                }
                            ),
                            logger: loggerFactory.CreateLogger<WorkflowManager>(),
                            // Fold a StartWorkflowAgent run's controller + task usage into THIS conversation's
                            // total. Late-bound because the WorkflowManager is created before the root `agent`
                            // loop (whose UsageSink is the conversation's ledger) exists.
                            rootUsageSink: () =>
                            {
                                var conversation = agent;
                                return conversation?.UsageSink;
                            },
                            // Transparency (Rules 1 & 2): a run's delegate sub-agents inherit THIS conversation's
                            // tools — the launching conversation is the first non-WorkflowAgent ancestor, and its
                            // SubAgentManager snapshot is already the sandbox tools MINUS the workflow/launch
                            // tools. Late-bound for the same reason as rootUsageSink.
                            inheritedToolSnapshot: () => agent?.SubAgentManager?.GetInheritableToolSnapshot(),
                            // Persist the controller loop's OWN conversation (the workflow agent's orchestration
                            // turns) to the shared store under the workflow-{id} thread so the ⚙ workflow tab is
                            // viewable after the run completes. Non-owning so controller teardown never disposes
                            // the shared store.
                            controllerConversationStore: new NonOwningConversationStore(conversationStore),
                            // A StartWorkflowAgent run may pass a preferred provider; build its controller agent
                            // AND delegate templates on that provider. Validation happens on the tool (below);
                            // this factory trusts the id. Must be agentFactory-buildable (openai/anthropic/test/
                            // test-anthropic/discovered Copilot) — CLI providers throw ProviderUnavailableException,
                            // surfaced as invalid_provider by the validator.
                            controllerProfileByProvider: providerId => new WorkflowControllerProfile(
                                () => agentFactory(providerId),
                                BuildControllerOptions(providerId),
                                // Shape the parent's inherited thinking (Option A: High floor) for THIS run's
                                // provider/model so a preferred-provider controller reasons on the correct
                                // transport. For Copilot, providerId == model id; non-Copilot falls back to the
                                // provider-id reasoning mapping.
                                BuildControllerReasoningExtraProperties(providerRegistry, providerId, providerId),
                                // Provider switch must also replace the launching provider's default model.
                                // For discovered Copilot providers the provider id is the raw model id; for
                                // family providers this is the same id the host's agent factory accepts.
                                outputTokenPolicy.ApplyDelegated(new GenerateReplyOptions { ModelId = providerId })
                            ),
                            // Scope the controller's persistence thread to THIS conversation so a human-chosen
                            // (non-unique) workflowId can never map two different conversations onto the same
                            // shared-store thread and inherit each other's controller history. The conversation
                            // id is already unique/time-based, so the scoped thread is deterministic and resume
                            // reconstructs it. Late-bound for the same reason as rootUsageSink (the manager is
                            // built before the root `agent` loop exists).
                            launchConversationId: () => agent?.ThreadId,
                            lifecycleServices: lifecycleServices
                        );

                        // Narrowed to the names the mode selected, so a mode that asks for
                        // StartWorkflowAgent alone does not also receive the three status tools.
                        _ = filteredRegistry.AddProvider(
                            ScopeWorkflowProvider(
                                new StartWorkflowToolProvider(
                                    workflowManager,
                                    validatePreferredProvider: p =>
                                        !providerRegistry.IsKnown(p) ? $"Unknown provider '{p}'."
                                        : !providerRegistry.IsAvailable(p) ? $"Provider '{p}' is not available."
                                        : null,
                                    // Admits the run's controller as a hierarchy node under the LAUNCHING
                                    // agent. Passed as a thunk (not a value) purely for symmetry with the
                                    // other late-bound launch inputs above; the handle is already built.
                                    callerCollaboration: () => rootCollaboration
                                ),
                                caps
                            )
                        );
                        ownedResources.Add(workflowManager);

                        // Publish this conversation's WorkflowManager so /subagents + the sub-agent WebSocket
                        // can surface its runs as tabs. Safe to leave a stale entry: WorkflowManager.DisposeAsync
                        // clears its runs, so ListRuns/TryGetRunLoop return empty/false after teardown, and the
                        // entry is overwritten if the conversation's agent is recreated.
                        sp.GetRequiredService<WorkflowRunRegistry>().Register(threadId, workflowManager);

                        // Keep the launch tools (and, for Workflow Author mode, the direct authoring/state
                        // tools too) out of sub-agent inheritance so a spawned sub-agent can't launch a
                        // nested workflow or mutate the parent's runtime out from under it.
                        if (subAgentOptions is not null)
                        {
                            subAgentOptions = AddWorkflowNonInheritedTools(subAgentOptions);
                        }
                    }

                    // Let THIS conversation's agent read the transcript of an agent it is above (#244) —
                    // the tool counterpart of the /agents/{id}/transcript route, resolving access through
                    // the same AgentHierarchyService so the two cannot disagree. Every spawned agent gets
                    // its own reader-bound instance too, so an ancestor deeper than the root can exercise
                    // the same visibility over the children IT spawned.
                    if (rootCollaboration is not null)
                    {
                        subAgentOptions = RegisterAgentTranscriptTool(
                            filteredRegistry,
                            subAgentOptions,
                            new AgentHierarchyService(
                                sp.GetRequiredService<MultiTurnAgentPool>(),
                                sp.GetRequiredService<WorkflowRunRegistry>(),
                                conversationStore,
                                sp.GetRequiredService<ILogger<AgentHierarchyService>>(),
                                sp.GetRequiredService<SubAgentScanCoverageCache>()
                            ),
                            threadId,
                            rootCollaboration.AgentId
                        );
                    }

                    // Persist spawned sub-agent transcripts (keyed per subagent-{agentId} thread) to the
                    // sample's shared conversation store so a focused child can be replayed via the
                    // existing conversation-messages endpoint. Only fills the fallback when unset, so a
                    // template-specified store still wins.
                    if (subAgentOptions is not null)
                    {
                        // stampProvenance: the child's parent thread and roster snapshot are resolved by
                        // the SPAWNING manager (#275), not captured here at the root — so a grandchild is
                        // attributed to its real parent instead of this root conversation.
                        subAgentOptions = ApplyDefaultSubAgentStore(
                            subAgentOptions,
                            conversationStore,
                            stampProvenance: true
                        );
                    }

                    agent = new MultiTurnAgentLoop(
                        providerAgent,
                        filteredRegistry,
                        threadId,
                        // The caller's own instructions (the code-review daemon's methodology, output
                        // contract and sub-agent-dispatch protocol), recorded at provision and appended
                        // LAST. Composed HERE, at the point of use, rather than where the workspace suffix
                        // is built: the degraded-sandbox branch above rebuilds effectiveMode from the bare
                        // `mode`, so anything folded in earlier is silently dropped on exactly the runs that
                        // are already going wrong.
                        systemPrompt: SystemPromptAugmenter
                            .ComposeAsync(
                                conversationStore,
                                threadId,
                                effectiveMode.SystemPrompt,
                                logger: loggerFactory.CreateLogger("LmStreaming.Sample.SystemPromptCompose")
                            )
                            .GetAwaiter()
                            .GetResult(),
                        defaultOptions: outputTokenPolicy.ApplyPrimary(
                            new GenerateReplyOptions
                            {
                                ModelId = modelId,
                                BuiltInTools = filteredBuiltInTools,
                                RequestResponseDumpFileName = requestResponseDumpFileName,
                                PromptCaching = PromptCachingMode.Auto,
                                ExtraProperties = extraProperties,
                            },
                            useDelegatedFallback: normalizedProviderId is "openai"
                        ),
                        // LmStreaming.Sample allows longer agentic runs than the library's 50-turn
                        // default: workspace/tool-heavy conversations routinely need more turns before
                        // the run hits its cap.
                        maxTurnsPerRun: 150,
                        outputChannelCapacity: outputChannelCapacity,
                        store: conversationStore,
                        logger: loggerFactory.CreateLogger<MultiTurnAgentLoop>(),
                        subAgentOptions: subAgentOptions,
                        subAgentTemplateSource: sharedSubAgentSource,
                        loggerFactory: loggerFactory,
                        persistRunLedger: true,
                        // Estimated public cost per model for conversation-wide usage accounting (#196).
                        // Null-resolving for models without a configured rate (cost shows "unavailable").
                        pricingResolver: pricingResolver,
                        // Enable the Wait/CancelWait/ListWaits park-and-wake tools plus the sample
                        // trigger sources (file_tail/schedule/subagent, and sandbox-gated process) for the
                        // MOCK providers only. Real providers are left untouched (triggerOptions: null) so
                        // OpenAI/Anthropic/Copilot behavior stays byte-for-byte unchanged and the sample
                        // exercises deferred-tool park/resume deterministically via the mock
                        // instruction-chain. Broader rollout (enabling triggers for real providers behind a
                        // flag) is tracked in #161.
                        triggerOptions: isTestMode ? triggerOptions : null,
                        lifecycleServices: lifecycleServices,
                        // Null unless the host opted in (#244). Passing it here is the entire opt-in for the
                        // subtree: the loop registers itself as the root node and forwards the same handle to
                        // the SubAgentManager it builds, so every descendant shares one directory and one
                        // ledger. Null keeps the legacy tool schemas and per-manager limits.
                        collaboration: rootCollaboration
                    );

                    // PR 2 of the todo-board plan (#583): every successful task-tool mutation pushes a
                    // live conversation_todo frame to this conversation's subscribers, exactly as the
                    // usage ledger's aggregate-changed callback feeds the usage banner. Wired HERE, after
                    // the loop exists, because the TaskManager was registered on the conversation
                    // registry long before the loop it publishes through could be constructed. The
                    // snapshot is stamped with the ROOT conversation's threadId (and the loop re-stamps
                    // its own regardless): sub-agents mutate this same shared instance, and a frame
                    // carrying a subagent-* id would be silently dropped by the client. Coalescing is
                    // structural — one frame per tool call — so a bulk-initialize of 30 tasks is one
                    // frame, not 30, with no timer, matching the usage push's no-timer pattern.
                    // Durability rides the same hook (#586 review F-005): the pool's read-path
                    // write-through only fires when someone ASKS for the board, so a board mutated and
                    // then evicted/swapped would be lost without a change-driven save. The writer
                    // coalesces bursts (capture-at-write-time, no timer, same engine as the usage
                    // ledger's writer) and, because it sits in ownedResources, the pool entry's
                    // teardown — eviction, provider/mode swap, shutdown — flushes the last change
                    // before the entry disappears. It never persists an empty board and never mints a
                    // metadata row for a thread that has none.
                    var todoBoardWriter = new TodoBoardPersistenceWriter(
                        conversationStore,
                        threadId,
                        () => taskManager.GetTodoBoardSnapshot(threadId),
                        loggerFactory.CreateLogger<TodoBoardPersistenceWriter>()
                    );
                    ownedResources.Add(todoBoardWriter);

                    var todoPublisher = agent;
                    // The capture is passed as a DELEGATE and runs inside PublishTodoBoardFrame's
                    // guard: #587 made GetTodoBoardSnapshot deliberately partial (an unmapped status
                    // member throws), and a capture evaluated here would blow past the publish guard
                    // into the task tool's last-resort catch, taking Schedule() down with it — exactly
                    // the silent failure #587 changed the code to prevent. The logger gives that
                    // last-resort catch a voice for whatever else a subscriber might throw.
                    taskManager.OnChangedLogger = loggerFactory.CreateLogger<TaskManager>();
                    taskManager.OnChanged += () =>
                    {
                        todoPublisher.PublishTodoBoardFrame(() => taskManager.GetTodoBoardSnapshot(threadId));
                        todoBoardWriter.Schedule();
                    };

                    // PR 6 of the todo-board plan (#583): the board talks back. Assignment notices
                    // (N1, on by default) and budgeted stalled-agent nudges (N2-N4, default OFF) ride
                    // the SAME OnChanged multicast the frame publisher and the durable writer use —
                    // the F-007 slot fix is what makes a third subscriber possible at all. The service
                    // is constructed AFTER hydration on purpose: whatever FromSnapshot restored is its
                    // baseline, so a recreate/restart cannot re-notify every pre-existing assignee.
                    var todoNudgeOptions = TodoNudgeOptions.FromConfiguration(builder.Configuration);
                    if (todoNudgeOptions.AnyNudgeEnabled)
                    {
                        var nudgeAgent = agent;
                        var nudgeService = new TodoNudgeService(
                            todoNudgeOptions,
                            taskManager.GetTasks,
                            // A name that resolves to a live sub-agent is nudged there; anything else
                            // would land in the root conversation and is gated on the explicit opt-in.
                            name =>
                                nudgeAgent.SubAgentManager is { } manager && manager.TryGetAgent(name, out _)
                                    ? TodoNudgeTargetKind.SubAgent
                                    : TodoNudgeTargetKind.RootConversation,
                            async (name, message, ct) =>
                            {
                                var target =
                                    nudgeAgent.SubAgentManager is { } manager
                                    && manager.TryGetAgent(name, out var subAgent)
                                    && subAgent is not null
                                        ? subAgent
                                        : nudgeAgent;
                                return await target.TrySendAsync([message], ct: ct) is not null;
                            },
                            TimeProvider.System,
                            loggerFactory.CreateLogger<TodoNudgeService>()
                        );
                        taskManager.OnChanged += nudgeService.OnBoardChangedHook;

                        // The stall tiers need run boundaries, which only the pump observes — so it
                        // exists only when a stall tier is on (shipped default: it is not built).
                        if (todoNudgeOptions.AnyStallNudgeEnabled)
                        {
                            ownedResources.Add(
                                new TodoNudgeEventPump(
                                    nudgeAgent,
                                    nudgeService,
                                    agentId =>
                                    {
                                        var snapshot = nudgeAgent
                                            .SubAgentManager?.ListAgents()
                                            .FirstOrDefault(s =>
                                                string.Equals(s.AgentId, agentId, StringComparison.Ordinal)
                                            );
                                        return snapshot is null
                                            ? null
                                            : new TodoNudgeSubAgentRun(
                                                snapshot.Name ?? snapshot.AgentId,
                                                Errored: snapshot.Status == SubAgentStatus.Error,
                                                Cancelled: snapshot.Status == SubAgentStatus.Stopped
                                            );
                                    },
                                    loggerFactory.CreateLogger<TodoNudgeEventPump>()
                                )
                            );
                        }
                    }

                    return new MultiTurnAgentPool.AgentCreationResult(
                        agent,
                        ownedResources.Count == 0 ? null : ownedResources
                    )
                    {
                        StagedBinding = stagedBinding,
                        // The same instance the per-conversation registry's task tools close over, and
                        // the same one every sub-agent inherits through the parent handler map — one
                        // board per conversation, attributed later (PR 4) rather than split per agent.
                        TodoBoard = taskManager,
                    };
                }
                catch
                {
                    // Dispose owned resources (MCP clients) if agent construction fails
                    DisposeOwnedResources(ownedResources);

                    throw;
                }
            }),
            providerRegistry: providerRegistry,
            conversationStore: conversationStore,
            logger: loggerFactory.CreateLogger<MultiTurnAgentPool>(),
            // The registry is the binding sink: the pool publishes/clears each conversation's
            // sandbox-established binding through it as part of the agent-entry commit/removal (WI #195).
            bindingSink: sandboxRegistryForCleanup,
            liveSessionResolver: (binding, ct) =>
                sandboxRegistryForCleanup.GetOrCreateLiveSessionAsync(binding.WorkspaceRef, ct, binding.Credential),
            lifecycleServices: hostLifecycleServices
        );

        // When a thread is fully removed (NOT recreated for a mode-switch — that preserves the
        // same threadId), drop its session→thread membership so the context-discovery injector
        // stops trying to enqueue messages into the disposed agent. The registry can't observe
        // sessionId from the threadId alone, so we walk the small per-session sets.
        pool.ThreadRemoved += threadId =>
        {
            // Best-effort: the registry's UnregisterThread is itself best-effort + idempotent,
            // and a session id we don't know about is a no-op. We don't have the sessionId in
            // hand, so the cleanest contract is to ask the registry to scrub.
            sandboxRegistryForCleanup.UnregisterThreadFromAllSessions(threadId);
            workflowRunRegistry.Remove(threadId);
            // Drop the remembered descendant graph too (#251): the pool is not a dependency of the
            // scanner (that would be a construction cycle), so invalidation is wired here instead.
            descendantScanner.Forget(threadId);
            // Stop mirroring it as well (#251). This drops the in-memory writer and its subscription
            // only — the transcript already in the workspace is RETAINED on purpose, since outliving
            // the conversation is the whole point of writing it there.
            transcriptMirror.Evict(threadId);
        };

        return pool;
    });

    // Workspace plugin-selection migration (prepare-then-replace). Two narrow registrations:
    //
    // 1. The pool is the only component that knows whether a thread has a run in progress, but it is
    //    sealed and enormous, so the migration depends on the one-method IAgentRunActivityProbe it
    //    actually needs — same alias pattern as IWorkspaceFileBrowser above, and the reason the
    //    migration's tests can substitute an activity fake without a real agent pool.
    // 2. The service itself is built by hand rather than by constructor injection because its two
    //    trailing timeout parameters are optional; the built-in container does not honour default
    //    parameter values and would reject the constructor outright.
    _ = builder.Services.AddSingleton<IAgentRunActivityProbe>(sp => sp.GetRequiredService<MultiTurnAgentPool>());
    _ = builder.Services.AddSingleton<IWorkspacePluginSelectionService>(sp => new WorkspacePluginSelectionService(
        sp.GetRequiredService<IWorkspaceStore>(),
        sp.GetRequiredService<WorkspaceCatalogCompatibilityService>(),
        sp.GetRequiredService<SandboxSessionRegistry>(),
        sp.GetRequiredService<IAgentRunActivityProbe>(),
        sandboxOptions,
        // Named so the timing parameters between here and the logger keep their defaults. The logger
        // is the only channel for this service's post-commit residuals — a retirement grace that
        // expired with a run still live, and a reconcile pass that could not finish — none of which
        // fail the request, so without it they would be invisible in production.
        logger: sp.GetRequiredService<ILogger<WorkspacePluginSelectionService>>()
    ));

    // Register the ChatWebSocketManager and the live-connection registry that lets backend
    // services (e.g. deferred auth) push out-of-band frames to connected chat clients.
    _ = builder.Services.AddSingleton<WebSocketConnectionRegistry>();
    _ = builder.Services.AddSingleton<ChatWebSocketManager>();

    var app = builder.Build();

    // Log startup information
    var logger = app.Services.GetRequiredService<ILogger<Program>>();
    logger.LogInformation(
        "Application started. Environment: {Environment}, WebSocket path: /ws",
        app.Environment.EnvironmentName
    );

    // Use Serilog request logging for HTTP requests
    _ = app.UseSerilogRequestLogging(options =>
        options.EnrichDiagnosticContext = (diagnosticContext, httpContext) =>
        {
            diagnosticContext.Set("RequestHost", httpContext.Request.Host.Value ?? string.Empty);
            diagnosticContext.Set("RequestScheme", httpContext.Request.Scheme ?? string.Empty);
            diagnosticContext.Set("UserAgent", httpContext.Request.Headers.UserAgent.ToString() ?? string.Empty);
        }
    );

    // Enable Vite dev server in development
    if (app.Environment.IsDevelopment())
    {
        _ = app.UseViteDevelopmentServer(true);
    }

    // Serve static files (including Vite build output)
    _ = app.UseStaticFiles();

    // CORS BEFORE identity (#346), not with the rest of the LmStreaming middleware below. A CORS
    // preflight is an OPTIONS request with no Authorization header, so an identity middleware in
    // front of it answers 401 and the browser never sends the real request; and a 403 refusal
    // written downstream of it would leave without Access-Control-Allow-Origin, unreadable by the
    // cross-origin SPA the stable refusal code exists for. UseLmStreaming below sees this has
    // already run and registers WebSockets only.
    _ = app.UseLmStreamingCors();

    // P1 slice 1 (#301): authentication, authorization, then the middleware that establishes the
    // request's Principal. Placed after UseStaticFiles so the SPA — including the screen that
    // explains a refusal — stays reachable while signed out, and before the API endpoints so every
    // /api route runs with a principal already resolved.
    _ = app.UseSampleIdentity();

    // Use LmStreaming middleware (enables WebSockets; CORS already registered above)
    _ = app.UseLmStreaming();

    // Map custom WebSocket endpoint for chat using ChatWebSocketManager
    _ = app.Map(
        "/ws",
        async (
            HttpContext context,
            ChatWebSocketManager wsManager,
            IChatModeStore modeStore,
            WebSocketConversationGate conversationGate,
            ILogger<Program> wsLogger,
            CancellationToken cancellationToken
        ) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsync("WebSocket connection required", cancellationToken);
                return;
            }

            // Get threadId from query string (optional - absent means "start a new conversation").
            var suppliedThreadId =
                context.Request.Query["threadId"].FirstOrDefault()
                ?? context.Request.Query["connectionId"].FirstOrDefault();

            // Supplied-but-blank is a client error, not an absent id, and is refused here rather than
            // normalised away. ?threadId=%20 used to reach WebSocketConversationGate.AdmitAsync, whose
            // ArgumentException surfaced as a 500 - and did so with Identity:Enforce OFF, where the gate
            // is otherwise a no-op, so a deployment with authorization disabled still had a route that
            // could be made to fault. Minting a GUID for it instead would be worse than the 500: the
            // caller's turns would land in a conversation whose id they never learn. /ws/subagent
            // already answers its own blank ids this way, and one prefix should not have two rules.
            if (suppliedThreadId is not null && string.IsNullOrWhiteSpace(suppliedThreadId))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsync("threadId must not be blank", cancellationToken);
                return;
            }

            var threadId = suppliedThreadId ?? Guid.NewGuid().ToString();

            // Per-conversation authorization (#419), BEFORE the handshake is accepted and before any
            // of the work below - the recording writer creates files, and a refused caller must not
            // be able to make the host do that. Until this existed the route was a login wall: being
            // somebody was enough to attach to, rehydrate, and freeze ANY thread id (#399).
            // Write, not Read: this socket accepts user turns and takes ownership of the pooled agent.
            if (
                !await conversationGate.AdmitAsync(
                    context,
                    threadId,
                    AchieveAi.LmDotnetTools.LmCore.Identity.AccessAction.Write,
                    cancellationToken
                )
            )
            {
                return;
            }

            // Get modeId from query string (optional, defaults to system default)
            var modeId = context.Request.Query["modeId"].FirstOrDefault();
            var mode = !string.IsNullOrEmpty(modeId) ? await modeStore.GetModeAsync(modeId, cancellationToken) : null;

            // Optional per-conversation provider override. Honored only when the thread has
            // not yet locked in a provider (first message). Persisted threads keep their
            // original provider regardless of what the client sends.
            var providerId = context.Request.Query["providerId"].FirstOrDefault();

            // Optional per-conversation workspace override. Honored only when the thread has not
            // yet locked in a workspace (first message); persisted threads keep their original
            // workspace. Null/empty → "default", identical to today.
            var workspaceId = context.Request.Query["workspaceId"].FirstOrDefault();

            // Defensively normalize an unknown workspace id (stale UI, a deleted workspace, or
            // hostile input) to "default". Otherwise the thread would lock to a non-existent
            // workspace and a sandbox session would be cached/persisted under a bogus id while
            // silently resolving to the default directory.
            if (!string.IsNullOrWhiteSpace(workspaceId))
            {
                var workspaceStore = context.RequestServices.GetRequiredService<IWorkspaceStore>();
                if (await workspaceStore.GetAsync(workspaceId, cancellationToken) is null)
                {
                    wsLogger.LogWarning(
                        "Unknown workspace id {WorkspaceId} requested for thread {ThreadId}; falling back to default.",
                        workspaceId,
                        threadId
                    );
                    workspaceId = null;
                }
            }

            var recordEnabled =
                app.Environment.IsDevelopment() && IsRecordingEnabled(context.Request.Query["record"].FirstOrDefault());

            StreamWriter? recordWriter = null;
            string? requestResponseDumpFileName = null;
            if (recordEnabled)
            {
                var recordingsDir = Path.Combine(app.Environment.ContentRootPath, "recordings");
                _ = Directory.CreateDirectory(recordingsDir);
                var sessionBaseName = $"{threadId}_{DateTime.UtcNow:yyyyMMddTHHmmss}";

                var wsFileName = $"{sessionBaseName}.ws.jsonl";
                recordWriter = new StreamWriter(
                    Path.Combine(recordingsDir, wsFileName),
                    false,
                    new UTF8Encoding(false)
                );

                requestResponseDumpFileName = Path.Combine(recordingsDir, $"{sessionBaseName}.llm");

                wsLogger.LogInformation(
                    "Recording enabled for thread {ThreadId}. WS file: {WsFile}, LLM dump base: {DumpBase}",
                    threadId,
                    wsFileName,
                    requestResponseDumpFileName
                );
            }

            // Established by IdentityMiddleware, which now guards this route (#342). Null only when
            // enforcement is off AND no development principal could be built; the pool then behaves
            // exactly as it did before #399.
            var ownerUserId = (
                context.Items[IdentityHttpItems.PrincipalKey] as AchieveAi.LmDotnetTools.LmCore.Identity.Principal
            )?.EffectiveUserId;

            var webSocket = await AcceptNegotiatedWebSocketAsync(context);
            wsLogger.LogInformation(
                "WebSocket connection established for thread {ThreadId} with mode {ModeId}",
                threadId,
                mode?.Id ?? "default"
            );

            try
            {
                await wsManager.HandleConnectionAsync(
                    webSocket,
                    threadId,
                    mode,
                    providerId,
                    requestResponseDumpFileName,
                    recordWriter,
                    cancellationToken,
                    workspaceId,
                    ownerUserId
                );
            }
            finally
            {
                if (recordWriter != null)
                {
                    await recordWriter.DisposeAsync();
                }

                if (webSocket.State == System.Net.WebSockets.WebSocketState.Open)
                {
                    await webSocket.CloseAsync(
                        System.Net.WebSockets.WebSocketCloseStatus.NormalClosure,
                        "Server closing",
                        CancellationToken.None
                    );
                }

                webSocket.Dispose();
                wsLogger.LogInformation("WebSocket connection closed for thread {ThreadId}", threadId);
            }
        }
    );

    // Map a SEPARATE WebSocket endpoint for a FOCUSED sub-agent (WI #194, presentation-only). Kept
    // distinct from "/ws" so the parent handler stays byte-compatible: this route resolves the live
    // child through the parent conversation's SubAgentManager (never the pool, which would wrongly
    // create a top-level agent for a "subagent-{id}" thread) and streams/relays it read-only.
    _ = app.Map(
        "/ws/subagent",
        async (
            HttpContext context,
            ChatWebSocketManager wsManager,
            WebSocketConversationGate conversationGate,
            ILogger<Program> wsLogger,
            CancellationToken cancellationToken
        ) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsync("WebSocket connection required", cancellationToken);
                return;
            }

            var parentThreadId = context.Request.Query["parentThreadId"].FirstOrDefault();
            var agentId = context.Request.Query["agentId"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(parentThreadId) || string.IsNullOrWhiteSpace(agentId))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsync("parentThreadId and agentId are required", cancellationToken);
                return;
            }

            // Per-conversation authorization (#419), BEFORE the handshake is accepted. Two answers,
            // not one: whether the caller may attach to the PARENT at all (refused here), and whether
            // the named child is actually that parent's (withheld from the handler instead, so a
            // child that is not theirs answers identically to a child that does not exist).
            var admission = await conversationGate.AdmitSubAgentAsync(
                context,
                parentThreadId,
                agentId,
                cancellationToken
            );
            if (!admission.Admitted)
            {
                return;
            }

            var webSocket = await AcceptNegotiatedWebSocketAsync(context);
            wsLogger.LogInformation(
                "Sub-agent WebSocket connection established for agent {AgentId} on parent {ParentThreadId}",
                agentId,
                parentThreadId
            );

            try
            {
                await wsManager.HandleSubAgentConnectionAsync(
                    webSocket,
                    parentThreadId,
                    agentId,
                    admission.MayReplayPersistedTranscript,
                    cancellationToken
                );
            }
            finally
            {
                if (webSocket.State == System.Net.WebSockets.WebSocketState.Open)
                {
                    await webSocket.CloseAsync(
                        System.Net.WebSockets.WebSocketCloseStatus.NormalClosure,
                        "Server closing",
                        CancellationToken.None
                    );
                }

                webSocket.Dispose();
                wsLogger.LogInformation("Sub-agent WebSocket connection closed for agent {AgentId}", agentId);
            }
        }
    );

    // Map controllers (conversations, chat-modes, tools, diagnostics)
    _ = app.MapControllers();

    // Fallback for SPA routing.
    // In Development, route through Vite dev server (proxied at /dist/*). The redirect must carry
    // the query string: a deep link like /?threadId=X otherwise lands on /dist/index.html with no
    // query, and the app silently opens the most recent thread instead of the linked one.
    if (app.Environment.IsDevelopment())
    {
        _ = app.MapGet(
            "/",
            (HttpContext context) =>
                Results.Redirect(BuildSpaRedirectTarget(context.Request.QueryString), permanent: false)
        );
    }
    else
    {
        // In non-development environments, serve the built SPA from wwwroot/dist.
        _ = app.MapFallbackToFile("dist/index.html");
    }

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}

public partial class Program
{
    /// <summary>
    ///     Maps a normalized provider id (plus, for discovered Copilot models, its transport) to the
    ///     reasoning/thinking request options that surface a model's reasoning. Anthropic-format
    ///     providers — the direct <c>anthropic</c>/<c>test-anthropic</c> providers and any Copilot model
    ///     whose transport is <see cref="CopilotModelTransport.Anthropic"/> (the Copilot proxy accepts
    ///     the thinking parameter, verified by
    ///     CopilotAnthropicLiveTests.Thinking_param_is_accepted_by_copilot_backend) — get an extended-
    ///     thinking budget; Copilot models on the OpenAI Responses transport get a reasoning-summary
    ///     request (CopilotResponsesLiveTests confirms gpt-5.5 returns reasoning). Other providers get
    ///     none. Without this wiring, Copilot-backed models return no thinking blocks.
    ///     <para>
    ///     Copilot Claude models that advertise <c>adaptive_thinking</c> (opus 4.x, sonnet 4.6+, sonnet 5)
    ///     REJECT the classic <c>thinking.type.enabled</c> budget request with HTTP 400 — they require
    ///     the newer <c>thinking.type.adaptive</c> + <c>output_config.effort</c> API, which this provider
    ///     does not model yet. For those we omit the classic thinking parameter rather than send an
    ///     unsupported one; they still reason, just without an explicit budget request.
    ///     </para>
    /// </summary>
    internal static ImmutableDictionary<string, object?> BuildReasoningExtraProperties(
        string normalizedProviderId,
        CopilotModelTransport? copilotTransport = null,
        bool copilotSupportsAdaptiveThinking = false
    )
    {
        var extraProperties = ImmutableDictionary<string, object?>.Empty;
        if (
            string.Equals(normalizedProviderId, "anthropic", StringComparison.Ordinal)
            || string.Equals(normalizedProviderId, "test-anthropic", StringComparison.Ordinal)
            || copilotTransport == CopilotModelTransport.Anthropic
        )
        {
            // Adaptive-thinking Copilot models reject the classic budget request; skip it for them.
            if (copilotSupportsAdaptiveThinking)
            {
                return extraProperties;
            }

            var budgetTokens = int.TryParse(
                Environment.GetEnvironmentVariable("ANTHROPIC_THINKING_BUDGET"),
                out var parsed
            )
                ? parsed
                : 2048;
            extraProperties = extraProperties.Add("Thinking", new AnthropicThinking(budgetTokens));
        }
        else if (copilotTransport == CopilotModelTransport.Responses)
        {
            extraProperties = extraProperties.Add("Reasoning", new ResponseReasoningOptions { Summary = "auto" });
        }

        return extraProperties;
    }

    /// <summary>
    ///     Builds the reasoning metadata for a StartWorkflowAgent CONTROLLER loop (and its plain-path
    ///     delegates), shaped for the controller's own model so the orchestrator thinks at the parent's
    ///     level instead of running un-nudged (the observed "acts as dumb as it acts" starvation). The
    ///     controller inherits a fixed <paramref name="effort"/> floor (Option A: High when the parent can
    ///     think) rather than a copied dictionary, because the controller may run on a per-run preferred
    ///     model whose transport differs from the conversation's.
    ///     <para>
    ///     Resolution mirrors <see cref="BuildReasoningExtraProperties"/> but keyed off the CONTROLLER model:
    ///     an adaptive Copilot Claude model reasons via <c>output_config.effort</c> (Shape emits it); a
    ///     classic Copilot Claude model advertises no efforts, so Shape is empty and we fall back to the
    ///     classic thinking budget for its transport; a non-Copilot controller model uses the provider-id
    ///     mapping. Returns <see cref="ImmutableDictionary{TKey,TValue}.Empty"/> only when the resolved
    ///     model/provider genuinely carries no reasoning surface.
    ///     </para>
    /// </summary>
    internal static ImmutableDictionary<string, object?> BuildControllerReasoningExtraProperties(
        ProviderRegistry providerRegistry,
        string copilotModelKey,
        string fallbackProviderId,
        ReasoningEffort effort = ReasoningEffort.High
    )
    {
        ArgumentNullException.ThrowIfNull(providerRegistry);

        if (
            !string.IsNullOrWhiteSpace(copilotModelKey)
            && providerRegistry.TryGetCopilotModel(copilotModelKey, out var copilotModel)
        )
        {
            var shaped = CopilotReasoningShaper.Shape(copilotModel, effort);
            if (shaped.Count > 0)
            {
                return shaped;
            }

            // Classic Copilot Claude advertises no efforts (Shape empty); fall back to the classic
            // thinking budget shaped for its transport (adaptive short-circuits to Empty inside).
            return BuildReasoningExtraProperties(
                fallbackProviderId,
                copilotModel.Transport,
                copilotModel.SupportsAdaptiveThinking
            );
        }

        return BuildReasoningExtraProperties(fallbackProviderId);
    }

    private static IStreamingAgent CreateTestAgent(ILoggerFactory loggerFactory, ITestAgentBuilder testAgentBuilder)
    {
        var testHandler = testAgentBuilder.CreateHandler("test", loggerFactory);

        var httpClient = new HttpClient(testHandler) { BaseAddress = new Uri("http://test-mode/v1") };

        var openClient = new OpenClient(
            httpClient,
            "http://test-mode/v1",
            logger: loggerFactory.CreateLogger<OpenClient>()
        );

        return new OpenClientAgent("MockLLM", openClient, loggerFactory.CreateLogger<OpenClientAgent>());
    }

    /// <summary>
    ///     Creates an OpenAI-compatible agent (works with OpenAI, Kimi 2.5 OpenAI mode, etc.).
    ///     Reads OPENAI_API_KEY, OPENAI_BASE_URL from env vars.
    /// </summary>
    private static IStreamingAgent CreateOpenAiAgent(ILoggerFactory loggerFactory)
    {
        var apiKey = EnvironmentHelper.GetApiKeyFromEnv("OPENAI_API_KEY");
        var baseUrl = EnvironmentHelper.GetApiBaseUrlFromEnv(
            "OPENAI_BASE_URL",
            defaultValue: "https://api.openai.com/v1"
        );

        Log.Information("Creating OpenAI agent with base URL: {BaseUrl}", baseUrl);

        var openClient = new OpenClient(apiKey, baseUrl, logger: loggerFactory.CreateLogger<OpenClient>());

        return new OpenClientAgent("OpenAI", openClient, loggerFactory.CreateLogger<OpenClientAgent>());
    }

    /// <summary>
    ///     Creates an Anthropic-compatible agent (works with Anthropic, Kimi 2.5 Anthropic mode, etc.).
    ///     Reads ANTHROPIC_API_KEY, ANTHROPIC_BASE_URL from env vars.
    /// </summary>
    private static IStreamingAgent CreateAnthropicAgent(ILoggerFactory loggerFactory)
    {
        var apiKey = EnvironmentHelper.GetApiKeyFromEnv("ANTHROPIC_API_KEY");
        var baseUrl = EnvironmentHelper.GetApiBaseUrlFromEnv(
            "ANTHROPIC_BASE_URL",
            defaultValue: "https://api.anthropic.com/v1"
        );

        Log.Information("Creating Anthropic agent with base URL: {BaseUrl}", baseUrl);

        var anthropicClient = new AnthropicClient(
            apiKey,
            baseUrl: baseUrl,
            logger: loggerFactory.CreateLogger<AnthropicClient>()
        );

        return new AnthropicAgent("Anthropic", anthropicClient, loggerFactory.CreateLogger<AnthropicAgent>());
    }

    /// <summary>
    ///     Creates an agent for a discovered Anthropic-compatible provider-family model (e.g. DeepSeek),
    ///     using that model's own base URL/API key rather than fixed env vars — see
    ///     <see cref="AnthropicCompatProviders.DiscoverFromEnv"/>.
    /// </summary>
    private static IStreamingAgent CreateAnthropicCompatAgent(AnthropicCompatModel model, ILoggerFactory loggerFactory)
    {
        // BaseUrl is operator-controlled (the family's {KEY}_ANTHROPIC_URL env var) and could carry
        // credentials in its user-info (user:pass@host) or query (?token=...) components. Log ONLY the
        // validated scheme+host+port origin so no secret material is persisted to application logs.
        var baseUrlOrigin = Uri.TryCreate(model.BaseUrl, UriKind.Absolute, out var parsedBaseUrl)
            ? $"{parsedBaseUrl.Scheme}://{parsedBaseUrl.Host}:{parsedBaseUrl.Port}"
            : "(invalid base URL)";
        Log.Information(
            "Creating {FamilyKey} agent with base URL origin: {BaseUrlOrigin}",
            model.FamilyKey,
            baseUrlOrigin
        );

        var anthropicClient = new AnthropicClient(
            model.ApiKey,
            baseUrl: model.BaseUrl,
            logger: loggerFactory.CreateLogger<AnthropicClient>()
        );

        return new AnthropicAgent(model.FamilyKey, anthropicClient, loggerFactory.CreateLogger<AnthropicAgent>());
    }

    // Shared across the GitHub Copilot-backed agents: one token (resolved from the Copilot/gh CLI
    // login) and one client-session id for the process lifetime.
    private static readonly Lazy<ICopilotTokenProvider> s_copilotTokenProvider = new(() =>
        new CliCredentialCopilotTokenProvider()
    );

    private static readonly Lazy<CopilotSessionContext> s_copilotSession = new(() => new CopilotSessionContext());

    /// <summary>
    ///     Discovers the routable Anthropic/OpenAI GitHub Copilot models. Returns an empty list when no
    ///     Copilot token resolves or the <c>/models</c> call fails/times out, so the sample degrades to
    ///     exposing no Copilot models rather than failing. Runs synchronously — it is called from the
    ///     <see cref="ProviderRegistry"/> DI factory (which resolves lazily on the first
    ///     <c>GET /api/providers</c>); ASP.NET Core has no <c>SynchronizationContext</c> so the blocking
    ///     wait cannot deadlock. A short discovery timeout bounds first-request latency if the Copilot
    ///     host hangs, instead of blocking the request thread for the HttpClient default timeout.
    /// </summary>
    private static IReadOnlyList<CopilotModelInfo> DiscoverCopilotModels(ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("LmStreaming.Sample.CopilotModelDiscovery");

        // Gate on a resolvable Copilot token first (same sync check ProviderRegistry uses for its
        // availability flag) so we don't fire an unauthenticated /models request when the developer
        // isn't logged in.
        if (new CliCredentialCopilotTokenProvider().ResolveToken() is null)
        {
            logger.LogInformation("No GitHub Copilot token resolved; skipping Copilot model discovery.");
            return [];
        }

        var client = new CopilotModelsClient(s_copilotTokenProvider.Value, s_copilotSession.Value, logger: logger);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            return client.GetModelsAsync(cts.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // The bounded discovery timeout elapsed — degrade to no Copilot models rather than
            // blocking startup. (The client propagates genuine cancellation; the sample owns the
            // timeout-to-empty fallback.)
            logger.LogWarning("Copilot model discovery timed out; exposing no Copilot models.");
            return [];
        }
    }

    /// <summary>
    ///     Maps a discovered Copilot model's transport to the matching agent factory. Anthropic-shaped
    ///     models route through the Copilot Messages backend; OpenAI-shaped models through the Copilot
    ///     Responses backend.
    /// </summary>
    internal static IStreamingAgent CreateCopilotModelAgent(CopilotModelInfo model, ILoggerFactory loggerFactory)
    {
        return model.Transport switch
        {
            CopilotModelTransport.Anthropic => CreateCopilotAnthropicAgent(model.DisplayName, loggerFactory),
            CopilotModelTransport.Responses => CreateCopilotResponsesAgent(model.DisplayName, loggerFactory),
            _ => throw new ProviderUnavailableException(model.Id, $"unsupported Copilot transport {model.Transport}"),
        };
    }

    /// <summary>
    ///     Creates an Anthropic Messages agent (Sonnet/Haiku) routed through the GitHub Copilot
    ///     backend. The model id is supplied per-thread by <see cref="GetModelIdForProvider"/>.
    /// </summary>
    private static IStreamingAgent CreateCopilotAnthropicAgent(string name, ILoggerFactory loggerFactory)
    {
        Log.Information("Creating Copilot-backed Anthropic agent: {Name}", name);
        return CopilotAnthropicAgentFactory.Create(
            name,
            s_copilotTokenProvider.Value,
            timeout: CopilotResponseTimeout,
            session: s_copilotSession.Value,
            logger: loggerFactory.CreateLogger<AnthropicAgent>()
        );
    }

    /// <summary>
    ///     Time-to-first-response ceiling for Copilot-backed streaming agents. Because the streaming
    ///     clients read with <c>ResponseHeadersRead</c>, this bounds how long a stuck/dead upstream
    ///     connection blocks before it surfaces as a timeout — it does NOT cap a healthy stream's length.
    ///     A tight-but-generous default (120s) keeps a hung backend from freezing a whole run (including a
    ///     blocking sub-agent spawn) for the full 5-minute HTTP default. Override with
    ///     <c>COPILOT_RESPONSE_TIMEOUT_SECONDS</c>.
    /// </summary>
    private static TimeSpan CopilotResponseTimeout =>
        int.TryParse(Environment.GetEnvironmentVariable("COPILOT_RESPONSE_TIMEOUT_SECONDS"), out var seconds)
        && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : TimeSpan.FromSeconds(120);

    /// <summary>
    ///     Creates an OpenAI Responses agent (GPT-5.5 / GPT-5.5 mini) routed through the GitHub Copilot
    ///     backend over SSE (stateless per turn — the multi-turn loop resends full history each turn).
    /// </summary>
    private static IStreamingAgent CreateCopilotResponsesAgent(string name, ILoggerFactory loggerFactory)
    {
        Log.Information("Creating Copilot-backed OpenAI Responses agent: {Name}", name);
        return CopilotResponsesAgentFactory.Create(
            name,
            s_copilotTokenProvider.Value,
            CopilotResponsesTransport.Sse,
            s_copilotSession.Value,
            logger: loggerFactory.CreateLogger<OpenAiResponsesAgent>()
        );
    }

    /// <summary>
    ///     Gets the model ID based on the provider mode and env vars.
    /// </summary>
    /// <summary>
    ///     Creates an Anthropic-format test agent using an <see cref="ITestAgentBuilder"/>-supplied handler.
    ///     This supports server-side tools (web_search, web_fetch, code_execution) and citations.
    /// </summary>
    private static IStreamingAgent CreateAnthropicTestAgent(
        ILoggerFactory loggerFactory,
        ITestAgentBuilder testAgentBuilder
    )
    {
        var testHandler = testAgentBuilder.CreateHandler("test-anthropic", loggerFactory);

        var httpClient = new HttpClient(testHandler) { BaseAddress = new Uri("http://test-mode/v1") };

        var anthropicClient = new AnthropicClient(
            httpClient,
            baseUrl: "http://test-mode/v1",
            logger: loggerFactory.CreateLogger<AnthropicClient>()
        );

        return new AnthropicAgent("MockAnthropic", anthropicClient, loggerFactory.CreateLogger<AnthropicAgent>());
    }

    /// <summary>
    ///     The system prompt a CLI-provider agent actually runs with: the mode prompt this factory was
    ///     handed — already carrying the workspace suffix and discovered CLAUDE.md/AGENTS.md block on the
    ///     sandbox path, which passes the rebuilt <c>effectiveMode</c>, and the bare mode prompt
    ///     elsewhere — plus the caller's provisioned appendix appended LAST. Same composition the generic
    ///     <c>MultiTurnAgentLoop</c> path performs inline.
    ///     <para>
    ///     Applied in EVERY CLI provider factory, not just the one an S2S caller happens to use today.
    ///     The provider is one config line (<c>LmStreamingProviderId</c>), and the appendix is the ONLY
    ///     channel a headless caller has for its methodology and output contract (#528) — a factory that
    ///     silently drops it turns a config edit into a silent regression, and the
    ///     <c>AppendixChars</c> composition log that exists to catch that recurrence would be blind on
    ///     exactly the path that regressed.
    ///     </para>
    ///     <para>
    ///     Sync-over-async by necessity: the agent-factory delegate these run under is synchronous, so
    ///     this follows the <c>GetAwaiter().GetResult()</c> pattern already used throughout it rather
    ///     than introducing a second seam.
    ///     </para>
    /// </summary>
    private static string ComposeCliProviderSystemPrompt(
        IConversationStore conversationStore,
        string threadId,
        string? modeSystemPrompt,
        ILoggerFactory loggerFactory
    ) =>
        SystemPromptAugmenter
            .ComposeAsync(
                conversationStore,
                threadId,
                modeSystemPrompt,
                logger: loggerFactory.CreateLogger("LmStreaming.Sample.SystemPromptCompose")
            )
            .GetAwaiter()
            .GetResult();

    private static CodexAgentLoop CreateCodexAgentLoop(
        string threadId,
        AgentProfile mode,
        FunctionRegistry functionRegistry,
        string? requestResponseDumpFileName,
        IConversationStore conversationStore,
        ILoggerFactory loggerFactory,
        string mcpEndpointUrl,
        string? llmQueryMcpBaseUrl,
        string? llmQueryMcpExamType,
        string? mockBaseUrlOverride = null,
        string? mockApiKeyOverride = null,
        MultiTurnLifecycleServices? lifecycleServices = null
    )
    {
        var enabledTools = mode.EnabledTools;
        var codexOptions = CreateCodexOptions(requestResponseDumpFileName, threadId);
        if (!string.IsNullOrWhiteSpace(mockBaseUrlOverride))
        {
            codexOptions = codexOptions with
            {
                BaseUrl = mockBaseUrlOverride,
                ApiKey = mockApiKeyOverride ?? codexOptions.ApiKey,
            };
        }
        if (enabledTools is { Count: > 0 } && !enabledTools.Contains("web_search", StringComparer.OrdinalIgnoreCase))
        {
            codexOptions = codexOptions with { WebSearchMode = "disabled" };
        }

        var mcpServers = new Dictionary<string, CodexMcpServerConfig>
        {
            ["sample_tools"] = new CodexMcpServerConfig
            {
                Url = mcpEndpointUrl,
                Enabled = true,
                EnabledTools = enabledTools == null ? null : [.. enabledTools],
            },
        };

        // Add LlmQuery book search MCP server if configured (medical knowledge mode)
        if (!string.IsNullOrEmpty(llmQueryMcpBaseUrl))
        {
            var queryParams = BuildLlmQueryParams(threadId, llmQueryMcpExamType ?? "NeetPG");
            mcpServers["books"] = new CodexMcpServerConfig
            {
                Url = $"{llmQueryMcpBaseUrl}/mcp/query?{queryParams}",
                Enabled = true,
            };
        }

        return new CodexAgentLoop(
            codexOptions,
            mcpServers,
            functionRegistry,
            enabledTools,
            threadId,
            systemPrompt: ComposeCliProviderSystemPrompt(conversationStore, threadId, mode.SystemPrompt, loggerFactory),
            defaultOptions: new GenerateReplyOptions
            {
                ModelId = GetModelIdForProvider("codex"),
                RequestResponseDumpFileName = requestResponseDumpFileName,
                PromptCaching = PromptCachingMode.Auto,
            },
            store: conversationStore,
            logger: loggerFactory.CreateLogger<CodexAgentLoop>(),
            loggerFactory: loggerFactory,
            persistRunLedger: true,
            lifecycleServices: lifecycleServices
        );
    }

    private static CodexSdkOptions CreateCodexOptions(string? requestResponseDumpFileName, string threadId)
    {
        var codexCliPath = ResolveCodexCliPath(Environment.GetEnvironmentVariable("CODEX_CLI_PATH"));
        var codexCliMinVersion = Environment.GetEnvironmentVariable("CODEX_CLI_MIN_VERSION") ?? "0.101.0";
        var apiKey = Environment.GetEnvironmentVariable("CODEX_API_KEY");
        var webSearchMode = Environment.GetEnvironmentVariable("CODEX_WEB_SEARCH_MODE") ?? "disabled";
        var sandboxMode = Environment.GetEnvironmentVariable("CODEX_SANDBOX_MODE") ?? "workspace-write";
        var approvalPolicy = Environment.GetEnvironmentVariable("CODEX_APPROVAL_POLICY") ?? "on-request";
        var baseUrl = Environment.GetEnvironmentVariable("CODEX_BASE_URL");
        var model = Environment.GetEnvironmentVariable("CODEX_MODEL") ?? "gpt-5.3-codex";
        var baseInstructions = Environment.GetEnvironmentVariable("CODEX_BASE_INSTRUCTIONS");
        var developerInstructions = Environment.GetEnvironmentVariable("CODEX_DEVELOPER_INSTRUCTIONS");
        var modelInstructionsFile = Environment.GetEnvironmentVariable("CODEX_MODEL_INSTRUCTIONS_FILE");
        var toolBridgeModeRaw = Environment.GetEnvironmentVariable("CODEX_TOOL_BRIDGE_MODE") ?? "hybrid";
        var exposeInternalToolsAsToolMessages =
            !bool.TryParse(
                Environment.GetEnvironmentVariable("CODEX_EXPOSE_INTERNAL_TOOLS_AS_TOOL_MESSAGES"),
                out var parsedExposeInternalToolsAsToolMessages
            ) || parsedExposeInternalToolsAsToolMessages;
        var emitLegacyInternalToolReasoningSummaries =
            bool.TryParse(
                Environment.GetEnvironmentVariable("CODEX_EMIT_LEGACY_INTERNAL_TOOL_REASONING_SUMMARIES"),
                out var parsedEmitLegacyInternalToolReasoningSummaries
            ) && parsedEmitLegacyInternalToolReasoningSummaries;
        var networkEnabled =
            !bool.TryParse(
                Environment.GetEnvironmentVariable("CODEX_NETWORK_ACCESS_ENABLED"),
                out var parsedNetworkEnabled
            ) || parsedNetworkEnabled;
        var skipGitRepoCheck =
            !bool.TryParse(Environment.GetEnvironmentVariable("CODEX_SKIP_GIT_REPO_CHECK"), out var parsedSkipGit)
            || parsedSkipGit;
        var emitSyntheticUpdates =
            bool.TryParse(
                Environment.GetEnvironmentVariable("CODEX_EMIT_SYNTHETIC_MESSAGE_UPDATES"),
                out var parsedEmitSyntheticUpdates
            ) && parsedEmitSyntheticUpdates;
        // Retained as a diagnostic-only compatibility knob; raw provider streaming remains default.
        var syntheticChunkSize = int.TryParse(
            Environment.GetEnvironmentVariable("CODEX_SYNTHETIC_MESSAGE_UPDATE_CHUNK_CHARS"),
            out var parsedChunkSize
        )
            ? parsedChunkSize
            : 28;
        var modelInstructionsThresholdChars = int.TryParse(
            Environment.GetEnvironmentVariable("CODEX_MODEL_INSTRUCTIONS_THRESHOLD_CHARS"),
            out var parsedModelInstructionsThresholdChars
        )
            ? parsedModelInstructionsThresholdChars
            : 8000;
        var appServerStartupTimeoutMs = int.TryParse(
            Environment.GetEnvironmentVariable("CODEX_APP_SERVER_STARTUP_TIMEOUT_MS"),
            out var parsedAppServerStartupTimeoutMs
        )
            ? parsedAppServerStartupTimeoutMs
            : 30000;
        var turnCompletionTimeoutMs = int.TryParse(
            Environment.GetEnvironmentVariable("CODEX_TURN_COMPLETION_TIMEOUT_MS"),
            out var parsedTurnCompletionTimeoutMs
        )
            ? parsedTurnCompletionTimeoutMs
            : 120000;
        var turnInterruptGracePeriodMs = int.TryParse(
            Environment.GetEnvironmentVariable("CODEX_TURN_INTERRUPT_GRACE_PERIOD_MS"),
            out var parsedTurnInterruptGracePeriodMs
        )
            ? parsedTurnInterruptGracePeriodMs
            : 5000;
        var rpcTraceEnabledFromEnv =
            bool.TryParse(Environment.GetEnvironmentVariable("CODEX_RPC_TRACE_ENABLED"), out var parsedRpcTraceEnabled)
            && parsedRpcTraceEnabled;
        var rpcTraceFileFromEnv = Environment.GetEnvironmentVariable("CODEX_RPC_TRACE_FILE");

        var toolBridgeMode = Enum.TryParse<CodexToolBridgeMode>(
            toolBridgeModeRaw,
            ignoreCase: true,
            out var parsedToolBridgeMode
        )
            ? parsedToolBridgeMode
            : CodexToolBridgeMode.Hybrid;

        var sessionId = !string.IsNullOrWhiteSpace(requestResponseDumpFileName)
            ? Path.GetFileName(requestResponseDumpFileName)
            : $"{threadId}-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfff}";
        var traceFilePath =
            !string.IsNullOrWhiteSpace(requestResponseDumpFileName) ? $"{requestResponseDumpFileName}.codex.rpc.jsonl"
            : string.IsNullOrWhiteSpace(rpcTraceFileFromEnv) ? null
            : rpcTraceFileFromEnv;
        var enableRpcTrace = rpcTraceEnabledFromEnv || !string.IsNullOrWhiteSpace(requestResponseDumpFileName);
        if (enableRpcTrace && string.IsNullOrWhiteSpace(traceFilePath))
        {
            var logsDir = Path.Combine(AppContext.BaseDirectory, "logs");
            _ = Directory.CreateDirectory(logsDir);
            traceFilePath = Path.Combine(logsDir, $"codex-rpc-{sessionId}.jsonl");
        }

        return new CodexSdkOptions
        {
            CodexCliPath = codexCliPath,
            CodexCliMinVersion = codexCliMinVersion,
            AppServerStartupTimeoutMs = appServerStartupTimeoutMs,
            TurnCompletionTimeoutMs = turnCompletionTimeoutMs,
            TurnInterruptGracePeriodMs = turnInterruptGracePeriodMs,
            Model = model,
            ApiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey,
            BaseUrl = string.IsNullOrWhiteSpace(baseUrl) ? null : baseUrl,
            WebSearchMode = webSearchMode,
            SandboxMode = sandboxMode,
            ApprovalPolicy = approvalPolicy,
            NetworkAccessEnabled = networkEnabled,
            SkipGitRepoCheck = skipGitRepoCheck,
            BaseInstructions = string.IsNullOrWhiteSpace(baseInstructions) ? null : baseInstructions,
            DeveloperInstructions = string.IsNullOrWhiteSpace(developerInstructions) ? null : developerInstructions,
            ModelInstructionsFile = string.IsNullOrWhiteSpace(modelInstructionsFile) ? null : modelInstructionsFile,
            UseModelInstructionsFileThresholdChars = modelInstructionsThresholdChars,
            ToolBridgeMode = toolBridgeMode,
            ExposeCodexInternalToolsAsToolMessages = exposeInternalToolsAsToolMessages,
            EmitLegacyInternalToolReasoningSummaries = emitLegacyInternalToolReasoningSummaries,
            EmitSyntheticMessageUpdates = emitSyntheticUpdates,
            SyntheticMessageUpdateChunkChars = syntheticChunkSize,
            EnableRpcTrace = enableRpcTrace,
            RpcTraceFilePath = traceFilePath,
#pragma warning disable CS0618 // Trace-only label; explicitly forwarded for RPC dump correlation.
            CodexSessionId = sessionId,
#pragma warning restore CS0618
            ProviderMode = "codex",
            Provider = "codex",
        };
    }

    private static string ResolveCodexCliPath(string? configuredPath)
    {
        return ResolveCodexCliPath(
            configuredPath,
            OperatingSystem.IsWindows(),
            Environment.GetEnvironmentVariable("PATH")
        );
    }

    private static string ResolveCodexCliPath(string? configuredPath, bool isWindows, string? path)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return configuredPath;
        }

        if (!isWindows)
        {
            return "codex";
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return "codex";
        }

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var executableName in new[] { "codex.exe", "codex.cmd" })
            {
                var candidate = Path.Combine(directory, executableName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return "codex";
    }

    private static string GetModelIdForProvider(string providerMode)
    {
        return providerMode.ToLowerInvariant() switch
        {
            "openai" => Environment.GetEnvironmentVariable("OPENAI_MODEL") ?? "gpt-4o",
            "anthropic" => Environment.GetEnvironmentVariable("ANTHROPIC_MODEL") ?? "claude-sonnet-4-20250514",
            "test-anthropic" => "claude-sonnet-4-5-20250929",
            "claude" or "claude-mock" => Environment.GetEnvironmentVariable("CLAUDE_MODEL") ?? "claude-sonnet-4-6",
            "codex" or "codex-mock" => Environment.GetEnvironmentVariable("CODEX_MODEL") ?? "gpt-5.3-codex",
            "copilot" or "copilot-mock" => Environment.GetEnvironmentVariable("COPILOT_MODEL") ?? "claude-sonnet-4.5",
            _ => "test-model",
        };
    }

    /// <summary>
    ///     Gets the built-in (server-side) tools based on the provider mode.
    ///     These are tools that execute on the provider's servers (e.g., Anthropic web_search).
    ///     Enabled for the providers backed by the real Anthropic Messages API — the live
    ///     <c>anthropic</c> provider and the <c>test-anthropic</c> mock.
    ///     NOT enabled for the Copilot-backed Claude models (<c>sonnet</c> / <c>haiku</c>): even
    ///     though they speak the Anthropic Messages format, the GitHub Copilot backend rejects the
    ///     server-side web_search tool with HTTP 400
    ///     (<c>{"error":{"message":"The use of the web search tool is not supported.","code":"unsupported_value"}}</c>),
    ///     which would break every request. Re-enable here if/when Copilot adds support.
    /// </summary>
    private static List<object>? GetBuiltInToolsForProvider(string providerMode)
    {
        return providerMode.ToLowerInvariant() switch
        {
            "anthropic" or "test-anthropic" => [new AnthropicWebSearchTool()],
            _ => null,
        };
    }

    internal static GenerateReplyOptions ApplyPrimaryOutputTokens(
        GenerateReplyOptions options,
        AgentOutputTokenPolicy policy,
        bool useDelegatedFallback = false
    ) => policy.ApplyPrimary(options, useDelegatedFallback);

    internal static GenerateReplyOptions ApplyDelegatedOutputTokens(
        GenerateReplyOptions? options,
        AgentOutputTokenPolicy policy
    ) => policy.ApplyDelegated(options);

    internal static SubAgentOptions ApplyDelegatedOutputTokens(
        SubAgentOptions options,
        AgentOutputTokenPolicy policy
    ) => policy.ApplyDelegated(options);

    /// <summary>
    /// Builds the root collaboration handle for one conversation, or returns null when collaboration
    /// resolves to off for its chat mode.
    /// </summary>
    /// <remarks>
    /// Extracted from the conversation factory so the mode default is not merely DECLARED by
    /// <see cref="ModeCapabilities.Collaboration"/> but demonstrably REACHES
    /// <see cref="AgentCollaborationHostOptions.ResolveForMode"/>: a correct predicate wired to a
    /// hard-coded <c>false</c> would still leave every mode on the legacy surface, and a test of the
    /// predicate alone cannot tell the two apart. The conversation's own threadId is the collaboration
    /// id — deliberately reusing the identity the store already keys on rather than minting a second
    /// one — so a resumed conversation rejoins the same logical collaboration.
    /// <para>
    /// Keyed on the mode's CAPABILITIES rather than its id: a mode asks for the collaboration surface
    /// by selecting one of its tools (<c>subagents:CheckAgents</c> and friends), so a copy of a
    /// collaborating mode collaborates too. The old <c>modeId == "workspace-agent"</c> default gave a
    /// copy the legacy surface no matter what it selected.
    /// </para>
    /// </remarks>
    internal static AgentCollaborationSetup? CreateRootCollaboration(
        AgentCollaborationHostOptions hostOptions,
        ModeCapabilities caps,
        string threadId
    ) =>
        hostOptions.ResolveForMode(defaultEnabled: caps.Collaboration) is { } collabOptions
            ? AgentCollaborationSetup.CreateRoot(
                collabOptions,
                collaborationId: threadId,
                agentId: threadId,
                name: "conversation"
            )
            : null;

    /// <summary>
    /// Attaches one conversation-scoped characteristics factory to every template while preserving
    /// template-specific agents for inherited model routing.
    /// </summary>
    internal static SubAgentOptions ApplyCharacteristicsAgentFactory(
        SubAgentOptions options,
        Func<SubAgentCharacteristics, SubAgentProviderAgent> characteristicsAgentFactory
    )
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(characteristicsAgentFactory);

        return options with
        {
            Templates = options.Templates.ToDictionary(
                entry => entry.Key,
                entry =>
                    entry.Value with
                    {
                        CharacteristicsAgentFactory = characteristics =>
                        {
                            var provider = characteristicsAgentFactory(characteristics);
                            return characteristics.IsModelExplicitlySelected || characteristics.IsModelTierResolved
                                ? provider
                                : provider with
                                {
                                    Agent = entry.Value.AgentFactory(),
                                    OwnsAgent = true,
                                };
                        },
                    },
                StringComparer.Ordinal
            ),
        };
    }

    /// <summary>
    /// Fills <see cref="SubAgentOptions.DefaultConversationStoreFactory"/> with the sample's shared
    /// conversation store when the options don't already specify one, so spawned sub-agents persist
    /// their transcripts (keyed per <c>subagent-{agentId}</c> thread) and can be replayed via the
    /// existing conversation-messages endpoint. This only supplies the FALLBACK: a template that sets
    /// its own <see cref="SubAgentTemplate.ConversationStoreFactory"/> — or options that already carry a
    /// <see cref="SubAgentOptions.DefaultConversationStoreFactory"/> — still wins, so the options are
    /// returned unchanged in that case.
    /// <para>
    /// The shared store is handed to children through a
    /// <see cref="NonOwningConversationStore"/> decorator so a child can
    /// never dispose it: <see cref="SubAgentManager"/> disposes a child store that is
    /// <see cref="IAsyncDisposable"/>, and every child shares this one application-wide store.
    /// </para>
    /// <para>
    /// When <paramref name="stampProvenance"/> is set, the fallback is installed as a
    /// <see cref="SubAgentOptions.ProvenanceAwareConversationStoreFactory"/> instead (#275): the
    /// SPAWNING manager — not this root call — hands the factory the child's ACTUAL parent thread and
    /// a describe over ITS OWN roster, so a grandchild (an agent whose spawning manager's own parent is
    /// itself a sub-agent) is stamped with its real parent and its live snapshot resolves. The previous
    /// shape captured a single (parentThreadId, describeChild) pair here at the root, which mislabelled
    /// every agent below the first level and left its snapshot null.
    /// </para>
    /// </summary>
    public static SubAgentOptions ApplyDefaultSubAgentStore(
        SubAgentOptions options,
        IConversationStore store,
        bool stampProvenance = false
    )
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(store);

        // Only supply the fallback: a store already resolved for a child (either factory) still wins.
        if (
            options.DefaultConversationStoreFactory is not null
            || options.ProvenanceAwareConversationStoreFactory is not null
        )
        {
            return options;
        }

        // Wrap the shared store in a non-owning decorator so a child can NEVER dispose it: SubAgentManager
        // disposes a child store that is IAsyncDisposable during spawn-cleanup/restart/completion/rollback,
        // and every child shares this one application-wide store. The wrapper implements neither
        // IDisposable nor IAsyncDisposable, so those ownership checks all skip it.
        if (!stampProvenance)
        {
            return options with { DefaultConversationStoreFactory = _ => new NonOwningConversationStore(store) };
        }

        // #275: provenance is resolved per SPAWNING manager, never captured once here at the root. The
        // manager supplies the child's real parent thread and a describe over its own live roster, so a
        // grandchild's stamp names its actual parent and its snapshot resolves (activating the terminal /
        // RemovalMarker merge path in SubAgentProvenance.Build for the first time on those agents).
        return options with
        {
            ProvenanceAwareConversationStoreFactory = (childThreadId, parentThreadId, describeChild) =>
                new NonOwningConversationStore(
                    store,
                    provenanceThreadId: string.IsNullOrWhiteSpace(parentThreadId) ? null : childThreadId,
                    provenance: string.IsNullOrWhiteSpace(parentThreadId)
                        ? null
                        : () => SubAgentProvenance.Build(parentThreadId, describeChild(childThreadId))
                ),
        };
    }

    /// <summary>
    /// Registers the <c>GetAgentTranscript</c> tool for <paramref name="readerAgentId"/> and, in the same
    /// step, arranges for every agent BELOW it to get its own instance instead of inheriting this one
    /// (#244).
    /// </summary>
    /// <remarks>
    /// The parts are one operation because doing only the first is a privilege escalation and doing only
    /// the second is a dead tool. <see cref="AgentTranscriptToolProvider"/> is bound to ONE reader, so an
    /// inherited copy would hand every descendant its ancestor's reach over the whole hierarchy — hence
    /// the exclusion from inheritance. But an excluded tool that is registered only on the root leaves
    /// every deeper ancestor unable to read the children it is genuinely above, which is the visibility
    /// the feature exists for. <see cref="SubAgentOptions.ChildToolProviderFactory"/> closes that: each
    /// spawned participant is handed a provider bound to ITSELF, so reach always matches the reader.
    /// Existing exclusions are unioned, never replaced.
    /// </remarks>
    /// <param name="registry">The reader's own function registry.</param>
    /// <param name="subAgentOptions">The reader's sub-agent options, or null when it spawns none.</param>
    /// <param name="hierarchy">The shared hierarchy/authorization service both surfaces resolve through.</param>
    /// <param name="threadId">The conversation whose hierarchy these readers belong to.</param>
    /// <param name="readerAgentId">The collaboration id of the agent owning <paramref name="registry"/>.</param>
    /// <returns>The options with the tool excluded from inheritance and re-bound per child.</returns>
    internal static SubAgentOptions? RegisterAgentTranscriptTool(
        FunctionRegistry registry,
        SubAgentOptions? subAgentOptions,
        AgentHierarchyService hierarchy,
        string threadId,
        string readerAgentId
    )
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(hierarchy);

        _ = registry.AddProvider(new AgentTranscriptToolProvider(hierarchy, threadId, readerAgentId));

        return subAgentOptions is null
            ? null
            : subAgentOptions with
            {
                NonInheritedToolNames =
                [
                    .. subAgentOptions.NonInheritedToolNames ?? [],
                    .. AgentTranscriptToolProvider.ToolNames,
                ],
                ChildToolProviderFactory = childAgentId => new AgentTranscriptToolProvider(
                    hierarchy,
                    threadId,
                    childAgentId
                ),
            };
    }

    internal static SubAgentSessionBinding BindConversationSubAgents(
        SandboxSessionRegistry registry,
        string sessionId,
        string conversationId,
        IReadOnlyDictionary<string, SubAgentTemplate> templates,
        Func<IStreamingAgent> agentFactory,
        Func<SubAgentCharacteristics, SubAgentProviderAgent> characteristicsAgentFactory
    )
    {
        ArgumentNullException.ThrowIfNull(registry);
        return registry.AddOrUpdateSubAgentBinding(
            sessionId,
            conversationId,
            templates,
            agentFactory,
            characteristicsAgentFactory
        );
    }

    private static async Task<SubAgentOptions?> BuildSubAgentOptionsAsync(
        bool isTestMode,
        ITestAgentBuilder testAgentBuilder,
        ILoggerFactory loggerFactory,
        Func<IStreamingAgent> providerAgentFactory,
        Func<SubAgentCharacteristics, SubAgentProviderAgent> characteristicsAgentFactory,
        SandboxSession? sandboxSession,
        WorkspaceSubAgentLoader workspaceLoader,
        MarketplaceSubAgentLoader marketplaceLoader,
        IWorkspaceStore workspaceStore,
        Microsoft.Extensions.Logging.ILogger logger
    )
    {
        // Base catalog: mock providers go through the ITestAgentBuilder seam (built-ins by default,
        // or scripted templates an E2E test injected); real providers start from the shared built-ins.
        var baseOptions = isTestMode
            ? testAgentBuilder.CreateSubAgentOptions(loggerFactory, providerAgentFactory)
            : new SubAgentOptions
            {
                Templates = BuiltInSubAgentTemplates.Create(providerAgentFactory),
                MaxConcurrentSubAgents = BuiltInSubAgentTemplates.DefaultMaxConcurrentSubAgents,
            };

        // No sandbox session (e.g. a non-workspace chat, or an E2E scenario with no gateway) means
        // nothing to discover. Preserve the base catalog and only attach the conversation factory.
        if (baseOptions is null)
        {
            return null;
        }

        if (sandboxSession is null)
        {
            return ApplyCharacteristicsAgentFactory(baseOptions, characteristicsAgentFactory);
        }

        var templates = new Dictionary<string, SubAgentTemplate>(baseOptions.Templates, StringComparer.Ordinal);
        await EnrichWithWorkspaceCatalogAsync(
                templates,
                providerAgentFactory,
                characteristicsAgentFactory,
                sandboxSession,
                workspaceLoader,
                marketplaceLoader,
                workspaceStore,
                logger
            )
            .ConfigureAwait(false);

        return ApplyCharacteristicsAgentFactory(
            baseOptions with
            {
                Templates = templates,
            },
            characteristicsAgentFactory
        );
    }

    /// <summary>
    ///     Layers workspace-discovered and marketplace sub-agents onto a base catalog
    ///     <paramref name="templates"/> in three tiers of decreasing trust/richness, each only
    ///     filling keys the prior tier left open:
    ///     <list type="number">
    ///       <item>Base catalog (built-ins / scripted) — already present, always wins.</item>
    ///       <item>Workspace-discovered files — the gateway found a real agent markdown in the
    ///         workspace; it carries the agent's full instruction body. The base catalog still wins.</item>
    ///       <item>Marketplace catalog — agents the UI's marketplace browser lists but that were never
    ///         materialised as workspace files. Best-effort prompt only, so they FILL GAPS left by the
    ///         tiers above. This is what makes a browsable marketplace agent a spawnable subagent_type.</item>
    ///     </list>
    ///     Every loader is best-effort (logs + returns empty on failure), so none of this can throw or
    ///     abort agent creation.
    /// </summary>
    private static async Task EnrichWithWorkspaceCatalogAsync(
        IDictionary<string, SubAgentTemplate> templates,
        Func<IStreamingAgent> providerAgentFactory,
        Func<SubAgentCharacteristics, SubAgentProviderAgent> characteristicsAgentFactory,
        SandboxSession sandboxSession,
        WorkspaceSubAgentLoader workspaceLoader,
        MarketplaceSubAgentLoader marketplaceLoader,
        IWorkspaceStore workspaceStore,
        Microsoft.Extensions.Logging.ILogger logger
    )
    {
        // Workspace file-discovery and the marketplace catalog are independent gateway round-trips.
        // Fetch them concurrently so the (sync-over-async) blocking window is one round-trip, not two.
        // Both loaders are best-effort (log + return empty on failure), so neither task faults; the
        // dictionary is mutated only AFTER both complete, so the in-order merge stays single-threaded.
        var discoveredTask = workspaceLoader.LoadWithCharacteristicsAsync(
            sandboxSession,
            providerAgentFactory,
            characteristicsAgentFactory
        );
        var marketplaceTask = LoadMarketplaceSubAgentsAsync(
            marketplaceLoader,
            workspaceStore,
            sandboxSession.WorkspaceId,
            providerAgentFactory,
            logger
        );

        _ = await Task.WhenAll(discoveredTask, marketplaceTask).ConfigureAwait(false);

        // Merge in precedence order: built-in (already present) > workspace file > marketplace.
        WorkspaceSubAgentLoader.MergeBuiltInWins(templates, discoveredTask.Result, logger);
        MarketplaceSubAgentLoader.MergeFillGaps(templates, marketplaceTask.Result, logger);
    }

    /// <summary>
    /// Resolves the workspace's enabled marketplaces and loads their catalog agents as one awaitable,
    /// so it can run concurrently with workspace file-discovery in
    /// <see cref="EnrichWithWorkspaceCatalogAsync"/>. Best-effort throughout.
    /// </summary>
    private static async Task<IReadOnlyDictionary<string, SubAgentTemplate>> LoadMarketplaceSubAgentsAsync(
        MarketplaceSubAgentLoader marketplaceLoader,
        IWorkspaceStore workspaceStore,
        string workspaceId,
        Func<IStreamingAgent> providerAgentFactory,
        Microsoft.Extensions.Logging.ILogger logger
    )
    {
        var marketplaces = await ResolveWorkspaceMarketplacesAsync(workspaceStore, workspaceId, logger)
            .ConfigureAwait(false);

        return await marketplaceLoader.LoadAsync(marketplaces, providerAgentFactory).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves the marketplace aliases enabled for <paramref name="workspaceId"/> so the marketplace
    /// sub-agent bridge only exposes agents from marketplaces this workspace actually enabled. Returns
    /// null (gateway default set) when the workspace is unknown or enables none, and never throws —
    /// catalog enrichment is best-effort.
    /// </summary>
    private static async Task<IReadOnlyList<string>?> ResolveWorkspaceMarketplacesAsync(
        IWorkspaceStore workspaceStore,
        string workspaceId,
        Microsoft.Extensions.Logging.ILogger logger
    )
    {
        try
        {
            var workspace = await workspaceStore.GetAsync(workspaceId).ConfigureAwait(false);
            return workspace?.Marketplaces is { Count: > 0 } selected ? selected : null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Failed to resolve marketplaces for workspace {WorkspaceId}; using the gateway default set.",
                workspaceId
            );
            return null;
        }
    }

    /// <summary>
    /// Seeds the workspace's root instruction file (AGENTS.md, else CLAUDE.md) into the system
    /// prompt at agent build, so the model has the workspace's high-priority instructions on turn 1
    /// WITHOUT having to read any file. The content is fetched THROUGH the gateway via the typed
    /// Sandbox SDK (<see cref="SandboxSessionRegistry.ReadWorkspaceFileAsync"/>, which addresses the
    /// file by its workspace-relative path): the local-host backend cannot read the container's
    /// <c>/workspace</c> filesystem, and this path is race-free (it does
    /// not depend on the async discovery webhook, which fires after the system prompt is built).
    /// AGENTS.md takes precedence over CLAUDE.md — the first file that exists is injected and the
    /// search stops. Returns an empty string when neither exists, the session has no host path, or
    /// the read fails; every failure is logged and swallowed — seeding is a best-effort enrichment,
    /// never a precondition for the chat session.
    /// </summary>
    private static string TryBuildRootContextSuffix(
        SandboxSessionRegistry sandboxRegistry,
        SandboxSession sandboxSession,
        ContextDiscoveryFormatter formatter,
        Microsoft.Extensions.Logging.ILogger logger
    )
    {
        const string ContextFileKind = "context_file";

        if (string.IsNullOrWhiteSpace(sandboxSession.HostPath))
        {
            return string.Empty;
        }

        // The workspace mount root inside the sandbox (e.g. "/workspace"). Root instruction files
        // sit directly under it; AGENTS.md wins over CLAUDE.md when both are present.
        var baseDir = sandboxSession.HostPath.TrimEnd('/', '\\');
        string[] candidates = ["AGENTS.md", "CLAUDE.md"];

        foreach (var name in candidates)
        {
            var path = $"{baseDir}/{name}";

            string? content;
            try
            {
                // Bound this sync-over-async gateway read so a slow/unresponsive gateway can't park a
                // thread-pool thread for the HttpClient default (100s) on every agent creation.
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                content = sandboxRegistry
                    .ReadWorkspaceFileAsync(sandboxSession.SessionId, path, cts.Token)
                    .GetAwaiter()
                    .GetResult();
            }
            catch (Exception ex)
            {
                // A thrown read (timeout / transport) means the gateway is unreachable, not that the
                // file is merely absent (a missing file returns null, handled below). Abandon the whole
                // seed instead of spending another bounded wait on the next candidate.
                logger.LogWarning(
                    ex,
                    "Failed to read workspace root context file {Path} for session {SessionId}; gateway unreachable, skipping root context seed.",
                    path,
                    sandboxSession.SessionId
                );
                return string.Empty;
            }

            if (string.IsNullOrWhiteSpace(content))
            {
                continue;
            }

            var block = formatter.BuildSystemPromptBlock(path, content, truncated: false);
            if (string.IsNullOrEmpty(block))
            {
                continue;
            }

            // Mark the seeded file as seen so a same-file delivery on the webhook side (the gateway
            // re-emits the root path right after session creation) is dropped by the injector —
            // otherwise the model would see the file twice on turn 1. Marked under the session-level
            // sentinel (the root seed is not attributed to any sub-agent), matching the injector's
            // fallback-path dedup target.
            _ = sandboxRegistry.TryMarkDiscoverySeen(
                sandboxSession.SessionId,
                SandboxSessionRegistry.SessionDiscoveryTarget,
                ContextFileKind,
                path
            );

            logger.LogInformation(
                "Seeded workspace root context file {Path} ({Length} chars) into the system prompt for session {SessionId}.",
                path,
                content.Length,
                sandboxSession.SessionId
            );

            return "\n\n" + block;
        }

        return string.Empty;
    }

    private static int ResolveCodexMcpPort()
    {
        if (
            int.TryParse(Environment.GetEnvironmentVariable("CODEX_MCP_PORT"), out var port)
            && port > 0
            && port <= 65535
        )
        {
            if (IsPortAvailable(port))
            {
                return port;
            }

            var fallbackPort = FindFreeTcpPort();
            Log.Warning(
                "Configured CODEX_MCP_PORT {ConfiguredPort} is already in use. Falling back to port {FallbackPort}.",
                port,
                fallbackPort
            );
            return fallbackPort;
        }

        const int defaultPort = 39200;
        if (IsPortAvailable(defaultPort))
        {
            return defaultPort;
        }

        var fallback = FindFreeTcpPort();
        Log.Warning(
            "Default CODEX_MCP_PORT {DefaultPort} is already in use. Falling back to port {FallbackPort}.",
            defaultPort,
            fallback
        );
        return fallback;
    }

    private static bool IsPortAvailable(int port)
    {
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static int FindFreeTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var assignedPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return assignedPort;
    }

    /// <summary>
    ///     Decides whether Vite.AspNetCore should spawn/supervise its own "npm run dev" child
    ///     (AutoRun). Defaults to true (unchanged single-process behavior). Set VITE_AUTO_RUN=false
    ///     when an external process is already running (or will run) its own Vite dev server against
    ///     this backend instance, to avoid a double-spawn race on the same dev-server port. Not used
    ///     by publish-launch.ps1 — that script publishes a static build and never starts a Vite dev
    ///     server at all.
    /// </summary>
    private static bool ResolveViteAutoRun() =>
        !string.Equals(
            Environment.GetEnvironmentVariable("VITE_AUTO_RUN"),
            "false",
            StringComparison.OrdinalIgnoreCase
        );

    /// <summary>
    ///     Returns true when recording is explicitly enabled via query string (record=1 or record=true).
    /// </summary>
    internal static bool IsRecordingEnabled(string? recordValue)
    {
        return recordValue is not null
            && (
                string.Equals(recordValue, "1", StringComparison.Ordinal)
                || string.Equals(recordValue, "true", StringComparison.OrdinalIgnoreCase)
            );
    }

    /// <summary>
    ///     Completes a WebSocket handshake, echoing the application subprotocol when the client
    ///     offered it (#342). RFC 6455 lets the server select at most one of the subprotocols the
    ///     client listed, and the credential token is never a candidate:
    ///     <see cref="IdentityMiddleware.PromoteWebSocketCredential" /> has already consumed and
    ///     removed it by the time this runs, so only application subprotocols remain.
    /// </summary>
    private static Task<System.Net.WebSockets.WebSocket> AcceptNegotiatedWebSocketAsync(HttpContext context)
    {
        var subProtocol = IdentityMiddleware.NegotiateWebSocketSubProtocol(context.Request);
        return subProtocol is null
            ? context.WebSockets.AcceptWebSocketAsync()
            : context.WebSockets.AcceptWebSocketAsync(subProtocol);
    }

    private static ClaudeAgentLoop CreateClaudeAgentLoop(
        string threadId,
        AgentProfile mode,
        string? requestResponseDumpFileName,
        IConversationStore conversationStore,
        ILoggerFactory loggerFactory,
        string? llmQueryMcpBaseUrl,
        string? llmQueryMcpExamType,
        string? mockBaseUrlOverride = null,
        string? mockAuthTokenOverride = null,
        MultiTurnLifecycleServices? lifecycleServices = null
    )
    {
        // Build AllowedTools from mode's enabled tools:
        // null = use defaults, empty = no built-in tools (MCP only), non-empty = specific tools
        var allowedTools =
            mode.EnabledTools == null ? "Read,WebSearch,WebFetch"
            : mode.EnabledTools.Count > 0 ? string.Join(",", mode.EnabledTools)
            : string.Empty;

        // claude-agent-sdk CLI v0.1.55 does not recognize --no-checkpoints /
        // --no-session-persistence. Setting DisableCheckpoints / DisableSessionPersistence makes
        // the CLI exit immediately with "unknown option", which surfaces to the chat client as
        // "the agent completes with no assistant content rendered" (issue #29).
        var claudeOptions = new ClaudeAgentSdkOptions
        {
            // Match the generic MultiTurnAgentLoop cap above so the Claude SDK provider path allows
            // the same longer agentic runs in this sample.
            MaxTurnsPerRun = 150,
            AllowedTools = allowedTools,
            BaseUrl = string.IsNullOrWhiteSpace(mockBaseUrlOverride) ? null : mockBaseUrlOverride,
            AuthToken = string.IsNullOrWhiteSpace(mockAuthTokenOverride) ? null : mockAuthTokenOverride,
            DisableExperimentalBetas = !string.IsNullOrWhiteSpace(mockBaseUrlOverride),
        };

        var mcpServers = BuildLlmQueryMcpServers(threadId, llmQueryMcpBaseUrl, llmQueryMcpExamType);

        var modelId = Environment.GetEnvironmentVariable("CLAUDE_MODEL") ?? "claude-sonnet-4-6";

        return new ClaudeAgentLoop(
            claudeOptions,
            mcpServers,
            threadId: threadId,
            systemPrompt: ComposeCliProviderSystemPrompt(conversationStore, threadId, mode.SystemPrompt, loggerFactory),
            defaultOptions: new GenerateReplyOptions
            {
                ModelId = modelId,
                RequestResponseDumpFileName = requestResponseDumpFileName,
                PromptCaching = PromptCachingMode.Auto,
            },
            store: conversationStore,
            logger: loggerFactory.CreateLogger<ClaudeAgentLoop>(),
            loggerFactory: loggerFactory,
            persistRunLedger: true,
            lifecycleServices: lifecycleServices
        );
    }

    private static CopilotAgentLoop CreateCopilotAgentLoop(
        string threadId,
        AgentProfile mode,
        FunctionRegistry functionRegistry,
        string? requestResponseDumpFileName,
        IConversationStore conversationStore,
        ILoggerFactory loggerFactory,
        string? mockBaseUrlOverride = null,
        string? mockApiKeyOverride = null,
        IReadOnlyDictionary<string, McpServerConfig>? extraMcpServers = null,
        string? workingDirectoryOverride = null,
        MultiTurnLifecycleServices? lifecycleServices = null
    )
    {
        var copilotCliPath = Environment.GetEnvironmentVariable("COPILOT_CLI_PATH") ?? "copilot";
        var copilotCliMinVersion = Environment.GetEnvironmentVariable("COPILOT_CLI_MIN_VERSION") ?? "0.0.410";
        var model = Environment.GetEnvironmentVariable("COPILOT_MODEL") ?? "claude-sonnet-4.5";
        var apiKey = Environment.GetEnvironmentVariable("COPILOT_API_KEY");
        var baseUrl = Environment.GetEnvironmentVariable("COPILOT_BASE_URL");
        var workingDirectory = Environment.GetEnvironmentVariable("COPILOT_WORKING_DIRECTORY");
        var rpcTraceFileFromEnv = Environment.GetEnvironmentVariable("COPILOT_RPC_TRACE_FILE");
        var rpcTraceEnabledFromEnv =
            bool.TryParse(
                Environment.GetEnvironmentVariable("COPILOT_RPC_TRACE_ENABLED"),
                out var parsedRpcTraceEnabled
            ) && parsedRpcTraceEnabled;
        var modelAllowlistProbeEnabled =
            !bool.TryParse(
                Environment.GetEnvironmentVariable("COPILOT_MODEL_ALLOWLIST_PROBE_ENABLED"),
                out var parsedModelProbe
            ) || parsedModelProbe;
        var defaultPermissionDecision =
            Environment.GetEnvironmentVariable("COPILOT_DEFAULT_PERMISSION_DECISION") ?? "allow";

        var sessionId = !string.IsNullOrWhiteSpace(requestResponseDumpFileName)
            ? Path.GetFileName(requestResponseDumpFileName)
            : $"{threadId}-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfff}";
        var traceFilePath =
            !string.IsNullOrWhiteSpace(requestResponseDumpFileName) ? $"{requestResponseDumpFileName}.copilot.rpc.jsonl"
            : string.IsNullOrWhiteSpace(rpcTraceFileFromEnv) ? null
            : rpcTraceFileFromEnv;
        var enableRpcTrace = rpcTraceEnabledFromEnv || !string.IsNullOrWhiteSpace(requestResponseDumpFileName);
        if (enableRpcTrace && string.IsNullOrWhiteSpace(traceFilePath))
        {
            var logsDir = Path.Combine(AppContext.BaseDirectory, "logs");
            _ = Directory.CreateDirectory(logsDir);
            traceFilePath = Path.Combine(logsDir, $"copilot-rpc-{sessionId}.jsonl");
        }

        // Per-spawn mock overrides take precedence over the host-process env vars so the
        // copilot-mock provider can target the in-process MockProviderHost without polluting
        // the parent process's COPILOT_BASE_URL.
        var effectiveBaseUrl = string.IsNullOrWhiteSpace(mockBaseUrlOverride) ? baseUrl : mockBaseUrlOverride;
        var effectiveApiKey = string.IsNullOrWhiteSpace(mockApiKeyOverride) ? apiKey : mockApiKeyOverride;
        // The Copilot CLI's model allowlist probe phones home to GitHub before the first turn —
        // the mock host doesn't implement it, so disable the probe whenever a mock URL is set.
        var effectiveModelAllowlistProbeEnabled =
            string.IsNullOrWhiteSpace(mockBaseUrlOverride) && modelAllowlistProbeEnabled;

        // Workspace Agent mode supplies an explicit working directory (the sandbox host path);
        // otherwise fall back to the env-configured value.
        var effectiveWorkingDirectory =
            !string.IsNullOrWhiteSpace(workingDirectoryOverride) ? workingDirectoryOverride
            : string.IsNullOrWhiteSpace(workingDirectory) ? null
            : workingDirectory;

        // Reuse the same allow-list expansion the FunctionRegistry-side wiring uses (Callsites A/B),
        // so the CLI's dynamic tool-bridge policy engine never rejects a tool the registry actually
        // advertises (e.g. Workspace Agent's EnabledBuiltInTools=["web_search"] must expand to
        // include the renamed "WebSearch"/"WebFetch" function tools, not just the literal built-in
        // name).
        var enabledTools = WebToolRegistrationPolicy.ResolveEnabledTools(mode.EnabledTools, mode.EnabledBuiltInTools);

        var copilotOptions = new CopilotSdkOptions
        {
            CopilotCliPath = copilotCliPath,
            CopilotCliMinVersion = copilotCliMinVersion,
            Model = model,
            ApiKey = string.IsNullOrWhiteSpace(effectiveApiKey) ? null : effectiveApiKey,
            BaseUrl = string.IsNullOrWhiteSpace(effectiveBaseUrl) ? null : effectiveBaseUrl,
            WorkingDirectory = effectiveWorkingDirectory,
            McpServers = extraMcpServers ?? ImmutableDictionary<string, McpServerConfig>.Empty,
            EnableRpcTrace = enableRpcTrace,
            RpcTraceFilePath = traceFilePath,
            CopilotSessionId = sessionId,
            ModelAllowlistProbeEnabled = effectiveModelAllowlistProbeEnabled,
            DefaultPermissionDecision = defaultPermissionDecision,
            ToolBridgeMode = CopilotToolBridgeMode.Dynamic,
            Provider = "copilot",
            ProviderMode = "copilot",
        };

        return new CopilotAgentLoop(
            copilotOptions,
            functionRegistry,
            enabledTools,
            threadId,
            systemPrompt: ComposeCliProviderSystemPrompt(conversationStore, threadId, mode.SystemPrompt, loggerFactory),
            defaultOptions: new GenerateReplyOptions
            {
                ModelId = GetModelIdForProvider("copilot"),
                RequestResponseDumpFileName = requestResponseDumpFileName,
                PromptCaching = PromptCachingMode.Auto,
            },
            store: conversationStore,
            logger: loggerFactory.CreateLogger<CopilotAgentLoop>(),
            loggerFactory: loggerFactory,
            persistRunLedger: true,
            lifecycleServices: lifecycleServices
        );
    }

    /// <summary>
    ///     Builds MCP server configuration for the LlmQuery book search endpoint.
    ///     Used by the medical knowledge mode to expose textbook search tools.
    /// </summary>
    private static Dictionary<string, McpServerConfig> BuildLlmQueryMcpServers(
        string conversationId,
        string? baseUrl,
        string? examType
    )
    {
        if (string.IsNullOrEmpty(baseUrl))
        {
            return [];
        }

        var headers = new Dictionary<string, string>
        {
            ["X-Exam-Type"] = examType ?? "NeetPG",
            ["X-Session-Id"] = conversationId,
        };

        return new Dictionary<string, McpServerConfig>
        {
            ["books"] = McpServerConfig.CreateHttp($"{baseUrl}/mcp/query", headers: headers),
        };
    }

    /// <summary>
    ///     Connects to the LlmQuery book search MCP server and adds its tools to the FunctionRegistry.
    ///     Used by Anthropic/OpenAI providers which route tool calls through the middleware pipeline.
    ///     Returns the created McpClient instances for proper disposal by the caller.
    /// </summary>
    private static (FunctionRegistry Registry, List<McpClient> McpClients) ConnectLlmQueryMcpClients(
        FunctionRegistry registry,
        string threadId,
        string baseUrl,
        string? examType,
        ILoggerFactory loggerFactory
    )
    {
        var createdClients = new List<McpClient>();
        var logger = loggerFactory.CreateLogger<Program>();
        try
        {
            var headers = new Dictionary<string, string>
            {
                ["X-Exam-Type"] = examType ?? "NeetPG",
                ["X-Session-Id"] = threadId,
            };

            var booksTransport = new HttpClientTransport(
                new HttpClientTransportOptions
                {
                    Name = "books",
                    Endpoint = new Uri($"{baseUrl}/mcp/query"),
                    AdditionalHeaders = headers,
                }
            );

            // Sync-over-async: acceptable in sample app (no SynchronizationContext)
            var booksClient = McpClient.CreateAsync(booksTransport).GetAwaiter().GetResult();
            createdClients.Add(booksClient);

            var mcpClients = new Dictionary<string, McpClient> { ["books"] = booksClient };

            _ = registry.AddMcpClientsAsync(mcpClients, "LlmQuery").GetAwaiter().GetResult();

            logger.LogInformation("Connected to LlmQuery book search MCP server for thread {ThreadId}", threadId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Failed to connect to LlmQuery MCP server at {BaseUrl} — continuing without MCP tools",
                baseUrl
            );
        }

        return (registry, createdClients);
    }

    /// <summary>
    ///     Stamps the sandbox gateway's per-app auth headers (issue #153 / ADR 0029) onto an MCP
    ///     transport header dictionary. Thin wrapper over <see cref="SandboxCredential.StampHeaders(IDictionary{string,string})"/>
    ///     — the single home for the "stamp id, conditionally stamp key" rule — kept so the two MCP
    ///     dictionary call sites read clearly.
    /// </summary>
    private static void AddSandboxAuthHeaders(IDictionary<string, string> headers, SandboxCredential cred) =>
        cred.StampHeaders(headers);

    /// <summary>
    ///     Scopes a workflow tool provider to the workflow tools the mode selected.
    /// </summary>
    /// <remarks>
    ///     Both workflow families - authoring and launch - are filtered by the SAME list, because the
    ///     Modes editor offers them as one <c>workflow</c> group and a mode's stored selection cannot
    ///     tell them apart. A null allow-list (a <c>workflow:*</c> selection, or a legacy mode that
    ///     predates capability selection) passes the family through untouched.
    /// </remarks>
    internal static IFunctionProvider ScopeWorkflowProvider(IFunctionProvider provider, ModeCapabilities caps) =>
        AllowListedFunctionProvider.Wrap(provider, caps.WorkflowToolAllowList);

    /// <summary>
    ///     Filters the per-conversation function registry (sample demo tools + the shared
    ///     <c>TaskManager</c> todo-board family) down to the tools a mode enables.
    /// </summary>
    /// <remarks>
    ///     Null <paramref name="enabledTools" /> means "everything" (the Default mode's contract); an
    ///     explicit list keeps exactly the named tools. This is the seam that starved Workspace Agent
    ///     of the task family: its <c>enabledTools: []</c> filtered out the todo-board tools the
    ///     multi-agent mode exists to drive, so Prompts.yaml now names them and
    ///     <c>ProgramModeToolNarrowingTests</c> pins the list against the live TaskManager enumeration.
    /// </remarks>
    internal static FunctionRegistry BuildModeFilteredRegistry(
        FunctionRegistry conversationRegistry,
        IReadOnlyList<string>? enabledTools
    )
    {
        var (allContracts, allHandlers) = conversationRegistry.Build();
        var enabledToolSet = enabledTools?.ToHashSet(StringComparer.Ordinal);
        var filteredRegistry = new FunctionRegistry();
        foreach (var contract in allContracts)
        {
            if (
                allHandlers.TryGetValue(contract.Name, out var handler)
                && (enabledToolSet == null || enabledToolSet.Contains(contract.Name))
            )
            {
                _ = filteredRegistry.AddFunction(contract, handler, "SampleTools");
            }
        }

        return filteredRegistry;
    }

    /// <summary>
    ///     Excludes the workflow launch + authoring tool families from sub-agent inheritance, so a
    ///     spawned sub-agent can't launch a nested workflow or mutate the parent's runtime out from
    ///     under it. Unions with any exclusions the host already set rather than replacing them.
    /// </summary>
    /// <remarks>
    ///     ONLY the workflow families belong here. The TaskManager todo-board tools must stay
    ///     inheritable: every child's task tools close over the parent conversation's one
    ///     <c>TaskManager</c>, which is how sub-agents and the parent coordinate on the shared board.
    /// </remarks>
    internal static SubAgentOptions AddWorkflowNonInheritedTools(SubAgentOptions options) =>
        options with
        {
            NonInheritedToolNames =
            [
                .. options.NonInheritedToolNames ?? [],
                .. StartWorkflowToolProvider.ToolNames,
                .. WorkflowToolProvider.AllToolNames,
            ],
        };

    /// <summary>
    ///     Builds the SPA target for the Development-only root redirect, carrying the request's query
    ///     string through so a deep link like <c>/?threadId=X</c> still selects its thread after the
    ///     hop to <c>/dist/index.html</c>.
    /// </summary>
    internal static string BuildSpaRedirectTarget(QueryString query) => $"/dist/index.html{query}";

    /// <summary>
    ///     Narrows a conversation's delegation surface to the sub-agent tools its mode selected.
    /// </summary>
    /// <remarks>
    ///     A null allow-list (a <c>subagents:*</c> selection, or a legacy mode) leaves the whole shape
    ///     intact. This governs what the PARENT is handed, which is a different question from
    ///     <see cref="SubAgentOptions.NonInheritedToolNames" /> - that one governs the children.
    /// </remarks>
    internal static SubAgentOptions? ApplySubAgentToolNarrowing(SubAgentOptions? options, ModeCapabilities caps) =>
        options is not null && caps.SubAgentToolAllowList is { } allowList
            ? options with
            {
                ExposedToolNames = allowList,
            }
            : options;

    /// <summary>
    ///     Builds the system-prompt suffix that tells a sandbox-backed agent where its workspace is and
    ///     which workspace tools it has.
    /// </summary>
    /// <remarks>
    ///     The tool names here are load-bearing, not decoration: a model told it has
    ///     Read/Write/Edit/Glob/Grep will confidently call them, so a mode that selected only
    ///     <c>sandbox:Read</c> must not be handed Workspace Agent's text. Derived from the mode's own
    ///     allow-list so a narrowed copy gets a narrowed promise. The names are the BARE tool names the
    ///     model sees; the <c>sandbox:</c> selection prefix never appears here.
    /// </remarks>
    /// <param name="hostPath">Absolute host path of the mounted workspace directory.</param>
    /// <param name="sandboxToolAllowList">
    ///     The mode's sandbox allow-list, or <c>null</c> when it took the whole gateway surface.
    /// </param>
    internal static string BuildWorkspaceSuffix(string hostPath, IReadOnlySet<string>? sandboxToolAllowList)
    {
        var prefix = "\n\nYour workspace directory is: " + hostPath;

        // Whole-surface modes keep the long-standing wording verbatim; it names the tools the gateway
        // has always provided and is what the Workspace Agent prompts were written against.
        if (sandboxToolAllowList is null)
        {
            return prefix
                + "\nUse this absolute path as the base for the file tools (Read, Write, Edit, Glob, Grep). "
                + "The shell tools (Bash, PowerShell) already start in this directory.";
        }

        if (sandboxToolAllowList.Count == 0)
        {
            return prefix + "\nNo workspace file or shell tools are available in this mode.";
        }

        var names = sandboxToolAllowList.OrderBy(n => n, StringComparer.Ordinal).ToList();
        var toolList =
            names.Count == 1 ? names[0] : string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1];

        return prefix
            + "\nUse this absolute path as the base for the workspace tools available to you: "
            + toolList
            + ". No other file or shell tools exist in this mode - do not attempt to use any.";
    }

    /// <summary>
    ///     Builds a single-entry MCP server configuration for an HTTP endpoint.
    ///     Used by CLI-driven providers (e.g. Copilot) which advertise MCP servers to the CLI
    ///     rather than routing tool calls through the middleware pipeline.
    /// </summary>
    private static Dictionary<string, McpServerConfig> BuildHttpMcpServer(
        string name,
        string endpoint,
        IReadOnlyDictionary<string, string> headers
    )
    {
        return new Dictionary<string, McpServerConfig>
        {
            [name] = McpServerConfig.CreateHttp(endpoint, headers: headers),
        };
    }

    /// <summary>
    ///     Connects to an HTTP MCP server and adds its tools to the FunctionRegistry.
    ///     Used by middleware-pipeline providers (Anthropic/OpenAI) which route tool calls through
    ///     the registry. Returns the created McpClient instances for proper disposal by the caller.
    ///     On failure the warning is logged and an empty list is returned so the agent still runs
    ///     (without the MCP tools), mirroring <see cref="ConnectLlmQueryMcpClients"/>.
    /// </summary>
    private static List<McpClient> ConnectHttpMcpClient(
        FunctionRegistry registry,
        string name,
        string endpoint,
        IReadOnlyDictionary<string, string> headers,
        ILoggerFactory loggerFactory,
        bool omitServerPrefix = false,
        Func<ToolHandler, ToolHandler>? handlerDecorator = null
    )
    {
        var createdClients = new List<McpClient>();
        var logger = loggerFactory.CreateLogger<Program>();
        try
        {
            var transport = new HttpClientTransport(
                new HttpClientTransportOptions
                {
                    Name = name,
                    Endpoint = new Uri(endpoint),
                    // AdditionalHeaders is IDictionary; copy the read-only input into a mutable map.
                    AdditionalHeaders = new Dictionary<string, string>(headers),
                }
            );

            // Sync-over-async: acceptable in sample app (no SynchronizationContext)
            var client = McpClient.CreateAsync(transport).GetAwaiter().GetResult();
            createdClients.Add(client);

            var mcpClients = new Dictionary<string, McpClient> { [name] = client };
            if (handlerDecorator is null)
            {
                _ = registry
                    .AddMcpClientsAsync(mcpClients, name, omitServerPrefix: omitServerPrefix)
                    .GetAwaiter()
                    .GetResult();
            }
            else
            {
                // Register the MCP tools into a scratch registry first so each tool handler can be
                // wrapped (e.g. the sandbox container-health guard) before it reaches the agent. The
                // wrapped tools are then exposed on the target registry as explicit functions; the
                // scratch registry has no other provider for `name`, so there is nothing to conflict
                // with. The McpClient stays owned by the caller (returned for disposal), so the
                // wrapped handlers keep working.
                var scratch = new FunctionRegistry();
                _ = scratch
                    .AddMcpClientsAsync(mcpClients, name, omitServerPrefix: omitServerPrefix)
                    .GetAwaiter()
                    .GetResult();
                var (contracts, handlers) = scratch.Build();
                foreach (var contract in contracts)
                {
                    if (handlers.TryGetValue(contract.Name, out var handler))
                    {
                        _ = registry.AddFunction(contract, handlerDecorator(handler), name);
                    }
                }
            }

            logger.LogInformation("Connected to MCP server '{Name}' at {Endpoint}", name, endpoint);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Failed to connect to MCP server '{Name}' at {Endpoint} — continuing without its tools",
                name,
                endpoint
            );
        }

        return createdClients;
    }

    /// <summary>
    ///     Connects to an HTTP MCP server but only exposes the tools named in <paramref name="toolNames"/>
    ///     on <paramref name="registry"/> — used by Workflow Author mode to give the model a narrow
    ///     Read/Grep/Skill slice of the sandbox instead of its full tool surface. Mirrors
    ///     <see cref="ConnectHttpMcpClient"/>'s <c>handlerDecorator is not null</c> branch (the only one
    ///     that enumerates contracts) plus a name filter; unlike that method this always requires a
    ///     decorator, since every caller of this helper wants the same container-health wrapping
    ///     Workspace Agent mode gets. On failure the warning is logged and an empty list is returned so
    ///     the agent still runs (without the MCP tools), mirroring <see cref="ConnectHttpMcpClient"/>.
    /// </summary>
    private static List<McpClient> ConnectFilteredHttpMcpClient(
        FunctionRegistry registry,
        string name,
        string endpoint,
        IReadOnlyDictionary<string, string> headers,
        ILoggerFactory loggerFactory,
        IReadOnlySet<string> toolNames,
        bool omitServerPrefix,
        Func<ToolHandler, ToolHandler> handlerDecorator
    )
    {
        var createdClients = new List<McpClient>();
        var logger = loggerFactory.CreateLogger<Program>();
        try
        {
            var transport = new HttpClientTransport(
                new HttpClientTransportOptions
                {
                    Name = name,
                    Endpoint = new Uri(endpoint),
                    AdditionalHeaders = new Dictionary<string, string>(headers),
                }
            );

            // Sync-over-async: acceptable in sample app (no SynchronizationContext)
            var client = McpClient.CreateAsync(transport).GetAwaiter().GetResult();
            createdClients.Add(client);

            var mcpClients = new Dictionary<string, McpClient> { [name] = client };
            var scratch = new FunctionRegistry();
            _ = scratch
                .AddMcpClientsAsync(mcpClients, name, omitServerPrefix: omitServerPrefix)
                .GetAwaiter()
                .GetResult();
            var (contracts, handlers) = scratch.Build();
            foreach (var contract in contracts)
            {
                if (toolNames.Contains(contract.Name) && handlers.TryGetValue(contract.Name, out var handler))
                {
                    _ = registry.AddFunction(contract, handlerDecorator(handler), name);
                }
            }

            logger.LogInformation(
                "Connected to MCP server '{Name}' at {Endpoint}, filtered to {ToolNames}",
                name,
                endpoint,
                string.Join(", ", toolNames)
            );
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Failed to connect to MCP server '{Name}' at {Endpoint} — continuing without its tools",
                name,
                endpoint
            );
        }

        return createdClients;
    }

    /// <summary>
    ///     Builds query parameter string for LlmQuery MCP endpoints (used by Codex which doesn't support HTTP headers).
    /// </summary>
    private static string BuildLlmQueryParams(string conversationId, string examType)
    {
        return $"X-Exam-Type={Uri.EscapeDataString(examType)}&X-Session-Id={Uri.EscapeDataString(conversationId)}";
    }

    /// <summary>
    ///     Finds the .env file to load. Checks the <c>LMSTREAMING_ENV_FILE</c> environment variable
    ///     first: when set to a path that exists, that path wins outright (no walk-up). When set but
    ///     the path does not exist, the override is ignored (never trusted blindly) and resolution
    ///     falls through to the same ancestor walk-up as when no override is set at all -- but it
    ///     says so on stderr first, because a silently-ignored override is indistinguishable from a
    ///     working one until a provider call fails with an unrelated-looking auth error.
    /// </summary>
    internal static string? FindEnvFile()
    {
        var overridePath = Environment.GetEnvironmentVariable("LMSTREAMING_ENV_FILE");
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            if (File.Exists(overridePath))
            {
                return overridePath;
            }

            // Console.Error, deliberately not a logger: this runs during host construction, before
            // Serilog is configured, so anything written through ILogger here goes nowhere.
            Console.Error.WriteLine(
                $"[warn] LMSTREAMING_ENV_FILE is set to '{overridePath}', but no file exists there. "
                    + "Ignoring the override and searching ancestor directories for .env / .env.test instead. "
                    + "Provider credentials configured in that file will NOT be loaded."
            );
        }

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var envPath = Path.Combine(dir.FullName, ".env");
            if (File.Exists(envPath))
            {
                return envPath;
            }

            var envTestPath = Path.Combine(dir.FullName, ".env.test");
            if (File.Exists(envTestPath))
            {
                return envTestPath;
            }

            if (
                dir.GetFiles("*.sln").Length > 0
                || dir.GetDirectories(".git").Length > 0
                || File.Exists(Path.Combine(dir.FullName, ".git"))
            )
            {
                break;
            }

            dir = dir.Parent;
        }

        return null;
    }

    /// <summary>
    ///     Builds the sandbox <see cref="WorkspaceRef"/> for a workspace, mapping every persisted
    ///     field the gateway needs at create time — directory, marketplaces and plugin selection.
    ///     <para>
    ///     This exists as ONE function on purpose. The same mapping is needed at two points that are
    ///     far apart in this file: the first create for a new conversation, and the reload callback
    ///     that rebuilds the ref when a session has to be recreated after a gateway 404. While those
    ///     were two independent expressions they drifted — the first-create copy omitted the plugin
    ///     selection entirely, so a fresh conversation opened with the gateway's legacy "load every
    ///     plugin" default and only picked up the workspace's real selection if its session happened
    ///     to be recreated later. Adding a field to <see cref="WorkspaceRef"/> must now touch one
    ///     place, and both paths get it.
    ///     </para>
    ///     <para>
    ///     <paramref name="workspace"/> is nullable because the first-create path resolves an id that
    ///     may have no stored workspace (the implicit "default"). That case yields a bare ref, which
    ///     is exactly the pre-existing behaviour: every optional field falls back to its own default.
    ///     </para>
    /// </summary>
    internal static WorkspaceRef BuildWorkspaceRef(
        string workspaceId,
        LmStreaming.Sample.Models.Workspace? workspace
    ) =>
        new(
            workspaceId,
            workspace?.DirectoryRelPath,
            workspace?.Marketplaces,
            ToSandboxPluginRefs(workspace?.PluginSelection)
        );

    /// <summary>
    ///     Maps the app's persisted <see cref="LmStreaming.Sample.Models.PluginRef"/> list to the
    ///     Sandbox SDK's plugin refs for a sandbox-create request.
    ///     <para>
    ///     The null/empty distinction is load-bearing and must survive this mapping:
    ///     <see langword="null"/> means "no explicit selection" — the gateway applies its legacy
    ///     "load every plugin" default — while an empty list is a deliberate "load none". Collapsing
    ///     null to an empty list here would silently disable every plugin for every workspace that
    ///     has never used the picker.
    ///     </para>
    /// </summary>
    private static IReadOnlyList<AchieveAi.LmDotnetTools.Sandbox.SandboxPluginRef>? ToSandboxPluginRefs(
        IReadOnlyList<LmStreaming.Sample.Models.PluginRef>? selection
    ) =>
        selection is null
            ? null
            : [.. selection.Select(p => new AchieveAi.LmDotnetTools.Sandbox.SandboxPluginRef(p.Marketplace, p.Plugin))];
}
