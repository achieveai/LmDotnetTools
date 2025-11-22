using System.Collections.Concurrent;

namespace AchieveAi.LmDotnetTools.AgUi.AspNetCore.Services;

/// <summary>
/// In-memory implementation of session mapping between CopilotKit and AG-UI
/// Thread-safe for concurrent access
/// </summary>
public sealed class CopilotKitSessionMapper : ICopilotKitSessionMapper
{
    private readonly ConcurrentDictionary<string, SessionMapping> _sessionToThreadMap = new();
    private readonly ConcurrentDictionary<string, string> _threadToSessionMap = new();

    /// <inheritdoc/>
    public string CreateOrResumeSession(string? threadId, string? runId)
    {
        // If threadId is provided, try to resume existing session
        if (!string.IsNullOrWhiteSpace(threadId))
        {
            if (_threadToSessionMap.TryGetValue(threadId, out var existingSessionId))
            {
                // Resume existing session, update runId if provided
                if (!string.IsNullOrWhiteSpace(runId))
                {
                    UpdateRunId(existingSessionId, runId);
                }
                return existingSessionId;
            }
        }

        // Create new session
        var sessionId = Guid.NewGuid().ToString();
        var mapping = new SessionMapping
        {
            SessionId = sessionId,
            ThreadId = threadId ?? sessionId,
            RunId = runId ?? Guid.NewGuid().ToString(),
            CreatedAt = DateTime.UtcNow,
        };

        _sessionToThreadMap[sessionId] = mapping;
        if (!string.IsNullOrWhiteSpace(threadId))
        {
            _threadToSessionMap[threadId] = sessionId;
        }

        return sessionId;
    }

    /// <inheritdoc/>
    public (string? ThreadId, string? RunId)? GetThreadInfo(string sessionId)
    {
        if (_sessionToThreadMap.TryGetValue(sessionId, out var mapping))
        {
            return (mapping.ThreadId, mapping.RunId);
        }
        return null;
    }

    /// <inheritdoc/>
    public void UpdateRunId(string sessionId, string runId)
    {
        if (_sessionToThreadMap.TryGetValue(sessionId, out var mapping))
        {
            var updatedMapping = mapping with { RunId = runId };
            _sessionToThreadMap[sessionId] = updatedMapping;
        }
    }

    /// <inheritdoc/>
    public bool RemoveSession(string sessionId)
    {
        if (_sessionToThreadMap.TryRemove(sessionId, out var mapping))
        {
            // Also remove from thread-to-session map if threadId exists
            if (!string.IsNullOrWhiteSpace(mapping.ThreadId))
            {
                _ = _threadToSessionMap.TryRemove(mapping.ThreadId, out _);
            }
            return true;
        }
        return false;
    }

    /// <inheritdoc/>
    public string? GetSessionByThread(string threadId)
    {
        return _threadToSessionMap.TryGetValue(threadId, out var sessionId) ? sessionId : null;
    }

    /// <summary>
    /// Internal record for storing session mapping data
    /// </summary>
    private sealed record SessionMapping
    {
        public required string SessionId { get; init; }
        public required string ThreadId { get; init; }
        public required string RunId { get; init; }
        public DateTime CreatedAt { get; init; }
    }
}
