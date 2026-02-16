using System.Text;
using AchieveAi.LmDotnetTools.AnthropicProvider.Agents;
using AchieveAi.LmDotnetTools.AnthropicProvider.Models;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using AchieveAi.LmDotnetTools.LmStreaming.AspNetCore.Extensions;
using AchieveAi.LmDotnetTools.LmTestUtils;
using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;
using AchieveAi.LmDotnetTools.OpenAIProvider.Agents;
using LmStreaming.Sample.Agents;
using LmStreaming.Sample.Models;
using LmStreaming.Sample.Persistence;
using LmStreaming.Sample.Services;
using LmStreaming.Sample.Tools;
using LmStreaming.Sample.WebSocket;
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

    // Register the FunctionRegistry with sample tools
    _ = builder.Services.AddSingleton(sp =>
    {
        var registry = new FunctionRegistry();
        _ = registry.AddFunctionsFromType(typeof(SampleTools));
        return registry;
    });

    // Register the FileConversationStore for conversation persistence
    var conversationsPath = Path.Combine(AppContext.BaseDirectory, "conversations");
    _ = builder.Services.AddSingleton<IConversationStore>(new FileConversationStore(conversationsPath));

    // Register the FileChatModeStore for chat mode persistence
    var chatModesPath = Path.Combine(AppContext.BaseDirectory, "chat-modes");
    _ = builder.Services.AddSingleton<IChatModeStore>(new FileChatModeStore(chatModesPath));

    // Register built-in (server-side) tool definitions for the tools API
    var providerMode = Environment.GetEnvironmentVariable("LM_PROVIDER_MODE") ?? "test";
    var builtInTools = GetBuiltInToolsForProvider(providerMode);
    var builtInToolDefinitions = builtInTools?
        .OfType<AnthropicBuiltInTool>()
        .Select(t => new ToolDefinition { Name = t.Name, Description = $"Server-side {t.Name} tool ({t.Type})" })
        .ToList()
        ?? [];
    _ = builder.Services.AddSingleton<IReadOnlyList<ToolDefinition>>(builtInToolDefinitions);

    // Register the provider agent factory (multi-provider support via LM_PROVIDER_MODE env var)
    Log.Information("LM Provider Mode: {ProviderMode}", providerMode);

    _ = builder.Services.AddSingleton<Func<IStreamingAgent>>(sp => () =>
        {
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();

            return providerMode.ToLowerInvariant() switch
            {
                "openai" => CreateOpenAiAgent(loggerFactory),
                "anthropic" => CreateAnthropicAgent(loggerFactory),
                "test-anthropic" => CreateAnthropicTestAgent(loggerFactory),
                _ => CreateTestAgent(loggerFactory),
            };
        });

    // Register the MultiTurnAgentPool with mode-aware factory
    _ = builder.Services.AddSingleton(sp =>
    {
        var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
        var functionRegistry = sp.GetRequiredService<FunctionRegistry>();
        var agentFactory = sp.GetRequiredService<Func<IStreamingAgent>>();
        var conversationStore = sp.GetRequiredService<IConversationStore>();

        return new MultiTurnAgentPool(
            (threadId, mode, requestResponseDumpFileName) =>
            {
                var providerAgent = agentFactory();

                // Create filtered function registry based on mode's enabled tools
                var filteredRegistry = functionRegistry;
                if (mode.EnabledTools != null && mode.EnabledTools.Count > 0)
                {
                    // Create a new registry with only the enabled tools
                    var enabledToolSet = mode.EnabledTools.ToHashSet();
                    var (allContracts, allHandlers) = functionRegistry.Build();

                    var newRegistry = new FunctionRegistry();
                    foreach (var contract in allContracts)
                    {
                        if (enabledToolSet.Contains(contract.Name) && allHandlers.TryGetValue(contract.Name, out var handler))
                        {
                            _ = newRegistry.AddFunction(contract, handler, "FilteredTools");
                        }
                    }

                    filteredRegistry = newRegistry;
                }

                var modelId = GetModelIdForProvider(providerMode);

                // Filter built-in (server-side) tools based on mode's enabled tools
                var allBuiltInTools = GetBuiltInToolsForProvider(providerMode);
                var filteredBuiltInTools = ModeToolFilter.FilterBuiltInTools(allBuiltInTools, mode.EnabledTools);

                return new MultiTurnAgentLoop(
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
                    },
                    store: conversationStore,
                    logger: loggerFactory.CreateLogger<MultiTurnAgentLoop>());
            },
            loggerFactory.CreateLogger<MultiTurnAgentPool>());
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
    ///     Creates a test-mode agent using TestSseMessageHandler for mock responses.
    /// </summary>
    private static IStreamingAgent CreateTestAgent(ILoggerFactory loggerFactory)
    {
        var handlerLogger = loggerFactory.CreateLogger<TestSseMessageHandler>();
        var testHandler = new TestSseMessageHandler(handlerLogger)
        {
            WordsPerChunk = 3,
            ChunkDelayMs = 300,
        };

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
    ///     Creates an Anthropic-format test agent using AnthropicTestSseMessageHandler for mock responses.
    ///     This supports server-side tools (web_search, web_fetch, code_execution) and citations.
    /// </summary>
    private static IStreamingAgent CreateAnthropicTestAgent(ILoggerFactory loggerFactory)
    {
        var handlerLogger = loggerFactory.CreateLogger<AnthropicTestSseMessageHandler>();
        var testHandler = new AnthropicTestSseMessageHandler(handlerLogger)
        {
            WordsPerChunk = 3,
            ChunkDelayMs = 300,
        };

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

    private static string GetModelIdForProvider(string providerMode)
    {
        return providerMode.ToLowerInvariant() switch
        {
            "openai" => Environment.GetEnvironmentVariable("OPENAI_MODEL") ?? "gpt-4o",
            "anthropic" => Environment.GetEnvironmentVariable("ANTHROPIC_MODEL") ?? "claude-sonnet-4-20250514",
            "test-anthropic" => "claude-sonnet-4-5-20250929",
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

    /// <summary>
    ///     Returns true when recording is explicitly enabled via query string (record=1 or record=true).
    /// </summary>
    internal static bool IsRecordingEnabled(string? recordValue)
    {
        return recordValue is not null
            && (string.Equals(recordValue, "1", StringComparison.Ordinal)
                || string.Equals(recordValue, "true", StringComparison.OrdinalIgnoreCase));
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
