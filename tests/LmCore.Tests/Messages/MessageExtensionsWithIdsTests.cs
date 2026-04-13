namespace AchieveAi.LmDotnetTools.LmCore.Tests.Messages;

public class MessageExtensionsWithIdsTests
{
    [Fact]
    public void WithIds_AppliesIds_ToUnifiedToolMessages()
    {
        const string runId = "run-1";
        const string parentRunId = "parent-1";
        const string threadId = "thread-1";

        var toolCall = new ToolCallMessage
        {
            ToolCallId = "call-1",
            FunctionName = "get_weather",
            FunctionArgs = "{}",
            ExecutionTarget = ExecutionTarget.ProviderServer,
            Role = Role.Assistant,
        };

        var toolCallUpdate = new ToolCallUpdateMessage
        {
            ToolCallId = "call-1",
            FunctionName = "get_weather",
            FunctionArgs = "{}",
            ExecutionTarget = ExecutionTarget.LocalFunction,
            Role = Role.Assistant,
            IsUpdate = true,
        };

        var toolCallResult = new ToolCallResultMessage
        {
            ToolCallId = "call-1",
            ToolName = "get_weather",
            Result = "{}",
            ExecutionTarget = ExecutionTarget.ProviderServer,
            Role = Role.Assistant,
        };

        var toolsCallResult = new ToolsCallResultMessage
        {
            Role = Role.User,
            ToolCallResults = [new ToolCallResult("call-1", "{}")],
        };

        var updatedToolCall = Assert.IsType<ToolCallMessage>(toolCall.WithIds(runId, parentRunId, threadId));
        var updatedToolCallUpdate = Assert.IsType<ToolCallUpdateMessage>(
            toolCallUpdate.WithIds(runId, parentRunId, threadId)
        );
        var updatedToolCallResult = Assert.IsType<ToolCallResultMessage>(
            toolCallResult.WithIds(runId, parentRunId, threadId)
        );
        var updatedToolsCallResult = Assert.IsType<ToolsCallResultMessage>(
            toolsCallResult.WithIds(runId, parentRunId, threadId)
        );

        Assert.Equal(runId, updatedToolCall.RunId);
        Assert.Equal(parentRunId, updatedToolCall.ParentRunId);
        Assert.Equal(threadId, updatedToolCall.ThreadId);
        Assert.Equal(ExecutionTarget.ProviderServer, updatedToolCall.ExecutionTarget);

        Assert.Equal(runId, updatedToolCallUpdate.RunId);
        Assert.Equal(parentRunId, updatedToolCallUpdate.ParentRunId);
        Assert.Equal(threadId, updatedToolCallUpdate.ThreadId);

        Assert.Equal(runId, updatedToolCallResult.RunId);
        Assert.Equal(parentRunId, updatedToolCallResult.ParentRunId);
        Assert.Equal(threadId, updatedToolCallResult.ThreadId);
        Assert.Equal(ExecutionTarget.ProviderServer, updatedToolCallResult.ExecutionTarget);

        Assert.Equal(runId, updatedToolsCallResult.RunId);
        Assert.Equal(threadId, updatedToolsCallResult.ThreadId);
    }
}

