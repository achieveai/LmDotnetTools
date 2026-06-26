using System.Collections.Immutable;
using AchieveAi.LmDotnetTools.LmCore.Core;

namespace AchieveAi.LmDotnetTools.AnthropicProvider.Tests.Models;

/// <summary>
///     Tests that the full thinking + tool call multi-turn scenario serializes correctly
///     into the Anthropic API request format. This exercises the fix for reasoning content
///     being lost when ToolsCallAggregateMessage was created during upstream aggregation.
/// </summary>
public class ThinkingWithToolCallSerializationTests
{
    /// <summary>
    ///     Simulates a multi-turn conversation where:
    ///     Turn 1: User asks a question → assistant thinks, calls a tool, gets results
    ///     Turn 2: Build the request for the second LLM call
    ///
    ///     The second request must include thinking blocks in the assistant message
    ///     alongside tool_use blocks. Without the fix, ReasoningMessages were dropped
    ///     during upstream aggregation, causing providers like Kimi to reject the request.
    /// </summary>
    [Fact]
    public void FromMessages_WithReasoningAndToolCallAggregate_PreservesThinkingBlocks()
    {
        // Arrange: Build the conversation history as it would appear before the second LLM call.
        // This is the history AFTER the fix in MessageTransformationMiddleware:
        // CompositeMessage(ReasoningMessage + ReasoningMessage + ToolsCallAggregateMessage)
        var toolCall = new ToolCall
        {
            FunctionName = "books-llm_query_search",
            FunctionArgs = "{\"query\":\"hematosis\"}",
            ToolCallId = "toolu_01ABC",
            ToolCallIdx = 0,
        };

        var toolsCallMessage = new ToolsCallMessage
        {
            ToolCalls = [toolCall],
            GenerationId = "gen1",
            FromAgent = "assistant",
        };

        var toolsCallResult = new ToolsCallResultMessage
        {
            ToolCallResults = [new ToolCallResult("toolu_01ABC", "Hematosis is the process of gas exchange.")],
            GenerationId = "gen1",
        };

        var aggregate = new ToolsCallAggregateMessage(toolsCallMessage, toolsCallResult, "assistant");

        var compositeMessage = new CompositeMessage
        {
            Messages =
            [
                new ReasoningMessage
                {
                    Reasoning = "The user is asking about hematosis. Let me search the medical books.",
                    Visibility = ReasoningVisibility.Plain,
                    Role = Role.Assistant,
                    GenerationId = "gen1",
                    MessageOrderIdx = 0,
                },
                new ReasoningMessage
                {
                    Reasoning = "encrypted-signature-blob-here",
                    Visibility = ReasoningVisibility.Encrypted,
                    Role = Role.Assistant,
                    GenerationId = "gen1",
                    MessageOrderIdx = 1,
                },
                aggregate,
            ],
            Role = Role.Assistant,
            GenerationId = "gen1",
        };

        var messages = new IMessage[]
        {
            new TextMessage { Role = Role.System, Text = "You are a medical knowledge assistant." },
            new TextMessage { Role = Role.User, Text = "What is hematosis?" },
            compositeMessage,
        };

        var options = new GenerateReplyOptions
        {
            ModelId = "claude-3-7-sonnet-20250219",
            ExtraProperties = ImmutableDictionary.Create<string, object?>()
                .Add("Thinking", new AnthropicThinking(2048)),
        };

        // Act
        var request = AnthropicRequest.FromMessages(messages, options);

        // Assert
        Assert.NotNull(request);
        Assert.Equal("You are a medical knowledge assistant.", request.System);
        Assert.NotNull(request.Thinking);
        Assert.Equal(2048, request.Thinking.BudgetTokens);

        // Should have 3 messages: user, assistant (thinking + tool_use), user (tool_result)
        Assert.Equal(3, request.Messages.Count);

        // First message: user
        Assert.Equal("user", request.Messages[0].Role);
        Assert.Single(request.Messages[0].Content);
        Assert.Equal("text", request.Messages[0].Content[0].Type);
        Assert.Equal("What is hematosis?", request.Messages[0].Content[0].Text);

        // Second message: assistant with thinking + tool_use
        var assistantMsg = request.Messages[1];
        Assert.Equal("assistant", assistantMsg.Role);

        // Should have thinking block (merged text+signature) + tool_use block
        Assert.Equal(2, assistantMsg.Content.Count);

        // First content: thinking block (merged from plain + encrypted reasoning)
        var thinkingBlock = assistantMsg.Content[0];
        Assert.Equal("thinking", thinkingBlock.Type);
        Assert.Equal("The user is asking about hematosis. Let me search the medical books.", thinkingBlock.Thinking);
        Assert.Equal("encrypted-signature-blob-here", thinkingBlock.ThinkingSignature);

        // Second content: tool_use block
        var toolUseBlock = assistantMsg.Content[1];
        Assert.Equal("tool_use", toolUseBlock.Type);
        Assert.Equal("toolu_01ABC", toolUseBlock.Id);
        Assert.Equal("books-llm_query_search", toolUseBlock.Name);

        // Third message: user with tool_result
        var toolResultMsg = request.Messages[2];
        Assert.Equal("user", toolResultMsg.Role);
        Assert.Single(toolResultMsg.Content);
        Assert.Equal("tool_result", toolResultMsg.Content[0].Type);
        Assert.Equal("toolu_01ABC", toolResultMsg.Content[0].ToolUseId);
        Assert.Equal("Hematosis is the process of gas exchange.", toolResultMsg.Content[0].Content);
    }

