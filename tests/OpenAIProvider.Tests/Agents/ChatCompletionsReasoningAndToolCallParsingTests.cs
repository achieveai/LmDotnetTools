using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmTestUtils;
using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
using AchieveAi.LmDotnetTools.OpenAIProvider.Agents;
using AchieveAi.LmDotnetTools.OpenAIProvider.Models;
using Microsoft.Extensions.Logging;

namespace AchieveAi.LmDotnetTools.OpenAIProvider.Tests.Agents;

/// <summary>
///     Drives the real <see cref="OpenClientAgent" /> over chat-completions payloads whose tool calls,
///     reasoning and text must all survive parsing: <c>content: null</c> beside other output, reasoning
///     in the same chunk as a tool call, payload on a final chunk without a role, and zero-token usage
///     riding on a content chunk.
/// </summary>
public sealed class ChatCompletionsReasoningAndToolCallParsingTests
{
    private const string BaseUrl = "http://test-mode/v1";

    private static OpenClientAgent AgentOver(HttpMessageHandler handler) =>
        new("TestAgent", new OpenClient(new HttpClient(handler), BaseUrl));

    private static async Task<List<IMessage>> StreamAsync(params string[] chunks)
    {
        var agent = AgentOver(FakeHttpMessageHandler.CreateSimpleSseStreamHandler([.. chunks, "[DONE]"]));
        var stream = await agent.GenerateReplyStreamingAsync(
            [new TextMessage { Role = Role.User, Text = "hi" }],
            new GenerateReplyOptions { ModelId = "m" }
        );
        var messages = new List<IMessage>();
        await foreach (var message in stream)
        {
            messages.Add(message);
        }

        return messages;
    }

    private static Task<IEnumerable<IMessage>> ReplyAsync(string responseJson) =>
        AgentOver(FakeHttpMessageHandler.CreateSimpleJsonHandler(responseJson))
            .GenerateReplyAsync(
                [new TextMessage { Role = Role.User, Text = "hi" }],
                new GenerateReplyOptions { ModelId = "m" }
            );

    [Fact]
    public async Task Reply_with_tool_calls_and_null_content_yields_the_tool_calls()
    {
        // OpenAI's own non-streaming shape for a tool call.
        var reply = await ReplyAsync(
            """
            {"id":"c1","object":"chat.completion","model":"m","choices":[{"index":0,"finish_reason":"tool_calls",
             "message":{"role":"assistant","content":null,
              "tool_calls":[{"id":"t1","type":"function","function":{"name":"calc","arguments":"{}"}}]}}],
             "usage":{"prompt_tokens":5,"completion_tokens":5,"total_tokens":10}}
            """
        );

        Assert.Equal("calc", Assert.Single(Assert.Single(reply.OfType<ToolsCallMessage>()).ToolCalls).FunctionName);
    }

    [Fact]
    public async Task Reply_tool_turn_replays_reasoning_on_the_tool_call_message_with_nothing_between_call_and_result()
    {
        var reply = await ReplyAsync(
            """
            {"id":"c1","object":"chat.completion","model":"m","choices":[{"index":0,"finish_reason":"tool_calls",
             "message":{"role":"assistant","content":"","reasoning_details":[{"type":"reasoning.encrypted","data":"SIG"}],
              "tool_calls":[{"id":"t1","type":"function","function":{"name":"calc","arguments":"{}"}}]}}],
             "usage":{"prompt_tokens":5,"completion_tokens":5,"total_tokens":10}}
            """
        );

        var request = ChatCompletionRequest.FromMessages(
            [
                new TextMessage { Role = Role.User, Text = "hi" },
                .. reply,
                new ToolsCallResultMessage { ToolCallResults = [new ToolCallResult("t1", "42")] },
            ],
            new GenerateReplyOptions { ModelId = "m" }
        );

        // An assistant message between the call and its result is rejected by the API.
        Assert.Equal([RoleEnum.User, RoleEnum.Assistant, RoleEnum.Tool], request.Messages.Select(m => m.Role!.Value));
        var detail = Assert.Single(request.Messages[1].ReasoningDetails ?? []);
        Assert.Equal(("reasoning.encrypted", "SIG"), (detail.Type, detail.Data));
    }

