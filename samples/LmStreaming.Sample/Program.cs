using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using AchieveAi.LmDotnetTools.LmStreaming.AspNetCore.Extensions;
using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;
using AchieveAi.LmDotnetTools.OpenAIProvider.Agents;
using LmStreaming.Sample.Agents;
using LmStreaming.Sample.Persistence;
using LmStreaming.Sample.Tools;
using LmStreaming.Sample.WebSocket;
using System.Text;
using Serilog;
using Serilog.Enrichers.CallerInfo;
using Serilog.Events;
using Serilog.Exceptions;
using Serilog.Formatting.Compact;
using Vite.AspNetCore;

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

    // Register the provider agent factory (creates OpenClientAgent with TestSseMessageHandler)
    _ = builder.Services.AddSingleton<Func<IStreamingAgent>>(sp => () =>
        {
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            var handlerLogger = loggerFactory.CreateLogger<TestSseMessageHandler>();

            // Create the test handler that follows instruction chains
            var testHandler = new TestSseMessageHandler(handlerLogger)
            {
                WordsPerChunk = 3,   // Stream 3 words at a time for visible streaming
                ChunkDelayMs = 300,   // 50ms delay between chunks
            };

            // Create HttpClient with the test handler
            var httpClient = new HttpClient(testHandler)
            {
                BaseAddress = new Uri("http://test-mode/v1"),
            };

            // Create OpenClient with the mock HttpClient
            var openClient = new OpenClient(
                httpClient,
                "http://test-mode/v1",
                logger: loggerFactory.CreateLogger<OpenClient>());

            // Create the streaming agent (middleware stack is built by MultiTurnAgentLoop)
            var agentLogger = loggerFactory.CreateLogger<OpenClientAgent>();
            return new OpenClientAgent("MockLLM", openClient, agentLogger);
        });

    // Register the MultiTurnAgentPool with mode-aware factory
    _ = builder.Services.AddSingleton(sp =>
    {
        var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
        var functionRegistry = sp.GetRequiredService<FunctionRegistry>();
        var agentFactory = sp.GetRequiredService<Func<IStreamingAgent>>();
        var conversationStore = sp.GetRequiredService<IConversationStore>();

        return new MultiTurnAgentPool(
            (threadId, mode) =>
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

                return new MultiTurnAgentLoop(
                    providerAgent,
                    filteredRegistry,
                    threadId,
                    systemPrompt: mode.SystemPrompt,
                    defaultOptions: new GenerateReplyOptions { ModelId = "test-model" },
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
        _ = app.UseViteDevelopmentServer();
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
            && string.Equals(
                context.Request.Query["record"].FirstOrDefault(),
                "true",
                StringComparison.OrdinalIgnoreCase);

        StreamWriter? recordWriter = null;
        if (recordEnabled)
        {
            var recordingsDir = Path.Combine(app.Environment.ContentRootPath, "recordings");
            Directory.CreateDirectory(recordingsDir);
            var fileName = $"{threadId}_{DateTime.UtcNow:yyyyMMddTHHmmss}.jsonl";
            recordWriter = new StreamWriter(Path.Combine(recordingsDir, fileName), false, new UTF8Encoding(false));
            wsLogger.LogInformation("Recording WebSocket messages to {FileName}", fileName);
        }

        var webSocket = await context.WebSockets.AcceptWebSocketAsync();
        wsLogger.LogInformation(
            "WebSocket connection established for thread {ThreadId} with mode {ModeId}",
            threadId,
            mode?.Id ?? "default");

        try
        {
            await wsManager.HandleConnectionAsync(webSocket, threadId, mode, recordWriter, cancellationToken);
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

    // Fallback for SPA routing - serve Vite-generated index.html with correct asset hashes
    _ = app.MapFallbackToFile("dist/index.html");

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
}
