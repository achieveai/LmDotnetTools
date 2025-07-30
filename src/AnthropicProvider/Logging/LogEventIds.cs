using Microsoft.Extensions.Logging;

namespace AchieveAi.LmDotnetTools.AnthropicProvider.Logging;

/// <summary>
/// Defines event IDs for structured logging in the Anthropic provider.
/// </summary>
public static class LogEventIds
{
    // Agent Events (1000-1999)
    public static readonly EventId AgentRequestInitiated = new(1001, "AgentRequestInitiated");
    public static readonly EventId AgentRequestCompleted = new(1002, "AgentRequestCompleted");
    public static readonly EventId AgentRequestFailed = new(1003, "AgentRequestFailed");
    public static readonly EventId AgentStreamingCompleted = new(1004, "AgentStreamingCompleted");
    
    // Internal Processing Events (2000-2999)
    public static readonly EventId RequestConversion = new(2001, "RequestConversion");
    public static readonly EventId MessageProcessing = new(2002, "MessageProcessing");
    public static readonly EventId StreamingEventProcessed = new(2003, "StreamingEventProcessed");
    public static readonly EventId MessageTransformation = new(2004, "MessageTransformation");
    
    // Error Events (3000-3999)
    public static readonly EventId ApiCallFailed = new(3001, "ApiCallFailed");
    public static readonly EventId StreamingError = new(3002, "StreamingError");
    public static readonly EventId ParserFailure = new(3003, "ParserFailure");
    public static readonly EventId ClientDisposalError = new(3004, "ClientDisposalError");
}