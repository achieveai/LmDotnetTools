using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;

namespace LmStreaming.Sample.Services;

/// <summary>
/// Default <see cref="ITestAgentBuilder"/> implementation used when no test override is registered.
/// Preserves the historical inline handler construction (3 words/chunk, 300 ms delay) and never
/// provides sub-agent options, so production wiring is identical to pre-refactor behavior.
/// </summary>
internal sealed class DefaultTestAgentBuilder : ITestAgentBuilder
{
    private const int DefaultWordsPerChunk = 3;
    private const int DefaultChunkDelayMs = 300;

    public HttpMessageHandler CreateHandler(string providerMode, ILoggerFactory loggerFactory)
    {
        if (string.Equals(providerMode, "test-anthropic", StringComparison.OrdinalIgnoreCase))
        {
            return new AnthropicTestSseMessageHandler(
                loggerFactory.CreateLogger<AnthropicTestSseMessageHandler>())
            {
                WordsPerChunk = DefaultWordsPerChunk,
                ChunkDelayMs = DefaultChunkDelayMs,
            };
        }

        return new TestSseMessageHandler(
            loggerFactory.CreateLogger<TestSseMessageHandler>())
        {
            WordsPerChunk = DefaultWordsPerChunk,
            ChunkDelayMs = DefaultChunkDelayMs,
        };
    }

    public SubAgentOptions? CreateSubAgentOptions(
        ILoggerFactory loggerFactory,
        Func<IStreamingAgent> providerAgentFactory) => null;
}
