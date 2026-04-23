using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;
using FluentAssertions;
using LmStreaming.Sample.E2E.Tests.Infrastructure;

namespace LmStreaming.Sample.E2E.Tests.Scenarios;

/// <summary>
/// Smoke tests for the scripted SSE harness: a single user turn is serviced by a single
/// parent-role plan that streams back plain text. Verifies both OpenAI and Anthropic wire
/// paths end-to-end through the sample's <c>/ws</c> endpoint.
/// </summary>
public sealed class BasicStreamingTests
{
    [Theory]
    [InlineData("test")]
    [InlineData("test-anthropic")]
    public async Task Parent_streams_plain_text_to_client(string providerMode)
    {
        var responder = ScriptedSseResponder.New()
            .ForRole("parent", ctx => ctx.SystemPromptContains("helpful assistant"))
                .Turn(t => t.Text("Hello from the scripted parent."))
            .Build();

        var handler = providerMode == "test-anthropic"
            ? responder.AsAnthropicHandler()
            : responder.AsOpenAiHandler();

        var builder = new E2EWebAppFactory.ScriptedBuilder(handler);
        using var factory = new E2EWebAppFactory(providerMode, builder);

        var threadId = $"basic-{providerMode}-{Guid.NewGuid():N}";
        var socket = await factory.ConnectWebSocketAsync(threadId);
        await using var client = new WebSocketTestClient(socket);

        await client.SendUserMessageAsync("say hello");
        using var frames = await client.CollectUntilDoneAsync(TimeSpan.FromSeconds(15));

        var streamedText = frames.ConcatText();
        streamedText.Should().Contain("scripted parent");
        responder.RemainingTurns["parent"].Should().Be(0);
    }
}
