using System.Collections.Immutable;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmCore.Utils;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using AchieveAi.LmDotnetTools.LmStreaming.AspNetCore.Extensions;
using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;
using AchieveAi.LmDotnetTools.OpenAIProvider.Agents;
using LmStreaming.Sample.Agents;
using LmStreaming.Sample.Models;
using LmStreaming.Sample.Persistence;
using LmStreaming.Sample.Tools;
using LmStreaming.Sample.WebSocket;
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

        var webSocket = await context.WebSockets.AcceptWebSocketAsync();
        wsLogger.LogInformation(
            "WebSocket connection established for thread {ThreadId} with mode {ModeId}",
            threadId,
            mode?.Id ?? "default");

        try
        {
            await wsManager.HandleConnectionAsync(webSocket, threadId, mode, cancellationToken);
        }
        finally
        {
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

    // Simple endpoint to test JSON serialization
    _ = app.MapGet("/api/message-types", (ILogger<Program> endpointLogger) =>
    {
        endpointLogger.LogDebug("Message-types endpoint called");

        var jsonOptions = JsonSerializerOptionsFactory.CreateForProduction();

        IMessage[] messages =
        [
            new TextMessage { Role = Role.User, Text = "Hello!" },
            new TextUpdateMessage { Role = Role.Assistant, Text = "Hi there", IsUpdate = true },
            new ToolsCallMessage
            {
                Role = Role.Assistant,
                ToolCalls = [new ToolCall { FunctionName = "get_weather", ToolCallId = "call_123", FunctionArgs = /*lang=json,strict*/ "{\"location\": \"NYC\"}" }]
            }
        ];

        var result = messages.Select(m => new
        {
            Type = m.GetType().Name,
            Json = JsonSerializer.Serialize(m, jsonOptions)
        }).ToList();

        endpointLogger.LogInformation(
            "Returning {MessageCount} message types: {Types}",
            result.Count,
            string.Join(", ", result.Select(r => r.Type)));

        return result;
    });

    // === Conversation Management Endpoints ===

    // GET /api/conversations - List all conversations
    _ = app.MapGet("/api/conversations", async (
        IConversationStore store,
        int limit = 50,
        int offset = 0,
        CancellationToken ct = default) =>
    {
        var threads = await store.ListThreadsAsync(limit, offset, ct);
        return threads.Select(t => new ConversationSummary
        {
            ThreadId = t.ThreadId,
            Title = t.Properties?.TryGetValue("title", out var titleObj) == true
                ? titleObj?.ToString() ?? "New Conversation"
                : "New Conversation",
            Preview = t.Properties?.TryGetValue("preview", out var previewObj) == true
                ? previewObj?.ToString()
                : null,
            LastUpdated = t.LastUpdated,
        });
    });

    // GET /api/conversations/{threadId}/messages - Load messages for a thread
    _ = app.MapGet("/api/conversations/{threadId}/messages", async (
        string threadId,
        IConversationStore store,
        CancellationToken ct = default) =>
    {
        var messages = await store.LoadMessagesAsync(threadId, ct);
        return messages;
    });

    // PUT /api/conversations/{threadId}/metadata - Update conversation metadata
    _ = app.MapPut("/api/conversations/{threadId}/metadata", async (
        string threadId,
        ConversationMetadataUpdate update,
        IConversationStore store,
        CancellationToken ct = default) =>
    {
        var existing = await store.LoadMetadataAsync(threadId, ct);
        var propertiesBuilder = existing?.Properties?.ToBuilder()
            ?? ImmutableDictionary.CreateBuilder<string, object>();

        if (update.Title != null)
        {
            propertiesBuilder["title"] = update.Title;
        }

        if (update.Preview != null)
        {
            propertiesBuilder["preview"] = update.Preview;
        }

        var metadata = new ThreadMetadata
        {
            ThreadId = threadId,
            CurrentRunId = existing?.CurrentRunId,
            LatestRunId = existing?.LatestRunId,
            LastUpdated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            SessionMappings = existing?.SessionMappings,
            Properties = propertiesBuilder.ToImmutable(),
        };

        await store.SaveMetadataAsync(threadId, metadata, ct);
        return Results.Ok();
    });

    // DELETE /api/conversations/{threadId} - Delete a conversation
    _ = app.MapDelete("/api/conversations/{threadId}", async (
        string threadId,
        IConversationStore store,
        MultiTurnAgentPool agentPool,
        CancellationToken ct = default) =>
    {
        await agentPool.RemoveAgentAsync(threadId);
        await store.DeleteThreadAsync(threadId, ct);
        return Results.NoContent();
    });

    // === Chat Mode Endpoints ===

    // GET /api/chat-modes - List all chat modes
    _ = app.MapGet("/api/chat-modes", async (
        IChatModeStore modeStore,
        CancellationToken ct = default) =>
    {
        var modes = await modeStore.GetAllModesAsync(ct);
        return modes;
    });

    // GET /api/chat-modes/{modeId} - Get a specific chat mode
    _ = app.MapGet("/api/chat-modes/{modeId}", async (
        string modeId,
        IChatModeStore modeStore,
        CancellationToken ct = default) =>
    {
        var mode = await modeStore.GetModeAsync(modeId, ct);
        return mode != null ? Results.Ok(mode) : Results.NotFound();
    });

    // POST /api/chat-modes - Create a new user mode
    _ = app.MapPost("/api/chat-modes", async (
        ChatModeCreateUpdate createData,
        IChatModeStore modeStore,
        CancellationToken ct = default) =>
    {
        var mode = await modeStore.CreateModeAsync(createData, ct);
        return Results.Created($"/api/chat-modes/{mode.Id}", mode);
    });

    // PUT /api/chat-modes/{modeId} - Update a user mode
    _ = app.MapPut("/api/chat-modes/{modeId}", async (
        string modeId,
        ChatModeCreateUpdate updateData,
        IChatModeStore modeStore,
        CancellationToken ct = default) =>
    {
        try
        {
            var mode = await modeStore.UpdateModeAsync(modeId, updateData, ct);
            return Results.Ok(mode);
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (KeyNotFoundException)
        {
            return Results.NotFound();
        }
    });

    // DELETE /api/chat-modes/{modeId} - Delete a user mode
    _ = app.MapDelete("/api/chat-modes/{modeId}", async (
        string modeId,
        IChatModeStore modeStore,
        CancellationToken ct = default) =>
    {
        try
        {
            await modeStore.DeleteModeAsync(modeId, ct);
            return Results.NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    });

    // POST /api/chat-modes/{modeId}/copy - Copy a mode
    _ = app.MapPost("/api/chat-modes/{modeId}/copy", async (
        string modeId,
        ChatModeCopy copyData,
        IChatModeStore modeStore,
        CancellationToken ct = default) =>
    {
        try
        {
            var mode = await modeStore.CopyModeAsync(modeId, copyData.NewName, ct);
            return Results.Created($"/api/chat-modes/{mode.Id}", mode);
        }
        catch (KeyNotFoundException)
        {
            return Results.NotFound();
        }
    });

    // GET /api/tools - List available tools
    _ = app.MapGet("/api/tools", (FunctionRegistry functionRegistry) =>
    {
        var (contracts, _) = functionRegistry.Build();
        return contracts.Select(c => new ToolDefinition
        {
            Name = c.Name,
            Description = c.Description,
        });
    });

    // POST /api/conversations/{threadId}/switch-mode - Switch mode for a conversation
    _ = app.MapPost("/api/conversations/{threadId}/switch-mode", async (
        string threadId,
        SwitchModeRequest request,
        IChatModeStore modeStore,
        MultiTurnAgentPool agentPool,
        CancellationToken ct = default) =>
    {
        var mode = await modeStore.GetModeAsync(request.ModeId, ct);
        if (mode == null)
        {
            return Results.NotFound(new { error = $"Mode '{request.ModeId}' not found." });
        }

        _ = await agentPool.RecreateAgentWithModeAsync(threadId, mode);
        return Results.Ok(new { modeId = mode.Id, modeName = mode.Name });
    });

    // Client logging endpoint - receives logs from browser and writes to server logs
    _ = app.MapPost("/api/logs", (
        ClientLogBatch batch,
        ILogger<Program> logEndpointLogger) =>
    {
        foreach (var entry in batch.Entries)
        {
            var level = entry.Level?.ToLowerInvariant() switch
            {
                "error" => LogLevel.Error,
                "warn" or "warning" => LogLevel.Warning,
                "info" or "information" => LogLevel.Information,
                "debug" => LogLevel.Debug,
                "trace" => LogLevel.Trace,
                _ => LogLevel.Information
            };

            // Log with client context using structured logging
            using (Serilog.Context.LogContext.PushProperty("ClientTimestamp", entry.Timestamp))
            using (Serilog.Context.LogContext.PushProperty("ClientFile", entry.File))
            using (Serilog.Context.LogContext.PushProperty("ClientLine", entry.Line))
            using (Serilog.Context.LogContext.PushProperty("ClientFunction", entry.Function))
            using (Serilog.Context.LogContext.PushProperty("ClientComponent", entry.Component))
            using (Serilog.Context.LogContext.PushProperty("Source", "Browser"))
            {
                if (entry.Data is JsonElement jsonElement && jsonElement.ValueKind != JsonValueKind.Undefined && jsonElement.ValueKind != JsonValueKind.Null)
                {
                    // Convert JsonElement to object (dictionary/list/primitive) for proper Serilog destructuring
                    // OR serialize it to a raw string if we want the raw JSON
                    // Here we'll try to deserialize it to a dynamic object or dictionary to let Serilog handle it
                    try
                    {
                        var rawText = jsonElement.GetRawText();
                        // Serilog doesn't automatically parse JSON strings into structure unless we do something special.
                        // But we can just log the raw JSON string if we want, or try to parse it.
                        // For simplicity and robustness, let's log the raw JSON string as "ClientDataJson"
                        // and also try to let Serilog destructure a Dictionary if possible.

                        using (Serilog.Context.LogContext.PushProperty("ClientData", rawText))
                        {
                            logEndpointLogger.Log(level, "[Client] {Message}", entry.Message);
                        }
                    }
                    catch
                    {
                        logEndpointLogger.Log(level, "[Client] {Message}", entry.Message);
                    }
                }
                else if (entry.Data != null)
                {
                    using (Serilog.Context.LogContext.PushProperty("ClientData", entry.Data, destructureObjects: true))
                    {
                        logEndpointLogger.Log(level, "[Client] {Message}", entry.Message);
                    }
                }
                else
                {
                    logEndpointLogger.Log(level, "[Client] {Message}", entry.Message);
                }
            }
        }

        return Results.Ok(new { received = batch.Entries.Length });
    });

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

public record ClientLogEntry(
    string? Level,
    string? Message,
    string? Timestamp,
    string? File,
    int? Line,
    string? Function,
    string? Component,
    object? Data); // Data will be bound as JsonElement by default in ASP.NET Core

public record ClientLogBatch(ClientLogEntry[] Entries);

// === Conversation Management DTOs ===

public record ConversationSummary
{
    public required string ThreadId { get; init; }
    public required string Title { get; init; }
    public string? Preview { get; init; }
    public required long LastUpdated { get; init; }
}

public record ConversationMetadataUpdate
{
    public string? Title { get; init; }
    public string? Preview { get; init; }
}

public record SwitchModeRequest
{
    public required string ModeId { get; init; }
}
