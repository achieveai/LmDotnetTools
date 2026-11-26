using System.Runtime.CompilerServices;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.OpenAiResponsesProvider.Agents;
using AchieveAi.LmDotnetTools.OpenAiResponsesProvider.Models;
using FluentAssertions;

namespace AchieveAi.LmDotnetTools.OpenAiResponsesProvider.Tests;

/// <summary>
/// Regression: the OpenAI Responses API (and the GitHub Copilot proxy of it) reports prompt-cache
/// hits and reasoning tokens via the NESTED detail objects on the usage block —
/// <c>input_tokens_details.cached_tokens</c> and <c>output_tokens_details.reasoning_tokens</c>.
/// <see cref="OpenAiResponsesAgent"/> must surface those on the emitted <see cref="UsageMessage"/>
/// (the core <see cref="AchieveAi.LmDotnetTools.LmCore.Models.Usage"/> model exposes them as
/// <c>TotalCachedTokens</c> / <c>TotalReasoningTokens</c>), otherwise every cache hit is silently
/// reported as zero and cost/cache telemetry is wrong.
/// </summary>
public sealed class CacheTokenUsageRegressionTests
{
    // Verbatim usage block from a live OpenAI Responses `response.completed` event, captured in
    // samples/MockProviderHost/fixtures/openai-responses-websocket/sample_ws_stream_001.redacted.json
    // (13,696 of 14,986 input tokens served from cache — a ~91% hit). reasoning_tokens is bumped to a
    // non-zero value here so the same test also guards the reasoning-token detail path.
    private const string CompletedEventJson = """
        {
          "type": "response.completed",
          "response": {
            "id": "resp_regression",
            "object": "response",
            "status": "completed",
            "usage": {
              "input_tokens": 14986,
              "input_tokens_details": { "cached_tokens": 13696 },
              "output_tokens": 121,
              "output_tokens_details": { "reasoning_tokens": 64 },
              "total_tokens": 15107
            }
          }
        }
        """;

    [Fact]
    public async Task UsageMessage_preserves_cached_and_reasoning_tokens_from_responses_api()
    {
        var completed = ResponseEventParser.Parse(CompletedEventJson);
        using var agent = new OpenAiResponsesAgent("test-agent", new StubResponsesClient(completed));

        var stream = await agent.GenerateReplyStreamingAsync([new TextMessage { Role = Role.User, Text = "hi" }]);

        UsageMessage? usage = null;
        await foreach (var m in stream)
        {
            if (m is UsageMessage u)
            {
                usage = u;
            }
        }

        usage.Should().NotBeNull("the response.completed event carries a usage block");

        // Baseline counts already map correctly today.
        usage!.Usage.PromptTokens.Should().Be(14986);
        usage.Usage.CompletionTokens.Should().Be(121);
        usage.Usage.TotalTokens.Should().Be(15107);

        // The bug: the nested *_tokens_details objects are dropped, so cache/reasoning read 0.
        usage
            .Usage.TotalCachedTokens.Should()
            .Be(13696, "the Responses API reported 13696 cached input tokens in input_tokens_details.cached_tokens");
        usage
            .Usage.TotalReasoningTokens.Should()
            .Be(64, "the Responses API reported 64 reasoning tokens in output_tokens_details.reasoning_tokens");
    }

