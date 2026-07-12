using System.Collections.Immutable;
using System.Net;
using System.Net.Sockets;
using System.Text;
using AchieveAi.LmDotnetTools.AnthropicProvider.Agents;
using AchieveAi.LmDotnetTools.AnthropicProvider.Models;
using AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Configuration;
using AchieveAi.LmDotnetTools.GithubCopilotProvider.Agents;
using AchieveAi.LmDotnetTools.GithubCopilotProvider.Auth;
using AchieveAi.LmDotnetTools.GithubCopilotProvider.Models;
using AchieveAi.LmDotnetTools.LmCore.AgentRuntime;
using AchieveAi.LmDotnetTools.OpenAiResponsesProvider.Agents;
using AchieveAi.LmDotnetTools.OpenAiResponsesProvider.Models;
using AchieveAi.LmDotnetTools.CodexSdkProvider.Configuration;
using AchieveAi.LmDotnetTools.CodexSdkProvider.Models;
using AchieveAi.LmDotnetTools.CopilotSdkProvider.Configuration;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmCore.Utils;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence.Sqlite;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using AchieveAi.LmDotnetTools.LmStreaming.AspNetCore.Extensions;
using AchieveAi.LmDotnetTools.LmStreaming.Sample.Triggers;
using AchieveAi.LmDotnetTools.LmWorkflow;
using AchieveAi.LmDotnetTools.LmWorkflow.Tools;
using AchieveAi.LmDotnetTools.LmTestUtils;
using AchieveAi.LmDotnetTools.McpMiddleware.Extensions;
using AchieveAi.LmDotnetTools.McpServer.AspNetCore.Extensions;
using AchieveAi.LmDotnetTools.Misc.Configuration;
using AchieveAi.LmDotnetTools.Misc.Utils;
using AchieveAi.LmDotnetTools.Misc.Web.Jina;
using AchieveAi.LmDotnetTools.OpenAIProvider.Agents;
using AchieveAi.LmDotnetTools.LmAgentInfra;
using AchieveAi.LmDotnetTools.LmAgentInfra.Agents;
using AchieveAi.LmDotnetTools.LmAgentInfra.Auth;
using AchieveAi.LmDotnetTools.LmAgentInfra.Context;
using AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;
using LmStreaming.Sample.Auth;
using LmStreaming.Sample.Controllers;
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
    if (!string.IsNullOrWhiteSpace(s2sInboundSecretEnv)
        && string.IsNullOrWhiteSpace(builder.Configuration[InboundS2SAuthAttribute.SecretConfigKey]))
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
    });

    _ = builder.Services.AddControllers();
    _ = builder.Services.AddEndpointsApiExplorer();

    // Add Vite services for frontend integration. The dev-server port is configurable via
    // VITE_DEV_PORT so an isolated instance can run alongside another without colliding on 5173
    // (the paired vite.config.ts reads the same env var). Defaults to 5173.
    var viteDevPort = ushort.TryParse(Environment.GetEnvironmentVariable("VITE_DEV_PORT"), out var parsedVitePort)
        ? parsedVitePort
        : (ushort)5173;
    _ = builder.Services.AddViteServices(options =>
    {
        options.Base = "/dist/";
        options.Server.AutoRun = true;
        options.Server.PackageDirectory = "ClientApp";
        options.Server.Port = viteDevPort;
    });

    var providerMode = Environment.GetEnvironmentVariable("LM_PROVIDER_MODE") ?? "test";
    var codexMcpPort = ResolveCodexMcpPort();
    Environment.SetEnvironmentVariable("CODEX_MCP_PORT_EFFECTIVE", codexMcpPort.ToString());

    // Provider registry — single source of truth for which providers the client can pick
    // and what the per-process default is. Read once at startup; shared via DI singleton.
    _ = builder.Services.AddSingleton<IFileSystemProbe, FileSystemProbe>();

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
        var copilotModels = DiscoverCopilotModels(sp.GetRequiredService<ILoggerFactory>());
        return new ProviderRegistry(copilotModels, probe, mockHost);
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

    // Every gateway-bound HttpClient gets the per-app bearer handler (ADR 0029) so its REST calls carry
    // X-Sbx-App-Id/X-Sbx-App-Key under an AUTH_ENFORCE gateway. No-op when no AppKey is configured, so an
    // unenforced gateway is unaffected.
    HttpClient GatewayHttpClient(TimeSpan? timeout = null)
    {
        var client = new HttpClient(
            new GatewayAuthHandler(sandboxOptions.AppId, sandboxOptions.AppKey) { InnerHandler = new HttpClientHandler() });
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

    // OAuth auth-provider services (GitHub + Azure DevOps token injection for sandbox egress).
    var authOptions =
        builder.Configuration.GetSection(AuthOptions.SectionName).Get<AuthOptions>() ?? new AuthOptions();
    _ = builder.Services.AddSingleton(authOptions);
    _ = builder.Services.AddSingleton<AuthSharedSecret>();
    var oauthTokenDir = string.IsNullOrWhiteSpace(authOptions.TokenStoreDir)
        ? Path.Combine(AppContext.BaseDirectory, "oauth-tokens")
        : authOptions.TokenStoreDir;
    _ = builder.Services.AddSingleton<IOAuthTokenStore>(sp => new FileOAuthTokenStore(
        oauthTokenDir,
        sp.GetRequiredService<ILogger<FileOAuthTokenStore>>()
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
        sp.GetRequiredService<AuthSharedSecret>()
    ));

    // Workspace sub-agent discovery. The loader asks the gateway what it has discovered in
    // the session's workspace (sub-agent markdown files under .claude/agents/, etc.) and maps
    // them into SubAgentTemplate so they show up as spawnable types in the Agent tool catalog.
    _ = builder.Services.AddSingleton<WorkspaceSubAgentLoader>();

    // Marketplace sub-agent bridge. Maps the agents the UI's marketplace browser lists (the
    // gateway's read-only catalog) into spawnable templates, filling any gap left by workspace
    // file-discovery so a browsable marketplace agent is also a usable Agent tool subagent_type.
    _ = builder.Services.AddSingleton<MarketplaceSubAgentLoader>();

    // Sandbox context-file (CLAUDE.md / AGENTS.md) injection. The formatter owns the
    // <context-discovery> wrapper tag shared by the boot-time system prompt and the mid-session
    // user-turn injection; the injector wires gateway webhook deliveries into every live agent
    // thread bound to the same sandbox session.
    _ = builder.Services.AddSingleton<ContextDiscoveryFormatter>();
    _ = builder.Services.AddSingleton<ContextDiscoveryInjector>();
    // Tracks received discovery webhooks per session for GET /api/diagnostics/context-discovery,
    // so an operator can confirm discoveries are actually arriving (vs. silently lost to an
    // unreachable callback host).
    _ = builder.Services.AddSingleton<ContextDiscoveryDiagnostics>();

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
    _ = builder.Services.AddSingleton<INotifyWaitStore>(_ =>
        new SqliteNotifyWaitStore(new SqliteConnectionFactory(notifyWaitDbPath)));

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

    // Register the FileWorkspaceStore for workspace persistence. The seeded default workspace
    // maps to today's configured sandbox leaf so the "default" workspace mounts the same directory
    // it always did.
    var workspacesPath = Path.Combine(AppContext.BaseDirectory, "workspaces");
    var defaultWorkspaceLeaf = sandboxOptions.ResolveWorkspace().Leaf;
    _ = builder.Services.AddSingleton<IWorkspaceStore>(
        new FileWorkspaceStore(workspacesPath, defaultWorkspaceLeaf)
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

    // Register the provider agent factory (multi-provider support via LM_PROVIDER_MODE env var)
    Log.Information("LM Provider Mode: {ProviderMode}", providerMode);

    // Test-mode DI seam: default implementation is behavior-preserving. E2E tests can
    // replace this via ConfigureTestServices to inject scripted SSE responders and
    // sub-agent templates without touching any real-provider code path.
    builder.Services.TryAddSingleton<ITestAgentBuilder, DefaultTestAgentBuilder>();

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

    // Register the MultiTurnAgentPool with provider- and mode-aware factory
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
        var codexLifetime = sp.GetRequiredService<CodexMcpServerLifetime>();
        var mockHostLifetime = sp.GetRequiredService<MockProviderHostLifetime>();
        var sandboxRegistryForCleanup = sp.GetRequiredService<SandboxSessionRegistry>();

        // Web tools (WebFetch/WebSearch) fallback provider. Built ONCE for the process (this factory
        // runs once for the singleton pool) and shared across conversations — the provider owns a
        // single app-lifetime HttpClient. Invalid configuration degrades gracefully: we log the
        // errors and leave the provider null so the sample still boots without web tools.
        var webToolsOptions = WebToolsOptions.FromEnvironment();
        var webToolsErrors = webToolsOptions.Validate();
        JinaWebProvider? jinaWebProvider = null;
        if (webToolsErrors.Count == 0
            && string.Equals(webToolsOptions.Backend, "jina", StringComparison.OrdinalIgnoreCase))
        {
            jinaWebProvider = new JinaWebProvider(
                webToolsOptions,
                loggerFactory.CreateLogger<JinaWebProvider>()
            );
        }
        else if (webToolsErrors.Count > 0)
        {
            loggerFactory
                .CreateLogger<Program>()
                .LogWarning("Web tools disabled (invalid configuration): {Errors}", string.Join("; ", webToolsErrors));
        }

        var pool = new MultiTurnAgentPool(
            context =>
            {
                var threadId = context.ThreadId;
                var mode = context.Mode;
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

                // Workspace Agent mode is backed by the sandbox MCP gateway. Resolve the sandbox
                // session up front (sync-over-async, consistent with the books wiring) and augment
                // the system prompt with the workspace's absolute host path — the local backend has
                // no '/workspace' mount, so the model must use the absolute path for the file tools.
                var isWorkspaceMode = mode.Id == SystemChatModes.WorkspaceAgentModeId;
                var sandboxRegistry = sp.GetRequiredService<SandboxSessionRegistry>();
                var sandboxLifetime = sp.GetRequiredService<SandboxGatewayLifetime>();
                SandboxSession? sandboxSession = null;
                var effectiveMode = mode;
                if (isWorkspaceMode)
                {
                    // Only the middleware providers (OpenAI/Anthropic/test/...) and Copilot are wired
                    // to route tool calls to the sandbox gateway. Reject the CLI-only providers and
                    // mock variants up front instead of creating an unused sandbox session and an
                    // agent with no sandbox tools.
                    if (
                        normalizedProviderId
                        is "codex"
                            or "claude"
                            or "codex-mock"
                            or "claude-mock"
                            or "copilot-mock"
                    )
                    {
                        throw new ProviderUnavailableException(
                            normalizedProviderId,
                            "Workspace Agent mode supports the OpenAI/Anthropic (and Copilot) providers; this provider is not wired for the sandbox."
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
                    var workspaceRef = new WorkspaceRef(
                        effectiveWorkspaceId,
                        workspace?.DirectoryRelPath,
                        workspace?.Marketplaces);

                    // Use the liveness-checked variant: the gateway evicts idle sessions on its own
                    // schedule, and reusing a cached-but-evicted handle silently strips the session's
                    // marketplace-provided tools (e.g. sandbox-Skill). This recreates the session on a
                    // gateway 404 so the agent always gets the full tool set without a process restart.
                    sandboxSession = sandboxRegistry
                        .GetOrCreateLiveSessionAsync(workspaceRef, credential: callerCredential)
                        .GetAwaiter()
                        .GetResult();
                    var wsSuffix =
                        "\n\nYour workspace directory is: "
                        + sandboxSession.HostPath
                        + "\nUse this absolute path as the base for the file tools (Read, Write, Edit, Glob, Grep). "
                        + "The shell tools (Bash, PowerShell) already start in this directory.";

                    // Seed any context files (CLAUDE.md / AGENTS.md) the gateway has already
                    // discovered into the system prompt. Mid-session deliveries land via the
                    // webhook + injector; this fills the boot-time hole where the gateway has
                    // already scanned the workspace before the first turn is sent.
                    var contextSuffix = TryBuildRootContextSuffix(
                        sandboxRegistry,
                        sandboxSession,
                        sp.GetRequiredService<ContextDiscoveryFormatter>(),
                        loggerFactory.CreateLogger("LmStreaming.Sample.ContextDiscoverySeed"));
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
                            mockApiKeyOverride: "mock-token"
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
                            mockAuthTokenOverride: "mock-token"
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
                                mockApiKeyOverride: "mock-token"
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
                            llmQueryMcpExamType
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
                            llmQueryMcpExamType
                        )
                    );
                }

                if (string.Equals(normalizedProviderId, "copilot", StringComparison.Ordinal))
                {
                    // Sandbox MCP header dict: X-Session-ID plus the app's sandbox auth headers.
                    // The caller's credential (S2S) wins over the process default so an S2S
                    // caller's /mcp tool calls carry its own identity; the interactive UI (null)
                    // falls back to the default (issue #153 M1/M2). Connect-time-frozen for the
                    // pooled agent by design — not re-evaluated per turn.
                    Dictionary<string, string>? sandboxMcpHeaders = null;
                    if (isWorkspaceMode)
                    {
                        sandboxMcpHeaders = new Dictionary<string, string>
                        {
                            ["X-Session-ID"] = sandboxSession!.SessionId,
                        };
                        AddSandboxAuthHeaders(
                            sandboxMcpHeaders,
                            callerCredential ?? sandboxCredential
                        );
                    }

                    return new MultiTurnAgentPool.AgentCreationResult(
                        CreateCopilotAgentLoop(
                            threadId,
                            effectiveMode,
                            functionRegistry,
                            requestResponseDumpFileName,
                            conversationStore,
                            loggerFactory,
                            extraMcpServers: isWorkspaceMode
                                ? BuildHttpMcpServer("sandbox", $"{sandboxLifetime.GatewayBaseUrl}/mcp", sandboxMcpHeaders!)
                                : null,
                            workingDirectoryOverride: isWorkspaceMode ? sandboxSession!.HostPath : null
                        )
                    );
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
                _ = conversationRegistry.AddFunctionsFromObject(new TaskManager(), providerName: "TaskManager");

                // Clone the per-conversation registry per-agent to avoid mutation, filtering by mode
                var (allContracts, allHandlers) = conversationRegistry.Build();
                var enabledToolSet = mode.EnabledTools?.ToHashSet();
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

                // Add LlmQuery book search MCP tools — only for medical knowledge mode
                // Track MCP clients for proper disposal alongside the agent
                List<IAsyncDisposable>? ownedResources = null;
                var workspaceWorkflowEnabled = false;
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
                        ownedResources = [.. mcpClients.Cast<IAsyncDisposable>()];
                    }
                }
                else if (isWorkspaceMode)
                {
                    // Workspace Agent gets its own per-conversation task tracker alongside the
                    // sandbox tools — the same way a coding agent carries a todo list. The mode's
                    // empty enabledTools list filtered the registry copy out above, and the tracker
                    // is independent of the sandbox, so add it here unconditionally; it stays usable
                    // even when the sandbox MCP endpoint is offline (degraded mode below).
                    _ = filteredRegistry.AddFunctionsFromObject(new TaskManager(), providerName: "TaskManager");

                    // Let the controller LLM author and drive an LmWorkflow workflow inside this
                    // same conversation loop (no separate WorkflowSession-owned loop) — the runtime
                    // observes the loop's own message stream (see ownedResources wiring below) to
                    // correlate Agent tool-call spawns back into workflow state.
                    //
                    // Let the Workspace Agent launch LmWorkflow workflows via the StartWorkflow tool
                    // family. Each StartWorkflow spins up an ISOLATED controller loop (its own model +
                    // restricted tool surface); the chat agent never gets direct
                    // SetWorkflow/GetWorkflow access (that #130 wiring is retired here). The tools
                    // themselves are wired below, once the conversation loop exists so an async
                    // workflow's completion notification can reach it.
                    //
                    // On by default, but disable-able per deployment WITHOUT a redeploy via
                    // WORKSPACE_AGENT_LMWORKFLOW_ENABLED=false.
                    workspaceWorkflowEnabled = !string.Equals(
                        Environment.GetEnvironmentVariable("WORKSPACE_AGENT_LMWORKFLOW_ENABLED"),
                        "false",
                        StringComparison.OrdinalIgnoreCase
                    );

                    // Workspace Agent mode: expose the sandbox file/shell tools via the gateway's
                    // MCP endpoint, bound to this agent's sandbox session by the X-Session-ID header
                    // and the app's sandbox auth headers. The caller's credential (S2S) wins over
                    // the process default so an S2S caller's /mcp calls carry its own identity; the
                    // interactive UI (null) falls back to the default (issue #153 M1/M2).
                    // Connect-time-frozen for the pooled agent; not re-evaluated per turn.
                    var sandboxMcpHeaders = new Dictionary<string, string>
                    {
                        ["X-Session-ID"] = sandboxSession!.SessionId,
                    };
                    AddSandboxAuthHeaders(sandboxMcpHeaders, callerCredential ?? sandboxCredential);
                    var sandboxClients = ConnectHttpMcpClient(
                        filteredRegistry,
                        "sandbox",
                        $"{sandboxLifetime.GatewayBaseUrl}/mcp",
                        sandboxMcpHeaders,
                        loggerFactory,
                        // Expose sandbox tools under their natural names (bash, edit, …) rather than
                        // sandbox-bash; the gateway is the sole MCP server here, so no collisions.
                        omitServerPrefix: true,
                        // Collapse the "container has no sandbox user" Docker-exec failure class into a
                        // single actionable message so the model stops retrying it (see SandboxToolHealth).
                        handlerDecorator: SandboxToolHealth.Wrap
                    );
                    if (sandboxClients.Count > 0)
                    {
                        ownedResources = [.. sandboxClients.Cast<IAsyncDisposable>()];
                    }
                    else
                    {
                        // The sandbox MCP endpoint is unreachable. Booting anyway is intentional
                        // (best-effort demo), but the workspace suffix added above claims file/shell
                        // tools that this agent does not have — rebuild the prompt from the original
                        // mode with an honest degraded-mode notice instead, so the model tells the
                        // user rather than hallucinating tool calls.
                        effectiveMode = mode with
                        {
                            SystemPrompt = mode.SystemPrompt
                                + "\n\nIMPORTANT: The sandbox workspace is currently UNAVAILABLE (its MCP endpoint "
                                + "could not be reached), so NO file or shell tools exist in this conversation. "
                                + "Do not claim or attempt to use them. Tell the user the workspace is offline and "
                                + "that restarting the app (or the sandbox gateway) should restore it.",
                        };
                        loggerFactory
                            .CreateLogger<Program>()
                            .LogWarning(
                                "Workspace Agent mode is running WITHOUT sandbox tools for thread {ThreadId}; "
                                    + "the system prompt now reports degraded mode instead of claiming tools",
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
                var isCopilotBackedModel = providerRegistry.TryGetCopilotModel(normalizedProviderId, out var copilotModelInfo);

                var webToolStatuses = WebToolRegistrationPolicy.Apply(
                    filteredRegistry,
                    normalizedProviderId,
                    mode.EnabledTools,
                    jinaWebProvider,
                    webToolsOptions,
                    loggerFactory,
                    isCopilotBackedModel
                );
                if (webToolStatuses.Count > 0)
                {
                    var webToolsLogger = loggerFactory.CreateLogger<Program>();
                    foreach (var status in webToolStatuses)
                    {
                        webToolsLogger.LogInformation("WebTools: {Status}", status);
                    }
                }

                try
                {
                    // Discovered Copilot models use their raw model id verbatim; fixed providers keep
                    // the curated per-provider id map.
                    var modelId = isCopilotBackedModel
                        ? copilotModelInfo.Id
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
                    // template AgentFactory reuses agentFactory(normalizedProviderId) so each spawn
                    // builds a FRESH provider agent on the same backend as the parent.
                    var isTestMode =
                        string.Equals(normalizedProviderId, "test", StringComparison.Ordinal)
                        || string.Equals(normalizedProviderId, "test-anthropic", StringComparison.Ordinal);
                    // Sync-over-async on the agent-creation factory delegate: there is no async
                    // seam exposed by the pool's agent factory contract, and this runs on the
                    // pool-creation path (no ASP.NET SynchronizationContext) so a .Result here
                    // cannot deadlock. The blocking call is HTTP only when a sandbox session is
                    // active; otherwise it completes synchronously.
                    Func<IStreamingAgent> subAgentFactory = () => agentFactory(normalizedProviderId);
                    var subAgentOptions = BuildSubAgentOptionsAsync(
                            isTestMode,
                            sp.GetRequiredService<ITestAgentBuilder>(),
                            loggerFactory,
                            subAgentFactory,
                            sandboxSession,
                            sp.GetRequiredService<WorkspaceSubAgentLoader>(),
                            sp.GetRequiredService<MarketplaceSubAgentLoader>(),
                            sp.GetRequiredService<IWorkspaceStore>(),
                            loggerFactory.CreateLogger("LmStreaming.Sample.SubAgentCatalog"))
                        .GetAwaiter()
                        .GetResult();

                    // When a sandbox session is active, share the catalog with the session
                    // registry so the context-discovery webhook can activate newly discovered
                    // subagents into the same source the loop is reading. Without a session there
                    // is no webhook path, so the loop falls back to wrapping the static templates
                    // in a private source inside its ctor.
                    MutableSubAgentTemplateSource? sharedSubAgentSource = null;
                    if (sandboxSession is not null && subAgentOptions is not null)
                    {
                        var binding = sp.GetRequiredService<SandboxSessionRegistry>()
                            .GetOrAddSubAgentBinding(
                                sandboxSession.SessionId,
                                threadId,
                                subAgentOptions.Templates,
                                subAgentFactory);
                        sharedSubAgentSource = binding.Source;

                        // Register this agent's threadId against the session so the
                        // context-discovery webhook can fan a context_file delivery out to it.
                        // Mode-switch recreations preserve threadId by design (and don't fire
                        // the pool's ThreadRemoved event), so this registration survives them.
                        sandboxRegistry.RegisterThread(sandboxSession.SessionId, threadId);
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
                        subAgentManagerAccessor: () => agent?.SubAgentManager) with
                    {
                        NotifyWaitStore = isTestMode ? notifyWaitStore : null,
                        ThreadId = isTestMode ? threadId : null,
                    };

                    // Wire the StartWorkflow tool family onto the conversation registry (Workspace Agent
                    // mode). Declared before the loop ctor so the launch tools are registered before the
                    // sub-agent snapshot is taken; the completion notifier is late-bound to `agent` (assigned
                    // just below). This replaces #130's direct SetWorkflow/GetWorkflow wiring.
                    if (workspaceWorkflowEnabled)
                    {
                        // Q2: the controller runs on a single, FIXED, pre-configured model — a configured
                        // value if present, else the conversation's own default model. Never the caller's.
                        var configuredControllerModel =
                            Environment.GetEnvironmentVariable("WORKFLOW_CONTROLLER_MODEL");
                        var controllerModelId = string.IsNullOrWhiteSpace(configuredControllerModel)
                            ? modelId
                            : configuredControllerModel;

                        // Q5: concurrent-workflow cap defaults to 8, overridable via config.
                        var maxConcurrentWorkflows =
                            int.TryParse(
                                Environment.GetEnvironmentVariable("WORKFLOW_MAX_CONCURRENT"),
                                out var configuredCap
                            ) && configuredCap >= 1
                                ? configuredCap
                                : 8;

                        var controllerSubAgentOptions = new SubAgentOptions
                        {
                            Templates = BuiltInSubAgentTemplates.CreateWorkflowControllerTemplates(subAgentFactory),
                            MaxConcurrentSubAgents = BuiltInSubAgentTemplates.DefaultMaxConcurrentSubAgents,
                        };

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
                                    _ = await conversation.SendAsync([notify], ct: notifyCt);
                                }
                            },
                            maxConcurrentWorkflows: maxConcurrentWorkflows,
                            controllerDefaultOptions: new GenerateReplyOptions { ModelId = controllerModelId },
                            logger: loggerFactory.CreateLogger<WorkflowManager>()
                        );

                        _ = filteredRegistry.AddProvider(new StartWorkflowToolProvider(workflowManager));
                        ownedResources = [.. ownedResources ?? [], workflowManager];

                        // Keep the launch tools out of sub-agent inheritance so a spawned sub-agent can't
                        // launch a nested workflow (out of scope for v1). Union with any exclusions the host
                        // already set rather than replacing them.
                        if (subAgentOptions is not null)
                        {
                            subAgentOptions = subAgentOptions with
                            {
                                NonInheritedToolNames =
                                [
                                    .. subAgentOptions.NonInheritedToolNames ?? [],
                                    .. StartWorkflowToolProvider.ToolNames,
                                ],
                            };
                        }
                    }

                    agent = new MultiTurnAgentLoop(
                        providerAgent,
                        filteredRegistry,
                        threadId,
                        systemPrompt: effectiveMode.SystemPrompt,
                        defaultOptions: new GenerateReplyOptions
                        {
                            ModelId = modelId,
                            // Output-token ceiling. The provider default is 4096; with extended thinking
                            // enabled (2048-token budget, set above) that left only ~2K for the answer, so
                            // a large structured reply could exhaust the budget while still thinking and
                            // emit no text at all (stop_reason=max_tokens). 8192 leaves ~6K for the answer
                            // after the 2K thinking budget.
                            MaxToken = 8192,
                            BuiltInTools = filteredBuiltInTools,
                            RequestResponseDumpFileName = requestResponseDumpFileName,
                            PromptCaching = PromptCachingMode.Auto,
                            ExtraProperties = extraProperties,
                        },
                        // LmStreaming.Sample allows longer agentic runs than the library's 50-turn
                        // default: workspace/tool-heavy conversations routinely need more turns before
                        // the run hits its cap.
                        maxTurnsPerRun: 150,
                        store: conversationStore,
                        logger: loggerFactory.CreateLogger<MultiTurnAgentLoop>(),
                        subAgentOptions: subAgentOptions,
                        subAgentTemplateSource: sharedSubAgentSource,
                        loggerFactory: loggerFactory,
                        persistRunLedger: true,
                        // Enable the Wait/CancelWait/ListWaits park-and-wake tools plus the sample
                        // trigger sources (file_tail/schedule/subagent, and sandbox-gated process) for the
                        // MOCK providers only. Real providers are left untouched (triggerOptions: null) so
                        // OpenAI/Anthropic/Copilot behavior stays byte-for-byte unchanged and the sample
                        // exercises deferred-tool park/resume deterministically via the mock
                        // instruction-chain. Broader rollout (enabling triggers for real providers behind a
                        // flag) is tracked in #161.
                        triggerOptions: isTestMode ? triggerOptions : null
                    );

                    return new MultiTurnAgentPool.AgentCreationResult(agent, ownedResources);
                }
                catch
                {
                    // Dispose owned resources (MCP clients) if agent construction fails
                    if (ownedResources != null)
                    {
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

                    throw;
                }
            },
            providerRegistry: providerRegistry,
            conversationStore: conversationStore,
            logger: loggerFactory.CreateLogger<MultiTurnAgentPool>()
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
        };

        return pool;
    });

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

    // Use LmStreaming middleware (enables WebSockets and CORS)
    _ = app.UseLmStreaming();

    // Map custom WebSocket endpoint for chat using ChatWebSocketManager
    _ = app.Map(
        "/ws",
        async (
            HttpContext context,
            ChatWebSocketManager wsManager,
            IChatModeStore modeStore,
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

            // Get threadId from query string (required for agent routing)
            var threadId =
                context.Request.Query["threadId"].FirstOrDefault()
                ?? context.Request.Query["connectionId"].FirstOrDefault()
                ?? Guid.NewGuid().ToString();

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

            var webSocket = await context.WebSockets.AcceptWebSocketAsync();
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
                    workspaceId
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

    // Map controllers (conversations, chat-modes, tools, diagnostics)
    _ = app.MapControllers();

    // Fallback for SPA routing.
    // In Development, route through Vite dev server (proxied at /dist/*).
    if (app.Environment.IsDevelopment())
    {
        _ = app.MapGet("/", () => Results.Redirect("/dist/index.html", permanent: false));
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
        bool copilotSupportsAdaptiveThinking = false)
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
    ///     Creates a test-mode agent using an <see cref="ITestAgentBuilder"/>-supplied handler.
    /// </summary>
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
    private static IStreamingAgent CreateCopilotModelAgent(CopilotModelInfo model, ILoggerFactory loggerFactory)
    {
        return model.Transport switch
        {
            CopilotModelTransport.Anthropic => CreateCopilotAnthropicAgent(model.DisplayName, loggerFactory),
            CopilotModelTransport.Responses => CreateCopilotResponsesAgent(model.DisplayName, loggerFactory),
            _ => throw new ProviderUnavailableException(
                model.Id,
                $"unsupported Copilot transport {model.Transport}"
            ),
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
        int.TryParse(
            Environment.GetEnvironmentVariable("COPILOT_RESPONSE_TIMEOUT_SECONDS"),
            out var seconds
        )
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
        string? mockApiKeyOverride = null
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
            systemPrompt: mode.SystemPrompt,
            defaultOptions: new GenerateReplyOptions
            {
                ModelId = GetModelIdForProvider("codex"),
                RequestResponseDumpFileName = requestResponseDumpFileName,
                PromptCaching = PromptCachingMode.Auto,
            },
            store: conversationStore,
            logger: loggerFactory.CreateLogger<CodexAgentLoop>(),
            loggerFactory: loggerFactory,
            persistRunLedger: true
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

    /// <summary>
    ///     Builds the sub-agent orchestration options for a chat agent, used by BOTH the real
    ///     middleware providers (OpenAI / Anthropic / Copilot-backed) AND the mock providers
    ///     (<c>test</c> / <c>test-anthropic</c>). The base catalog differs by provider kind — mock
    ///     providers go through the <see cref="ITestAgentBuilder"/> seam (built-ins by default, or
    ///     scripted templates injected by E2E); real providers start from the shared built-ins — but
    ///     the workspace enrichment is identical for both, so the <c>Agent</c> tool advertises the
    ///     same workspace + marketplace catalog regardless of provider. That parity is what lets the
    ///     instruction-chain <c>tools_echo</c> / <c>tool_schema</c> probe (mock-only) validate the
    ///     workspace-discovered and marketplace sub-agents. Each template reuses
    ///     <paramref name="providerAgentFactory"/> — invoked per spawn so every sub-agent gets a
    ///     FRESH provider agent on the same backend as the parent.
    /// </summary>
    private static async Task<SubAgentOptions?> BuildSubAgentOptionsAsync(
        bool isTestMode,
        ITestAgentBuilder testAgentBuilder,
        ILoggerFactory loggerFactory,
        Func<IStreamingAgent> providerAgentFactory,
        SandboxSession? sandboxSession,
        WorkspaceSubAgentLoader workspaceLoader,
        MarketplaceSubAgentLoader marketplaceLoader,
        IWorkspaceStore workspaceStore,
        Microsoft.Extensions.Logging.ILogger logger)
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
        // nothing to discover — keep the base catalog exactly as-is so injected E2E templates and the
        // plain built-in surface are untouched.
        if (baseOptions is null || sandboxSession is null)
        {
            return baseOptions;
        }

        var templates = new Dictionary<string, SubAgentTemplate>(baseOptions.Templates, StringComparer.Ordinal);
        await EnrichWithWorkspaceCatalogAsync(
                templates, providerAgentFactory, sandboxSession, workspaceLoader, marketplaceLoader, workspaceStore, logger)
            .ConfigureAwait(false);

        return baseOptions with { Templates = templates };
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
        SandboxSession sandboxSession,
        WorkspaceSubAgentLoader workspaceLoader,
        MarketplaceSubAgentLoader marketplaceLoader,
        IWorkspaceStore workspaceStore,
        Microsoft.Extensions.Logging.ILogger logger)
    {
        // Workspace file-discovery and the marketplace catalog are independent gateway round-trips.
        // Fetch them concurrently so the (sync-over-async) blocking window is one round-trip, not two.
        // Both loaders are best-effort (log + return empty on failure), so neither task faults; the
        // dictionary is mutated only AFTER both complete, so the in-order merge stays single-threaded.
        var discoveredTask = workspaceLoader.LoadAsync(sandboxSession, providerAgentFactory);
        var marketplaceTask = LoadMarketplaceSubAgentsAsync(
            marketplaceLoader, workspaceStore, sandboxSession.WorkspaceId, providerAgentFactory, logger);

        await Task.WhenAll(discoveredTask, marketplaceTask).ConfigureAwait(false);

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
        Microsoft.Extensions.Logging.ILogger logger)
    {
        var marketplaces = await ResolveWorkspaceMarketplacesAsync(workspaceStore, workspaceId, logger)
            .ConfigureAwait(false);

        return await marketplaceLoader
            .LoadAsync(marketplaces, providerAgentFactory)
            .ConfigureAwait(false);
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
        Microsoft.Extensions.Logging.ILogger logger)
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
                workspaceId);
            return null;
        }
    }

    /// <summary>
    /// Seeds the workspace's root instruction file (AGENTS.md, else CLAUDE.md) into the system
    /// prompt at agent build, so the model has the workspace's high-priority instructions on turn 1
    /// WITHOUT having to read any file. The content is fetched THROUGH the gateway's MCP <c>Read</c>
    /// tool (<see cref="SandboxSessionRegistry.ReadWorkspaceFileAsync"/>): the local-host backend
    /// cannot read the container's <c>/workspace</c> filesystem, and this path is race-free (it does
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
        Microsoft.Extensions.Logging.ILogger logger)
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
                    sandboxSession.SessionId);
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
            // otherwise the model would see the file twice on turn 1.
            _ = sandboxRegistry.TryMarkDiscoverySeen(sandboxSession.SessionId, ContextFileKind, path);

            logger.LogInformation(
                "Seeded workspace root context file {Path} ({Length} chars) into the system prompt for session {SessionId}.",
                path,
                content.Length,
                sandboxSession.SessionId);

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

    private static ClaudeAgentLoop CreateClaudeAgentLoop(
        string threadId,
        AgentProfile mode,
        string? requestResponseDumpFileName,
        IConversationStore conversationStore,
        ILoggerFactory loggerFactory,
        string? llmQueryMcpBaseUrl,
        string? llmQueryMcpExamType,
        string? mockBaseUrlOverride = null,
        string? mockAuthTokenOverride = null
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
            systemPrompt: mode.SystemPrompt,
            defaultOptions: new GenerateReplyOptions
            {
                ModelId = modelId,
                RequestResponseDumpFileName = requestResponseDumpFileName,
                PromptCaching = PromptCachingMode.Auto,
            },
            store: conversationStore,
            logger: loggerFactory.CreateLogger<ClaudeAgentLoop>(),
            loggerFactory: loggerFactory,
            persistRunLedger: true
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
        string? workingDirectoryOverride = null
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
            mode.EnabledTools,
            threadId,
            systemPrompt: mode.SystemPrompt,
            defaultOptions: new GenerateReplyOptions
            {
                ModelId = GetModelIdForProvider("copilot"),
                RequestResponseDumpFileName = requestResponseDumpFileName,
                PromptCaching = PromptCachingMode.Auto,
            },
            store: conversationStore,
            logger: loggerFactory.CreateLogger<CopilotAgentLoop>(),
            loggerFactory: loggerFactory,
            persistRunLedger: true
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
    ///     Builds query parameter string for LlmQuery MCP endpoints (used by Codex which doesn't support HTTP headers).
    /// </summary>
    private static string BuildLlmQueryParams(string conversationId, string examType)
    {
        return $"X-Exam-Type={Uri.EscapeDataString(examType)}&X-Session-Id={Uri.EscapeDataString(conversationId)}";
    }

    /// <summary>
    ///     Finds the .env file by searching up from the current directory.
    /// </summary>
    internal static string? FindEnvFile()
    {
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
}