    [Fact]
    public async Task Stream_reasoning_chunk_with_null_content_yields_reasoning_then_text()
    {
        var messages = await StreamAsync(
            """{"id":"g","choices":[{"index":0,"delta":{"role":"assistant","content":null,"reasoning_content":"think"}}]}""",
            """{"id":"g","choices":[{"index":0,"delta":{"content":"answer"}}]}""",
            """{"id":"g","choices":[{"index":0,"delta":{"role":"assistant","content":""},"finish_reason":"stop"}]}"""
        );

        Assert.Equal("think", Assert.Single(messages.OfType<ReasoningUpdateMessage>()).Reasoning);
        Assert.Equal("answer", Assert.Single(messages.OfType<TextUpdateMessage>()).Text);
    }

    [Fact]
    public async Task Stream_tool_call_chunk_keeps_reasoning_from_the_same_chunk_ahead_of_the_call()
    {
        var messages = await StreamAsync(
            """{"id":"g","choices":[{"index":0,"delta":{"role":"assistant","content":"","reasoning_details":[{"type":"reasoning.encrypted","data":"SIG"}],"tool_calls":[{"index":0,"id":"t1","type":"function","function":{"name":"calc","arguments":"{}"}}]}}]}""",
            """{"id":"g","choices":[{"index":0,"delta":{"role":"assistant","content":""},"finish_reason":"tool_calls"}]}"""
        );

        var signature = messages.FindIndex(m => m is ReasoningMessage { Reasoning: "SIG" });
        var call = messages.FindIndex(m => m is ToolsCallUpdateMessage);
        Assert.True(signature >= 0 && call > signature, $"signature at {signature}, tool call at {call}");
    }

    [Fact]
    public async Task Stream_final_chunk_without_a_role_keeps_its_tool_calls()
    {
        var messages = await StreamAsync(
            """{"id":"g","choices":[{"index":0,"delta":{"role":"assistant","content":""}}]}""",
            """{"id":"g","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"t1","type":"function","function":{"name":"calc","arguments":"{}"}}]},"finish_reason":"tool_calls"}]}"""
        );

        var update = Assert.Single(Assert.Single(messages.OfType<ToolsCallUpdateMessage>()).ToolCallUpdates);
        Assert.Equal("calc", update.FunctionName);
    }

    [Theory]
    [InlineData("stop")]
    [InlineData("length")]
    [InlineData("model_length")]
    [InlineData("error")]
    [InlineData("tool_calls")]
    [InlineData("eos")]
    [InlineData("end_turn")]
    public async Task Stream_standalone_empty_terminal_response_logs_reason_without_changing_output(string finishReason)
    {
        var logger = new ListLogger();
        using var http = new HttpClient(
            FakeHttpMessageHandler.CreateSimpleSseStreamHandler([
                $$$"""{"id":"empty","choices":[{"index":0,"delta":{},"finish_reason":"{{{finishReason}}}"}],"usage":{"prompt_tokens":29534,"completion_tokens":0,"total_tokens":29534}}""",
                "[DONE]",
            ])
        );
        using var client = new OpenClient(http, BaseUrl, logger: logger);
        var agent = new OpenClientAgent("TestAgent", client);
        var stream = await agent.GenerateReplyStreamingAsync(
            [new TextMessage { Role = Role.User, Text = "PRIVATE_PROMPT" }],
            new GenerateReplyOptions { ModelId = "m" }
        );
        var messages = new List<IMessage>();
        await foreach (var message in stream)
        {
            messages.Add(message);
        }

        Assert.IsType<UsageMessage>(Assert.Single(messages));
        var entry = Assert.Single(
            logger.Entries,
            e => e.Message.StartsWith("Streaming response metadata", StringComparison.Ordinal)
        );
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Contains(
            $"ChoiceCount: 1, FinishReason: {finishReason}, HasDelta: True, HasContent: False, HasToolCalls: False, HasReasoning: False",
            entry.Message
        );
        Assert.Contains("PromptTokens: 29534, CompletionTokens: 0, TotalTokens: 29534", entry.Message);
        Assert.All(logger.Entries, e => Assert.DoesNotContain("PRIVATE_PROMPT", e.Message));
    }

