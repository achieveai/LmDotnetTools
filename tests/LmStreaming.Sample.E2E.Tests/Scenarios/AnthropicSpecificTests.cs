using System.Text.Json;
using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;
using FluentAssertions;
using LmStreaming.Sample.E2E.Tests.Infrastructure;

namespace LmStreaming.Sample.E2E.Tests.Scenarios;

/// <summary>
/// Anthropic-only features that have no OpenAI counterpart: extended thinking deltas and
/// cache-creation / cache-read token metrics on the <c>message_start</c> usage event.
/// These exercise <c>AnthropicSseStreamHttpContent</c> plumbing surfaced via the scripted
/// responder.
/// </summary>
public sealed class AnthropicSpecificTests
{
    [Fact]
    public async Task Extended_thinking_is_streamed_as_reasoning_frames()
    {
        var responder = ScriptedSseResponder.New()
            .ForRole("parent", ctx => ctx.SystemPromptContains("helpful assistant"))
                .Turn(t => t
                    .Thinking(128)
                    .Text("Final answer after reasoning."))
            .Build();

        var builder = new E2EWebAppFactory.ScriptedBuilder(responder.AsAnthropicHandler());
        using var factory = new E2EWebAppFactory("test-anthropic", builder);

        var threadId = $"anthropic-thinking-{Guid.NewGuid():N}";
        var socket = await factory.ConnectWebSocketAsync(threadId);
        await using var client = new WebSocketTestClient(socket);

        await client.SendUserMessageAsync("think then answer");
        var frames = await client.CollectUntilDoneAsync(TimeSpan.FromSeconds(20));

        var reasoningFrames = frames
            .OfMessageType("reasoning_update")
            .Concat(frames.OfMessageType("reasoning"))
            .ToList();
        reasoningFrames.Should().NotBeEmpty("Anthropic thinking should produce reasoning frames");

        var streamedText = frames.ConcatText();
        streamedText.Should().Contain("Final answer after reasoning");
    }

    [Fact]
    public async Task Cache_metrics_surface_in_usage_frame()
    {
        var responder = ScriptedSseResponder.New()
            .ForRole("parent", ctx => ctx.SystemPromptContains("helpful assistant"))
                .Turn(t => t
                    .CacheMetrics(cacheCreationInputTokens: 42, cacheReadInputTokens: 17)
                    .Text("cached reply"))
            .Build();

        var builder = new E2EWebAppFactory.ScriptedBuilder(responder.AsAnthropicHandler());
        using var factory = new E2EWebAppFactory("test-anthropic", builder);

        var threadId = $"anthropic-cache-{Guid.NewGuid():N}";
        var socket = await factory.ConnectWebSocketAsync(threadId);
        await using var client = new WebSocketTestClient(socket);

        await client.SendUserMessageAsync("hit the cache");
        var frames = await client.CollectUntilDoneAsync(TimeSpan.FromSeconds(20));

        var usageFrame = frames
            .OfMessageType("usage")
            .FirstOrDefault(f => HasCacheMetrics(f));
        usageFrame.ValueKind.Should().NotBe(JsonValueKind.Undefined,
            "a usage frame with cache-creation/cache-read metrics should be emitted");
    }

    private static bool HasCacheMetrics(JsonElement frame)
    {
        if (!frame.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        return HasPositiveInt(usage, "cache_creation_input_tokens")
            || HasPositiveInt(usage, "cache_read_input_tokens")
            || HasExtraProperty(usage, "cache_creation_input_tokens")
            || HasExtraProperty(usage, "cache_read_input_tokens");
    }

    private static bool HasPositiveInt(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var prop)
            && prop.ValueKind == JsonValueKind.Number
            && prop.GetInt32() > 0;

    private static bool HasExtraProperty(JsonElement usage, string name)
    {
        if (!usage.TryGetProperty("extra_properties", out var extras)
            || extras.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        return HasPositiveInt(extras, name);
    }
}
