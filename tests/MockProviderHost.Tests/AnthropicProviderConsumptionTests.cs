using AchieveAi.LmDotnetTools.AnthropicProvider.Agents;
using AchieveAi.LmDotnetTools.AnthropicProvider.Models;
using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;
using FluentAssertions;

namespace AchieveAi.LmDotnetTools.MockProviderHost.Tests;

/// <summary>
/// End-to-end consumption: an Anthropic-shaped client (LmCore's <see cref="AnthropicClient"/>)
/// speaks real HTTP to the mock host on its bound port, and the streamed scripted response
/// decodes into the expected assistant text. Validates the wire shape, not just the inner handler.
/// </summary>
public sealed class AnthropicProviderConsumptionTests
{
    [Fact]
    public async Task AnthropicClient_streams_scripted_text_through_the_host()
    {
        var responder = ScriptedSseResponder.New()
            .ForRole("parent", ctx => ctx.SystemPromptContains("helpful assistant"))
                .Turn(t => t.Text("hello from the anthropic mock"))
            .Build();

        await using var fixture = await EphemeralHostFixture.StartAsync(responder);
        using var httpClient = new HttpClient();
        var client = new AnthropicClient(httpClient, baseUrl: fixture.BaseUrl + "/v1");

        var request = new AnthropicRequest
        {
            Model = "claude-test",
            MaxTokens = 1024,
            Stream = true,
            System = "You are a helpful assistant.",
            Messages =
            [
                new AnthropicMessage
                {
                    Role = "user",
                    Content = [new AnthropicContent { Type = "text", Text = "say hello" }],
                },
            ],
        };

        var collected = new System.Text.StringBuilder();
        var stream = await client.StreamingChatCompletionsAsync(request);
        await foreach (var streamEvent in stream)
        {
            if (streamEvent is AnthropicContentBlockDeltaEvent { Delta: AnthropicTextDelta textDelta })
            {
                _ = collected.Append(textDelta.Text);
            }
        }

        collected.ToString().Should().Contain("hello from the anthropic mock");
        responder.RemainingTurns["parent"].Should().Be(0);
    }
}