    [Fact]
    public async Task Stream_metadata_omits_payload_and_skips_nonterminal_chunks()
    {
        var logger = new ListLogger();
        using var http = new HttpClient(
            FakeHttpMessageHandler.CreateSimpleSseStreamHandler([
                """{"id":"c","choices":[{"index":0,"delta":{"role":"assistant","content":"PRIVATE_PARTIAL"}}]}""",
                """{"id":"c","choices":[{"index":0,"delta":{"content":"PRIVATE_CONTENT","reasoning_content":"PRIVATE_REASONING","reasoning_details":[{"type":"reasoning.encrypted","data":"PRIVATE_SIGNATURE"}],"tool_calls":[{"index":0,"id":"t1","type":"function","function":{"name":"PRIVATE_TOOL","arguments":"PRIVATE_ARGUMENTS"}}]},"finish_reason":"tool_calls"}],"usage":{"prompt_tokens":0,"completion_tokens":0,"total_tokens":0}}""",
                "[DONE]",
            ])
        );
        using var client = new OpenClient(http, BaseUrl, logger: logger);
        var request = ChatCompletionRequest.FromMessages(
            [new TextMessage { Role = Role.User, Text = "PRIVATE_PROMPT" }],
            new GenerateReplyOptions { ModelId = "m" }
        );
        var stream = client.StreamingChatCompletionsAsync(request);
        var responses = new List<ChatCompletionResponse>();
        await foreach (var response in stream)
        {
            responses.Add(response);
        }

        Assert.Equal(2, responses.Count);
        Assert.Equal("PRIVATE_CONTENT", responses[1].Choices![0].Delta!.Content!.Get<string>());
        var entry = Assert.Single(
            logger.Entries,
            e => e.Message.StartsWith("Streaming response metadata", StringComparison.Ordinal)
        );
        Assert.Contains(
            "FinishReason: tool_calls, HasDelta: True, HasContent: True, HasToolCalls: True, HasReasoning: True",
            entry.Message
        );
        Assert.All(logger.Entries, e => Assert.DoesNotContain("PRIVATE_", e.Message));
    }

    [Theory]
    [InlineData("[]", "ChoiceCount: 0, FinishReason: none, HasDelta: False")]
    [InlineData("[{\"index\":0,\"finish_reason\":\"stop\"}]", "ChoiceCount: 1, FinishReason: stop, HasDelta: False")]
    public async Task Stream_metadata_handles_usage_only_and_missing_terminal_delta(string choices, string expected)
    {
        var logger = new ListLogger();
        using var http = new HttpClient(
            FakeHttpMessageHandler.CreateSimpleSseStreamHandler([
                $$$"""{"id":"c","choices":{{{choices}}},"usage":{"prompt_tokens":29534,"completion_tokens":0,"total_tokens":29534}}""",
                "[DONE]",
            ])
        );
        using var client = new OpenClient(http, BaseUrl, logger: logger);
        var request = ChatCompletionRequest.FromMessages(
            [new TextMessage { Role = Role.User, Text = "hi" }],
            new GenerateReplyOptions { ModelId = "m" }
        );
        var stream = client.StreamingChatCompletionsAsync(request);
        var count = 0;
        await foreach (var response in stream)
        {
            count++;
        }

        Assert.Equal(1, count);
        var entry = Assert.Single(
            logger.Entries,
            e => e.Message.StartsWith("Streaming response metadata", StringComparison.Ordinal)
        );
        Assert.Contains(expected, entry.Message);
    }

    [Fact]
    public async Task Stream_content_chunk_carrying_zero_token_usage_is_kept()
    {
        var messages = await StreamAsync(
            """{"id":"g","choices":[{"index":0,"delta":{"role":"assistant","content":"hello"}}],"usage":{"prompt_tokens":0,"completion_tokens":0,"total_tokens":0}}""",
            """{"id":"g","choices":[{"index":0,"delta":{"role":"assistant","content":""},"finish_reason":"stop"}],"usage":{"prompt_tokens":0,"completion_tokens":0,"total_tokens":0}}"""
        );

        Assert.Equal("hello", Assert.Single(messages.OfType<TextUpdateMessage>()).Text);
        Assert.Empty(messages.OfType<UsageMessage>());
    }
}
