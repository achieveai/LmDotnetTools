using AchieveAi.LmDotnetTools.LmCore.Approval;

namespace AchieveAi.LmDotnetTools.LmCore.Tests.Approval;

/// <summary>
/// Pins how <see cref="ToolCallExecutor"/> behaves once a preparer is wired in: refused calls never
/// reach a handler, results still come back in the caller's order, and a batch's approvals are
/// settled concurrently rather than one wait after another.
/// </summary>
public class ToolCallExecutorApprovalTests
{
    private static ToolsCallMessage Message(params string[] toolCallIds) =>
        new()
        {
            ToolCalls =
            [
                .. toolCallIds.Select(
                    id => new ToolCall
                    {
                        FunctionName = "echo",
                        FunctionArgs = $$"""{"id":"{{id}}"}""",
                        ToolCallId = id,
                        ExecutionTarget = ExecutionTarget.LocalFunction,
                    }
                ),
            ],
            Role = Role.Assistant,
            ThreadId = "thread_1",
            RunId = "run_1",
            GenerationId = "gen_1",
        };

    private static Dictionary<string, ToolCallResultHandler> EchoMap(List<string>? executionOrder = null) =>
        new()
        {
            ["echo"] = (argsJson, ctx, _) =>
            {
                if (executionOrder != null)
                {
                    lock (executionOrder)
                    {
                        executionOrder.Add(ctx.ToolCallId ?? "?");
                    }
                }

                return Task.FromResult(new ToolCallResult(null, argsJson));
            },
        };

    [Fact]
    public async Task ExecuteAsync_WithNoApprovalConfigured_BehavesExactlyAsBefore()
    {
        var order = new List<string>();

        var result = await ToolCallExecutor.ExecuteAsync(
            Message("call_1", "call_2"),
            EchoMap(order),
            ToolInvocationPreparer.Disabled
        );

        Assert.Equal(["call_1", "call_2"], order);
        Assert.Equal(2, result.ToolCallResults.Count);
        Assert.All(result.ToolCallResults, r => Assert.False(r.IsError));
        Assert.Equal("""{"id":"call_1"}""", result.ToolCallResults[0].Result);
    }

    [Fact]
    public async Task ExecuteAsync_WhenApprovalIsDenied_ReturnsAnErrorResultAndNeverRunsTheHandler()
    {
        var order = new List<string>();
        var preparer = new ToolInvocationPreparer(
            new ToolApprovalOptions { Gates = [RecordingGate.Denying("operator said no")] }
        );

        var result = await ToolCallExecutor.ExecuteAsync(Message("call_1"), EchoMap(order), preparer);

        Assert.Empty(order);
        var single = Assert.Single(result.ToolCallResults);
        Assert.True(single.IsError);
        Assert.Equal(ToolApprovalOutcomes.Denied, single.ErrorCode);
        Assert.Equal("call_1", single.ToolCallId);
        Assert.Equal("echo", single.ToolName);
        Assert.Contains("operator said no", single.Result);
    }

    [Fact]
    public async Task ExecuteAsync_WithAMixedBatch_RunsOnlyTheApprovedCallsAndKeepsInputOrder()
    {
        var order = new List<string>();
        var preparer = new ToolInvocationPreparer(
            new ToolApprovalOptions
            {
                Gates =
                [
                    new RecordingGate(
                        (context, _) =>
                            Task.FromResult(
                                context.ToolCallId == "call_2"
                                    ? ToolApprovalVerdict.Deny("nope")
                                    : ToolApprovalVerdict.Allow()
                            )
                    ),
                ],
            }
        );

        var result = await ToolCallExecutor.ExecuteAsync(
            Message("call_1", "call_2", "call_3"),
            EchoMap(order),
            preparer
        );

        Assert.Equal(["call_1", "call_3"], order);
        Assert.Equal(
            ["call_1", "call_2", "call_3"],
            result.ToolCallResults.Select(r => r.ToolCallId)
        );
        Assert.False(result.ToolCallResults[0].IsError);
        Assert.True(result.ToolCallResults[1].IsError);
        Assert.False(result.ToolCallResults[2].IsError);
    }

