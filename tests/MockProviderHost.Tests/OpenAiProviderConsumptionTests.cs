using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;
using AchieveAi.LmDotnetTools.MockProviderHost.Tests.Infrastructure;
using AchieveAi.LmDotnetTools.OpenAIProvider.Agents;
using AchieveAi.LmDotnetTools.OpenAIProvider.Models;
using FluentAssertions;

namespace AchieveAi.LmDotnetTools.MockProviderHost.Tests;

/// <summary>
/// End-to-end consumption: an OpenAI-shaped client (LmCore's <see cref="OpenClient"/>) speaks
/// real HTTP to the mock host on its bound port, and the streamed scripted response decodes
/// into the expected assistant text. Validates the wire shape, not just the inner handler.
/// </summary>
public sealed class OpenAiProviderConsumptionTests
{
    [Fact]
    public async Task OpenClient_streams_scripted_text_through_the_host()
    {
        var responder = ScriptedSseResponder.New()
            .ForRole("parent", ctx => ctx.SystemPromptContains("helpful assistant"))
                .Turn(t => t.Text("hello from the mock host"))
            .Build();

        await using var fixture = await MockProviderHostFixture.StartAsync(responder);
        using var httpClient = new HttpClient();
        var openClient = new OpenClient(httpClient, fixture.BaseUrl + "/v1");

        var request = new ChatCompletionRequest(
            "gpt-test",
            [
                new ChatMessage { Role = RoleEnum.System, Content = ChatMessage.CreateContent("You are a helpful assistant.") },
                new ChatMessage { Role = RoleEnum.User, Content = ChatMessage.CreateContent("say hello") },
            ])
        {
            Stream = true,
        };

        var collected = new System.Text.StringBuilder();
        await foreach (var chunk in openClient.StreamingChatCompletionsAsync(request))
        {
            var content = chunk.Choices?.FirstOrDefault()?.Delta?.Content;
            if (content is null || !content.Is<string>())
            {
                continue;
            }
            var text = content.Get<string>();
            if (!string.IsNullOrEmpty(text))
            {
                _ = collected.Append(text);
            }
        }

        collected.ToString().Should().Contain("hello from the mock host");
        responder.RemainingTurns["parent"].Should().Be(0);
    }
}
