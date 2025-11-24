using System.Collections.Immutable;
using AchieveAi.LmDotnetTools.AgUi.DataObjects.Enums;
using AchieveAi.LmDotnetTools.AgUi.DataObjects.Events;
using AchieveAi.LmDotnetTools.AgUi.Protocol.Converters;
using AchieveAi.LmDotnetTools.AgUi.Protocol.Publishing;
using AchieveAi.LmDotnetTools.AgUi.Sample.Agents;
using AchieveAi.LmDotnetTools.AgUi.Sample.Models;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using Microsoft.AspNetCore.Mvc;

namespace AchieveAi.LmDotnetTools.AgUi.Sample.Controllers;

/// <summary>
/// Controller for triggering agent executions
/// Provides REST API for running agents and streaming results
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class AgentController : ControllerBase
{
    private readonly ILogger<AgentController> _logger;
    private readonly IEventPublisher _eventPublisher;
    private readonly IMessageConverter _converter;
    private readonly ToolCallingAgent _toolCallingAgent;
    private readonly InstructionChainAgent _instructionChainAgent;
    private static readonly string[] value = ["ToolCallingAgent", "InstructionChainAgent"];

    public AgentController(
        ILogger<AgentController> logger,
        IEventPublisher eventPublisher,
        IMessageConverter converter,
        ToolCallingAgent toolCallingAgent,
        InstructionChainAgent instructionChainAgent
    )
    {
        _logger = logger;
        _eventPublisher = eventPublisher;
        _converter = converter;
        _toolCallingAgent = toolCallingAgent;
        _instructionChainAgent = instructionChainAgent;

        _logger.LogInformation("AgentController initialized with agents: ToolCallingAgent, InstructionChainAgent");
    }

    /// <summary>
    /// Run an agent with a user message
    /// </summary>
    /// <param name="request">Agent run request</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Agent run response with session information</returns>
    [HttpPost("run")]
    [ProducesResponseType(typeof(RunAgentResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(RunAgentResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(RunAgentResponse), StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<RunAgentResponse>> RunAgent(
        [FromBody] RunAgentRequest request,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(request);

        _logger.LogInformation(
            "RunAgent called with agent: {AgentName}, message: {Message}, stream: {Stream}",
            request.AgentName,
            request.Message,
            request.Stream
        );

        if (!ModelState.IsValid)
        {
            _logger.LogWarning("Invalid request model state");
            return BadRequest(
                new RunAgentResponse
                {
                    Status = "error",
                    ErrorMessage = "Invalid request",
                    AgentName = request.AgentName,
                }
            );
        }

        try
        {
            // Get the appropriate agent
            var agent = GetAgent(request.AgentName);
            if (agent == null)
            {
                _logger.LogWarning("Unknown agent requested: {AgentName}", request.AgentName);
                return BadRequest(
                    new RunAgentResponse
                    {
                        Status = "error",
                        ErrorMessage =
                            $"Unknown agent: {request.AgentName}. Available agents: ToolCallingAgent, InstructionChainAgent",
                        AgentName = request.AgentName,
                    }
                );
            }

            // Create or use existing session
            _logger.LogInformation(
                "[DEBUG] Received conversationId from request: {ConversationId}",
                request.ConversationId ?? "NULL"
            );
            var sessionId = request.ConversationId ?? Guid.NewGuid().ToString();
            _logger.LogInformation(
                "[DEBUG] Using session ID: {SessionId} for agent: {AgentName} (Generated new: {Generated})",
                sessionId,
                request.AgentName,
                request.ConversationId == null
            );

            // Create user message
            var userMessage = new TextMessage
            {
                Text = request.Message,
                Role = Role.User,
                FromAgent = "User",
                GenerationId = Guid.NewGuid().ToString(),
                Metadata = ImmutableDictionary<string, object>.Empty,
            };

            var messages = new List<IMessage> { userMessage };

            // NOTE: SESSION_STARTED is sent by the WebSocket handler when the connection is established
            // We don't need to send it here as it would be redundant

            // Publish run started event
            await _eventPublisher.PublishAsync(
                new RunStartedEvent
                {
                    SessionId = sessionId,
                    RunId = Guid.NewGuid().ToString(),
                    StartedAt = DateTime.UtcNow,
                },
                cancellationToken
            );

            _logger.LogInformation("Starting agent execution in background for session {SessionId}", sessionId);

            // Execute agent in background
            _ = Task.Run(
                async () =>
                {
                    try
                    {
                        await ExecuteAgentAsync(agent, messages, sessionId, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Background agent execution failed for session {SessionId}", sessionId);
                        await _eventPublisher.PublishAsync(
                            new ErrorEvent
                            {
                                SessionId = sessionId,
                                ErrorCode = "AGENT_EXECUTION_ERROR",
                                Message = ex.Message,
                                Recoverable = false,
                            },
                            cancellationToken
                        );
                    }
                },
                cancellationToken
            );

            // Build WebSocket URL
            var wsScheme = Request.IsHttps ? "wss" : "ws";
            var wsUrl = $"{wsScheme}://{Request.Host}/ag-ui/ws?sessionId={sessionId}";

            var response = new RunAgentResponse
            {
                SessionId = sessionId,
                AgentName = request.AgentName,
                Status = "running",
                WebSocketUrl = wsUrl,
                Timestamp = DateTime.UtcNow,
            };

            _logger.LogInformation("Agent execution started successfully for session {SessionId}", sessionId);

            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to run agent {AgentName}", request.AgentName);
            return StatusCode(
                500,
                new RunAgentResponse
                {
                    Status = "error",
                    ErrorMessage = $"Internal error: {ex.Message}",
                    AgentName = request.AgentName,
                }
            );
        }
    }

    /// <summary>
    /// Get available agents
    /// </summary>
    [HttpGet("list")]
    [ProducesResponseType(typeof(string[]), StatusCodes.Status200OK)]
    public ActionResult<string[]> ListAgents()
    {
        _logger.LogDebug("ListAgents called");
        var agents = new[] { "ToolCallingAgent", "InstructionChainAgent" };
        return Ok(agents);
    }

    /// <summary>
    /// Health check endpoint
    /// </summary>
    [HttpGet("health")]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    public ActionResult<object> Health()
    {
        _logger.LogTrace("Health check called");
        return Ok(
            new
            {
                status = "healthy",
                timestamp = DateTime.UtcNow,
                agents = value,
            }
        );
    }

    private IStreamingAgent? GetAgent(string agentName)
    {
        return agentName switch
        {
            "ToolCallingAgent" => _toolCallingAgent,
            "InstructionChainAgent" => _instructionChainAgent,
            _ => null,
        };
    }

    private async Task ExecuteAgentAsync(
        IStreamingAgent agent,
        IEnumerable<IMessage> messages,
        string sessionId,
        CancellationToken cancellationToken
    )
    {
        _logger.LogInformation("Executing agent for session {SessionId}", sessionId);

        var options = new GenerateReplyOptions { MaxToken = 4096, Temperature = 0.7f };

        try
        {
            var responseStream = await agent.GenerateReplyStreamingAsync(messages, options, cancellationToken);

            await foreach (var message in responseStream.WithCancellation(cancellationToken))
            {
                _logger.LogDebug(
                    "Agent produced message of type {MessageType} for session {SessionId}",
                    message.GetType().Name,
                    sessionId
                );

                // Convert LmCore message to AG-UI events
                var events = _converter.ConvertToAgUiEvents(message, sessionId);

                foreach (var evt in events)
                {
                    _logger.LogDebug("Publishing event {EventType} for session {SessionId}", evt.Type, sessionId);
                    await _eventPublisher.PublishAsync(evt, cancellationToken);
                }
            }

            // Publish run finished event
            await _eventPublisher.PublishAsync(
                new RunFinishedEvent
                {
                    SessionId = sessionId,
                    RunId = sessionId, // In real app, track separate run ID
                    FinishedAt = DateTime.UtcNow,
                    Status = RunStatus.Success,
                },
                cancellationToken
            );

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
            throw;
        }
    }
}
