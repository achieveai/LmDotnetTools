using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmTestUtils;
using AchieveAi.LmDotnetTools.OpenAIProvider.Agents;
using AchieveAi.LmDotnetTools.OpenAIProvider.Models;

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
