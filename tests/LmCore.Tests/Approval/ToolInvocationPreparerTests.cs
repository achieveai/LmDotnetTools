using AchieveAi.LmDotnetTools.LmCore.Approval;

namespace AchieveAi.LmDotnetTools.LmCore.Tests.Approval;

/// <summary>
/// Pins the fail-closed contract: a handler runs only after every configured approver explicitly
/// allowed the call, and every other path — denial, timeout, overload, a missing approver, a
/// throwing hook, cancellation — blocks it.
/// </summary>
public class ToolInvocationPreparerTests
{
    private static ToolInvocationRequest Request(string args = """{"path":"/etc/passwd"}""") =>
        new()
        {
            ToolName = "readFile",
            ArgumentsJson = args,
            ToolCallId = "call_1",
            ThreadId = "thread_1",
            RunId = "run_1",
            GenerationId = "gen_1",
        };

    [Fact]
    public void Disabled_ConfiguresNothing()
    {
        Assert.False(ToolInvocationPreparer.Disabled.IsEnabled);
        Assert.False(new ToolInvocationPreparer().IsEnabled);
        Assert.False(new ToolInvocationPreparer(new ToolApprovalOptions()).IsEnabled);
    }

    [Fact]
    public async Task PrepareAsync_WithNothingConfigured_ApprovesWithoutConsultingAnything()
    {
        var prepared = await ToolInvocationPreparer.Disabled.PrepareAsync(Request());

        Assert.True(prepared.IsApproved);
        Assert.Equal(ToolApprovalOutcomes.Allowed, prepared.Outcome);
        Assert.Equal("""{"path":"/etc/passwd"}""", prepared.Arguments.Json);
    }

    [Fact]
    public async Task PrepareAsync_WhenEveryGateAllows_Approves()
    {
        var first = RecordingGate.Allowing();
        var second = RecordingGate.Allowing();
        var preparer = new ToolInvocationPreparer(new ToolApprovalOptions { Gates = [first, second] });

        var prepared = await preparer.PrepareAsync(Request());

        Assert.True(prepared.IsApproved);
        Assert.Equal(1, first.CallCount);
        Assert.Equal(1, second.CallCount);
    }

    [Fact]
    public async Task PrepareAsync_WhenAnyGateDenies_Blocks()
    {
        var preparer = new ToolInvocationPreparer(
            new ToolApprovalOptions { Gates = [RecordingGate.Allowing(), RecordingGate.Denying("not on my watch")] }
        );

        var prepared = await preparer.PrepareAsync(Request());

        Assert.False(prepared.IsApproved);
        Assert.Equal(ToolApprovalOutcomes.Denied, prepared.Outcome);
        Assert.Equal("not on my watch", prepared.Reason);
    }

    [Fact]
    public async Task PrepareAsync_WhenApprovalRequiredButNoGateConfigured_FailsClosed()
    {
        var preparer = new ToolInvocationPreparer(new ToolApprovalOptions { RequireApproval = true });

        var prepared = await preparer.PrepareAsync(Request());

        Assert.False(prepared.IsApproved);
        Assert.Equal(ToolApprovalOutcomes.MissingApprover, prepared.Outcome);
    }

    [Fact]
    public async Task PrepareAsync_WhenGateThrows_BlocksAsHookError()
    {
        var preparer = new ToolInvocationPreparer(
            new ToolApprovalOptions
            {
                Gates = [RecordingGate.Throwing(new InvalidOperationException("approver is down"))],
            }
        );

        var prepared = await preparer.PrepareAsync(Request());

        Assert.False(prepared.IsApproved);
        Assert.Equal(ToolApprovalOutcomes.HookError, prepared.Outcome);
        Assert.Equal("approver is down", prepared.Reason);
    }

