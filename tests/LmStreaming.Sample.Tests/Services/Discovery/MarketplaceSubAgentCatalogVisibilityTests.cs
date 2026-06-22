using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using LmStreaming.Sample.Services;
using LmStreaming.Sample.Services.Discovery;
using Xunit.Abstractions;

namespace LmStreaming.Sample.Tests.Services.Discovery;

/// <summary>
/// Validates the user-visible payoff of the marketplace bridge: once a marketplace catalog agent is
/// mapped into the Agent-tool catalog, it shows up in the <c>Agent</c> tool's DESCRIPTION — the exact
/// string the instruction-chain <c>tools_echo</c> / <c>tool_schema</c> probe echoes and that a parent
/// LLM literally reads to choose a <c>subagent_type</c>. This is the "do I now see the other agents?"
/// check, rendered through the real <see cref="SubAgentToolProvider"/>.
/// </summary>
public class MarketplaceSubAgentCatalogVisibilityTests
{
    private readonly ITestOutputHelper _output;

    public MarketplaceSubAgentCatalogVisibilityTests(ITestOutputHelper output) => _output = output;

    private static readonly Func<IStreamingAgent> AgentFactory = () => new Mock<IStreamingAgent>().Object;

    [Fact]
    public void AgentToolDescription_ListsMarketplaceAgents_AlongsideBuiltIns()
    {
        // Reproduce exactly what Program.BuildSubAgentOptionsAsync produces after the fix:
        // built-ins, then marketplace catalog filling the gaps.
        var templates = BuiltInSubAgentTemplates.Create(AgentFactory);
        var fromMarketplace = MarketplaceSubAgentLoader.MapCatalog(MarketplaceCatalogFixture.Sample(), AgentFactory);
        MarketplaceSubAgentLoader.MergeFillGaps(templates, fromMarketplace, NullLogger.Instance);

        var source = new MutableSubAgentTemplateSource(templates);
        var description = SubAgentToolDescriptionProbe.Render(source);

        // Show the actual catalog text a model would read (the tools_echo / tool_schema output).
        _output.WriteLine(description);

        // Built-ins are still present...
        description.Should().Contain("general-purpose");
        description.Should().Contain("researcher");

        // ...AND the marketplace agents now appear — both in the subagent_type enum ("One of: ...")
        // and in the per-template catalog with their descriptions.
        description.Should().Contain("orleans-reviewer");
        description.Should().Contain("Senior Orleans code reviewer");
        description.Should().Contain("silent-failure-hunter");
        description.Should().Contain("Finds swallowed errors and silent failures");
    }
}
