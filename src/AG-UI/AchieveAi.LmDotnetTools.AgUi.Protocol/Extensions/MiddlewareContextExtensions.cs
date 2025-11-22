using System.Collections.Concurrent;
using AchieveAi.LmDotnetTools.LmCore.Middleware;

namespace AchieveAi.LmDotnetTools.AgUi.Protocol.Extensions;

/// <summary>
/// Extension methods for MiddlewareContext to support AG-UI session management.
/// </summary>
/// <remarks>
/// Since MiddlewareContext is a simple record struct without a properties dictionary,
/// we use a thread-safe static dictionary to store session state. This approach ensures
/// that session IDs can be maintained across middleware invocations within the same conversation.
///
/// IMPORTANT: This implementation uses conversation ID as the key. If conversation ID is not
/// available, a fallback mechanism generates a session ID per invocation.
/// </remarks>
public static class MiddlewareContextExtensions
{
    private static readonly ConcurrentDictionary<string, string> _sessionIds = new();
    private static readonly ConcurrentDictionary<string, string> _runIds = new();

    /// <summary>
    /// Gets the existing AG-UI session ID from the context, or creates a new one if it doesn't exist.
    /// The session ID is maintained across multiple invocations within the same conversation.
    /// </summary>
    /// <param name="context">The middleware context</param>
    /// <param name="conversationId">
    /// Optional conversation ID to use as the key for session tracking.
    /// If not provided, a new session ID is generated for each invocation.
    /// </param>
    /// <returns>The session ID for this conversation</returns>
    /// <remarks>
    /// This method ensures that the same session ID is used throughout the entire conversation
    /// when a conversation ID is provided. This is critical for:
    /// - Session state management
    /// - Conversation history tracking
    /// - Tool call correlation
    /// - Event aggregation
    ///
    /// If conversation ID is not available, each invocation gets a unique session ID.
    /// </remarks>
    public static string GetOrCreateSessionId(this MiddlewareContext context, string? conversationId = null)
    {
        if (string.IsNullOrEmpty(conversationId))
        {
            // No conversation ID - generate a new session ID
            // This is a fallback for cases where conversation tracking isn't available
            return Guid.NewGuid().ToString();
        }

        return _sessionIds.GetOrAdd(conversationId, conversationId);
    }

    /// <summary>
    /// Gets the existing AG-UI run ID from the context, or creates a new one if it doesn't exist.
    /// A run ID represents a single agent invocation within a session.
    /// </summary>
    /// <param name="context">The middleware context</param>
    /// <param name="conversationId">
    /// Optional conversation ID to use as the key.
    /// If not provided, a new run ID is generated for each invocation.
    /// </param>
    /// <returns>The run ID for this agent invocation</returns>
    /// <remarks>
    /// Run IDs are scoped to a single agent invocation. They are used to:
    /// - Track individual requests within a session
    /// - Correlate run-started and run-finished events
    /// - Measure execution time for a single invocation
    ///
    /// NOTE: Run IDs should be cleared after each invocation to ensure
    /// a fresh ID for the next request.
    /// </remarks>
    public static string GetOrCreateRunId(this MiddlewareContext context, string? conversationId = null)
    {
        if (string.IsNullOrEmpty(conversationId))
        {
            // No conversation ID - generate a new run ID
            return Guid.NewGuid().ToString();
        }

        return _runIds.GetOrAdd(conversationId, _ => Guid.NewGuid().ToString());
    }

    /// <summary>
    /// Clears the AG-UI session and run IDs for a given conversation.
    /// This should be called when starting a completely new conversation.
    /// </summary>
    /// <param name="context">The middleware context</param>
    /// <param name="conversationId">The conversation ID to clear</param>
    /// <remarks>
    /// Use this method with caution. In most cases, you want to preserve the session ID
    /// across multiple invocations. Only clear when explicitly starting a new conversation.
    /// </remarks>
    public static void ClearAgUiSession(this MiddlewareContext context, string conversationId)
    {
        if (!string.IsNullOrEmpty(conversationId))
        {
            _ = _sessionIds.TryRemove(conversationId, out _);
            _ = _runIds.TryRemove(conversationId, out _);
        }
    }

    /// <summary>
    /// Checks if the context has an existing AG-UI session ID for a given conversation.
    /// </summary>
    /// <param name="context">The middleware context</param>
    /// <param name="conversationId">The conversation ID to check</param>
    /// <returns>True if a session ID exists, false otherwise</returns>
    public static bool HasAgUiSession(this MiddlewareContext context, string? conversationId)
    {
        if (string.IsNullOrEmpty(conversationId))
        {
            return false;
        }

        return _sessionIds.ContainsKey(conversationId);
    }

    /// <summary>
    /// Clears only the run ID for a given conversation, preserving the session ID.
    /// This should be called after each invocation to ensure a fresh run ID for the next request.
    /// </summary>
    /// <param name="context">The middleware context</param>
    /// <param name="conversationId">The conversation ID</param>
    public static void ClearRunId(this MiddlewareContext context, string? conversationId)
    {
        if (!string.IsNullOrEmpty(conversationId))
        {
            _ = _runIds.TryRemove(conversationId, out _);
        }
    }

    /// <summary>
    /// Clears all stored session and run IDs. This is useful for cleanup or testing scenarios.
    /// </summary>
    public static void ClearAll()
    {
        _sessionIds.Clear();
        _runIds.Clear();
    }
}
