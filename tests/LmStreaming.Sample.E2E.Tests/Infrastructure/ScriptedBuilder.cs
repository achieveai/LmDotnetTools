using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
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
    private readonly HttpMessageHandler _handler;
    private readonly Func<ILoggerFactory, Func<IStreamingAgent>, SubAgentOptions?>? _subAgentFactory;

    public ScriptedBuilder(
        HttpMessageHandler handler,
        Func<ILoggerFactory, Func<IStreamingAgent>, SubAgentOptions?>? subAgentFactory = null)
    {
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        _subAgentFactory = subAgentFactory;
    }

    public HttpMessageHandler CreateHandler(string providerMode, ILoggerFactory loggerFactory) => _handler;

    public SubAgentOptions? CreateSubAgentOptions(
        ILoggerFactory loggerFactory,
        Func<IStreamingAgent> providerAgentFactory) =>
            _subAgentFactory?.Invoke(loggerFactory, providerAgentFactory);
}
