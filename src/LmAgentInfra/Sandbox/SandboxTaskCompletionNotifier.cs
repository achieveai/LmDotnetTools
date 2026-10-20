using System.Diagnostics;
using System.Globalization;
using System.Text;
using AchieveAi.LmDotnetTools.LmAgentInfra.Agents;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmMultiTurn;

namespace AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;

/// <summary>
/// A background <c>Bash</c>/<c>PowerShell</c> task that ended in a sandbox: the <c>task_completed</c> event of
/// the unified sandbox webhook (gateway ADR 0043). <see cref="AgentId"/> is the agent that started it:
/// <c>""</c> (or null) for the default agent, which is every conversation's primary agent.
/// </summary>
public sealed record SandboxTaskCompletion(
    string SessionId,
    string? AgentId,
    string TaskId,
    string Status,
    int? ExitCode,
    long? DurationMs,
    string? StdoutTail,
    string? StderrTail,
    bool OutputTruncated
);

/// <summary>
/// Tells the agent that started a background task that it ended, as a <see cref="NotifyMessage"/> of kind
/// <see cref="NotifyKind"/>, so the model need not poll <c>TaskOutput</c>.
/// </summary>
/// <remarks>
/// <para>
/// Routing: a sub-agent's gateway id carries its conversation's tag
/// (<see cref="SandboxSubAgentIdentity"/>), so its notice goes exactly to that sub-agent, and is dropped
/// when it is no longer running. The primary agent presents no id, so every conversation on the workspace
/// shares the gateway's default bucket and the event says only <c>""</c>: the conversation is the one
/// <see cref="SandboxBackgroundTaskTracker"/> recorded when its <c>Bash</c> call returned the task id. With no
/// record yet, a session with several conversations waits up to <see cref="OwnerRecordWait"/> for it: the
/// gateway sends the event the moment the task ends, so a fast task's event can beat its own tool result to
/// the tracker. With no record at all (a task started before a restart), the notice goes to the session's only
/// conversation, and is dropped when there are several rather than telling one about a task it never started.
/// </para>
/// <para>Best effort: every failure is logged and swallowed, like context discovery.</para>
/// </remarks>
public sealed class SandboxTaskCompletionNotifier(
    SandboxSessionRegistry registry,
    MultiTurnAgentPool pool,
    ILogger<SandboxTaskCompletionNotifier> logger
)
{
    /// <summary>The <see cref="NotifyMessage.NotifyKind"/> of a background-task notice.</summary>
    public const string NotifyKind = NotifyKinds.BackgroundTask;

    internal const string InputIdPrefix = "bgtask-";

    private static readonly TimeSpan OwnerPollInterval = TimeSpan.FromMilliseconds(25);

    /// <summary>
    /// How long a primary task's notice waits for <see cref="SandboxBackgroundTaskTracker"/> to record which
    /// conversation started it, when the session has several.
    /// </summary>
    public TimeSpan OwnerRecordWait { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Delivers the notice; returns the number of conversations it reached (0 or 1).</summary>
    public async Task<int> NotifyAsync(SandboxTaskCompletion completion, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(completion);
        var message = BuildMessage(completion);
        try
        {
            return string.IsNullOrEmpty(completion.AgentId)
                ? await NotifyPrimaryAsync(completion, message, ct).ConfigureAwait(false)
                : await NotifySubAgentAsync(completion, message, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Background task {TaskId} of session {SessionId}: delivering the completion notice failed.",
                completion.TaskId,
                completion.SessionId
            );
            return 0;
        }
    }

    private async Task<int> NotifyPrimaryAsync(
        SandboxTaskCompletion completion,
        NotifyMessage message,
        CancellationToken ct
    )
    {
        string threadId;
        if (registry.TryTakeBackgroundTaskOwner(completion.SessionId, completion.TaskId, out var owner))
        {
            threadId = owner;
        }
        else
        {
            // No recorded owner: the task's tool result has not reached the tracker yet, or it started before a
            // restart or on a path the tracker does not wrap. Only a session with one conversation can be
            // attributed without it.
            var threads = registry.GetThreads(completion.SessionId);
            if (threads.Count > 1 && await WaitForOwnerAsync(completion, ct).ConfigureAwait(false) is { } late)
            {
                threads = [late];
            }
            else if (threads.Count != 1)
            {
                logger.LogInformation(
                    "Background task {TaskId} of session {SessionId}: no recorded owner and {Count} conversations "
                        + "share the default agent; dropping the completion notice.",
                    completion.TaskId,
                    completion.SessionId,
                    threads.Count
                );
                return 0;
            }

            threadId = threads[0];
        }

        if (!pool.TryGet(threadId, out var agent) || agent is null)
        {
            logger.LogInformation(
                "Background task {TaskId} of session {SessionId}: conversation {ThreadId} is no longer loaded; dropping.",
                completion.TaskId,
                completion.SessionId,
                threadId
            );
            return 0;
        }

        _ = await agent
            .SendAsync([message], InputIdPrefix + Guid.NewGuid().ToString("N"), parentRunId: null, ct)
            .ConfigureAwait(false);
        logger.LogInformation(
            "Background task {TaskId} of session {SessionId}: notified conversation {ThreadId}.",
            completion.TaskId,
            completion.SessionId,
            threadId
        );
        return 1;
    }

    private async Task<string?> WaitForOwnerAsync(SandboxTaskCompletion completion, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(started) < OwnerRecordWait)
        {
            await Task.Delay(OwnerPollInterval, ct).ConfigureAwait(false);
            if (registry.TryTakeBackgroundTaskOwner(completion.SessionId, completion.TaskId, out var owner))
            {
                return owner;
            }
        }

        return null;
    }

    private async Task<int> NotifySubAgentAsync(
        SandboxTaskCompletion completion,
        NotifyMessage message,
        CancellationToken ct
    )
    {
        foreach (var threadId in registry.GetThreads(completion.SessionId))
        {
            if (
                !SandboxSubAgentIdentity.TryGetSubAgentId(completion.AgentId, threadId, out var subAgentId)
                || !pool.TryGet(threadId, out var agent)
                || agent is not ISubAgentContextSink sink
            )
            {
                continue;
            }

            // The sink addresses a sub-agent by its scoped thread id, and appends the notice to its running
            // turn without queueing input: a finished sub-agent is not restarted to hear about it.
            var result = await sink.TryDeliverContextAsync(SubAgentThreadIds.For(threadId, subAgentId), [message], ct)
                .ConfigureAwait(false);
            logger.LogInformation(
                "Background task {TaskId} of session {SessionId}: sub-agent {SubAgentId} on {ThreadId} -> {Result}.",
                completion.TaskId,
                completion.SessionId,
                subAgentId,
                threadId,
                result
            );
            return result == SubAgentContextDeliveryResult.Delivered ? 1 : 0;
        }

        logger.LogInformation(
            "Background task {TaskId} of session {SessionId}: no live conversation owns agent {AgentId}; dropping.",
            completion.TaskId,
            completion.SessionId,
            completion.AgentId
        );
        return 0;
    }

    internal static NotifyMessage BuildMessage(SandboxTaskCompletion completion)
    {
        var text = new StringBuilder();
        _ = text.Append(CultureInfo.InvariantCulture, $"Background task {completion.TaskId} has ended: ");
        _ = completion.Status switch
        {
            // The gateway reports exit_code 0 for every task that ends on its own (webhooks.md, "Known
            // limitation"), so a "completed" task's code says nothing about success.
            "completed" => text.Append(
                "it exited. The sandbox does not report the real exit code yet; judge success from the output."
            ),
            "terminated" => text.Append("it was killed."),
            _ => text.Append(
                CultureInfo.InvariantCulture,
                $"{completion.Status} (exit code {completion.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "unknown"})."
            ),
        };
        if (completion.DurationMs is { } ms)
        {
            _ = text.Append(CultureInfo.InvariantCulture, $" It ran for {ms / 1000.0:0.#} s.");
        }

        AppendTail(text, "stdout", completion.StdoutTail);
        AppendTail(text, "stderr", completion.StderrTail);
        if (completion.OutputTruncated)
        {
            _ = text.Append("\n\nThe output above is only the end; read the rest with TaskOutput.");
        }

        return NotifyMessage.Create(
            NotifyKind,
            detail: text.ToString(),
            label: $"Task {completion.TaskId} {completion.Status}"
        );
    }

    private static void AppendTail(StringBuilder text, string stream, string? tail)
    {
        if (!string.IsNullOrWhiteSpace(tail))
        {
            _ = text.Append(CultureInfo.InvariantCulture, $"\n\n{stream} (last part):\n{tail.TrimEnd()}");
        }
    }
}
