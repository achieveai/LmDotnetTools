using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;

namespace LmStreaming.Sample.Services;

/// <summary>
/// DI seam for test-mode agent construction. Only consulted when the provider mode is
/// <c>test</c> or <c>test-anthropic</c>. In production modes (<c>openai</c>, <c>anthropic</c>,
/// <c>codex</c>, <c>copilot</c>, <c>claude</c>) this service is never invoked.
/// </summary>
/// <remarks>
/// The default implementation replicates the historical inline handler creation so wiring
/// this seam is behavior-preserving. E2E tests register a replacement implementation via
/// <c>WebApplicationFactory.ConfigureServices</c> to inject scripted SSE responders and
/// optional sub-agent templates.
/// </remarks>
public interface ITestAgentBuilder
{
    /// <summary>
    /// Creates the <see cref="HttpMessageHandler"/> that backs the test-mode LLM client.
    /// </summary>
    /// <param name="providerMode">Either <c>"test"</c> (OpenAI-flavored) or <c>"test-anthropic"</c>.</param>
    /// <param name="loggerFactory">Logger factory for constructing handler loggers.</param>
    HttpMessageHandler CreateHandler(string providerMode, ILoggerFactory loggerFactory);

    /// <summary>
    /// Optionally produces sub-agent orchestration options. Returning <c>null</c> means the
    /// parent agent runs without sub-agent tools — identical to historical sample behavior.
    /// </summary>
    /// <param name="loggerFactory">Logger factory.</param>
    /// <param name="providerAgentFactory">
    /// Factory that yields the base provider agent; sub-agent templates typically reuse this
    /// (with their own system prompt) so sub-agents share the test transport.
    /// </param>
    SubAgentOptions? CreateSubAgentOptions(
        ILoggerFactory loggerFactory,
        Func<IStreamingAgent> providerAgentFactory);
}
