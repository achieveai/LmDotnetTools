using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Collections.Immutable;
using AchieveAi.LmDotnetTools.AnthropicProvider.Agents;
using AchieveAi.LmDotnetTools.AnthropicProvider.Models;
using AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Configuration;
using AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Models;
using AchieveAi.LmDotnetTools.CodexSdkProvider.Configuration;
using AchieveAi.LmDotnetTools.CodexSdkProvider.Models;
using AchieveAi.LmDotnetTools.CopilotSdkProvider.Configuration;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using AchieveAi.LmDotnetTools.LmStreaming.AspNetCore.Extensions;
using AchieveAi.LmDotnetTools.LmTestUtils;
using AchieveAi.LmDotnetTools.McpMiddleware.Extensions;
using AchieveAi.LmDotnetTools.McpServer.AspNetCore.Extensions;
using AchieveAi.LmDotnetTools.OpenAIProvider.Agents;
using ModelContextProtocol.Client;
using LmStreaming.Sample.Agents;
using LmStreaming.Sample.Models;
using LmStreaming.Sample.Persistence;
using LmStreaming.Sample.Services;
using LmStreaming.Sample.Tools;
using LmStreaming.Sample.WebSocket;
using Microsoft.Extensions.DependencyInjection.Extensions;
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

    // Configure Serilog from appsettings.json with all enrichers
    _ = builder.Host.UseSerilog((context, services, configuration) =>
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
                assemblyPrefix: "AchieveAi.",  // Match our assemblies
                filePathDepth: 3)              // Include last 3 path segments
                                               // File sink with structured JSON (includes all enriched properties)
            .WriteTo.File(
                new CompactJsonFormatter(),
                logPath,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                shared: true)
            // Console sink with readable format
            .WriteTo.Console(
                outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}{NewLine}    {Message:lj}{NewLine}{Exception}");

        Log.Information("Serilog configured. Log file location: {LogPath}", logPath);
    });

    // Add LmStreaming services
    _ = builder.Services.AddLmStreaming(options =>
    {
        options.WebSocketPath = "/ws";
        options.WriteIndentedJson = builder.Environment.IsDevelopment();
    });

    _ = builder.Services.AddControllers();
    _ = builder.Services.AddEndpointsApiExplorer();

    // Add Vite services for frontend integration
    _ = builder.Services.AddViteServices(options =>
    {
        options.Base = "/dist/";
        options.Server.AutoRun = true;
        options.Server.PackageDirectory = "ClientApp";
        options.Server.Port = 5173;
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

    _ = builder.Services.AddSingleton<ProviderRegistry>();

    // Register the FunctionRegistry with sample tools
    _ = builder.Services.AddSingleton(sp =>
    {
        var registry = new FunctionRegistry();
        _ = registry.AddFunctionsFromType(typeof(SampleTools));
        return registry;
    });

    // Codex MCP server: registered unconditionally but started lazily, so non-codex boots
    // don't pay the startup cost and so the codex provider stays selectable from the
    // dropdown regardless of LM_PROVIDER_MODE.
    _ = builder.Services.AddSingleton<IFunctionProvider>(
        new TypeFunctionProvider(typeof(SampleTools), providerName: "SampleTools"));
    _ = builder.Services.AddMcpFunctionProviderServerLazy(options =>
    {
        options.Port = codexMcpPort;
        options.IncludeStatefulFunctions = true;
    });
    _ = builder.Services.AddSingleton<CodexMcpServerLifetime>();

    // Register the FileConversationStore for conversation persistence
    var conversationsPath = Path.Combine(AppContext.BaseDirectory, "conversations");
    _ = builder.Services.AddSingleton<IConversationStore>(new FileConversationStore(conversationsPath));

    // Register the FileChatModeStore for chat mode persistence
    var chatModesPath = Path.Combine(AppContext.BaseDirectory, "chat-modes");
    _ = builder.Services.AddSingleton<IChatModeStore>(new FileChatModeStore(chatModesPath));

    // Register built-in (server-side) tool definitions for the tools API. The list is
    // computed for the boot default so the global tools API stays stable; per-conversation
    // built-in tools are derived from the resolved provider id at agent-creation time.
    var builtInTools = GetBuiltInToolsForProvider(providerMode);
    var builtInToolDefinitions = builtInTools?
        .OfType<AnthropicBuiltInTool>()
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
    _ = builder.Services.AddSingleton<Func<string, IStreamingAgent>>(sp => providerId =>
        {
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            return providerId.ToLowerInvariant() switch
            {
                "openai" => CreateOpenAiAgent(loggerFactory),
                "anthropic" => CreateAnthropicAgent(loggerFactory),
                "test-anthropic" => CreateAnthropicTestAgent(
                    loggerFactory,
                    sp.GetRequiredService<ITestAgentBuilder>()),
                "test" => CreateTestAgent(
                    loggerFactory,
                    sp.GetRequiredService<ITestAgentBuilder>()),
                _ => throw new ProviderUnavailableException(
                    providerId,
                    "no IStreamingAgent factory is registered for this provider"),
            };
        });

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
        var providerRegistry = sp.GetRequiredService<ProviderRegistry>();
        var codexLifetime = sp.GetRequiredService<CodexMcpServerLifetime>();
        var mockHostLifetime = sp.GetRequiredService<MockProviderHostLifetime>();

        return new MultiTurnAgentPool(
            (threadId, mode, providerId, requestResponseDumpFileName) =>
            {
                var isMedicalMode = mode.Id == SystemChatModes.MedicalKnowledgeModeId;
                var mcpBaseUrl = isMedicalMode ? llmQueryMcpBaseUrl : null;
                var normalizedProviderId = providerId.ToLowerInvariant();

                // *-mock providers reuse the same agent-loop helpers as their real counterparts;
                // the mock-host base URL is threaded into the SDK options as a per-spawn override
                // applied to the child CLI process's environment block.
                var mockBaseUrl = mockHostLifetime.BaseUrl;

                if (string.Equals(normalizedProviderId, "codex-mock", StringComparison.Ordinal))
                {
                    if (string.IsNullOrWhiteSpace(mockBaseUrl))
                    {
                        throw new ProviderUnavailableException(
                            "codex-mock", "the in-process mock provider host is not running");
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
                            ex);
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
                            mockApiKeyOverride: "mock-token"));
                }

                if (string.Equals(normalizedProviderId, "claude-mock", StringComparison.Ordinal))
                {
                    if (string.IsNullOrWhiteSpace(mockBaseUrl))
                    {
                        throw new ProviderUnavailableException(
                            "claude-mock", "the in-process mock provider host is not running");
                    }

                    return new MultiTurnAgentPool.AgentCreationResult(
                        CreateClaudeAgentLoop(
                            threadId,
                            mode,
                            requestResponseDumpFileName,
                            conversationStore,
                            loggerFactory,
                            mcpBaseUrl,
                            llmQueryMcpExamType,
                            mockBaseUrlOverride: mockBaseUrl,
                            mockAuthTokenOverride: "mock-token"));
                }

                if (string.Equals(normalizedProviderId, "copilot-mock", StringComparison.Ordinal))
                {
                    if (string.IsNullOrWhiteSpace(mockBaseUrl))
                    {
                        throw new ProviderUnavailableException(
                            "copilot-mock", "the in-process mock provider host is not running");
                    }

                    return new MultiTurnAgentPool.AgentCreationResult(
                        CreateCopilotAgentLoop(
                            threadId,
                            mode,
                            functionRegistry,
                            requestResponseDumpFileName,
                            conversationStore,
                            loggerFactory,
                            mockBaseUrlOverride: $"{mockBaseUrl}/v1",
                            mockApiKeyOverride: "mock-token"));
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
                            ex);
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
                            llmQueryMcpExamType));
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
                            llmQueryMcpExamType));
                }

                if (string.Equals(normalizedProviderId, "copilot", StringComparison.Ordinal))
                {
                    return new MultiTurnAgentPool.AgentCreationResult(
                        CreateCopilotAgentLoop(
                            threadId,
                            mode,
                            functionRegistry,
                            requestResponseDumpFileName,
                            conversationStore,
                            loggerFactory));
                }

                var providerAgent = agentFactory(normalizedProviderId);

                // Clone the shared registry per-agent to avoid mutation, filtering by mode
                var (allContracts, allHandlers) = functionRegistry.Build();
                var enabledToolSet = mode.EnabledTools?.ToHashSet();
                var filteredRegistry = new FunctionRegistry();
                foreach (var contract in allContracts)
                {
                    if (allHandlers.TryGetValue(contract.Name, out var handler)
                        && (enabledToolSet == null || enabledToolSet.Contains(contract.Name)))
                    {
                        _ = filteredRegistry.AddFunction(contract, handler, "SampleTools");
                    }
                }

                // Add LlmQuery book search MCP tools — only for medical knowledge mode
                // Track MCP clients for proper disposal alongside the agent
                List<IAsyncDisposable>? ownedResources = null;
                if (!string.IsNullOrEmpty(mcpBaseUrl))
                {
                    var (_, mcpClients) = ConnectLlmQueryMcpClients(
                        filteredRegistry, threadId, mcpBaseUrl, llmQueryMcpExamType,
                        loggerFactory);
                    if (mcpClients.Count > 0)
                    {
                        ownedResources = mcpClients.Cast<IAsyncDisposable>().ToList();
                    }
                }

                try
                {
                    var modelId = GetModelIdForProvider(normalizedProviderId);

                    // Filter built-in (server-side) tools based on mode's enabled tools
                    var allBuiltInTools = GetBuiltInToolsForProvider(normalizedProviderId);
                    var filteredBuiltInTools = ModeToolFilter.FilterBuiltInTools(allBuiltInTools, mode.EnabledTools);

                    // Enable extended thinking for Anthropic-compatible providers
                    var extraProperties = ImmutableDictionary<string, object?>.Empty;
                    if (string.Equals(normalizedProviderId, "anthropic", StringComparison.Ordinal)
                        || string.Equals(normalizedProviderId, "test-anthropic", StringComparison.Ordinal))
                    {
                        var budgetTokens = int.TryParse(
                            Environment.GetEnvironmentVariable("ANTHROPIC_THINKING_BUDGET"),
                            out var parsed) ? parsed : 2048;
                        extraProperties = extraProperties.Add(
                            "Thinking", new AnthropicThinking(budgetTokens));
                    }

                    // Test-mode DI seam: opt-in sub-agent orchestration. Real-provider modes
                    // never resolve this service, so production wiring is unchanged.
                    var isTestMode = string.Equals(normalizedProviderId, "test", StringComparison.Ordinal)
                        || string.Equals(normalizedProviderId, "test-anthropic", StringComparison.Ordinal);
                    var subAgentOptions = isTestMode
                        ? sp.GetRequiredService<ITestAgentBuilder>()
                            .CreateSubAgentOptions(loggerFactory, () => agentFactory(normalizedProviderId))
                        : null;

                    var agent = new MultiTurnAgentLoop(
                        providerAgent,
                        filteredRegistry,
                        threadId,
                        systemPrompt: mode.SystemPrompt,
                        defaultOptions: new GenerateReplyOptions
                        {
                            ModelId = modelId,
                            BuiltInTools = filteredBuiltInTools,
                            RequestResponseDumpFileName = requestResponseDumpFileName,
                            PromptCaching = PromptCachingMode.Auto,
                            ExtraProperties = extraProperties,
                        },
                        store: conversationStore,
                        logger: loggerFactory.CreateLogger<MultiTurnAgentLoop>(),
                        subAgentOptions: subAgentOptions);

                    return new MultiTurnAgentPool.AgentCreationResult(agent, ownedResources);
                }
                catch
                {
                    // Dispose owned resources (MCP clients) if agent construction fails
                    if (ownedResources != null)
                    {
                        foreach (var resource in ownedResources)
                        {
                            try { resource.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
                            catch { /* ignore cleanup errors */ }
                        }
                    }

                    throw;
                }
            },
            providerRegistry: providerRegistry,
            conversationStore: conversationStore,
            logger: loggerFactory.CreateLogger<MultiTurnAgentPool>());
    });

    // Register the ChatWebSocketManager
    _ = builder.Services.AddSingleton<ChatWebSocketManager>();

    var app = builder.Build();

    // Log startup information
    var logger = app.Services.GetRequiredService<ILogger<Program>>();
    logger.LogInformation(
        "Application started. Environment: {Environment}, WebSocket path: /ws",
        app.Environment.EnvironmentName);

    // Use Serilog request logging for HTTP requests
    _ = app.UseSerilogRequestLogging(options => options.EnrichDiagnosticContext = (diagnosticContext, httpContext) =>
        {
            diagnosticContext.Set("RequestHost", httpContext.Request.Host.Value ?? string.Empty);
            diagnosticContext.Set("RequestScheme", httpContext.Request.Scheme ?? string.Empty);
            diagnosticContext.Set("UserAgent", httpContext.Request.Headers.UserAgent.ToString() ?? string.Empty);
        });

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
    _ = app.Map("/ws", async (
        HttpContext context,
        ChatWebSocketManager wsManager,
        IChatModeStore modeStore,
        ILogger<Program> wsLogger,
        CancellationToken cancellationToken) =>
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("WebSocket connection required", cancellationToken);
            return;
        }

        // Get threadId from query string (required for agent routing)
        var threadId = context.Request.Query["threadId"].FirstOrDefault()
            ?? context.Request.Query["connectionId"].FirstOrDefault()
            ?? Guid.NewGuid().ToString();

        // Get modeId from query string (optional, defaults to system default)
        var modeId = context.Request.Query["modeId"].FirstOrDefault();
        var mode = !string.IsNullOrEmpty(modeId)
            ? await modeStore.GetModeAsync(modeId, cancellationToken)
            : null;

        // Optional per-conversation provider override. Honored only when the thread has
        // not yet locked in a provider (first message). Persisted threads keep their
        // original provider regardless of what the client sends.
        var providerId = context.Request.Query["providerId"].FirstOrDefault();

        var recordEnabled = app.Environment.IsDevelopment()
            && IsRecordingEnabled(context.Request.Query["record"].FirstOrDefault());

        StreamWriter? recordWriter = null;
        string? requestResponseDumpFileName = null;
        if (recordEnabled)
        {
            var recordingsDir = Path.Combine(app.Environment.ContentRootPath, "recordings");
            _ = Directory.CreateDirectory(recordingsDir);
            var sessionBaseName = $"{threadId}_{DateTime.UtcNow:yyyyMMddTHHmmss}";

            var wsFileName = $"{sessionBaseName}.ws.jsonl";
            recordWriter = new StreamWriter(
                Path.Combine(
                    recordingsDir,
                    wsFileName),
                false,
                new UTF8Encoding(false));

            requestResponseDumpFileName = Path.Combine(recordingsDir, $"{sessionBaseName}.llm");

            wsLogger.LogInformation(
                "Recording enabled for thread {ThreadId}. WS file: {WsFile}, LLM dump base: {DumpBase}",
                threadId,
                wsFileName,
                requestResponseDumpFileName);
        }

        var webSocket = await context.WebSockets.AcceptWebSocketAsync();
        wsLogger.LogInformation(
            "WebSocket connection established for thread {ThreadId} with mode {ModeId}",
            threadId,
            mode?.Id ?? "default");

        try
        {
            await wsManager.HandleConnectionAsync(
                webSocket,
                threadId,
                mode,
                providerId,
                requestResponseDumpFileName,
                recordWriter,
                cancellationToken);
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
                    CancellationToken.None);
            }

            webSocket.Dispose();
            wsLogger.LogInformation("WebSocket connection closed for thread {ThreadId}", threadId);
        }
    });

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
    ///     Creates a test-mode agent using an <see cref="ITestAgentBuilder"/>-supplied handler.
    /// </summary>
    private static IStreamingAgent CreateTestAgent(
        ILoggerFactory loggerFactory,
        ITestAgentBuilder testAgentBuilder)
    {
        var testHandler = testAgentBuilder.CreateHandler("test", loggerFactory);

        var httpClient = new HttpClient(testHandler)
        {
            BaseAddress = new Uri("http://test-mode/v1"),
        };

        var openClient = new OpenClient(
            httpClient,
            "http://test-mode/v1",
            logger: loggerFactory.CreateLogger<OpenClient>());

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
            defaultValue: "https://api.openai.com/v1");

        Log.Information("Creating OpenAI agent with base URL: {BaseUrl}", baseUrl);

        var openClient = new OpenClient(
            apiKey,
            baseUrl,
            logger: loggerFactory.CreateLogger<OpenClient>());

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
            defaultValue: "https://api.anthropic.com/v1");

        Log.Information("Creating Anthropic agent with base URL: {BaseUrl}", baseUrl);

        var anthropicClient = new AnthropicClient(
            apiKey,
            baseUrl: baseUrl,
            logger: loggerFactory.CreateLogger<AnthropicClient>());

        return new AnthropicAgent("Anthropic", anthropicClient, loggerFactory.CreateLogger<AnthropicAgent>());
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
        ITestAgentBuilder testAgentBuilder)
    {
        var testHandler = testAgentBuilder.CreateHandler("test-anthropic", loggerFactory);

        var httpClient = new HttpClient(testHandler)
        {
            BaseAddress = new Uri("http://test-mode/v1"),
        };

        var anthropicClient = new AnthropicClient(
            httpClient,
            baseUrl: "http://test-mode/v1",
            logger: loggerFactory.CreateLogger<AnthropicClient>());

        return new AnthropicAgent(
            "MockAnthropic",
            anthropicClient,
            loggerFactory.CreateLogger<AnthropicAgent>());
    }

    private static CodexAgentLoop CreateCodexAgentLoop(
        string threadId,
        ChatMode mode,
        FunctionRegistry functionRegistry,
        string? requestResponseDumpFileName,
        IConversationStore conversationStore,
        ILoggerFactory loggerFactory,
        string mcpEndpointUrl,
        string? llmQueryMcpBaseUrl,
        string? llmQueryMcpExamType,
        string? mockBaseUrlOverride = null,
        string? mockApiKeyOverride = null)
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
            loggerFactory: loggerFactory);
    }

    private static CodexSdkOptions CreateCodexOptions(string? requestResponseDumpFileName, string threadId)
    {
        var codexCliPath = Environment.GetEnvironmentVariable("CODEX_CLI_PATH") ?? "codex";
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
        var exposeInternalToolsAsToolMessages = !bool.TryParse(
            Environment.GetEnvironmentVariable("CODEX_EXPOSE_INTERNAL_TOOLS_AS_TOOL_MESSAGES"),
            out var parsedExposeInternalToolsAsToolMessages) || parsedExposeInternalToolsAsToolMessages;
        var emitLegacyInternalToolReasoningSummaries = bool.TryParse(
            Environment.GetEnvironmentVariable("CODEX_EMIT_LEGACY_INTERNAL_TOOL_REASONING_SUMMARIES"),
            out var parsedEmitLegacyInternalToolReasoningSummaries) && parsedEmitLegacyInternalToolReasoningSummaries;
        var networkEnabled = !bool.TryParse(
            Environment.GetEnvironmentVariable("CODEX_NETWORK_ACCESS_ENABLED"),
            out var parsedNetworkEnabled) || parsedNetworkEnabled;
        var skipGitRepoCheck = !bool.TryParse(
            Environment.GetEnvironmentVariable("CODEX_SKIP_GIT_REPO_CHECK"),
            out var parsedSkipGit) || parsedSkipGit;
        var emitSyntheticUpdates = bool.TryParse(
            Environment.GetEnvironmentVariable("CODEX_EMIT_SYNTHETIC_MESSAGE_UPDATES"),
            out var parsedEmitSyntheticUpdates) && parsedEmitSyntheticUpdates;
        // Retained as a diagnostic-only compatibility knob; raw provider streaming remains default.
        var syntheticChunkSize = int.TryParse(
            Environment.GetEnvironmentVariable("CODEX_SYNTHETIC_MESSAGE_UPDATE_CHUNK_CHARS"),
            out var parsedChunkSize)
            ? parsedChunkSize
            : 28;
        var modelInstructionsThresholdChars = int.TryParse(
            Environment.GetEnvironmentVariable("CODEX_MODEL_INSTRUCTIONS_THRESHOLD_CHARS"),
            out var parsedModelInstructionsThresholdChars)
            ? parsedModelInstructionsThresholdChars
            : 8000;
        var appServerStartupTimeoutMs = int.TryParse(
            Environment.GetEnvironmentVariable("CODEX_APP_SERVER_STARTUP_TIMEOUT_MS"),
            out var parsedAppServerStartupTimeoutMs)
            ? parsedAppServerStartupTimeoutMs
            : 30000;
        var turnCompletionTimeoutMs = int.TryParse(
            Environment.GetEnvironmentVariable("CODEX_TURN_COMPLETION_TIMEOUT_MS"),
            out var parsedTurnCompletionTimeoutMs)
            ? parsedTurnCompletionTimeoutMs
            : 120000;
        var turnInterruptGracePeriodMs = int.TryParse(
            Environment.GetEnvironmentVariable("CODEX_TURN_INTERRUPT_GRACE_PERIOD_MS"),
            out var parsedTurnInterruptGracePeriodMs)
            ? parsedTurnInterruptGracePeriodMs
            : 5000;
        var rpcTraceEnabledFromEnv = bool.TryParse(
            Environment.GetEnvironmentVariable("CODEX_RPC_TRACE_ENABLED"),
            out var parsedRpcTraceEnabled)
            && parsedRpcTraceEnabled;
        var rpcTraceFileFromEnv = Environment.GetEnvironmentVariable("CODEX_RPC_TRACE_FILE");

        var toolBridgeMode = Enum.TryParse<CodexToolBridgeMode>(toolBridgeModeRaw, ignoreCase: true, out var parsedToolBridgeMode)
            ? parsedToolBridgeMode
            : CodexToolBridgeMode.Hybrid;

        var sessionId = !string.IsNullOrWhiteSpace(requestResponseDumpFileName)
            ? Path.GetFileName(requestResponseDumpFileName)
            : $"{threadId}-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfff}";
        var traceFilePath = !string.IsNullOrWhiteSpace(requestResponseDumpFileName)
            ? $"{requestResponseDumpFileName}.codex.rpc.jsonl"
            : string.IsNullOrWhiteSpace(rpcTraceFileFromEnv)
                ? null
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
            CodexSessionId = sessionId,
            ProviderMode = "codex",
            Provider = "codex",
        };
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
    /// </summary>
    private static List<object>? GetBuiltInToolsForProvider(string providerMode)
    {
        return providerMode.ToLowerInvariant() switch
        {
            "anthropic" or "test-anthropic" => [new AnthropicWebSearchTool()],
            _ => null,
        };
    }

    private static int ResolveCodexMcpPort()
    {
        if (int.TryParse(Environment.GetEnvironmentVariable("CODEX_MCP_PORT"), out var port)
            && port > 0
            && port <= 65535)
        {
            if (IsPortAvailable(port))
            {
                return port;
            }

            var fallbackPort = FindFreeTcpPort();
            Log.Warning(
                "Configured CODEX_MCP_PORT {ConfiguredPort} is already in use. Falling back to port {FallbackPort}.",
                port,
                fallbackPort);
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
            fallback);
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
            && (string.Equals(recordValue, "1", StringComparison.Ordinal)
                || string.Equals(recordValue, "true", StringComparison.OrdinalIgnoreCase));
    }

    private static ClaudeAgentLoop CreateClaudeAgentLoop(
        string threadId,
        ChatMode mode,
        string? requestResponseDumpFileName,
        IConversationStore conversationStore,
        ILoggerFactory loggerFactory,
        string? llmQueryMcpBaseUrl,
        string? llmQueryMcpExamType,
        string? mockBaseUrlOverride = null,
        string? mockAuthTokenOverride = null)
    {
        // Build AllowedTools from mode's enabled tools:
        // null = use defaults, empty = no built-in tools (MCP only), non-empty = specific tools
        var allowedTools = mode.EnabledTools == null
            ? "Read,WebSearch,WebFetch"
            : mode.EnabledTools.Count > 0
                ? string.Join(",", mode.EnabledTools)
                : string.Empty;

        var claudeOptions = new ClaudeAgentSdkOptions
        {
            MaxTurnsPerRun = 50,
            DisableCheckpoints = true,
            DisableSessionPersistence = true,
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
            loggerFactory: loggerFactory);
    }

    private static CopilotAgentLoop CreateCopilotAgentLoop(
        string threadId,
        ChatMode mode,
        FunctionRegistry functionRegistry,
        string? requestResponseDumpFileName,
        IConversationStore conversationStore,
        ILoggerFactory loggerFactory,
        string? mockBaseUrlOverride = null,
        string? mockApiKeyOverride = null)
    {
        var copilotCliPath = Environment.GetEnvironmentVariable("COPILOT_CLI_PATH") ?? "copilot";
        var copilotCliMinVersion = Environment.GetEnvironmentVariable("COPILOT_CLI_MIN_VERSION") ?? "0.0.410";
        var model = Environment.GetEnvironmentVariable("COPILOT_MODEL") ?? "claude-sonnet-4.5";
        var apiKey = Environment.GetEnvironmentVariable("COPILOT_API_KEY");
        var baseUrl = Environment.GetEnvironmentVariable("COPILOT_BASE_URL");
        var workingDirectory = Environment.GetEnvironmentVariable("COPILOT_WORKING_DIRECTORY");
        var rpcTraceFileFromEnv = Environment.GetEnvironmentVariable("COPILOT_RPC_TRACE_FILE");
        var rpcTraceEnabledFromEnv = bool.TryParse(
            Environment.GetEnvironmentVariable("COPILOT_RPC_TRACE_ENABLED"),
            out var parsedRpcTraceEnabled)
            && parsedRpcTraceEnabled;
        var modelAllowlistProbeEnabled = !bool.TryParse(
            Environment.GetEnvironmentVariable("COPILOT_MODEL_ALLOWLIST_PROBE_ENABLED"),
            out var parsedModelProbe) || parsedModelProbe;
        var defaultPermissionDecision = Environment.GetEnvironmentVariable("COPILOT_DEFAULT_PERMISSION_DECISION") ?? "allow";

        var sessionId = !string.IsNullOrWhiteSpace(requestResponseDumpFileName)
            ? Path.GetFileName(requestResponseDumpFileName)
            : $"{threadId}-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfff}";
        var traceFilePath = !string.IsNullOrWhiteSpace(requestResponseDumpFileName)
            ? $"{requestResponseDumpFileName}.copilot.rpc.jsonl"
            : string.IsNullOrWhiteSpace(rpcTraceFileFromEnv)
                ? null
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
        var effectiveModelAllowlistProbeEnabled = string.IsNullOrWhiteSpace(mockBaseUrlOverride)
            && modelAllowlistProbeEnabled;

        var copilotOptions = new CopilotSdkOptions
        {
            CopilotCliPath = copilotCliPath,
            CopilotCliMinVersion = copilotCliMinVersion,
            Model = model,
            ApiKey = string.IsNullOrWhiteSpace(effectiveApiKey) ? null : effectiveApiKey,
            BaseUrl = string.IsNullOrWhiteSpace(effectiveBaseUrl) ? null : effectiveBaseUrl,
            WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory) ? null : workingDirectory,
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
            loggerFactory: loggerFactory);
    }

    /// <summary>
    ///     Builds MCP server configuration for the LlmQuery book search endpoint.
    ///     Used by the medical knowledge mode to expose textbook search tools.
    /// </summary>
    private static Dictionary<string, McpServerConfig> BuildLlmQueryMcpServers(
        string conversationId,
        string? baseUrl,
        string? examType)
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
        ILoggerFactory loggerFactory)
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

            var booksTransport = new HttpClientTransport(new HttpClientTransportOptions
            {
                Name = "books",
                Endpoint = new Uri($"{baseUrl}/mcp/query"),
                AdditionalHeaders = headers,
            });

            // Sync-over-async: acceptable in sample app (no SynchronizationContext)
            var booksClient = McpClient.CreateAsync(booksTransport).GetAwaiter().GetResult();
            createdClients.Add(booksClient);

            var mcpClients = new Dictionary<string, McpClient>
            {
                ["books"] = booksClient,
            };

            registry.AddMcpClientsAsync(mcpClients, "LlmQuery").GetAwaiter().GetResult();

            logger.LogInformation(
                "Connected to LlmQuery book search MCP server for thread {ThreadId}",
                threadId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Failed to connect to LlmQuery MCP server at {BaseUrl} — continuing without MCP tools",
                baseUrl);
        }

        return (registry, createdClients);
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

            if (dir.GetFiles("*.sln").Length > 0
                || dir.GetDirectories(".git").Length > 0
                || File.Exists(Path.Combine(dir.FullName, ".git")))
            {
                break;
            }

            dir = dir.Parent;
        }

        return null;
    }
}