    [Fact]
    public async Task ExecuteAsync_SettlesTheBatchesApprovalsConcurrently()
    {
        // The approver refuses to answer until all three requests have arrived. Preparing them one
        // at a time would deadlock, so completing at all is the assertion.
        var arrived = 0;
        var allArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var preparer = new ToolInvocationPreparer(
            new ToolApprovalOptions
            {
                Gates =
                [
                    new RecordingGate(
                        async (_, _) =>
                        {
                            if (Interlocked.Increment(ref arrived) == 3)
                            {
                                allArrived.TrySetResult();
                            }

                            await allArrived.Task;
                            return ToolApprovalVerdict.Allow();
                        }
                    ),
                ],
            }
        );

        var order = new List<string>();
        var result = await ToolCallExecutor
            .ExecuteAsync(Message("call_1", "call_2", "call_3"), EchoMap(order), preparer)
            .WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(3, arrived);
        Assert.Equal(["call_1", "call_2", "call_3"], order);
        Assert.All(result.ToolCallResults, r => Assert.False(r.IsError));
    }

    [Fact]
    public async Task ExecuteAsync_WhenApprovalsCompleteOutOfOrder_ExecutionOrderIsStillTheInputOrder()
    {
        // call_1's approval lands last; it must still execute first.
        var lastApproved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var others = 0;
        var preparer = new ToolInvocationPreparer(
            new ToolApprovalOptions
            {
                Gates =
                [
                    new RecordingGate(
                        async (context, _) =>
                        {
                            if (context.ToolCallId == "call_1")
                            {
                                await lastApproved.Task;
                            }
                            else if (Interlocked.Increment(ref others) == 2)
                            {
                                lastApproved.TrySetResult();
                            }

                            return ToolApprovalVerdict.Allow();
                        }
                    ),
                ],
            }
        );

        var order = new List<string>();
        _ = await ToolCallExecutor
            .ExecuteAsync(Message("call_1", "call_2", "call_3"), EchoMap(order), preparer)
            .WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(["call_1", "call_2", "call_3"], order);
    }

    [Fact]
    public async Task ExecuteAsync_ForAnUnknownFunction_NeverOpensAGate()
    {
        var gate = RecordingGate.Allowing();
        var preparer = new ToolInvocationPreparer(new ToolApprovalOptions { Gates = [gate] });
        var message = new ToolsCallMessage
        {
            ToolCalls =
            [
                new ToolCall
                {
                    FunctionName = "notRegistered",
                    FunctionArgs = "{}",
                    ToolCallId = "call_1",
                },
            ],
            Role = Role.Assistant,
        };

        var result = await ToolCallExecutor.ExecuteAsync(message, EchoMap(), preparer);

        Assert.Equal(0, gate.CallCount);
        var single = Assert.Single(result.ToolCallResults);
        Assert.True(single.IsError);
        Assert.Contains("is not available", single.Result);
    }

    [Fact]
    public async Task ExecuteAsync_WhenBlocked_ReportsTheRefusalThroughTheResultCallback()
    {
        var callback = new RecordingResultCallback();
        var preparer = new ToolInvocationPreparer(
            new ToolApprovalOptions { Gates = [RecordingGate.Denying("policy")] }
        );

        _ = await ToolCallExecutor.ExecuteAsync(Message("call_1"), EchoMap(), preparer, callback);

        Assert.Equal(["call_1"], callback.Started);
        var error = Assert.Single(callback.Errors);
        Assert.Equal("call_1", error.ToolCallId);
        Assert.Contains("policy", error.Error);
        var reported = Assert.Single(callback.Results);
        Assert.True(reported.IsError);
        Assert.Equal(ToolApprovalOutcomes.Denied, reported.ErrorCode);
    }

    [Fact]
    public async Task ExecuteAsync_PassesRunCorrelationToTheApprover()
    {
        var gate = RecordingGate.Allowing();
        var preparer = new ToolInvocationPreparer(new ToolApprovalOptions { Gates = [gate] });

        _ = await ToolCallExecutor.ExecuteAsync(Message("call_1"), EchoMap(), preparer);

        var seen = Assert.Single(gate.Seen);
        Assert.Equal("echo", seen.ToolName);
        Assert.Equal("call_1", seen.ToolCallId);
        Assert.Equal("thread_1", seen.ThreadId);
        Assert.Equal("run_1", seen.RunId);
        Assert.Equal("gen_1", seen.GenerationId);
        Assert.Equal("""{"id":"call_1"}""", seen.Arguments.Json);
    }
}
