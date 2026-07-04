using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;
using LmStreaming.Sample.Services;

namespace LmStreaming.Sample.E2E.Tests.Infrastructure;

/// <summary>
/// Adapter that plugs a scripted <see cref="System.Net.Http.HttpMessageHandler"/> into the
/// sample's test-mode agent path, together with a caller-supplied <see cref="SubAgentOptions"/>
/// factory. Shared between the WebSocket-transport E2E suite and the browser E2E suite via
/// <c>&lt;Compile Link&gt;</c> in the browser project's csproj.
/// </summary>
public sealed class ScriptedBuilder : ITestAgentBuilder
{
    private readonly HttpMessageHandler? _handler;
    private readonly ScriptedSseResponder? _responder;
    private readonly Func<ILoggerFactory, Func<IStreamingAgent>, SubAgentOptions?>? _subAgentFactory;

    public ScriptedBuilder(
        HttpMessageHandler handler,
        Func<ILoggerFactory, Func<IStreamingAgent>, SubAgentOptions?>? subAgentFactory = null)
    {
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        _subAgentFactory = subAgentFactory;
    }

    /// <summary>
    /// Provider-aware overload: derives the per-wire handler from the requested provider mode, so a
    /// conversation can SWITCH between the scripted <c>test</c> (OpenAI wire) and <c>test-anthropic</c>
    /// (Anthropic wire) providers at runtime. Both handlers come from the SAME responder and share its
    /// plan queue, so a single scripted plan serves whichever wire the current provider uses. The
    /// single-handler ctor above pins one wire and cannot switch.
    /// </summary>
    public ScriptedBuilder(
        ScriptedSseResponder responder,
        Func<ILoggerFactory, Func<IStreamingAgent>, SubAgentOptions?>? subAgentFactory = null)
    {
        _responder = responder ?? throw new ArgumentNullException(nameof(responder));
        _subAgentFactory = subAgentFactory;
    }

    public HttpMessageHandler CreateHandler(string providerMode, ILoggerFactory loggerFactory)
    {
        if (_responder != null)
        {
            // Mirror DefaultTestAgentBuilder: honor the requested provider mode's wire format.
            return string.Equals(providerMode, "test-anthropic", StringComparison.OrdinalIgnoreCase)
                ? _responder.AsAnthropicHandler()
                : _responder.AsOpenAiHandler();
        }

        return _handler!;
    }

    public SubAgentOptions? CreateSubAgentOptions(
        ILoggerFactory loggerFactory,
        Func<IStreamingAgent> providerAgentFactory)
    {
        return _subAgentFactory?.Invoke(loggerFactory, providerAgentFactory);
    }
}
