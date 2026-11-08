using AchieveAi.LmDotnetTools.LmAgentInfra.Capabilities;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using Microsoft.Extensions.Logging.Abstractions;

namespace AchieveAi.LmDotnetTools.LmAgentInfra.Tests.Capabilities;

/// <summary>
/// Issue #931: a session whose gateway lists <c>SearchSkills</c> uses it in place of <c>SearchSkill</c>, and a session
/// whose gateway does not keeps LMDT's. <c>SearchAgent</c> stays whole, and a listed <c>SearchAgents</c> changes nothing
/// about it. Driven the way the host drives a build: stage, hand the sandbox listing to the stage, commit, then read
/// the root provider.
/// </summary>
public class CapabilityTiersGatewaySearchTests
{
    private const string T = "thread-1792610878575-63a7aa01";
    private const string SearchSkills = CapabilityTiers.GatewaySearchSkillsToolName;
    private const string SearchAgents = CapabilityTiers.GatewaySearchAgentsToolName;

    [Fact]
    public async Task GatewayListsBothTools_SearchSkillGoes_SearchAgentStays()
    {
        using var tiers = await BuildAsync(AgentsTiered, SearchSkills, SearchAgents);

        Names(tiers)
            .Should()
            .NotContain(CapabilityTiers.SearchSkillToolName, "the gateway's SearchSkills replaces it for this session")
            .And.Contain(CapabilityTiers.SearchAgentToolName, "plugin and host agents are searched here");
    }

    [Fact]
    public async Task GatewayListsNoTools_LmdtSearchKeepsEveryAgentAndSkill()
    {
        using var tiers = await BuildAsync(AgentsTiered);

        Names(tiers).Should().Contain([CapabilityTiers.SearchSkillToolName, CapabilityTiers.SearchAgentToolName]);
        var text = await SearchAgentAsync(tiers, """{"query":"review changes"}""");
        text.Should().Contain("alpha:reviewer", "a session with no gateway search still finds plugin agents here");
        Description(tiers, CapabilityTiers.SearchAgentToolName).Should().NotContain(SearchAgents);
    }

    [Fact]
    public async Task GatewayListsSearchAgents_ChangesNothingAboutTheAgentOrSkillSearch()
    {
        using var without = await BuildAsync(AgentsTiered);
        using var with = await BuildAsync(AgentsTiered, SearchAgents);
        const string Query = """{"query":"review changes"}""";

        var text = await SearchAgentAsync(with, Query);

        text.Should().Contain("alpha:reviewer").And.Contain("host-reviewer");
        text.Should().Be(await SearchAgentAsync(without, Query));
        Names(with).Should().Equal(Names(without), "SearchAgents must not retire SearchSkill or change any tool");
        Description(with, CapabilityTiers.SearchAgentToolName)
            .Should()
            .Be(Description(without, CapabilityTiers.SearchAgentToolName));
        with.ListedTemplates(T)!.UnlistedHint.Should().Be(CapabilityTiers.UnlistedAgentsHint);
    }

    [Fact]
    public async Task GatewayListsBothTools_SearchPluginPointsAtSearchSkillsAndSearchAgent()
    {
        using var tiers = await BuildAsync(AgentsTiered, SearchSkills, SearchAgents);

        var search = tiers
            .RootProvider.GetFunctions()
            .Single(f => f.Contract.Name == CapabilityTiers.SearchPluginToolName);
        var result = await search.Handler("""{"query":"zzzz-no-such-area"}""", new ToolCallContext(), default);

        var text = result.Should().BeOfType<ToolHandlerResult.Resolved>().Subject.Payload.Text;
        text.Should()
            .Contain($"{SearchSkills} and {CapabilityTiers.SearchAgentToolName}.")
            .And.NotContain(SearchAgents)
            .And.NotContain("SearchSkill and");
    }

    [Theory]
    [InlineData(SearchSkills)]
    [InlineData(SearchAgents)]
    public async Task UntieredBuild_DropsGatewayTools_SoItsToolListDoesNotChange(string toolName)
    {
        using var tiers = new CapabilityTiers(T, await StoreAsync(), NullLogger.Instance);
        using var stage = await tiers.StageAsync(null, null, default);

        stage.TryDropGatewaySearchTool(toolName).Should().BeTrue();
    }

