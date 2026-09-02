namespace AchieveAi.LmDotnetTools.LmCore.Tests.Middleware;

public class ToolCallExecutorTests
{
    [Fact]
    public async Task ExecuteAsync_SuccessfulExecution_SetsToolNameAndExecutionTarget()
    {
        // Arrange
        var toolCallId = "call_success_1";
        var toolCallMessage = new ToolsCallMessage
        {
            ToolCalls =
            [
                new ToolCall
                {
                    FunctionName = "getWeather",
                    FunctionArgs = """{"location":"Seattle"}""",
                    ToolCallId = toolCallId,
                    ExecutionTarget = ExecutionTarget.LocalFunction,
                },
            ],
            Role = Role.Assistant,
        };

        var functionMap = new Dictionary<string, ToolCallResultHandler>
        {
            ["getWeather"] = (_, _, _) => Task.FromResult(new ToolCallResult(null, "Sunny, 72F")),
        };

        // Act
        var result = await ToolCallExecutor.ExecuteAsync(toolCallMessage, functionMap);

        // Assert
        Assert.NotNull(result);
        var toolCallResult = Assert.Single(result.ToolCallResults);
        Assert.Equal(toolCallId, toolCallResult.ToolCallId);
        Assert.Equal("getWeather", toolCallResult.ToolName);
        Assert.Equal(ExecutionTarget.LocalFunction, toolCallResult.ExecutionTarget);
        Assert.False(toolCallResult.IsError);
        Assert.Equal("Sunny, 72F", toolCallResult.Result);
    }

    [Fact]
    public async Task ExecuteAsync_FailedExecution_SetsIsErrorTrue()
    {
        // Arrange
        var toolCallId = "call_fail_1";
        var toolCallMessage = new ToolsCallMessage
        {
            ToolCalls =
            [
                new ToolCall
                {
                    FunctionName = "failingFunction",
                    FunctionArgs = "{}",
                    ToolCallId = toolCallId,
                    ExecutionTarget = ExecutionTarget.LocalFunction,
                },
            ],
            Role = Role.Assistant,
        };

        var functionMap = new Dictionary<string, ToolCallResultHandler>
        {
            ["failingFunction"] = (_, _, _) => throw new InvalidOperationException("Something went wrong"),
        };

        // Act
        var result = await ToolCallExecutor.ExecuteAsync(toolCallMessage, functionMap);

        // Assert
        Assert.NotNull(result);
        var toolCallResult = Assert.Single(result.ToolCallResults);
        Assert.Equal(toolCallId, toolCallResult.ToolCallId);
        Assert.True(toolCallResult.IsError);
        Assert.Equal("failingFunction", toolCallResult.ToolName);
        Assert.Equal(ExecutionTarget.LocalFunction, toolCallResult.ExecutionTarget);
        Assert.Contains("Something went wrong", toolCallResult.Result);
    }

    [Fact]
    public async Task ExecuteAsync_UnavailableFunction_SetsIsErrorTrue()
    {
        // Arrange
        var toolCallId = "call_unavailable_1";
        var toolCallMessage = new ToolsCallMessage
        {
            ToolCalls =
            [
                new ToolCall
                {
                    FunctionName = "nonExistentFunction",
                    FunctionArgs = "{}",
                    ToolCallId = toolCallId,
                    ExecutionTarget = ExecutionTarget.LocalFunction,
                },
            ],
            Role = Role.Assistant,
        };

        var functionMap = new Dictionary<string, ToolCallResultHandler>
        {
            ["existingFunction"] = (_, _, _) => Task.FromResult(new ToolCallResult(null, "ok")),
        };

        // Act
        var result = await ToolCallExecutor.ExecuteAsync(toolCallMessage, functionMap);

        // Assert
        Assert.NotNull(result);
        var toolCallResult = Assert.Single(result.ToolCallResults);
        Assert.Equal(toolCallId, toolCallResult.ToolCallId);
        Assert.True(toolCallResult.IsError);
        Assert.Equal("nonExistentFunction", toolCallResult.ToolName);
        Assert.Equal(ExecutionTarget.LocalFunction, toolCallResult.ExecutionTarget);
        Assert.Contains("not available", toolCallResult.Result);
        Assert.Contains("existingFunction", toolCallResult.Result);
    }

    [Fact]
    public async Task ExecuteAsync_SuccessCount_UsesIsErrorFlag()
    {
        // Arrange - mix of successful and failing tool calls
        var toolCallMessage = new ToolsCallMessage
        {
            ToolCalls =
            [
                new ToolCall
                {
                    FunctionName = "successFunc",
                    FunctionArgs = "{}",
                    ToolCallId = "call_1",
                    ExecutionTarget = ExecutionTarget.LocalFunction,
                },
                new ToolCall
                {
                    FunctionName = "failFunc",
                    FunctionArgs = "{}",
                    ToolCallId = "call_2",
                    ExecutionTarget = ExecutionTarget.LocalFunction,
                },
                new ToolCall
                {
                    FunctionName = "missingFunc",
                    FunctionArgs = "{}",
                    ToolCallId = "call_3",
                    ExecutionTarget = ExecutionTarget.LocalFunction,
                },
            ],
            Role = Role.Assistant,
        };

        var functionMap = new Dictionary<string, ToolCallResultHandler>
        {
            ["successFunc"] = (_, _, _) => Task.FromResult(new ToolCallResult(null, "ok")),
            ["failFunc"] = (_, _, _) => throw new Exception("boom"),
        };

        // Act
        var result = await ToolCallExecutor.ExecuteAsync(toolCallMessage, functionMap);

        // Assert
        Assert.Equal(3, result.ToolCallResults.Count);

        // Verify success/error flags
        var successResult = result.ToolCallResults.First(r => r.ToolCallId == "call_1");
        Assert.False(successResult.IsError);

        var failResult = result.ToolCallResults.First(r => r.ToolCallId == "call_2");
        Assert.True(failResult.IsError);

        var missingResult = result.ToolCallResults.First(r => r.ToolCallId == "call_3");
        Assert.True(missingResult.IsError);

        // Only 1 of 3 should be successful (uses IsError flag, not string matching)
        var successCount = result.ToolCallResults.Count(r => !r.IsError);
        Assert.Equal(1, successCount);
    }

