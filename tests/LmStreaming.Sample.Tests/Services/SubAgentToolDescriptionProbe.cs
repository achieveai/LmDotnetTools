using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;

namespace LmStreaming.Sample.Tests.Services;

/// <summary>
/// Renders the <c>Agent</c> tool's description exactly as a parent LLM receives it — the same string
/// the instruction-chain <c>tools_echo</c> / <c>tool_schema</c> probe echoes. Shared by the sub-agent
/// catalog tests (isolation + marketplace visibility) so the rendering boilerplate lives in one place;
/// if the <see cref="SubAgentManager"/>/<see cref="SubAgentToolProvider"/> contract changes, only this
/// helper updates instead of every test that probes the catalog text.
/// </summary>
internal static class SubAgentToolDescriptionProbe
{
    internal static string Render(MutableSubAgentTemplateSource source)
    {
        var parentMock = new Mock<IMultiTurnAgent>();
        parentMock
            .Setup(p => p.SendAsync(
                It.IsAny<List<IMessage>>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SendReceipt("receipt", null, DateTimeOffset.UtcNow));

        var manager = new SubAgentManager(
            parentAgent: parentMock.Object,
            parentContracts: [],
            parentHandlers: new Dictionary<string, ToolHandler>(),
            options: new SubAgentOptions { Templates = source.Templates, MaxConcurrentSubAgents = 5 },
            source: source);

        var provider = new SubAgentToolProvider(manager, source);
        var agent = provider.GetFunctions().First(f => f.Contract.Name == "Agent");
        return agent.Contract.Description ?? string.Empty;
    }
}
