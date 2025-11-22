using System.Runtime.CompilerServices;
using System.Text.Json;
using AchieveAi.LmDotnetTools.AgUi.DataObjects;
using AchieveAi.LmDotnetTools.AgUi.DataObjects.Enums;
using AchieveAi.LmDotnetTools.AgUi.DataObjects.Events;
using AchieveAi.LmDotnetTools.AgUi.Persistence.Models;
using AchieveAi.LmDotnetTools.AgUi.Persistence.Repositories;
using AchieveAi.LmDotnetTools.AgUi.Protocol.Converters;
using AchieveAi.LmDotnetTools.AgUi.Protocol.Extensions;
using AchieveAi.LmDotnetTools.AgUi.Protocol.Publishing;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AchieveAi.LmDotnetTools.AgUi.Protocol.Middleware;

/// <summary>
/// AG-UI streaming middleware that intercepts messages and publishes AG-UI events.
/// Implements IToolResultCallback to capture tool execution results from FunctionCallMiddleware.
/// </summary>
/// <remarks>
/// This middleware follows the interceptor pattern - it processes messages flowing through
/// the pipeline without creating its own stream. Session and run IDs are managed through
/// the MiddlewareContext to ensure they persist across multiple invocations.
/// Optionally persists sessions and messages to SQLite when persistence is enabled.
/// </remarks>
public class AgUiStreamingMiddleware : IStreamingMiddleware, IToolResultCallback
{
    private readonly IEventPublisher _eventPublisher;
    private readonly IMessageConverter _converter;
    private readonly ILogger<AgUiStreamingMiddleware> _logger;
    private readonly AgUiMiddlewareOptions _options;
    private readonly ISessionRepository? _sessionRepository;
    private readonly IMessageRepository? _messageRepository;
    private readonly IEventRepository? _eventRepository;
    private MiddlewareContext? _currentContext;

    /// <summary>
    /// Initializes a new instance of the <see cref="AgUiStreamingMiddleware"/> class.
    /// </summary>
    /// <param name="eventPublisher">The event publisher for AG-UI events.</param>
    /// <param name="converter">The message converter for LmCore to AG-UI conversion.</param>
    /// <param name="logger">Optional logger for diagnostics.</param>
    /// <param name="options">Optional middleware configuration options.</param>
    /// <param name="sessionRepository">Optional session repository for persistence.</param>
    /// <param name="messageRepository">Optional message repository for persistence.</param>
    /// <param name="eventRepository">Optional event repository for persistence.</param>
    public AgUiStreamingMiddleware(
        IEventPublisher eventPublisher,
        IMessageConverter converter,
        ILogger<AgUiStreamingMiddleware>? logger = null,
        IOptions<AgUiMiddlewareOptions>? options = null,
        ISessionRepository? sessionRepository = null,
        IMessageRepository? messageRepository = null,
        IEventRepository? eventRepository = null)
    {
        _eventPublisher = eventPublisher;
        _converter = converter;
        _logger = logger ?? NullLogger<AgUiStreamingMiddleware>.Instance;
        _options = options?.Value ?? new AgUiMiddlewareOptions();
        _sessionRepository = sessionRepository;
        _messageRepository = messageRepository;
        _eventRepository = eventRepository;
    }

    public string? Name => "AgUiStreamingMiddleware";

    /// <summary>
    /// Invokes the middleware for synchronous scenarios
    /// </summary>
    public async Task<IEnumerable<IMessage>> InvokeAsync(
        MiddlewareContext context,
        IAgent agent,
        CancellationToken cancellationToken = default)
    {
        // For non-streaming responses, just pass through
        return await agent.GenerateReplyAsync(context.Messages, context.Options, cancellationToken);
    }

    /// <summary>
    /// Invokes the middleware for streaming scenarios.
    /// Follows the interceptor pattern: gets the stream from the next middleware,
    /// then processes and yields messages through without modification.
    /// </summary>
    public async Task<IAsyncEnumerable<IMessage>> InvokeStreamingAsync(
        MiddlewareContext context,
        IStreamingAgent agent,
        CancellationToken cancellationToken = default)
    {
        // Get stream from next middleware in the chain
        var stream = await agent.GenerateReplyStreamingAsync(
            context.Messages,
            context.Options,
            cancellationToken);

        // Process stream and yield messages (interceptor pattern)
        return ProcessStreamWithEvents(stream, context, cancellationToken);
    }

