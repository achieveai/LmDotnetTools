using System.ComponentModel.DataAnnotations;

namespace AchieveAi.LmDotnetTools.AgUi.Sample.Models;

/// <summary>
/// Request model for running an agent
/// </summary>
public class RunAgentRequest
{
    /// <summary>
    /// Name of the agent to run (EchoAgent, ToolCallingAgent, MultiStepAgent)
    /// </summary>
    [Required]
    public string AgentName { get; set; } = string.Empty;

    /// <summary>
    /// User message to send to the agent
    /// </summary>
    [Required]
    [MinLength(1)]
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Optional conversation/session ID for continuing a conversation
    /// If not provided, a new session will be created
    /// </summary>
    public string? ConversationId { get; set; }

    /// <summary>
    /// Whether to stream the response (default: true)
    /// </summary>
    public bool Stream { get; set; } = true;
}