    /// <summary>
    ///     Tests that when there are only tool calls (no reasoning) in the aggregate,
    ///     the serialization still works correctly (regression guard).
    /// </summary>
    [Fact]
    public void FromMessages_WithToolCallAggregateOnly_SerializesCorrectly()
    {
        var toolCall = new ToolCall
        {
            FunctionName = "search",
            FunctionArgs = "{\"q\":\"test\"}",
            ToolCallId = "toolu_01XYZ",
            ToolCallIdx = 0,
        };

        var aggregate = new ToolsCallAggregateMessage(
            new ToolsCallMessage { ToolCalls = [toolCall], GenerationId = "gen1" },
            new ToolsCallResultMessage
            {
                ToolCallResults = [new ToolCallResult("toolu_01XYZ", "result")],
                GenerationId = "gen1",
            },
            "assistant"
        );

        var messages = new IMessage[]
        {
            new TextMessage { Role = Role.User, Text = "Search for test" },
            aggregate,
        };

        var request = AnthropicRequest.FromMessages(messages);

        Assert.Equal(3, request.Messages.Count);
        Assert.Equal("user", request.Messages[0].Role);
        Assert.Equal("assistant", request.Messages[1].Role);
        Assert.Equal("tool_use", request.Messages[1].Content[0].Type);
        Assert.Equal("user", request.Messages[2].Role);
        Assert.Equal("tool_result", request.Messages[2].Content[0].Type);
    }

    /// <summary>
    ///     Tests that ReasoningMessage with both Plain and Encrypted visibility
    ///     get correctly merged into a single thinking block with text + signature.
    /// </summary>
    [Fact]
    public void FromMessages_MergesAdjacentThinkingBlocks()
    {
        var messages = new IMessage[]
        {
            new TextMessage { Role = Role.User, Text = "Hello" },
            new ReasoningMessage
            {
                Reasoning = "Let me think...",
                Visibility = ReasoningVisibility.Plain,
                Role = Role.Assistant,
            },
            new ReasoningMessage
            {
                Reasoning = "sig-encrypted-blob",
                Visibility = ReasoningVisibility.Encrypted,
                Role = Role.Assistant,
            },
            new TextMessage { Role = Role.Assistant, Text = "Here's my answer." },
        };

        var request = AnthropicRequest.FromMessages(messages);

        // user, assistant (thinking + text)
        Assert.Equal(2, request.Messages.Count);

        var assistantMsg = request.Messages[1];
        Assert.Equal("assistant", assistantMsg.Role);
        Assert.Equal(2, assistantMsg.Content.Count);

        // Merged thinking block
        var thinkingBlock = assistantMsg.Content[0];
        Assert.Equal("thinking", thinkingBlock.Type);
        Assert.Equal("Let me think...", thinkingBlock.Thinking);
        Assert.Equal("sig-encrypted-blob", thinkingBlock.ThinkingSignature);

        // Text block
        var textBlock = assistantMsg.Content[1];
        Assert.Equal("text", textBlock.Type);
        Assert.Equal("Here's my answer.", textBlock.Text);
    }