    /// <summary>
    /// Processes the message stream, converting LmCore messages to AG-UI events.
    /// This method implements the interceptor pattern: it receives messages from upstream,
    /// publishes AG-UI events as side effects, and yields all messages through unchanged.
    /// </summary>
    /// <remarks>
    /// CRITICAL: This method follows these principles:
    /// 1. Never creates its own stream - receives it as a parameter
    /// 2. Always yields messages through, even if event publishing fails
    /// 3. Publishes AG-UI events as side effects (not the main flow)
    /// 4. Never breaks the stream with exceptions
    /// </remarks>
    private async IAsyncEnumerable<IMessage> ProcessStreamWithEvents(
        IAsyncEnumerable<IMessage> messages,
        MiddlewareContext context,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Get session ID from GenerateReplyOptions first, fall back to generating new one
        // This ensures sessionId is consistent with the controller's sessionId
        var sessionId = context.GetOrCreateSessionId(context.Options?.ThreadId);
        var threadId = context.Options?.ThreadId;

        var runId = context.Options?.RunId
            ?? context.GetOrCreateRunId();

        // Store context for tool callbacks
        _currentContext = context;

        // Persist session start (fire-and-forget, non-blocking)
        _ = PersistSessionStartAsync(sessionId, context, ct);

        // Publish run-started event as side effect
        await PublishEventSafely(new RunStartedEvent
        {
            SessionId = sessionId,
            ThreadId = threadId,
            RunId = runId
        }, ct);

        // Process all messages, yielding them through
        await foreach (var message in messages.WithCancellation(ct).ConfigureAwait(false))
        {
            // Persist message (fire-and-forget, non-blocking)
            _ = PersistMessageAsync(message, sessionId, ct);

            // Convert and publish AG-UI events as side effect
            // Errors in publishing don't break the stream
            await ProcessMessageAsync(message, sessionId, threadId, runId, ct);

            // ALWAYS yield message through, even if event publishing failed
            yield return message;
        }

        foreach (var evt in _converter.Flush(sessionId, threadId, runId))
        {
            await PublishEventSafely(evt, ct);
        }

        // Publish run-finished event as side effect
        // We always report success here - errors in event publishing are logged
        // but don't affect the overall run status
        await PublishEventSafely(new RunFinishedEvent
        {
            SessionId = sessionId,
            ThreadId = threadId,
            RunId = runId,
            Status = RunStatus.Success
        }, ct);

        // Persist session end (fire-and-forget, non-blocking)
        _ = PersistSessionEndAsync(sessionId, RunStatus.Success, ct);
    }

    /// <summary>
    /// Processes a single message by converting it to AG-UI events and publishing them.
    /// Errors in conversion or publishing are logged but don't break the stream.
    /// </summary>
    private async Task ProcessMessageAsync(IMessage message, string sessionId, string? threadId, string? runId, CancellationToken ct)
    {
        await PublishMessageEventsSafely(message, sessionId, threadId, runId, ct);
    }

    /// <summary>
    /// Safely publishes AG-UI events for a message without breaking the stream.
    /// This wrapper ensures that errors in conversion or publishing don't propagate.
    /// </summary>
    private async Task PublishMessageEventsSafely(IMessage message, string sessionId, string? threadId, string? runId, CancellationToken ct)
    {
        try
        {
            var events = _converter.ConvertToAgUiEvents(message, sessionId, threadId, runId);

            foreach (var evt in events)
            {
                await PublishEventSafely(evt, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Re-throw cancellation - this is expected behavior
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Error converting/publishing message events for session {SessionId}. " +
                "Message type: {MessageType}. Stream will continue.",
                sessionId,
                message.GetType().Name);
            // DON'T throw - continue processing
        }
    }

    /// <summary>
    /// Safely publishes a single AG-UI event without breaking the stream.
    /// Only OperationCanceledException is re-thrown; all other exceptions are logged and swallowed.
    /// </summary>
    private async Task PublishEventSafely(AgUiEventBase evt, CancellationToken ct)
    {
        try
        {
            await _eventPublisher.PublishAsync(evt, ct);

            if (_options.EnableDebugLogging)
            {
                _logger.LogDebug("Published event {EventType} for session {SessionId}",
                    evt.Type, evt.SessionId);
            }
        }
        catch (OperationCanceledException)
        {
            // Re-throw cancellation - this is expected and should stop processing
            _logger.LogDebug("Event publishing cancelled for {EventType}", evt.Type);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to publish event {EventType} for session {SessionId}. " +
                "Event will be skipped but stream will continue.",
                evt.Type,
                evt.SessionId);
            // DON'T throw - continue processing
            // This ensures the message stream continues even if event publishing fails
        }
    }