    // #694 — a 15,231,668-byte tool result was sent verbatim to a provider with a
    // 10,485,760-byte field limit and 400'd the whole turn. The executor is the production
    // boundary, so the bound must land here (persisted history is then bounded too).
    private const int ReproducedOversizedLength = 15_231_668;

    private static ToolsCallMessage SingleCall(string functionName, string toolCallId) =>
        new()
        {
            ToolCalls =
            [
                new ToolCall
                {
                    FunctionName = functionName,
                    FunctionArgs = "{}",
                    ToolCallId = toolCallId,
                    ExecutionTarget = ExecutionTarget.LocalFunction,
                },
            ],
            Role = Role.Assistant,
        };

    [Fact]
    public async Task ExecuteAsync_OversizedResult_IsBoundedToDefaultLimitWithPrefixAndMarker()
    {
        var original = new string('x', ReproducedOversizedLength);
        var functionMap = new Dictionary<string, ToolCallResultHandler>
        {
            ["dump"] = (_, _, _) => Task.FromResult(new ToolCallResult(null, original)),
        };

        var result = await ToolCallExecutor.ExecuteAsync(SingleCall("dump", "call_big"), functionMap);

        var bounded = Assert.Single(result.ToolCallResults);
        Assert.True(bounded.IsTruncated);
        Assert.Equal("call_big", bounded.ToolCallId);
        Assert.True(
            System.Text.Encoding.UTF8.GetByteCount(bounded.Result) <= ToolResultLimits.Default.MaxResultBytes,
            $"bounded result is {bounded.Result.Length} chars"
        );
        Assert.StartsWith(original[..4096], bounded.Result, StringComparison.Ordinal);
        Assert.EndsWith(" of 15,231,668 bytes]", bounded.Result, StringComparison.Ordinal);
        Assert.Contains(ToolResultLimits.TruncationMarkerPrefix, bounded.Result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_SmallResult_IsByteIdenticalWithoutMarker()
    {
        var functionMap = new Dictionary<string, ToolCallResultHandler>
        {
            ["getWeather"] = (_, _, _) => Task.FromResult(new ToolCallResult(null, "Sunny, 72F")),
        };

        var result = await ToolCallExecutor.ExecuteAsync(SingleCall("getWeather", "call_small"), functionMap);

        var untouched = Assert.Single(result.ToolCallResults);
        Assert.False(untouched.IsTruncated);
        Assert.Equal("Sunny, 72F", untouched.Result);
        Assert.DoesNotContain(ToolResultLimits.TruncationMarkerPrefix, untouched.Result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_HonoursConfiguredLimit_AndBoundsResultsSeenByCallback()
    {
        var limits = new ToolResultLimits { MaxResultBytes = 256 };
        var functionMap = new Dictionary<string, ToolCallResultHandler>
        {
            ["dump"] = (_, _, _) => Task.FromResult(new ToolCallResult(null, new string('y', 10_000))),
        };
        var callback = new Mock<IToolResultCallback>();
        ToolCallResult? seenByCallback = null;
        _ = callback
            .Setup(c =>
                c.OnToolResultAvailableAsync(
                    It.IsAny<string>(),
                    It.IsAny<ToolCallResult>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Callback<string, ToolCallResult, CancellationToken>((_, r, _) => seenByCallback = r)
            .Returns(Task.CompletedTask);

        var result = await ToolCallExecutor.ExecuteAsync(
            SingleCall("dump", "call_cfg"),
            functionMap,
            limits,
            callback.Object
        );

        var bounded = Assert.Single(result.ToolCallResults);
        Assert.True(bounded.IsTruncated);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(bounded.Result) <= 256);
        Assert.EndsWith(" of 10,000 bytes]", bounded.Result, StringComparison.Ordinal);
        Assert.NotNull(seenByCallback);
        Assert.Equal(bounded.Result, seenByCallback.Value.Result);
    }

    [Fact]
    public async Task ExecuteAsync_OversizedErrorResult_IsBoundedToo()
    {
        var limits = new ToolResultLimits { MaxResultBytes = 256 };
        var functionMap = new Dictionary<string, ToolCallResultHandler>
        {
            ["boom"] = (_, _, _) => throw new InvalidOperationException(new string('e', 10_000)),
        };

        var result = await ToolCallExecutor.ExecuteAsync(SingleCall("boom", "call_err"), functionMap, limits);

        var bounded = Assert.Single(result.ToolCallResults);
        Assert.True(bounded.IsError);
        Assert.True(bounded.IsTruncated);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(bounded.Result) <= 256);
        Assert.Contains(ToolResultLimits.TruncationMarkerPrefix, bounded.Result, StringComparison.Ordinal);
    }
}
