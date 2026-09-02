using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using LmStreaming.Sample.Services;

namespace LmStreaming.Sample.Tests;

/// <summary>
/// The host opts into the #670 measurement sink, and each conversation gets its own. Runs the REAL
/// composition-root path, so deleting the opt-in from <c>BuildSubAgentOptionsAsync</c> turns this red
/// rather than merely leaving a field unset somewhere no one looks.
/// </summary>
public class ProgramSubAgentInstrumentationTests
{
    private static async Task<SubAgentOptions?> BuildAsync() =>
        await global::Program.BuildSubAgentOptionsAsync(
            isTestMode: false,
            testAgentBuilder: Mock.Of<ITestAgentBuilder>(),
            loggerFactory: NullLoggerFactory.Instance,
            providerAgentFactory: () => Mock.Of<IStreamingAgent>(),
            characteristicsAgentFactory: _ => throw new InvalidOperationException("not spawned here"),
            sandboxSession: null,
            workspaceLoader: null!,
            marketplaceLoader: null!,
            workspaceStore: null!,
            logger: NullLogger.Instance,
            mode: new AgentProfile("mode-1", "Mode One", "primary prompt")
        );

    [Fact]
    public async Task EachConversation_GetsItsOwnSink()
    {
        var first = await BuildAsync();
        var second = await BuildAsync();

        first!.Instrumentation.Should().NotBeNull("the host opts in so a run's coordination cost is measurable");

        // A shared sink would fold every conversation's spawns into one number and make a per-run
        // baseline meaningless - exactly the comparison #670 exists to enable.
        second!.Instrumentation.Should().NotBeSameAs(first.Instrumentation);
    }
}