    #region IToolResultCallback Implementation

    /// <summary>
    /// Called when a tool call starts execution
    /// </summary>
    public async Task OnToolCallStartedAsync(
        string toolCallId,
        string functionName,
        string functionArgs,
        CancellationToken cancellationToken = default)
    {
        // Get session ID from stored context
        if (_currentContext == null)
        {
            _logger.LogWarning("Tool call started but no context available");
            return;
        }

        var sessionId = _currentContext.Value.GetOrCreateSessionId(_currentContext.Value.Options?.ThreadId);
        var threadId = _currentContext.Value.Options?.ThreadId;
        var runId = _currentContext.Value.Options?.RunId;

        // Publish tool-call-start event
        var toolCallStartEvent = new ToolCallStartEvent
        {
            SessionId = sessionId,
            ThreadId = threadId,
            RunId = runId,
            ToolCallId = toolCallId,
            ToolName = functionName
        };

        await PublishEventSafely(toolCallStartEvent, cancellationToken);

        // Publish tool-call-arguments event with complete args
        var toolCallArgsEvent = new ToolCallArgumentsEvent
        {
            SessionId = sessionId,
            ThreadId = threadId,
            RunId = runId,
            ToolCallId = toolCallId,
            Delta = functionArgs
        };

        await PublishEventSafely(toolCallArgsEvent, cancellationToken);
    }

    /// <summary>
    /// Called when a tool call result becomes available
    /// </summary>
    public async Task OnToolResultAvailableAsync(
        string toolCallId,
        ToolCallResult result,
        CancellationToken cancellationToken = default)
    {
        // Get session ID from stored context
        if (_currentContext == null)
        {
            _logger.LogWarning("Tool result available but no context available");
            return;
        }

        var sessionId = _currentContext.Value.GetOrCreateSessionId(_currentContext.Value.Options?.ThreadId);
        var threadId = _currentContext.Value.Options?.ThreadId;
        var runId = _currentContext.Value.Options?.RunId;

        // Tool results are no longer separate events in the protocol
        // They should be embedded in messages. For now, just emit tool-call-end event
        var toolCallEndEvent = new ToolCallEndEvent
        {
            SessionId = sessionId,
            ThreadId = threadId,
            RunId = runId,
            ToolCallId = toolCallId,
            Duration = TimeSpan.Zero // We don't track duration here
        };

        await PublishEventSafely(toolCallEndEvent, cancellationToken);
    }

    /// <summary>
    /// Called when a tool call encounters an error
    /// </summary>
    public async Task OnToolCallErrorAsync(
        string toolCallId,
        string functionName,
        string error,
        CancellationToken cancellationToken = default)
    {
        // Get session ID from stored context
        if (_currentContext == null)
        {
            _logger.LogWarning("Tool call error but no context available");
            return;
        }

        var sessionId = _currentContext.Value.GetOrCreateSessionId(_currentContext.Value.Options?.ThreadId);
        var threadId = _currentContext.Value.Options?.ThreadId;
        var runId = _currentContext.Value.Options?.RunId;

        // Tool call errors should be reported as RUN_ERROR events
        var errorEvent = new ErrorEvent
        {
            SessionId = sessionId,
            ThreadId = threadId,
            RunId = runId,
            ErrorCode = "TOOL_CALL_ERROR",
            Message = $"Tool call {functionName} failed: {error}",
            Recoverable = true
        };

        await PublishEventSafely(errorEvent, cancellationToken);

        // Also publish tool-call-end event
        var toolCallEndEvent = new ToolCallEndEvent
        {
            SessionId = sessionId,
            ThreadId = threadId,
            RunId = runId,
            ToolCallId = toolCallId,
            Duration = TimeSpan.Zero
        };

        await PublishEventSafely(toolCallEndEvent, cancellationToken);
    }

