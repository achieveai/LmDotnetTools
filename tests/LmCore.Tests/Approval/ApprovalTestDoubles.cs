using AchieveAi.LmDotnetTools.LmCore.Approval;

namespace AchieveAi.LmDotnetTools.LmCore.Tests.Approval;

/// <summary>An approver whose answer the test supplies, recording every call it receives.</summary>
internal sealed class RecordingGate : IToolApprovalGate
{
    private readonly Func<ToolApprovalContext, CancellationToken, Task<ToolApprovalVerdict>> _decide;
    private int _callCount;

    public RecordingGate(Func<ToolApprovalContext, CancellationToken, Task<ToolApprovalVerdict>> decide) =>
        _decide = decide;

    public static RecordingGate Allowing() =>
        new((_, _) => Task.FromResult(ToolApprovalVerdict.Allow()));

    public static RecordingGate Denying(string? reason = null) =>
        new((_, _) => Task.FromResult(ToolApprovalVerdict.Deny(reason)));

    public static RecordingGate Throwing(Exception exception) =>
        new((_, _) => throw exception);

    /// <summary>An approver that never answers until the caller's token says otherwise.</summary>
    public static RecordingGate Hanging() =>
        new(
            async (_, ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return ToolApprovalVerdict.Allow();
            }
        );

    public int CallCount => Volatile.Read(ref _callCount);

    public List<ToolApprovalContext> Seen { get; } = [];

    public ValueTask<ToolApprovalVerdict> RequestApprovalAsync(
        ToolApprovalContext context,
        CancellationToken cancellationToken
    )
    {
        _ = Interlocked.Increment(ref _callCount);
        lock (Seen)
        {
            Seen.Add(context);
        }

        return new ValueTask<ToolApprovalVerdict>(_decide(context, cancellationToken));
    }
}

/// <summary>A policy whose answer the test supplies, recording whether it was consulted.</summary>
internal sealed class RecordingPolicy : IToolExecutionPolicy
{
    private readonly Func<ToolApprovalContext, ToolApprovalVerdict> _decide;
    private int _callCount;

    public RecordingPolicy(Func<ToolApprovalContext, ToolApprovalVerdict> decide) => _decide = decide;

    public static RecordingPolicy Allowing() => new(_ => ToolApprovalVerdict.Allow());

    public static RecordingPolicy Denying(string? reason = null) => new(_ => ToolApprovalVerdict.Deny(reason));

    public int CallCount => Volatile.Read(ref _callCount);

    public ValueTask<ToolApprovalVerdict> EvaluateAsync(
        ToolApprovalContext context,
        CancellationToken cancellationToken
    )
    {
        _ = Interlocked.Increment(ref _callCount);
        return new ValueTask<ToolApprovalVerdict>(_decide(context));
    }
}

/// <summary>A policy that throws, to prove a failing hook blocks rather than falls through.</summary>
internal sealed class ThrowingPolicy : IToolExecutionPolicy
{
    private readonly Exception _exception;

    public ThrowingPolicy(Exception exception) => _exception = exception;

    public ValueTask<ToolApprovalVerdict> EvaluateAsync(
        ToolApprovalContext context,
        CancellationToken cancellationToken
    ) => throw _exception;
}

/// <summary>Records the callback traffic the executor produces for a tool call.</summary>
internal sealed class RecordingResultCallback : IToolResultCallback
{
    public List<string> Started { get; } = [];

    public List<(string ToolCallId, string Error)> Errors { get; } = [];

    public List<ToolCallResult> Results { get; } = [];

    public Task OnToolResultAvailableAsync(
        string toolCallId,
        ToolCallResult result,
        CancellationToken cancellationToken = default
    )
    {
        lock (Results)
        {
            Results.Add(result);
        }

        return Task.CompletedTask;
    }

    public Task OnToolCallStartedAsync(
        string toolCallId,
        string functionName,
        string functionArgs,
        CancellationToken cancellationToken = default
    )
    {
        lock (Started)
        {
            Started.Add(toolCallId);
        }

        return Task.CompletedTask;
    }

    public Task OnToolCallErrorAsync(
        string toolCallId,
        string functionName,
        string error,
        CancellationToken cancellationToken = default
    )
    {
        lock (Errors)
        {
            Errors.Add((toolCallId, error));
        }

        return Task.CompletedTask;
    }
}
