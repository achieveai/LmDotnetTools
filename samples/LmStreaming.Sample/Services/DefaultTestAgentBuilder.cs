using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;

namespace LmStreaming.Sample.Services;

/// <summary>
/// Default <see cref="ITestAgentBuilder"/> implementation used when no test override is registered.
/// Preserves the historical inline handler construction (3 words/chunk, 300 ms delay) and exposes
/// the shared built-in sub-agent catalog so the test providers (<c>test</c> / <c>test-anthropic</c>)
/// register the <c>Agent</c> sub-agent tool exactly like the real middleware providers.
/// </summary>
internal sealed class DefaultTestAgentBuilder : ITestAgentBuilder
{
    private const int DefaultWordsPerChunk = 3;
    private const int DefaultChunkDelayMs = 300;

    public HttpMessageHandler CreateHandler(string providerMode, ILoggerFactory loggerFactory)
    {
        return string.Equals(providerMode, "test-anthropic", StringComparison.OrdinalIgnoreCase)
            ? new AnthropicTestSseMessageHandler(
                loggerFactory.CreateLogger<AnthropicTestSseMessageHandler>())
            {
                WordsPerChunk = DefaultWordsPerChunk,
                ChunkDelayMs = DefaultChunkDelayMs,
            }
            : new TestSseMessageHandler(
                loggerFactory.CreateLogger<TestSseMessageHandler>())
            {
                WordsPerChunk = DefaultWordsPerChunk,
                ChunkDelayMs = DefaultChunkDelayMs,
            };
    }

    public SubAgentOptions? CreateSubAgentOptions(
        ILoggerFactory loggerFactory,
        Func<IStreamingAgent> providerAgentFactory)
    {
        return new SubAgentOptions
        {
            Templates = BuiltInSubAgentTemplates.Create(providerAgentFactory),
            MaxConcurrentSubAgents = BuiltInSubAgentTemplates.DefaultMaxConcurrentSubAgents,
        };
    }
}