    [Theory]
    [InlineData(
        """{ "cached_tokens": 0, "cache_write_tokens": 1975, "cache_write_tokens_details": { "ttl_30m_tokens": 1975, "ttl_12h_tokens": 0 } }""",
        0,
        1975L,
        0L
    )]
    [InlineData("""{ "cached_tokens": 1975, "cache_write_tokens": 0 }""", 1975, 0L, 0L)]
    [InlineData(
        """{ "cached_tokens": 1975, "cache_write_tokens": 0, "cache_write_tokens_details": { "ttl_30m_tokens": 0, "ttl_12h_tokens": 0 } }""",
        1975,
        0L,
        0L
    )]
    [InlineData(
        """{ "cached_tokens": 1975, "cache_write_tokens": 0, "cache_write_tokens_details": { "ttl_30m_tokens": 0, "ttl_12h_tokens": 1 } }""",
        1975,
        null,
        null
    )]
    [InlineData(
        """{ "cached_tokens": 1975, "cache_write_tokens": 0, "cache_write_tokens_details": null }""",
        1975,
        null,
        null
    )]
    [InlineData("""{ "cached_tokens": 1975 }""", 1975, null, null)]
    [InlineData("""{ "cached_tokens": -1, "cache_write_tokens": 0 }""", 0, null, null)]
    [InlineData("""{ "cache_write_tokens": 100 }""", 0, null, null)]
    [InlineData("""{ "cached_tokens": 1975, "cache_write_tokens": 4 }""", 1975, null, null)]
    [InlineData("""{ "cached_tokens": 0, "cache_write_tokens": -1 }""", 0, null, null)]
    [InlineData("""{ "cached_tokens": 0, "cache_write_tokens": "1975" }""", 0, null, null)]
    [InlineData(
        """{ "cached_tokens": 0, "cache_write_tokens": 1975, "cache_write_tokens_details": { "ttl_30m_tokens": 1000, "ttl_12h_tokens": 975 } }""",
        0,
        1975L,
        975L
    )]
    [InlineData(
        """{ "cached_tokens": 0, "cache_write_tokens": 1975, "cache_write_tokens_details": { "ttl_30m_tokens": 1975 } }""",
        0,
        1975L,
        null
    )]
    [InlineData(
        """{ "cached_tokens": 0, "cache_write_tokens": 1975, "cache_write_tokens_details": { "ttl_30m_tokens": 1976, "ttl_12h_tokens": -1 } }""",
        0,
        1975L,
        null
    )]
    [InlineData(
        """{ "cached_tokens": 0, "cache_write_tokens": 1975, "cache_write_tokens_details": { "ttl_30m_tokens": 1975, "ttl_12h_tokens": 1 } }""",
        0,
        1975L,
        null
    )]
    public async Task UsageMessage_preserves_validated_input_inclusive_cache_writes_through_serialization(
        string inputDetails,
        int cachedTokens,
        long? writeTotal,
        long? write12h
    )
    {
        var completed = ResponseEventParser.Parse(
            $$"""
            {
              "type": "response.completed",
              "response": {
                "usage": {
                  "input_tokens": 1978,
                  "input_tokens_details": {{inputDetails}},
                  "output_tokens": 5,
                  "total_tokens": 1983
                }
              }
            }
            """
        );
        using var agent = new OpenAiResponsesAgent("test-agent", new StubResponsesClient(completed));
        var stream = await agent.GenerateReplyStreamingAsync([new TextMessage { Role = Role.User, Text = "hi" }]);
        UsageMessage? message = null;
        await foreach (var item in stream)
        {
            if (item is UsageMessage usage)
            {
                message = usage;
            }
        }

        message.Should().NotBeNull();
        var persisted = JsonSerializer.Deserialize<Usage>(JsonSerializer.Serialize(message!.Usage));
        persisted!.PromptTokens.Should().Be(1978);
        persisted.TotalTokens.Should().Be(1983);
        persisted.TotalCachedTokens.Should().Be(cachedTokens);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(persisted));
        var details = json.RootElement.TryGetProperty("input_tokens_details", out var value) ? value : default;
        ReadCacheWrite(details, "responses_cache_write_tokens").Should().Be(writeTotal);
        ReadCacheWrite(details, "responses_cache_write_12h_tokens").Should().Be(write12h);
    }

    private static long? ReadCacheWrite(JsonElement details, string property) =>
        details.ValueKind == JsonValueKind.Object && details.TryGetProperty(property, out var count)
            ? count.GetInt64()
            : null;

    /// <summary>Minimal <see cref="IOpenAiResponsesClient"/> that replays a fixed event sequence.</summary>
    private sealed class StubResponsesClient : IOpenAiResponsesClient
    {
        private readonly ResponseEvent[] _events;

        public StubResponsesClient(params ResponseEvent[] events) => _events = events;

        public async IAsyncEnumerable<ResponseEvent> StreamResponseAsync(
            ResponseCreateRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default
        )
        {
            foreach (var ev in _events)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return ev;
                await Task.Yield();
            }
        }

        public void Dispose() { }
    }
}