    /// <summary>
    ///     Two consecutive Plain reasoning messages with no signature stay SEPARATE (the merge
    ///     requires a text+signature pair, which isn't present here). Because neither acquires a
    ///     signature, both are demoted to <c>text</c> blocks — an unsigned <c>thinking</c> block is
    ///     rejected on replay (e.g. Kimi: <c>400 invalid_request_error</c>), so it must never be sent.
    /// </summary>
    [Fact]
    public void FromMessages_DoesNotMerge_WhenBothThinkingBlocksHaveText()
    {
        var messages = new IMessage[]
        {
            new TextMessage { Role = Role.User, Text = "Hello" },
            new ReasoningMessage
            {
                Reasoning = "First thought",
                Visibility = ReasoningVisibility.Plain,
                Role = Role.Assistant,
            },
            new ReasoningMessage
            {
                Reasoning = "Second thought",
                Visibility = ReasoningVisibility.Plain,
                Role = Role.Assistant,
            },
            new TextMessage { Role = Role.Assistant, Text = "Answer." },
        };

        var request = AnthropicRequest.FromMessages(messages);

        Assert.Equal(2, request.Messages.Count);
        var assistantMsg = request.Messages[1];
        Assert.Equal("assistant", assistantMsg.Role);

        // Three content blocks: the two unsigned reasoning blocks stay SEPARATE (not merged) but
        // are demoted to text (no signature → not a valid thinking block on replay), then the answer.
        Assert.Equal(3, assistantMsg.Content.Count);

        Assert.Equal("text", assistantMsg.Content[0].Type);
        Assert.Equal("First thought", assistantMsg.Content[0].Text);

        Assert.Equal("text", assistantMsg.Content[1].Type);
        Assert.Equal("Second thought", assistantMsg.Content[1].Text);

        Assert.Equal("text", assistantMsg.Content[2].Type);
        Assert.Equal("Answer.", assistantMsg.Content[2].Text);

        // Crucially: no unsigned thinking block survives (that's what the backend rejects).
        Assert.DoesNotContain(assistantMsg.Content, c => c.Type == "thinking" && string.IsNullOrEmpty(c.ThinkingSignature));
    }

    /// <summary>
    ///     Tests the JSON wire format for thinking content blocks.
    ///     Verifies that ThinkingSignature maps to "signature" (not "thinkingSignature"),
    ///     and that null fields are omitted by JsonIgnore(WhenWritingNull).
    /// </summary>
    [Fact]
    public void ThinkingContent_SerializesToCorrectJsonFieldNames()
    {
        var options = new System.Text.Json.JsonSerializerOptions
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        };

        // Full thinking block (text + signature)
        var fullBlock = new AnthropicContent
        {
            Type = "thinking",
            Thinking = "my thoughts",
            ThinkingSignature = "sig-blob",
        };
        var fullJson = System.Text.Json.JsonSerializer.Serialize(fullBlock, options);
        Assert.Contains("\"thinking\":\"my thoughts\"", fullJson);
        Assert.Contains("\"signature\":\"sig-blob\"", fullJson);
        Assert.DoesNotContain("thinkingSignature", fullJson);

        // Text-only thinking block (signature omitted)
        var textOnlyBlock = new AnthropicContent
        {
            Type = "thinking",
            Thinking = "my thoughts",
            ThinkingSignature = null,
        };
        var textOnlyJson = System.Text.Json.JsonSerializer.Serialize(textOnlyBlock, options);
        Assert.Contains("\"thinking\":\"my thoughts\"", textOnlyJson);
        Assert.DoesNotContain("signature", textOnlyJson);

