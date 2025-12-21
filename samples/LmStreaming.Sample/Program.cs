using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmCore.Utils;
using AchieveAi.LmDotnetTools.LmStreaming.AspNetCore.Extensions;
using AchieveAi.LmDotnetTools.LmStreaming.AspNetCore.SSE;
using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;
using AchieveAi.LmDotnetTools.OpenAIProvider.Agents;
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
    builder.Services.AddViteServices(options =>
    {
        options.Base = "/dist/";
        options.Server.AutoRun = true;
        options.Server.Port = 5173;
    });

    // Register the mock LLM streaming agent using TestSseMessageHandler
    _ = builder.Services.AddSingleton<IStreamingAgent>(sp =>
    {
        var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
        var handlerLogger = loggerFactory.CreateLogger<TestSseMessageHandler>();

        // Create the test handler that follows instruction chains
        var testHandler = new TestSseMessageHandler(handlerLogger)
        {
            WordsPerChunk = 3,   // Stream 3 words at a time for visible streaming
            ChunkDelayMs = 50    // 50ms delay between chunks
        };

        // Create HttpClient with the test handler
        var httpClient = new HttpClient(testHandler)
        {
            BaseAddress = new Uri("http://test-mode/v1")
        };

        // Create OpenClient with the mock HttpClient
        var openClient = new OpenClient(
            httpClient,
            "http://test-mode/v1",
            logger: loggerFactory.CreateLogger<OpenClient>());

        // Create the streaming agent
        var agentLogger = loggerFactory.CreateLogger<OpenClientAgent>();
        var agent = new OpenClientAgent("MockLLM", openClient, agentLogger);

        // Wrap with MessageTransformationMiddleware to assign messageOrderIdx and chunkIdx
        var middleware = new MessageTransformationMiddleware(
            logger: loggerFactory.CreateLogger<MessageTransformationMiddleware>());
        return agent.WithMiddleware(middleware);
    });

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

    // Map custom WebSocket endpoint for chat
    var jsonOptions = JsonSerializerOptionsFactory.CreateForProduction();
    _ = app.Map("/ws", async (
        HttpContext context,
        IStreamingAgent streamingAgent,
        ILogger<Program> wsLogger,
        CancellationToken cancellationToken) =>
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("WebSocket connection required", cancellationToken);
            return;
        }

        var webSocket = await context.WebSockets.AcceptWebSocketAsync();
        var connectionId = context.Request.Query["connectionId"].FirstOrDefault() ?? Guid.NewGuid().ToString();
        wsLogger.LogInformation("WebSocket connection established: {ConnectionId}", connectionId);

        var buffer = new byte[4096];
        var messageBuilder = new StringBuilder();

        try
        {
            while (webSocket.State == WebSocketState.Open)
            {
                WebSocketReceiveResult result;
                messageBuilder.Clear();

                do
                {
                    result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        wsLogger.LogInformation("Client requested close for {ConnectionId}", connectionId);
                        await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closed by client", cancellationToken);
                        return;
                    }

                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        var chunk = Encoding.UTF8.GetString(buffer, 0, result.Count);
                        messageBuilder.Append(chunk);
                    }
                } while (!result.EndOfMessage);

                if (messageBuilder.Length > 0)
                {
                    var json = messageBuilder.ToString();
                    wsLogger.LogDebug("Received message from {ConnectionId}: {Json}", connectionId, json);

                    // Try to parse as ChatRequest
                    try
                    {
                        var chatRequest = JsonSerializer.Deserialize<ChatRequest>(json, jsonOptions);
                        if (chatRequest?.Message != null)
                        {
                            wsLogger.LogInformation("Processing chat request: {Message}", chatRequest.Message);

                            // Build conversation with user message
                            var userMessage = new TextMessage
                            {
                                Role = Role.User,
                                Text = chatRequest.Message
                            };

                            // Generate streaming response
                            var options = new GenerateReplyOptions { ModelId = "test-model" };
                            var streamingMessages = await streamingAgent.GenerateReplyStreamingAsync(
                                [userMessage],
                                options,
                                cancellationToken);

                            // Stream each message over WebSocket
                            await foreach (var message in streamingMessages.WithCancellation(cancellationToken))
                            {
                                if (webSocket.State != WebSocketState.Open) break;

                                var messageJson = JsonSerializer.Serialize(message, jsonOptions);
                                var bytes = Encoding.UTF8.GetBytes(messageJson);
                                await webSocket.SendAsync(
                                    new ArraySegment<byte>(bytes),
                                    WebSocketMessageType.Text,
                                    true,
                                    cancellationToken);

                                wsLogger.LogDebug("Sent message type: {MessageType}", message.GetType().Name);
                            }

                            // Send done signal
                            var doneJson = """{"$type":"done"}""";
                            var doneBytes = Encoding.UTF8.GetBytes(doneJson);
                            await webSocket.SendAsync(
                                new ArraySegment<byte>(doneBytes),
                                WebSocketMessageType.Text,
                                true,
                                cancellationToken);

                            wsLogger.LogInformation("Chat response completed for {ConnectionId}", connectionId);
                        }
                    }
                    catch (JsonException ex)
                    {
                        wsLogger.LogWarning(ex, "Invalid JSON from {ConnectionId}", connectionId);
                        var errorJson = """{"$type":"error","message":"Invalid JSON"}""";
                        var errorBytes = Encoding.UTF8.GetBytes(errorJson);
                        await webSocket.SendAsync(
                            new ArraySegment<byte>(errorBytes),
                            WebSocketMessageType.Text,
                            true,
                            cancellationToken);
                    }
                }
            }
        }
        catch (WebSocketException ex)
        {
            wsLogger.LogWarning(ex, "WebSocket error for {ConnectionId}", connectionId);
        }
        catch (OperationCanceledException)
        {
            wsLogger.LogInformation("WebSocket operation cancelled for {ConnectionId}", connectionId);
        }
        finally
        {
            if (webSocket.State == WebSocketState.Open)
            {
                await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Server closing", CancellationToken.None);
            }
            webSocket.Dispose();
            wsLogger.LogInformation("WebSocket connection closed: {ConnectionId}", connectionId);
        }
    });

    // SSE endpoint that streams messages using mock LLM
    _ = app.MapPost("/api/chat", async (
        ChatRequest request,
        IMessageSseStreamer sseStreamer,
        IStreamingAgent streamingAgent,
        HttpResponse response,
        ILogger<Program> endpointLogger,
        CancellationToken cancellationToken) =>
    {
        endpointLogger.LogInformation(
            "Chat request received. Message: {Message}, Length: {MessageLength}",
            request.Message,
            request.Message?.Length ?? 0);

        // Build conversation with user message
        var userMessage = new TextMessage
        {
            Role = Role.User,
            Text = request.Message ?? string.Empty
        };

        // Generate streaming response from mock LLM
        var options = new GenerateReplyOptions { ModelId = "test-model" };
        var streamingMessages = await streamingAgent.GenerateReplyStreamingAsync(
            [userMessage],
            options,
            cancellationToken);

        // Stream as SSE
        await sseStreamer.StreamAsync(response, streamingMessages, cancellationToken);
        await sseStreamer.WriteDoneAsync(response, cancellationToken);

        endpointLogger.LogInformation("Chat request completed successfully");
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
                if (entry.Data != null)
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

public record ChatRequest(string Message);

public record ClientLogEntry(
    string? Level,
    string? Message,
    string? Timestamp,
    string? File,
    int? Line,
    string? Function,
    string? Component,
    object? Data);

public record ClientLogBatch(ClientLogEntry[] Entries);
