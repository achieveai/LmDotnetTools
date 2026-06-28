using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmTestUtils;
using AchieveAi.LmDotnetTools.OpenAIProvider.Agents;

namespace AchieveAi.LmDotnetTools.OpenAIProvider.Tests.Agents;

/// <summary>
/// Regression: OpenAI Chat Completions reports prompt-cache hits and reasoning spend in the
/// <c>prompt_tokens_details.cached_tokens</c> / <c>completion_tokens_details.reasoning_tokens</c>
/// nested objects (distinct from the Responses API's <c>input_tokens_details</c> naming). The
/// emitted <see cref="UsageMessage"/> must carry those through to <c>TotalCachedTokens</c> /
/// <c>TotalReasoningTokens</c>; otherwise every chat-completions cache hit is reported as zero.
/// Drives the real <see cref="OpenClientAgent"/> against a canned chat-completion response.
/// </summary>
public sealed class ChatCompletionsCacheTokenUsageTests
{
    private const string BaseUrl = "http://test-mode/v1";

    // A non-streaming Chat Completions body whose usage carries the chat-completions-style nested
    // detail objects (13696 of 14986 prompt tokens cached; 64 reasoning tokens).
    private const string ResponseJson = """
        {
          "id": "chatcmpl-cache-test",
          "object": "chat.completion",
          "created": 0,
          "model": "gpt-4o",
          "choices": [
            { "index": 0, "message": { "role": "assistant", "content": "hi" }, "finish_reason": "stop" }
          ],
          "usage": {
            "prompt_tokens": 14986,
            "completion_tokens": 121,
            "total_tokens": 15107,
            "prompt_tokens_details": { "cached_tokens": 13696 },
            "completion_tokens_details": { "reasoning_tokens": 64 }
          }
        }
        """;

    [Fact]
    public async Task UsageMessage_preserves_cached_and_reasoning_tokens_from_chat_completions()
    {
        using var httpClient = new HttpClient(FakeHttpMessageHandler.CreateSimpleJsonHandler(ResponseJson));
        var client = new OpenClient(httpClient, BaseUrl);
        var agent = new OpenClientAgent("TestAgent", client);

        var response = await agent.GenerateReplyAsync(
            [new TextMessage { Role = Role.User, Text = "hi" }],
            new GenerateReplyOptions { ModelId = "gpt-4o" }
        );

        var usageMessage = Assert.Single(response.OfType<UsageMessage>());
        var usage = usageMessage.Usage;
        Assert.Equal(14986, usage.PromptTokens);
        Assert.Equal(121, usage.CompletionTokens);
        // Chat Completions reported 13696 cached tokens (prompt_tokens_details.cached_tokens)
        // and 64 reasoning tokens (completion_tokens_details.reasoning_tokens).
        Assert.Equal(13696, usage.TotalCachedTokens);
        Assert.Equal(64, usage.TotalReasoningTokens);
    }
}
