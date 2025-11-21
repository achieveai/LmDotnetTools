using System.Collections.Immutable;
using AchieveAi.LmDotnetTools.AgUi.AspNetCore.Models;
using AchieveAi.LmDotnetTools.AgUi.AspNetCore.Services;
using AchieveAi.LmDotnetTools.AgUi.Protocol.Middleware;
using AchieveAi.LmDotnetTools.AgUi.Sample.Agents;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using Microsoft.AspNetCore.Mvc;

namespace AchieveAi.LmDotnetTools.AgUi.Sample.Controllers;

/// <summary>
/// Controller providing CopilotKit-compatible API endpoint
/// Bridges CopilotKit React frontend to AG-UI backend infrastructure
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class CopilotKitController : ControllerBase
{
    private readonly ILogger<CopilotKitController> _logger;
    private readonly ICopilotKitSessionMapper _sessionMapper;
    private readonly ToolCallingAgent _toolCallingAgent;
    private readonly InstructionChainAgent _instructionChainAgent;
    private readonly IFunctionCallMiddlewareFactory _middlewareFactory;

    public CopilotKitController(
        ILogger<CopilotKitController> logger,
        ICopilotKitSessionMapper sessionMapper,
        ToolCallingAgent toolCallingAgent,
        InstructionChainAgent instructionChainAgent,
        IFunctionCallMiddlewareFactory middlewareFactory)
    {
        _logger = logger;
        _sessionMapper = sessionMapper;
        _toolCallingAgent = toolCallingAgent;
        _instructionChainAgent = instructionChainAgent;
        _middlewareFactory = middlewareFactory;

        _logger.LogInformation("CopilotKitController initialized - using AgUiStreamingMiddleware for event publishing");
    }

    /// <summary>
    /// CopilotKit-compatible endpoint for running agents
    /// Accepts CopilotKit request format and returns WebSocket connection info
    /// </summary>
    /// <param name="request">CopilotKit request with messages and thread context</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Response with WebSocket URL and session information</returns>
    [HttpPost]
    [ProducesResponseType(typeof(CopilotKitResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(CopilotKitResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(CopilotKitResponse), StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<CopilotKitResponse>> HandleCopilotKitRequest(
        [FromBody] CopilotKitRequest request,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("CopilotKit request received - ThreadId: {ThreadId}, RunId: {RunId}, AgentName: {AgentName}",
            request.ThreadId ?? "NULL", request.RunId ?? "NULL", request.AgentName ?? "DEFAULT");

        if (!ModelState.IsValid)
        {
            _logger.LogWarning("Invalid CopilotKit request model state");
            return BadRequest(new CopilotKitResponse
            {
                Status = "error",
                ErrorMessage = "Invalid request format"
            });
        }

        try
        {
            // 1. Map threadId/runId to sessionId (create or resume)
            var sessionId = _sessionMapper.CreateOrResumeSession(request.ThreadId, request.RunId);
            var threadInfo = _sessionMapper.GetThreadInfo(sessionId);

            _logger.LogInformation("Session mapping - SessionId: {SessionId}, ThreadId: {ThreadId}, RunId: {RunId}",
                sessionId, threadInfo?.ThreadId, threadInfo?.RunId);

            // 2. Get the appropriate agent
            var agentName = request.AgentName ?? "ToolCallingAgent"; // Default agent
            var agent = GetAgent(agentName);
            if (agent == null)
            {
                _logger.LogWarning("Unknown agent requested: {AgentName}", agentName);
                return BadRequest(new CopilotKitResponse
                {
                    Status = "error",
                    ErrorMessage = $"Unknown agent: {agentName}. Available agents: ToolCallingAgent, InstructionChainAgent"
                });
            }

            // 3. Convert CopilotKit messages to LmCore format
            var messages = ConvertMessages(request.Messages);

            _logger.LogInformation("Converted {Count} CopilotKit messages to LmCore format", messages.Count);

            // 4. Execute agent in background
            // Note: AgUiStreamingMiddleware will automatically publish all events including
            // SESSION_STARTED, RUN_STARTED, TEXT_MESSAGE_*, TOOL_CALL_*, RUN_FINISHED, and errors
            _logger.LogInformation("Starting agent execution in background for session {SessionId}", sessionId);

            _ = Task.Run(async () =>
            {
                try
                {
                    await ExecuteAgentAsync(agent, messages, sessionId, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Background agent execution failed for session {SessionId}", sessionId);
                    // AgUiStreamingMiddleware handles error event publishing
                }
            }, cancellationToken);

            // 6. Build WebSocket URL
            var wsScheme = Request.IsHttps ? "wss" : "ws";
            var wsUrl = $"{wsScheme}://{Request.Host}/ag-ui/ws?sessionId={sessionId}";

            // 7. Return response
            var response = new CopilotKitResponse
            {
                SessionId = sessionId,
                ThreadId = threadInfo?.ThreadId ?? sessionId,
                RunId = threadInfo?.RunId ?? Guid.NewGuid().ToString(),
                WebSocketUrl = wsUrl,
                Status = "running",
                AgentName = agentName,
                Timestamp = DateTime.UtcNow
            };

            _logger.LogInformation("CopilotKit request processed successfully - Session: {SessionId}, WebSocket: {WsUrl}",
                sessionId, wsUrl);

            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process CopilotKit request");
            return StatusCode(500, new CopilotKitResponse
            {
                Status = "error",
                ErrorMessage = $"Internal error: {ex.Message}"
            });
        }
    }

    /// <summary>
    /// Health check endpoint for CopilotKit integration
    /// </summary>
    [HttpGet("health")]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    public ActionResult<object> Health()
    {
        _logger.LogTrace("CopilotKit health check called");
        return Ok(new
        {
            status = "healthy",
            timestamp = DateTime.UtcNow,
            protocol = "AG-UI",
            supportedAgents = new[] { "ToolCallingAgent", "InstructionChainAgent" }
        });
    }

    private IStreamingAgent? GetAgent(string agentName) => agentName switch
    {
        "ToolCallingAgent" => _toolCallingAgent,
        "InstructionChainAgent" => _instructionChainAgent,
        _ => null
    };

    private List<IMessage> ConvertMessages(List<CopilotKitMessage>? messages)
    {
        if (messages == null || messages.Count == 0)
        {
            return new List<IMessage>();
        }

        return messages.Select(msg => new TextMessage
        {
            Text = msg.Content,
            Role = msg.Role.ToLowerInvariant() switch
            {
                "user" => Role.User,
                "assistant" => Role.Assistant,
                "system" => Role.System,
                _ => Role.User
            },
            FromAgent = msg.Name ?? (msg.Role.ToLowerInvariant() == "user" ? "User" : "Assistant"),
            GenerationId = Guid.NewGuid().ToString(),
            Metadata = ImmutableDictionary<string, object>.Empty
        }).ToList<IMessage>();
    }

    private async Task ExecuteAgentAsync(
        IStreamingAgent agent,
        IEnumerable<IMessage> messages,
        string sessionId,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Executing agent for session {SessionId}", sessionId);

        var options = new GenerateReplyOptions
        {
            MaxToken = 4096,
            Temperature = 0.7f,
            SessionId = sessionId,
            ConversationId = sessionId,
            RunId = Guid.NewGuid().ToString()
        };

        try
        {
            // Get AgUiStreamingMiddleware from DI
            var agUiMiddleware = HttpContext.RequestServices.GetRequiredService<AgUiStreamingMiddleware>();

            // Create FunctionCallMiddleware
            var functionCallMiddleware = _middlewareFactory.Create();

            // CRITICAL: Wire AgUiStreamingMiddleware as callback for tool execution events
            // This enables real-time TOOL_CALL_START, TOOL_CALL_ARGS, and TOOL_CALL_RESULT events
            functionCallMiddleware.WithResultCallback(agUiMiddleware);

            // Build middleware chain in correct order:
            // 1. AgUiStreamingMiddleware intercepts and publishes events
            // 2. FunctionCallMiddleware executes tools and triggers callbacks
            var wrappedAgent = agent
                .WithMiddleware(agUiMiddleware)
                .WithMiddleware(functionCallMiddleware);

            _logger.LogInformation("Agent wrapped with AgUiStreamingMiddleware and FunctionCallMiddleware");

            // Execute agent - middleware automatically publishes ALL events:
            // - SESSION_STARTED, RUN_STARTED (on first message)
            // - TEXT_MESSAGE_START, TEXT_MESSAGE_CONTENT, TEXT_MESSAGE_END (as text streams)
            // - TOOL_CALL_START, TOOL_CALL_ARGS, TOOL_CALL_RESULT (for each tool)
            // - RUN_FINISHED (on completion)
            // - ERROR_EVENT (on failures)
            var responseStream = await wrappedAgent.GenerateReplyStreamingAsync(messages, options, cancellationToken);

            // Just consume the stream - no manual event publishing needed
            await foreach (var message in responseStream.WithCancellation(cancellationToken))
            {
                _logger.LogDebug("Agent produced message of type {MessageType} for session {SessionId}",
                    message.GetType().Name, sessionId);
                // AgUiStreamingMiddleware already published events for this message
            }

            _logger.LogInformation("Agent execution completed successfully for session {SessionId}", sessionId);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Agent execution cancelled for session {SessionId}", sessionId);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Agent execution failed for session {SessionId}", sessionId);
            // AgUiStreamingMiddleware automatically publishes ERROR_EVENT
            throw;
        }
    }
}
