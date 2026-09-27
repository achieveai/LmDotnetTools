using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using AchieveAi.LmDotnetTools.LmWorkflow.Persistence;
using AchieveAi.LmDotnetTools.LmWorkflow.Runtime;
using CodeReviewDaemon.Sample.Agents;

namespace CodeReviewDaemon.Sample.Orchestration;

/// <summary>Trusted host envelope. Model arguments cannot choose the hosted thread or run identity.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record WorkflowPublicationRequest(
    string ThreadId,
    string RunId,
    string ToolCallId,
    string ToolName,
    JsonObject Args
);

/// <summary>Authenticates the existing host callback and resolves its active, run-scoped publication capability.</summary>
internal sealed class WorkflowPublicationGateway(
    string sharedSecret,
    Func<WorkflowPublicationRequest, CancellationToken, Task<ReviewPublicationTools?>> resolve
)
{
    public bool Authenticate(string authorization)
    {
        if (string.IsNullOrWhiteSpace(sharedSecret) || !authorization.StartsWith("Bearer ", StringComparison.Ordinal))
        {
            return false;
        }
        return CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(sharedSecret)),
            SHA256.HashData(Encoding.UTF8.GetBytes(authorization[7..]))
        );
    }

    public async Task<JsonObject> InvokeAsync(WorkflowPublicationRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ThreadId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RunId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ToolCallId);
        var scope =
            await resolve(request, ct).ConfigureAwait(false)
            ?? throw new UnauthorizedAccessException(
                "No active publication capability matches this hosted invocation."
            );
        var actionId = RequiredString(request.Args, "actionId", 200);
        var body = RequiredString(request.Args, "body", 65536);
        return request.ToolName switch
        {
            "review_publish_summary" => await scope.PublishSummaryAsync(actionId, body, ct).ConfigureAwait(false),
            "review_publish_inline" => await scope
                .PublishInlineAsync(
                    actionId,
                    body,
                    RequiredString(request.Args, "path", 4096),
                    request.Args["line"]?.GetValue<int>() ?? throw new ArgumentException("line is required."),
                    RequiredString(request.Args, "side", 10) switch
                    {
                        "LEFT" => ReviewCommentSide.Left,
                        "RIGHT" => ReviewCommentSide.Right,
                        _ => throw new ArgumentException("side must be LEFT or RIGHT."),
                    },
                    ct
                )
                .ConfigureAwait(false),
            "review_reply" => await scope
                .ReplyAsync(
                    actionId,
                    body,
                    RequiredString(request.Args, "providerThreadId", 100),
                    RequiredString(request.Args, "parentCommentId", 100),
                    ct
                )
                .ConfigureAwait(false),
            _ => throw new ArgumentException("Unsupported publication tool."),
        };
    }

    private static string RequiredString(JsonObject args, string name, int maximumLength)
    {
        if (
            args[name] is not JsonValue value
            || !value.TryGetValue<string>(out var text)
            || string.IsNullOrWhiteSpace(text)
            || text.Length > maximumLength
        )
        {
            throw new ArgumentException($"Invalid publication argument '{name}'.");
        }
        return text;
    }
}

/// <summary>Ephemeral lookup only; snapshots and the hosted accepted-input record authorize every call.</summary>
internal sealed class WorkflowPublicationScopes(IWorkflowStore snapshots, LmStreamingS2SClient client)
{
    private readonly ConcurrentDictionary<string, Scope> _active = new(StringComparer.Ordinal);

    private sealed class Scope(WorkflowInvocation invocation, ReviewPublicationTools tools)
    {
        public WorkflowInvocation Invocation { get; } = invocation;
        public ReviewPublicationTools Tools { get; } = tools;
    }

    private sealed class RegistrationLease(Action release) : IDisposable
    {
        public void Dispose() => release();
    }

    internal int ActiveCount => _active.Count;

    public IDisposable Register(string threadId, WorkflowInvocation invocation, ReviewPublicationTools tools)
    {
        tools.LimitToDeadline(invocation.DeadlineUtc);
        // Expired grants cannot authorize writes. Reconciliation reconstructs its read-only tools from
        // durable session/receipt state, so these objects need not stay reachable until daemon restart.
        foreach (var stale in _active)
            if (stale.Value.Invocation.DeadlineUtc is not { } deadline || deadline <= DateTimeOffset.UtcNow)
                Remove(stale.Key, stale.Value);
        var scope = new Scope(invocation, tools);
        _active[threadId] = scope;
        return new RegistrationLease(() => Remove(threadId, scope));
    }

    private void Remove(string threadId, Scope scope) =>
        _ = ((ICollection<KeyValuePair<string, Scope>>)_active).Remove(new(threadId, scope));

    public async Task<ReviewPublicationTools?> ResolveAsync(WorkflowPublicationRequest request, CancellationToken ct)
    {
        if (
            !_active.TryGetValue(request.ThreadId, out var scope)
            || scope.Invocation.IsCorrection
            || scope.Invocation.Task.Tools?.Contains(request.ToolName, StringComparer.Ordinal) != true
        )
        {
            return null;
        }
        var invocation = scope.Invocation;
        var snapshot = await snapshots.LoadAsync(invocation.InstanceId, ct).ConfigureAwait(false);
        if (
            snapshot is null
            || snapshot.IsComplete
            || invocation.DeadlineUtc is not { } deadline
            || deadline <= DateTimeOffset.UtcNow
            || !snapshot.Deadlines.TryGetValue(invocation.UnitName, out var savedDeadline)
            || savedDeadline != deadline
            || !snapshot.Tasks.Any(t =>
                t.Name == invocation.UnitName
                && t.ToolCallId == invocation.InvocationId
                && t.Status == WorkflowTaskStatus.InFlight
            )
        )
        {
            return null;
        }
        var inputId = IdempotentInputId.Create(WorkflowAgentInvoker.InvocationKey(invocation), false, false);
        var hosted = await client.GetStatusByInputIdAsync(request.ThreadId, inputId, ct).ConfigureAwait(false);
        return
            _active.TryGetValue(request.ThreadId, out var current)
            && ReferenceEquals(current, scope)
            && deadline > DateTimeOffset.UtcNow
            && string.Equals(hosted.RunId, request.RunId, StringComparison.Ordinal)
            && string.Equals(hosted.Status, "InProgress", StringComparison.OrdinalIgnoreCase)
            ? scope.Tools
            : null;
    }
}
