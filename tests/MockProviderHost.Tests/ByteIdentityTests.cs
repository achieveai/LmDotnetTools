using System.Text;
using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;
using AchieveAi.LmDotnetTools.MockProviderHost.Tests.Infrastructure;
using FluentAssertions;

namespace AchieveAi.LmDotnetTools.MockProviderHost.Tests;

/// <summary>
/// Asserts the wrapper-only invariant: every byte the host streams over the wire was produced
/// by the inner <see cref="ScriptedSseResponder"/>. A <see cref="TeeHandler"/> wraps each inner
/// handler and captures the bytes it produced; the test then compares those captured bytes
/// against what the HTTP client received from the host.
/// </summary>
public sealed class ByteIdentityTests
{
    private const string OpenAiRequestBody = """
        {
          "model": "gpt-test",
          "stream": true,
          "messages": [
            {"role": "system", "content": "You are a helpful assistant."},
            {"role": "user", "content": "say hello"}
          ]
        }
        """;

    private const string AnthropicRequestBody = """
        {
          "model": "claude-test",
          "stream": true,
          "max_tokens": 1024,
          "system": "You are a helpful assistant.",
          "messages": [
            {"role": "user", "content": "say hello"}
          ]
        }
        """;

    [Fact]
    public async Task OpenAi_endpoint_streams_inner_handler_bytes_unchanged()
    {
        var responder = BuildResponder();
        using var openAiTee = new TeeHandler(responder.AsOpenAiHandler());
        using var anthropicTee = new TeeHandler(responder.AsAnthropicHandler());

        await using var fixture = await MockProviderHostFixture.StartAsync(openAiTee, anthropicTee);
        using var client = new HttpClient { BaseAddress = new Uri(fixture.BaseUrl) };
        using var content = new StringContent(OpenAiRequestBody, Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("/v1/chat/completions", content);
        response.EnsureSuccessStatusCode();
        var wireBytes = await response.Content.ReadAsByteArrayAsync();

        wireBytes.Should().Equal(openAiTee.CapturedBytes);
        Encoding.UTF8.GetString(wireBytes).Should().Contain("data:");
        anthropicTee.CapturedBytes.Should().BeEmpty("anthropic handler must not be touched on the openai route");
    }

    [Fact]
    public async Task Anthropic_endpoint_streams_inner_handler_bytes_unchanged()
    {
        var responder = BuildResponder();
        using var openAiTee = new TeeHandler(responder.AsOpenAiHandler());
        using var anthropicTee = new TeeHandler(responder.AsAnthropicHandler());

        await using var fixture = await MockProviderHostFixture.StartAsync(openAiTee, anthropicTee);
        using var client = new HttpClient { BaseAddress = new Uri(fixture.BaseUrl) };
        using var content = new StringContent(AnthropicRequestBody, Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("/v1/messages", content);
        response.EnsureSuccessStatusCode();
        var wireBytes = await response.Content.ReadAsByteArrayAsync();

        wireBytes.Should().Equal(anthropicTee.CapturedBytes);
        Encoding.UTF8.GetString(wireBytes).Should().Contain("event: message_start");
        openAiTee.CapturedBytes.Should().BeEmpty("openai handler must not be touched on the anthropic route");
    }

    private static ScriptedSseResponder BuildResponder() =>
        ScriptedSseResponder.New()
            .ForRole("parent", ctx => ctx.SystemPromptContains("helpful assistant"))
                .Turn(t => t.Text("Hello from the scripted parent."))
            .Build();
}
