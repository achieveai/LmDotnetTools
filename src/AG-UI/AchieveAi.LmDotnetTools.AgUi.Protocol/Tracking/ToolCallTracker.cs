using System.Collections.Concurrent;

namespace AchieveAi.LmDotnetTools.AgUi.Protocol.Tracking;

/// <summary>
/// Default implementation of IToolCallTracker
/// </summary>
public class ToolCallTracker : IToolCallTracker
{
    private readonly ConcurrentDictionary<string, string> _toolCallIdMap = new();
    private readonly ConcurrentDictionary<string, ToolCallState> _toolCallStates = new();

    /// <inheritdoc/>
    public string GetOrCreateToolCallId(string? lmCoreToolCallId)
    {
        return string.IsNullOrEmpty(lmCoreToolCallId)
            ? Guid.NewGuid().ToString()
            : _toolCallIdMap.GetOrAdd(lmCoreToolCallId, _ => Guid.NewGuid().ToString());
    }

    /// <inheritdoc/>
    public string GetToolCallId(string? lmCoreToolCallId)
    {
        return string.IsNullOrEmpty(lmCoreToolCallId)
            ? Guid.NewGuid().ToString()
            : _toolCallIdMap.TryGetValue(lmCoreToolCallId, out var agUiId) ? agUiId : lmCoreToolCallId;
    }

    /// <inheritdoc/>
    public void StartToolCall(string toolCallId, string toolName)
    {
        _toolCallStates[toolCallId] = new ToolCallState
        {
            ToolCallId = toolCallId,
            ToolName = toolName,
            StartTime = DateTime.UtcNow,
        };
    }

    /// <inheritdoc/>
    public TimeSpan EndToolCall(string toolCallId)
    {
        if (_toolCallStates.TryGetValue(toolCallId, out var state))
        {
            var duration = DateTime.UtcNow - state.StartTime;
            _ = _toolCallStates.TryRemove(toolCallId, out _);
            return duration;
        }

        return TimeSpan.Zero;
    }

    private class ToolCallState
    {
        public string ToolCallId { get; init; } = string.Empty;
        public string ToolName { get; init; } = string.Empty;
        public DateTime StartTime { get; init; }
    }
}
