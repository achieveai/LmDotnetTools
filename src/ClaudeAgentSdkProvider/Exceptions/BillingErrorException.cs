namespace AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Exceptions;

/// <summary>
///     Exception thrown when the Claude Agent SDK CLI returns a billing error.
///     This typically means "Credit balance is too low" or similar billing issues.
///     Callers should catch this exception and recreate the agent from scratch.
/// </summary>
public class BillingErrorException : Exception
{
    /// <summary>
    ///     The error type (e.g., "billing_error")
    /// </summary>
    public string? ErrorType { get; }

    /// <summary>
    ///     The session ID that was active when the error occurred
    /// </summary>
    public string? SessionId { get; }

    public BillingErrorException(string? errorType, string? message, string? sessionId)
        : base(message ?? "Billing error occurred")
    {
        ErrorType = errorType;
        SessionId = sessionId;
    }

    public BillingErrorException(string? errorType, string? message, string? sessionId, Exception? innerException)
        : base(message ?? "Billing error occurred", innerException)
    {
        ErrorType = errorType;
        SessionId = sessionId;
    }
}
