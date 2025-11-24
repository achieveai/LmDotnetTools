using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using AchieveAi.LmDotnetTools.AgUi.AspNetCore.Models;
using AchieveAi.LmDotnetTools.AgUi.DataObjects;
using AchieveAi.LmDotnetTools.AgUi.Protocol.Middleware;
using AchieveAi.LmDotnetTools.AgUi.Protocol.Publishing;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AchieveAi.LmDotnetTools.AgUi.AspNetCore.Controllers;

/// <summary>
/// Controller providing standard AG-UI protocol endpoint
/// Streams events via Server-Sent Events (SSE) over HTTP
/// </summary>
[ApiController]
[Route("api/ag-ui")]
public class AgUiController : ControllerBase
{
    private readonly ILogger<AgUiController> _logger;
    private readonly IEventPublisher _eventPublisher;
    private readonly IServiceProvider _serviceProvider;

    public AgUiController(
        ILogger<AgUiController> logger,
        IEventPublisher _eventPublisher,
        IServiceProvider serviceProvider
    )
    {
        _logger = logger;
        this._eventPublisher = _eventPublisher;
        _serviceProvider = serviceProvider;
    }

    /// <summary>
    /// AG-UI protocol endpoint that streams events via SSE
    /// </summary>
    /// <param name="request">AG-UI request with messages and thread context</param>
    /// <param name="cancellationToken">Cancellation token</param>
    [HttpPost]
    [Produces("text/event-stream")]
    public async Task StreamAgUiEvents([FromBody] AgUiRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request, nameof(request));

        var sessionId = request.ThreadId ?? Guid.NewGuid().ToString();

        _logger.LogInformation(
            "AG-UI SSE request received - SessionId: {SessionId}, Agent: {Agent}",
            sessionId,
            request.Agent ?? "default"
        );

        // Set SSE headers
        Response.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers.Connection = "keep-alive";
        Response.Headers["X-Accel-Buffering"] = "no"; // Disable nginx buffering

        try
        {
            // Start agent execution in background task
            var executionTask = Task.Run(
                async () =>
                {
                    try
                    {
                        await ExecuteAgentAsync(request, sessionId, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Agent execution failed for session {SessionId}", sessionId);
                    }
                },
                cancellationToken
            );

            // Subscribe to events and stream as SSE
            await foreach (var evt in _eventPublisher.SubscribeAsync(sessionId, cancellationToken))
            {
                var json = JsonSerializer.Serialize(evt);
                var sseData = $"data: {json}\n\n";
                var bytes = Encoding.UTF8.GetBytes(sseData);
                await Response.Body.WriteAsync(bytes, cancellationToken);
                await Response.Body.FlushAsync(cancellationToken);

                _logger.LogDebug("Streamed SSE event: {EventType} for session {SessionId}", evt.Type, sessionId);

                if (evt.Type == AgUiEventTypes.RUN_FINISHED)
                {
                    _eventPublisher.Unsubscribe(sessionId);
                    break;
                }
            }

            // Wait for agent execution to complete
            await executionTask;

            _logger.LogInformation("AG-UI SSE stream completed for session {SessionId}", sessionId);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("AG-UI SSE stream cancelled for session {SessionId}", sessionId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AG-UI SSE stream failed for session {SessionId}", sessionId);

            // Try to send error event
            try
            {
                var errorJson = JsonSerializer.Serialize(
                    new
                    {
                        type = "RUN_ERROR",
                        sessionId,
                        error = ex.Message,
                    }
                );
                var errorData = $"data: {errorJson}\n\n";
                var errorBytes = Encoding.UTF8.GetBytes(errorData);
                await Response.Body.WriteAsync(errorBytes, CancellationToken.None);
                await Response.Body.FlushAsync(CancellationToken.None);
            }
            catch
            {
                // Best effort - client may have disconnected
            }
        }
        finally
        {
            _eventPublisher.Unsubscribe(sessionId);
        }
    }

    private async Task ExecuteAgentAsync(AgUiRequest request, string sessionId, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Executing agent for session {SessionId}", sessionId);

        // Convert AG-UI messages to LmCore format
        var messages = ConvertMessages(request.Messages);

        var options = new GenerateReplyOptions
        {
            MaxToken = 4096,
            Temperature = 0.7f,
            RunId = request.RunId ?? Guid.NewGuid().ToString(),
            ThreadId = sessionId,
        };

        // Get agent - try to resolve specific agent by name or use first available
        IStreamingAgent? agent = null;

        if (!string.IsNullOrEmpty(request.Agent))
        {
            // Try to get named agent from service provider
            // This would require a registry pattern - for now just get all and select by type name
            var agents = _serviceProvider.GetServices<IStreamingAgent>();
            agent = agents.FirstOrDefault(a =>
                a.GetType().Name.Equals(request.Agent, StringComparison.OrdinalIgnoreCase)
            );
        }

        // Fallback to first registered agent
        agent ??= _serviceProvider.GetServices<IStreamingAgent>().FirstOrDefault();

        if (agent == null)
        {
            _logger.LogError("No IStreamingAgent registered in DI container");
            throw new InvalidOperationException(
                "No agent available - please register at least one IStreamingAgent in DI"
            );
        }

        // Get AgUiStreamingMiddleware from DI
        var agUiMiddleware = _serviceProvider.GetRequiredService<AgUiStreamingMiddleware>();

        // Wrap agent with middleware
        var wrappedAgent = agent.WithMiddleware(agUiMiddleware);

        _logger.LogInformation("Agent wrapped with AgUiStreamingMiddleware for session {SessionId}", sessionId);

        // Execute agent - middleware will publish events to channel
        var responseStream = await wrappedAgent.GenerateReplyStreamingAsync(messages, options, cancellationToken);

        // Consume the stream
        await foreach (var message in responseStream.WithCancellation(cancellationToken))
        {
            _logger.LogDebug(
                "Agent produced message of type {MessageType} for session {SessionId}",
                message.GetType().Name,
                sessionId
            );
        }

        _logger.LogInformation("Agent execution completed for session {SessionId}", sessionId);
    }

    private static ImmutableList<IMessage> ConvertMessages(List<AgUiMessage>? messages)
    {
        return messages == null || messages.Count == 0
            ? []
            : [.. messages
            .Select(m =>
                new TextMessage
                {
                    Role = m.Role.ToLowerInvariant() switch
                    {
                        "user" => Role.User,
                        "assistant" => Role.Assistant,
                        "system" => Role.System,
                        "tool" => Role.Tool,
                        _ => Role.User,
                    },
                    Text = m.Content,
                    FromAgent = m.Name,
                } as IMessage
            )];
    }
}