    [Fact]
    public async Task ModeWithoutAgentCap_KeepsSearchSkillsAndDropsSearchAgents()
    {
        using var tiers = new CapabilityTiers(T, await StoreAsync(), NullLogger.Instance);
        var gw = new FakeSkillGateway();
        using var stage = await tiers.StageAsync(new TierSettings([], [], [], []), gw.For(T), default);

        stage.TryDropGatewaySearchTool(SearchSkills).Should().BeFalse();
        stage
            .TryDropGatewaySearchTool(SearchAgents)
            .Should()
            .BeTrue("no mode shows a tool that finds nothing for a named agent");
        stage.TryDropGatewaySearchTool("Read").Should().BeFalse("every other tool is not this method's to drop");
    }

    [Fact]
    public async Task ToolTiering_NeverHidesGatewaySearchTools()
    {
        using var tiers = new CapabilityTiers(T, await StoreAsync(), NullLogger.Instance);
        var gw = new FakeSkillGateway();
        using var stage = await tiers.StageAsync(new TierSettings([], [], [], [], ToolCap: 0), gw.For(T), default);
        _ = stage.TryDropGatewaySearchTool(SearchSkills);

        stage.AttachTools([Tool("Read"), Tool(SearchSkills)]);
        await stage.CommitAsync(default);

        var provider = tiers.RootProvider.Should().BeAssignableTo<IDeferredToolProvider>().Subject;
        provider
            .IsAdvertised(SearchSkills)
            .Should()
            .BeTrue("tiering it would hide how an agent finds what is unlisted");
        provider.IsAdvertised("Read").Should().BeFalse("guard: a cap of 0 does hide an ordinary sandbox tool");
    }

    [Fact]
    public void AllowList_GainsSearchSkillsOnlyWhereSkillIsAllowed()
    {
        CapabilityTiers.WithGatewaySearchTools(null).Should().BeNull("null means every tool, so nothing needs adding");
        CapabilityTiers.WithGatewaySearchTools(Set("Read")).Should().BeEquivalentTo(["Read"]);
        CapabilityTiers
            .WithGatewaySearchTools(Set("Skill", "Read"))
            .Should()
            .BeEquivalentTo(["Skill", "Read", SearchSkills]);
    }

    private static TierSettings AgentsTiered => new([], [], [], [], 10, 2);

    private static HashSet<string> Set(params string[] names) => new(names, StringComparer.Ordinal);

    private static FunctionContract Tool(string name) => new() { Name = name, Description = name };

    private static string[] Names(CapabilityTiers tiers) =>
        [.. tiers.RootProvider.GetFunctions().Select(f => f.Contract.Name)];

    private static string Description(CapabilityTiers tiers, string tool) =>
        tiers.RootProvider.GetFunctions().Single(f => f.Contract.Name == tool).Contract.Description!;

    private static async Task<string> SearchAgentAsync(CapabilityTiers tiers, string args)
    {
        var search = tiers
            .RootProvider.GetFunctions()
            .Single(f => f.Contract.Name == CapabilityTiers.SearchAgentToolName);
        var result = await search.Handler(args, new ToolCallContext(), CancellationToken.None);
        return result.Should().BeOfType<ToolHandlerResult.Resolved>().Subject.Payload.Text;
    }

    private static async Task<InMemoryConversationStore> StoreAsync()
    {
        var store = new InMemoryConversationStore();
        await store.SaveMetadataAsync(T, new ThreadMetadata { ThreadId = T, LastUpdated = 1 });
        return store;
    }

    private static async Task<CapabilityTiers> BuildAsync(TierSettings settings, params string[] listing)
    {
        var tiers = new CapabilityTiers(T, await StoreAsync(), NullLogger.Instance);
        tiers.AttachAgents(
            new MutableSubAgentTemplateSource(
                new Dictionary<string, SubAgentTemplate>(StringComparer.Ordinal)
                {
                    ["alpha:reviewer"] = Template("Review changes: review changes for defects.", marketplace: "gb"),
                    ["host-reviewer"] = Template("Looks over a change before it ships.", marketplace: null),
                }
            )
        );
        var gw = new FakeSkillGateway();
        using var stage = await tiers.StageAsync(settings, gw.For(T), default);
        foreach (var tool in listing)
        {
            stage
                .TryDropGatewaySearchTool(tool)
                .Should()
                .Be(tool == SearchAgents, "SearchAgents alone is always dropped");
        }

        await stage.CommitAsync(default);
        tiers.IsActive.Should().BeTrue("guard: the build applied");
        return tiers;
    }

    private static SubAgentTemplate Template(string description, string? marketplace) =>
        new()
        {
            Description = description,
            SystemPrompt = "Tier tests never spawn.",
            AgentFactory = () => throw new NotSupportedException("Tier tests never spawn."),
            Marketplace = marketplace,
        };
}
