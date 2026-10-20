using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;

namespace AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;

/// <summary>
/// Remembers which conversation started each background task, so the gateway's <c>task_completed</c> for a
/// primary agent (which sends no agent id) can be told to the right conversation
/// (<see cref="SandboxTaskCompletionNotifier"/>).
/// </summary>
internal static class SandboxBackgroundTaskTracker
{
    /// <summary>
    /// The gateway's result for a <c>run_in_background</c> <c>Bash</c>/<c>PowerShell</c> call
    /// (<c>tools/command.rs</c>); the gateway parses the same prefix itself.
    /// </summary>
    internal const string TaskStartedPrefix = "Task started with ID: ";

    /// <summary>
    /// Decorates a sandbox tool handler of conversation <paramref name="threadId"/> on
    /// <paramref name="sessionId"/>: when its primary agent starts a background task, the task id is recorded
    /// against the conversation. Sub-agent calls are skipped, since their gateway agent id already routes them.
    /// The result passes through unchanged.
    /// </summary>
    internal static ToolHandler Wrap(
        ToolHandler inner,
        SandboxSessionRegistry registry,
        string sessionId,
        string threadId
    )
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(registry);

        return async (argsJson, context, cancellationToken) =>
        {
            var result = await inner(argsJson, context, cancellationToken).ConfigureAwait(false);
            if (
                result is ToolHandlerResult.Resolved resolved
                && TryReadTaskId(resolved.Payload.Text, out var taskId)
                && SandboxSubAgentIdentity.ForCurrentFlow(threadId) is null
            )
            {
                registry.RecordBackgroundTaskOwner(sessionId, taskId, threadId);
            }

            return result;
        };
    }

    internal static bool TryReadTaskId(string? toolResultText, out string taskId)
    {
        taskId = string.Empty;
        if (toolResultText is null || !toolResultText.StartsWith(TaskStartedPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var id = toolResultText[TaskStartedPrefix.Length..].Trim();
        if (id.Length == 0 || !id.All(char.IsAsciiDigit))
        {
            return false;
        }

        taskId = id;
        return true;
    }
}