    [Fact]
    public async Task PrepareAsync_WhenPolicyThrows_BlocksAsHookError()
    {
        var gate = RecordingGate.Allowing();
        var preparer = new ToolInvocationPreparer(
            new ToolApprovalOptions
            {
                HostPolicy = new ThrowingPolicy(new InvalidOperationException("policy exploded")),
                Gates = [gate],
            }
        );

        var prepared = await preparer.PrepareAsync(Request());

        Assert.False(prepared.IsApproved);
        Assert.Equal(ToolApprovalOutcomes.HookError, prepared.Outcome);
        Assert.Equal(0, gate.CallCount);
    }

    [Fact]
    public async Task PrepareAsync_WhenGateReturnsDefaultVerdict_BlocksAsDenied()
    {
        var preparer = new ToolInvocationPreparer(
            new ToolApprovalOptions
            {
                Gates = [new RecordingGate((_, _) => Task.FromResult(default(ToolApprovalVerdict)))],
            }
        );

        var prepared = await preparer.PrepareAsync(Request());

        Assert.False(prepared.IsApproved);
        Assert.Equal(ToolApprovalOutcomes.Denied, prepared.Outcome);
    }

    [Theory]
    [InlineData("Allowed")]
    [InlineData("ALLOWED")]
    [InlineData("allow")]
    [InlineData("")]
    public async Task PrepareAsync_WhenGateReturnsSomethingOtherThanExactlyAllowed_Blocks(string outcome)
    {
        var preparer = new ToolInvocationPreparer(
            new ToolApprovalOptions
            {
                Gates = [new RecordingGate((_, _) => Task.FromResult(ToolApprovalVerdict.Blocked(outcome)))],
            }
        );

        var prepared = await preparer.PrepareAsync(Request());

        Assert.False(prepared.IsApproved);
    }

    [Fact]
    public async Task PrepareAsync_WhenProviderPolicyDenies_NeverOpensAGate()
    {
        var gate = RecordingGate.Allowing();
        var hostPolicy = RecordingPolicy.Allowing();
        var preparer = new ToolInvocationPreparer(
            new ToolApprovalOptions
            {
                ProviderPolicy = RecordingPolicy.Denying("provider forbids it"),
                HostPolicy = hostPolicy,
                Gates = [gate],
            }
        );

        var prepared = await preparer.PrepareAsync(Request());

        Assert.False(prepared.IsApproved);
        Assert.Equal(ToolApprovalOutcomes.ProviderPolicyDenied, prepared.Outcome);
        Assert.Equal(0, hostPolicy.CallCount);
        Assert.Equal(0, gate.CallCount);
    }

    [Fact]
    public async Task PrepareAsync_WhenHostPolicyDenies_NeverOpensAGate()
    {
        var gate = RecordingGate.Allowing();
        var preparer = new ToolInvocationPreparer(
            new ToolApprovalOptions
            {
                ProviderPolicy = RecordingPolicy.Allowing(),
                HostPolicy = RecordingPolicy.Denying(),
                Gates = [gate],
            }
        );

        var prepared = await preparer.PrepareAsync(Request());

        Assert.False(prepared.IsApproved);
        Assert.Equal(ToolApprovalOutcomes.HostPolicyDenied, prepared.Outcome);
        Assert.Equal(0, gate.CallCount);
    }

    [Fact]
    public async Task PrepareAsync_WhenOneGateDenies_DoesNotWaitForTheOthers()
    {
        // The slow approver never answers. If the deny did not short-circuit, this would hang.
        var neverAnswers = new TaskCompletionSource<ToolApprovalVerdict>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var denied = new TaskCompletionSource();
        var preparer = new ToolInvocationPreparer(
            new ToolApprovalOptions
            {
                Gates =
                [
                    new RecordingGate(
                        (_, _) =>
                        {
                            denied.TrySetResult();
                            return Task.FromResult(ToolApprovalVerdict.Deny("no"));
                        }
                    ),
                    new RecordingGate((_, _) => neverAnswers.Task),
                ],
            }
        );

        var prepared = await preparer.PrepareAsync(Request()).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(denied.Task.IsCompleted);
        Assert.False(prepared.IsApproved);
        Assert.Equal(ToolApprovalOutcomes.Denied, prepared.Outcome);
    }

