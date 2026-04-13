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

        var functionMap = new Dictionary<string, Func<string, Task<string>>>
        {
            ["getWeather"] = _ => Task.FromResult("Sunny, 72F"),
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

        var functionMap = new Dictionary<string, Func<string, Task<string>>>
        {
            ["failingFunction"] = _ => throw new InvalidOperationException("Something went wrong"),
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

        var functionMap = new Dictionary<string, Func<string, Task<string>>>
        {
            ["existingFunction"] = _ => Task.FromResult("ok"),
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

        var functionMap = new Dictionary<string, Func<string, Task<string>>>
        {
            ["successFunc"] = _ => Task.FromResult("ok"),
            ["failFunc"] = _ => throw new Exception("boom"),
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
}
