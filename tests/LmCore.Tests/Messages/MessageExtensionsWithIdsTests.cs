using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Models;

namespace AchieveAi.LmDotnetTools.LmCore.Tests.Messages;

public class MessageExtensionsWithIdsTests
{
    // The run's GenerationId must be stamped across EVERY WithIds arm — especially the
    // update/result/usage arms (TextUpdateMessage, ToolCallUpdateMessage, ToolsCallUpdateMessage,
    // ToolCallResultMessage, ToolsCallResultMessage, ReasoningUpdateMessage, UsageMessage). A
    // future arm that forgets GenerationId silently breaks client merge-key grouping, so cover
    // every variant here parameterized rather than sampling a few.
    public static TheoryData<string, IMessage> MessageVariants()
    {
        const string opaque = "provider-opaque-id";
        return new TheoryData<string, IMessage>
        {
            { nameof(TextMessage), new TextMessage { Text = "t", Role = Role.Assistant, GenerationId = opaque } },
            { nameof(TextUpdateMessage), new TextUpdateMessage { Text = "t", Role = Role.Assistant, GenerationId = opaque } },
            {
                nameof(TextWithCitationsMessage),
                new TextWithCitationsMessage { Text = "t", Role = Role.Assistant, GenerationId = opaque }
            },
            {
                nameof(ToolCallMessage),
                new ToolCallMessage { ToolCallId = "c1", FunctionName = "f", FunctionArgs = "{}", Role = Role.Assistant, GenerationId = opaque }
            },
            {
                nameof(ToolCallUpdateMessage),
                new ToolCallUpdateMessage { ToolCallId = "c1", FunctionName = "f", FunctionArgs = "{}", Role = Role.Assistant, IsUpdate = true, GenerationId = opaque }
            },
            {
                nameof(ToolCallResultMessage),
                new ToolCallResultMessage { ToolCallId = "c1", ToolName = "f", Result = "{}", Role = Role.User, GenerationId = opaque }
            },
            {
                nameof(ToolsCallMessage),
                new ToolsCallMessage { Role = Role.Assistant, GenerationId = opaque }
            },
            {
                nameof(ToolsCallUpdateMessage),
                new ToolsCallUpdateMessage { Role = Role.Assistant, GenerationId = opaque }
            },
            {
                nameof(ToolsCallResultMessage),
                new ToolsCallResultMessage { Role = Role.User, ToolCallResults = [new ToolCallResult("c1", "{}")], GenerationId = opaque }
            },
            {
                nameof(ReasoningMessage),
                new ReasoningMessage { Reasoning = "r", Role = Role.Assistant, GenerationId = opaque }
            },
            {
                nameof(ReasoningUpdateMessage),
                new ReasoningUpdateMessage { Reasoning = "r", Role = Role.Assistant, GenerationId = opaque }
            },
            {
                nameof(UsageMessage),
                new UsageMessage { Usage = new Usage { PromptTokens = 1, CompletionTokens = 1, TotalTokens = 2 }, Role = Role.Assistant, GenerationId = opaque }
            },
        };
    }

    [Theory]
    [MemberData(nameof(MessageVariants))]
    public void WithIds_StampsRunGenerationId_AcrossAllArms(string variantName, IMessage message)
    {
        const string runGenerationId = "0123456789abcdef0123456789abcdef";
        const string runId = "run-1";
        var options = new GenerateReplyOptions { RunId = runId, GenerationId = runGenerationId };

        var updated = message.WithIds(options);

        Assert.Equal(runGenerationId, updated.GenerationId);
        Assert.Equal(runId, updated.RunId);
        // The arm preserved its concrete type (no fall-through to the default '_ => message' case)
        // — a missing switch arm would return the original instance unchanged.
        Assert.True(
            message.GetType() == updated.GetType(),
            $"{variantName} must be handled by a dedicated WithIds arm preserving its type"
        );
    }

    // BUG H1: every message emitted within a run must carry the run's GenerationId (the value
    // advertised by run_assignment), NOT the opaque per-message response/reasoning id a provider
    // stamps. WithIds(options) is the provider-agnostic seam every provider already calls to apply
    // run identity, so it must also override GenerationId from options.GenerationId. Tested with
    // real (non-mock) message objects.
    [Fact]
    public void WithIds_StampsRunGenerationId_OverridingProviderGenerationId()
    {
        const string runId = "fedcba9876543210fedcba9876543210";
        const string runGenerationId = "0123456789abcdef0123456789abcdef";
        var options = new GenerateReplyOptions { RunId = runId, GenerationId = runGenerationId };

        // Provider stamped distinct opaque ids on each message (encrypted response/reasoning tokens).
        var toolCall = new ToolCallMessage
        {
            ToolCallId = "call-1",
            FunctionName = "get_weather",
            FunctionArgs = "{}",
            Role = Role.Assistant,
            GenerationId = "resp_opaque_AAAA",
        };
        var toolCallResult = new ToolCallResultMessage
        {
            ToolCallId = "call-1",
            ToolName = "get_weather",
            Result = "{}",
            Role = Role.User,
            GenerationId = "resp_opaque_BBBB",
        };
        var text = new TextMessage
        {
            Text = "hi",
            Role = Role.Assistant,
            GenerationId = "resp_opaque_CCCC",
        };

        var updatedToolCall = Assert.IsType<ToolCallMessage>(toolCall.WithIds(options));
        var updatedToolCallResult = Assert.IsType<ToolCallResultMessage>(toolCallResult.WithIds(options));
        var updatedText = Assert.IsType<TextMessage>(text.WithIds(options));

        // The run's GenerationId overrides every provider opaque id.
        Assert.Equal(runGenerationId, updatedToolCall.GenerationId);
        Assert.Equal(runGenerationId, updatedToolCallResult.GenerationId);
        Assert.Equal(runGenerationId, updatedText.GenerationId);

        // RunId still applied as before.
        Assert.Equal(runId, updatedToolCall.RunId);
        Assert.Equal(runId, updatedToolCallResult.RunId);
        Assert.Equal(runId, updatedText.RunId);
    }

    // When the run advertises no GenerationId, the provider's GenerationId is preserved (no-op),
    // so non-run callers and providers that don't thread a run generation are unaffected.
    [Fact]
    public void WithIds_PreservesProviderGenerationId_WhenOptionsHaveNone()
    {
        var options = new GenerateReplyOptions { RunId = "run-1" };
        var toolCall = new ToolCallMessage
        {
            ToolCallId = "call-1",
            FunctionName = "f",
            FunctionArgs = "{}",
            Role = Role.Assistant,
            GenerationId = "provider-gen",
        };

        var updated = Assert.IsType<ToolCallMessage>(toolCall.WithIds(options));

        Assert.Equal("provider-gen", updated.GenerationId);
        Assert.Equal("run-1", updated.RunId);
    }

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