    [Fact]
    public async Task PrepareAsync_WhenLateDenyFollowsAnEarlyAllow_DenyStillWins()
    {
        var allowed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var preparer = new ToolInvocationPreparer(
            new ToolApprovalOptions
            {
                Gates =
                [
                    new RecordingGate(
                        (_, _) =>
                        {
                            allowed.SetResult();
                            return Task.FromResult(ToolApprovalVerdict.Allow());
                        }
                    ),
                    new RecordingGate(
                        async (_, _) =>
                        {
                            // Answer only after the other approver has already allowed.
                            await allowed.Task;
                            return ToolApprovalVerdict.Deny("second look says no");
                        }
                    ),
                ],
            }
        );

        var prepared = await preparer.PrepareAsync(Request()).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(prepared.IsApproved);
        Assert.Equal(ToolApprovalOutcomes.Denied, prepared.Outcome);
    }

    [Fact]
    public async Task PrepareAsync_WhenCancelledWhileWaiting_BlocksAsCancelled()
    {
        using var cts = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var preparer = new ToolInvocationPreparer(
            new ToolApprovalOptions
            {
                Gates =
                [
                    new RecordingGate(
                        async (_, ct) =>
                        {
                            entered.TrySetResult();
                            await Task.Delay(Timeout.Infinite, ct);
                            return ToolApprovalVerdict.Allow();
                        }
                    ),
                ],
            }
        );

        var pending = preparer.PrepareAsync(Request(), cts.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await cts.CancelAsync();

        var prepared = await pending.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(prepared.IsApproved);
        Assert.Equal(ToolApprovalOutcomes.Cancelled, prepared.Outcome);
    }

    [Fact]
    public async Task PrepareAsync_WhenAnAllowArrivesAfterCancellation_StillBlocks()
    {
        // An approver that ignores the token and answers anyway must not resurrect a cancelled call.
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var preparer = new ToolInvocationPreparer(new ToolApprovalOptions { Gates = [RecordingGate.Allowing()] });

        var prepared = await preparer.PrepareAsync(Request(), cts.Token);

        Assert.False(prepared.IsApproved);
        Assert.Equal(ToolApprovalOutcomes.Cancelled, prepared.Outcome);
    }

    [Fact]
    public async Task PrepareAsync_WhenTheApprovalWaitElapses_BlocksAsTimeout()
    {
        var time = new ManualTimeProvider();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var preparer = new ToolInvocationPreparer(
            new ToolApprovalOptions
            {
                MaxApprovalWait = TimeSpan.FromMinutes(5),
                TimeProvider = time,
                Gates =
                [
                    new RecordingGate(
                        async (_, ct) =>
                        {
                            entered.TrySetResult();
                            await Task.Delay(Timeout.Infinite, ct);
                            return ToolApprovalVerdict.Allow();
                        }
                    ),
                ],
            }
        );

        var pending = preparer.PrepareAsync(Request());
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        time.Advance(TimeSpan.FromMinutes(4));
        Assert.False(pending.IsCompleted);

        time.Advance(TimeSpan.FromMinutes(1));
        var prepared = await pending.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(prepared.IsApproved);
        Assert.Equal(ToolApprovalOutcomes.Timeout, prepared.Outcome);
    }

    [Fact]
    public async Task PrepareAsync_WhenTheOperationDeadlineIsSooner_ItWins()
    {
        var time = new ManualTimeProvider();
        var deadline = time.GetUtcNow() + TimeSpan.FromSeconds(30);
        var request = Request() with { OperationDeadline = deadline };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        DateTimeOffset seenExpiry = default;
        var preparer = new ToolInvocationPreparer(
            new ToolApprovalOptions
            {
                MaxApprovalWait = TimeSpan.FromMinutes(5),
                TimeProvider = time,
                Gates =
                [
                    new RecordingGate(
                        async (context, ct) =>
                        {
                            seenExpiry = context.ExpiresAt;
                            entered.TrySetResult();
                            await Task.Delay(Timeout.Infinite, ct);
                            return ToolApprovalVerdict.Allow();
                        }
                    ),
                ],
            }
        );

        var pending = preparer.PrepareAsync(request);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        time.Advance(TimeSpan.FromSeconds(31));
        var prepared = await pending.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(deadline, seenExpiry);
        Assert.Equal(ToolApprovalOutcomes.Timeout, prepared.Outcome);
    }

    [Fact]
    public async Task PrepareAsync_WhenTooManyApprovalsArePending_BlocksAsOverload()
    {
        var release = new TaskCompletionSource<ToolApprovalVerdict>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var preparer = new ToolInvocationPreparer(
            new ToolApprovalOptions
            {
                MaxPendingApprovals = 1,
                Gates =
                [
                    new RecordingGate(
                        (_, _) =>
                        {
                            entered.TrySetResult();
                            return release.Task;
                        }
                    ),
                ],
            }
        );

        var first = preparer.PrepareAsync(Request());
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var second = await preparer.PrepareAsync(Request()).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(second.IsApproved);
        Assert.Equal(ToolApprovalOutcomes.Overload, second.Outcome);

        release.SetResult(ToolApprovalVerdict.Allow());
        Assert.True((await first.WaitAsync(TimeSpan.FromSeconds(10))).IsApproved);

        // The slot is released again once the first decision lands.
        var third = await preparer.PrepareAsync(Request()).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(third.IsApproved);
    }

    [Fact]
    public async Task PrepareAsync_ShowsTheApproverTheArgumentsThatWillActuallyRun()
    {
        string? seenJson = null;
        string? seenHash = null;
        var preparer = new ToolInvocationPreparer(
            new ToolApprovalOptions
            {
                Gates =
                [
                    new RecordingGate(
                        (context, _) =>
                        {
                            seenJson = context.Arguments.Json;
                            seenHash = context.Arguments.Sha256Hex;
                            return Task.FromResult(ToolApprovalVerdict.Allow());
                        }
                    ),
                ],
            }
        );

        var prepared = await preparer.PrepareAsync(Request("""{"path":"/tmp/a"}"""));

        string? executedJson = null;
        var result = await preparer.InvokeAsync(
            prepared,
            (argsJson, _, _) =>
            {
                executedJson = argsJson;
                return Task.FromResult(new ToolCallResult(null, "ok"));
            },
            new ToolCallContext { ToolCallId = prepared.ToolCallId }
        );

        Assert.Equal("""{"path":"/tmp/a"}""", seenJson);
        Assert.Equal(seenJson, executedJson);
        Assert.Equal(seenHash, prepared.Arguments.Sha256Hex);
        Assert.Equal("ok", result.Result);
    }

    [Fact]
    public async Task InvokeAsync_WhenTheCallerMutatesItsArgumentsAfterApproval_RunsTheApprovedBytes()
    {
        // The window between "approved" and "invoked" is where a swapped payload would do its
        // damage: an approver sees a harmless path, and the handler executes a different one.
        var callerOwnedArgs = new System.Text.StringBuilder("""{"path":"/tmp/safe"}""");
        string? seenByApprover = null;
        var preparer = new ToolInvocationPreparer(
            new ToolApprovalOptions
            {
                Gates =
                [
                    new RecordingGate(
                        (context, _) =>
                        {
                            seenByApprover = context.Arguments.Json;
                            return Task.FromResult(ToolApprovalVerdict.Allow());
                        }
                    ),
                ],
            }
        );

        var prepared = await preparer.PrepareAsync(Request(callerOwnedArgs.ToString()));

        _ = callerOwnedArgs.Clear().Append("""{"path":"/etc/shadow"}""");

        string? executedJson = null;
        _ = await preparer.InvokeAsync(
            prepared,
            (argsJson, _, _) =>
            {
                executedJson = argsJson;
                return Task.FromResult(new ToolCallResult(null, "ok"));
            },
            new ToolCallContext { ToolCallId = prepared.ToolCallId }
        );

        Assert.Equal("""{"path":"/tmp/safe"}""", seenByApprover);
        Assert.Equal(seenByApprover, executedJson);
    }

    [Fact]
    public async Task InvokeAsync_WhenBlocked_NeverCallsTheHandler()
    {
        var invocations = 0;
        var preparer = new ToolInvocationPreparer(new ToolApprovalOptions { Gates = [RecordingGate.Denying("nope")] });

        var prepared = await preparer.PrepareAsync(Request());
        var result = await preparer.InvokeAsync(
            prepared,
            (_, _, _) =>
            {
                _ = Interlocked.Increment(ref invocations);
                return Task.FromResult(new ToolCallResult(null, "should never happen"));
            },
            new ToolCallContext { ToolCallId = prepared.ToolCallId }
        );

        Assert.Equal(0, invocations);
        Assert.True(result.IsError);
        Assert.Equal(ToolApprovalOutcomes.Denied, result.ErrorCode);
        Assert.Contains("nope", result.Result);
        Assert.Contains("readFile", result.Result);
    }

    [Fact]
    public async Task InvokeAsync_WhenApproved_CallsTheHandlerExactlyOnce()
    {
        var invocations = 0;
        var preparer = new ToolInvocationPreparer(new ToolApprovalOptions { Gates = [RecordingGate.Allowing()] });

        var prepared = await preparer.PrepareAsync(Request());
        var result = await preparer.InvokeAsync(
            prepared,
            (_, _, _) =>
            {
                _ = Interlocked.Increment(ref invocations);
                return Task.FromResult(new ToolCallResult(null, "done"));
            },
            new ToolCallContext { ToolCallId = prepared.ToolCallId }
        );

        Assert.Equal(1, invocations);
        Assert.Equal("done", result.Result);
    }

    [Fact]
    public void ToBlockedResult_OnAnApprovedInvocation_Throws()
    {
        var prepared = new PreparedToolInvocation
        {
            ToolName = "readFile",
            Arguments = CanonicalToolArguments.Freeze("{}"),
            Outcome = ToolApprovalOutcomes.Allowed,
        };

        _ = Assert.Throws<InvalidOperationException>(() => prepared.ToBlockedResult());
    }

    [Fact]
    public void Verdict_Default_IsNotAllowed()
    {
        ToolApprovalVerdict verdict = default;

        Assert.False(verdict.IsAllowed);
        Assert.Null(verdict.Outcome);
    }

    [Fact]
    public void CanonicalArguments_NormalizeEmptyToAnEmptyObject_AndHashStably()
    {
        Assert.Equal("{}", CanonicalToolArguments.Freeze(null).Json);
        Assert.Equal("{}", CanonicalToolArguments.Freeze(string.Empty).Json);
        Assert.Equal(
            CanonicalToolArguments.Freeze("""{"a":1}""").Sha256Hex,
            CanonicalToolArguments.Freeze("""{"a":1}""").Sha256Hex
        );
        Assert.NotEqual(
            CanonicalToolArguments.Freeze("""{"a":1}""").Sha256Hex,
            CanonicalToolArguments.Freeze("""{"a": 1}""").Sha256Hex
        );
        Assert.Equal(64, CanonicalToolArguments.Freeze("{}").Sha256Hex.Length);
        Assert.Equal(
            CanonicalToolArguments.Freeze("{}").Sha256Hex,
            CanonicalToolArguments.Freeze("{}").Sha256Hex.ToLowerInvariant()
        );
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Options_RejectNonPositiveApprovalWaits(int seconds) =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ToolApprovalOptions { MaxApprovalWait = TimeSpan.FromSeconds(seconds) }
        );

    [Fact]
    public void Options_RejectAnInfiniteApprovalWait() =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ToolApprovalOptions { MaxApprovalWait = Timeout.InfiniteTimeSpan }
        );

    [Fact]
    public void Options_RejectANonPositivePendingLimit() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new ToolApprovalOptions { MaxPendingApprovals = 0 });
}