        // Signature-only thinking block (thinking text omitted)
        var sigOnlyBlock = new AnthropicContent
        {
            Type = "thinking",
            Thinking = null,
            ThinkingSignature = "sig-blob",
        };
        var sigOnlyJson = System.Text.Json.JsonSerializer.Serialize(sigOnlyBlock, options);
        // "thinking" as a field key (with colon) should NOT appear — only "thinking" as a type value
        Assert.DoesNotContain("\"thinking\":", sigOnlyJson);
        Assert.Contains("\"signature\":\"sig-blob\"", sigOnlyJson);
    }

    /// <summary>
    ///     Regression: a signature-only ("Encrypted") reasoning block with NO adjacent thinking-text
    ///     block to merge with must be DROPPED — never emitted as a <c>thinking</c> block with an empty
    ///     <c>thinking</c> field. This happens whenever the thinking-text and signature halves get
    ///     separated (e.g. a tool_use block lands between them, so the adjacent-merge can't combine
    ///     them), or when only the signature survived in history. Anthropic/Copilot reject the stranded
    ///     block with <c>400 "messages.N.content.M.thinking.thinking: Field required"</c>. The previous
    ///     cleanup only demoted/dropped <em>signatureless</em> blocks, leaving this orphan stranded.
    /// </summary>
    [Fact]
    public void FromMessages_DropsOrphanedSignatureOnlyThinkingBlock()
    {
        var messages = new IMessage[]
        {
            new TextMessage { Role = Role.User, Text = "Hi" },
            // Signature-only reasoning, immediately followed by a non-thinking block: nothing to merge with.
            new ReasoningMessage
            {
                Reasoning = "sig-encrypted-blob",
                Visibility = ReasoningVisibility.Encrypted,
                Role = Role.Assistant,
            },
            new TextMessage { Role = Role.Assistant, Text = "Answer." },
        };

        var request = AnthropicRequest.FromMessages(messages);

        // No thinking block with an empty 'thinking' field may survive — that's exactly what the
        // backend rejects with "thinking.thinking: Field required".
        var allContent = request.Messages.SelectMany(m => m.Content).ToList();
        Assert.DoesNotContain(allContent, c => c.Type == "thinking" && string.IsNullOrEmpty(c.Thinking));
    }

    /// <summary>
    ///     Regression mirroring the real Workspace-Agent failure: an assistant turn whose thinking
    ///     text and signature get split by tool_use blocks. The plain-text half is demoted to text and
    ///     the signature-only half must be dropped, so no empty <c>thinking</c> block reaches the wire.
    /// </summary>
    [Fact]
    public void FromMessages_ThinkingSplitByToolUse_DropsEmptyThinkingBlock()
    {
        var toolCall = new ToolCall
        {
            FunctionName = "Skill",
            FunctionArgs = "{}",
            ToolCallId = "toolu_skill_1",
            ToolCallIdx = 0,
        };
        var aggregate = new ToolsCallAggregateMessage(
            new ToolsCallMessage { ToolCalls = [toolCall], GenerationId = "gen1" },
            new ToolsCallResultMessage
            {
                ToolCallResults = [new ToolCallResult("toolu_skill_1", "ok")],
                GenerationId = "gen1",
            },
            "assistant"
        );

        var messages = new IMessage[]
        {
            new TextMessage { Role = Role.User, Text = "Set up the repos." },
            new TextMessage { Role = Role.Assistant, Text = "I'll help with that." },
            aggregate, // tool_use lands between the assistant text and the trailing signature block
            new ReasoningMessage
            {
                Reasoning = "sig-encrypted-blob",
                Visibility = ReasoningVisibility.Encrypted,
                Role = Role.Assistant,
            },
            new TextMessage { Role = Role.Assistant, Text = "Done." },
        };

        var request = AnthropicRequest.FromMessages(messages);

        var allContent = request.Messages.SelectMany(m => m.Content).ToList();
        Assert.DoesNotContain(allContent, c => c.Type == "thinking" && string.IsNullOrEmpty(c.Thinking));
    }

    /// <summary>
    ///     Regression: an assistant turn whose history interleaves text/thinking AFTER its tool_use
    ///     blocks must be reordered so the tool_use blocks are LAST. Anthropic rejects a turn with a
    ///     text block after a tool_use with 400 "tool_use ids were found without tool_result blocks
    ///     immediately after" — even when the matching tool_result blocks ARE present in the next
    ///     message. Streaming/merge order can place demoted-thinking or trailing text after the calls.
    /// </summary>
    [Fact]
    public void FromMessages_OrdersToolUseLast_WhenTextFollowsToolUseInSameTurn()
    {
        // CompositeMessage flattens these into ONE assistant message: text, tool_use, text.
        var composite = new CompositeMessage
        {
            Messages =
            [
                new TextMessage { Role = Role.Assistant, Text = "The user wants to set up two repos." },
                new ToolCallMessage
                {
                    FunctionName = "Skill",
                    FunctionArgs = "{}",
                    ToolCallId = "toolu_skill_1",
                    ToolCallIdx = 0,
                },
                new TextMessage { Role = Role.Assistant, Text = "I'll help you set up those repos." },
            ],
            Role = Role.Assistant,
            GenerationId = "gen1",
        };

        var messages = new IMessage[]
        {
            new TextMessage { Role = Role.User, Text = "Set up the repos." },
            composite,
        };

        var request = AnthropicRequest.FromMessages(messages);

        var assistant = request.Messages.First(m =>
            m.Role == "assistant" && m.Content.Any(c => c.Type is "tool_use" or "server_tool_use"));

        // No text/thinking block may appear after any tool_use block (Anthropic requires tool_use last).
        var firstToolIdx = assistant.Content.FindIndex(c => c.Type is "tool_use" or "server_tool_use");
        var lastNonToolIdx = assistant.Content.FindLastIndex(c => c.Type is not ("tool_use" or "server_tool_use"));
        Assert.True(
            firstToolIdx > lastNonToolIdx,
            $"tool_use must come after all text/thinking blocks; got: [{string.Join(", ", assistant.Content.Select(c => c.Type))}]");
    }
}