    #endregion

    #region Persistence Methods

    /// <summary>
    /// Persists session start information to the database (fire-and-forget pattern).
    /// </summary>
    /// <remarks>
    /// This method runs asynchronously without blocking the message stream.
    /// Errors are logged but do not affect stream processing.
    /// </remarks>
    private async Task PersistSessionStartAsync(
        string sessionId,
        MiddlewareContext context,
        CancellationToken ct)
    {
        if (_sessionRepository == null)
        {
            return; // Persistence not enabled
        }

        try
        {
            var session = new SessionEntity
            {
                Id = sessionId,
                ConversationId = null, // TODO: Extract from context metadata when available
                StartTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Status = "Started",
                MetadataJson = null // TODO: Add metadata if needed
            };

            await _sessionRepository.CreateAsync(session, ct);
            _logger.LogDebug("Persisted session start for {SessionId}", sessionId);
        }
        catch (OperationCanceledException)
        {
            // Cancellation is expected, don't log as error
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist session start for {SessionId}", sessionId);
            // DON'T throw - persistence failures shouldn't break the stream
        }
    }

    /// <summary>
    /// Persists a message to the database (fire-and-forget pattern).
    /// </summary>
    /// <remarks>
    /// This method runs asynchronously without blocking the message stream.
    /// Errors are logged but do not affect stream processing.
    /// </remarks>
    private async Task PersistMessageAsync(
        IMessage message,
        string sessionId,
        CancellationToken ct)
    {
        if (_messageRepository == null)
        {
            return; // Persistence not enabled
        }

        try
        {
            // Generate message ID (IMessage doesn't have an Id property)
            var messageId = message.GenerationId ?? Guid.NewGuid().ToString();

            var messageEntity = new MessageEntity
            {
                Id = messageId,
                SessionId = sessionId,
                MessageJson = JsonSerializer.Serialize(message, new JsonSerializerOptions
                {
                    WriteIndented = false,
                    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
                }),
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                MessageType = message.GetType().Name
            };

            await _messageRepository.CreateAsync(messageEntity, ct);
            _logger.LogTrace("Persisted message {MessageId} for session {SessionId}", messageEntity.Id, sessionId);
        }
        catch (OperationCanceledException)
        {
            // Cancellation is expected, don't log as error
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist message for session {SessionId}", sessionId);
            // DON'T throw - persistence failures shouldn't break the stream
        }
    }

    /// <summary>
    /// Persists session end information to the database (fire-and-forget pattern).
    /// </summary>
    /// <remarks>
    /// This method runs asynchronously without blocking the message stream.
    /// Errors are logged but do not affect stream processing.
    /// </remarks>
    private async Task PersistSessionEndAsync(
        string sessionId,
        RunStatus status,
        CancellationToken ct)
    {
        if (_sessionRepository == null)
        {
            return; // Persistence not enabled
        }

        try
        {
            // Get existing session to update it
            var session = await _sessionRepository.GetByIdAsync(sessionId, ct);
            if (session == null)
            {
                _logger.LogWarning("Session {SessionId} not found for end persistence", sessionId);
                return;
            }

            // Update session with end information
            var updatedSession = session with
            {
                EndTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Status = status == RunStatus.Success ? "Completed" : "Failed"
            };

            await _sessionRepository.UpdateAsync(updatedSession, ct);
            _logger.LogDebug("Persisted session end for {SessionId} with status {Status}", sessionId, updatedSession.Status);
        }
        catch (OperationCanceledException)
        {
            // Cancellation is expected, don't log as error
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist session end for {SessionId}", sessionId);
            // DON'T throw - persistence failures shouldn't break the stream
        }
    }

    #endregion
}
