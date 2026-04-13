using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Utils;
using AchieveAi.LmDotnetTools.LmTestUtils;
using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace AchieveAi.LmDotnetTools.AnthropicProvider.Tests.Agents;

public class PromptCachingE2ETests : LoggingTestBase
{
    public PromptCachingE2ETests(ITestOutputHelper output)
        : base(output) { }

    [Fact]
    public async Task StreamingWithCaching_RequestHasCacheControl_ResponseHasCacheMetrics()
    {
        Logger.LogInformation("Starting prompt caching E2E test");

        var requestCapture = new RequestCapture();
        var httpClient = TestModeHttpClientFactory.CreateAnthropicTestClient(
            LoggerFactory, requestCapture, chunkDelayMs: 0
        );
        var anthropicClient = new AnthropicClient("test-api-key", httpClient: httpClient);
        var agent = new AnthropicAgent(
            "TestAgent", anthropicClient, LoggerFactory.CreateLogger<AnthropicAgent>()
        );

        var messages = new IMessage[]
        {
            new TextMessage { Role = Role.System, Text = "You are a helpful assistant." },
            new TextMessage { Role = Role.User, Text = "What's the weather?" },
        };

        var options = new GenerateReplyOptions
        {
            ModelId = "claude-sonnet-4-20250514",
            PromptCaching = PromptCachingMode.Auto,
            BuiltInTools = [new AnthropicWebSearchTool { MaxUses = 5 }],
            Functions =
            [
                new FunctionContract
                {
                    Name = "get_weather",
                    Description = "Get weather info",
                    Parameters =
                    [
                        new FunctionParameterContract
                        {
                            Name = "city",
                            ParameterType = SchemaHelper.CreateJsonSchemaFromType(typeof(string)),
                        },
                    ],
                },
            ],
        };

        // Act - stream the response
        var responseMessages = new List<IMessage>();
        await foreach (var msg in await agent.GenerateReplyStreamingAsync(messages, options))
        {
            responseMessages.Add(msg);
            Logger.LogDebug("Streamed: {MessageType}", msg.GetType().Name);
        }

        // Assert - Request validation
        var capturedRequest = requestCapture.GetAnthropicRequest();
        Assert.NotNull(capturedRequest);

        // System should be in array form (caching converts string to array)
        Assert.True(capturedRequest.SystemIsArray, "System should be in array form when caching is enabled");

        // System text should still be accessible
        Assert.Equal("You are a helpful assistant.", capturedRequest.System);

        // System array should have cache_control on last block
        var systemRaw = capturedRequest.SystemRaw;
        Assert.NotNull(systemRaw);
        Assert.Equal(JsonValueKind.Array, systemRaw.Value.ValueKind);
        var lastSystemBlock = systemRaw.Value[systemRaw.Value.GetArrayLength() - 1];
        Assert.True(
            lastSystemBlock.TryGetProperty("cache_control", out _),
            "Last system content block should have cache_control"
        );

        // Last tool should have cache_control
        var tools = capturedRequest.Tools.ToList();
        Assert.Equal(2, tools.Count);
        Assert.False(tools[0].HasCacheControl, "First tool should not have cache_control");
        Assert.True(tools[1].HasCacheControl, "Last tool should have cache_control");

        // Assert - Response validation
        var usageMessage = responseMessages.OfType<UsageMessage>().FirstOrDefault();
        Assert.NotNull(usageMessage);

        // Standard usage should be present
        Assert.True(usageMessage.Usage.CompletionTokens > 0, "CompletionTokens should be positive");

        Logger.LogInformation(
            "E2E test passed: cache_control on request, usage on response. " +
            "CachedTokens={CachedTokens}, CompletionTokens={CompletionTokens}",
            usageMessage.Usage.TotalCachedTokens,
            usageMessage.Usage.CompletionTokens
        );
    }
}
