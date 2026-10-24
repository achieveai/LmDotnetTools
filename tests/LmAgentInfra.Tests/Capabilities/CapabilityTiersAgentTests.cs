using System.Text.Json;
using System.Text.Json.Serialization;
using AchieveAi.LmDotnetTools.LmAgentInfra.Capabilities;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmMultiTurn.Compaction;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using AchieveAi.LmDotnetTools.Sandbox;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AchieveAi.LmDotnetTools.LmAgentInfra.Tests.Capabilities;

/// <summary>
/// Sub-agent templates as a tiered kind (<see cref="TierSettings.AgentCap"/>): the root's <c>Agent</c> listing, the
/// <c>agent</c> seed and its validation after a restart, rotation by <c>Agent</c> use, and what <c>SearchAgent</c> and
/// <c>SearchPlugin</c> answer. Driven the way the host drives them: attach the conversation's template source, configure
/// on every build, the compaction hook at each activation, the search tools through the root provider.
/// </summary>
public class CapabilityTiersAgentTests
{
    private const string T = "thread-1792610878575-63a7aa01";
    private const string Child = "thread-1792611042672-cdb0bb02";
    private const string SearchAgent = CapabilityTiers.SearchAgentToolName;
    private const string SearchPlugin = CapabilityTiers.SearchPluginToolName;

    public static TheoryData<int?, string[]> AgentCapRows =>
        new()
        {
            // Untiered: an Agent call is never a use, and agent: selectors are ignored.
            { null, [] },
            { 0, ["agent:general-purpose"] },
            { 1, ["agent:beta:critic", "agent:general-purpose"] },
            { 2, ["agent:alpha:reviewer", "agent:beta:critic", "agent:general-purpose"] },
        };

    [Fact]
    public async Task ListedTemplates_RootOnly_LoadedPlusPinned_SameInstanceUntilTheSetChanges()
    {
        var store = await StoreAsync(T);
        var gw = new FakeSkillGateway();
        var settings = Agents(["skill:alpha:a", "agent:beta:critic"], ["agent:general-purpose"], agentCap: 2);
        using var tiers = await TiersAsync(store, Source(), settings, gw);

        var listing = tiers.ListedTemplates(T);

        listing.Should().NotBeNull();
        listing!.Names.Should().BeEquivalentTo("beta:critic", "general-purpose");
        listing.UnlistedHint.Should().Be(CapabilityTiers.UnlistedAgentsHint);
        tiers.ListedTemplates(T).Should().BeSameAs(listing, "an unchanged set keeps the Agent tool's memo a hit");
        tiers.ListedTemplates(Child).Should().BeNull("a sub-agent's Agent tool lists every template");

        await tiers.ConfigureAsync(settings, gw.For(T), CancellationToken.None);
        tiers.ListedTemplates(T).Should().BeSameAs(listing, "a rebuild with the same view lists the same instance");

        // beta:critic goes unused and is evicted; the pin stays.
        _ = await tiers.OnCheckpointActivatedAsync(Activation(T, 10, 9, AgentCall(4, "alpha:reviewer")), default);

        var rotated = tiers.ListedTemplates(T);
        rotated.Should().NotBeSameAs(listing);
        rotated!.Names.Should().BeEquivalentTo("alpha:reviewer", "general-purpose");

        // Switched in place to a mode without caps.agent: the view keeps its agent seed, yet every template is listed.
        await tiers.ConfigureAsync(settings with { AgentCap = null }, gw.For(T), CancellationToken.None);
        tiers.ListedTemplates(T).Should().BeNull();
    }

    [Theory]
    [InlineData(null, true)] // a mode without caps.agent
    [InlineData(2, false)] // no template source attached: the Agent tool fails open
    public async Task ListedTemplates_IsNull_WhereAgentsAreNotTiered(int? agentCap, bool attachSource)
    {
        var store = await StoreAsync(T);
        using var tiers = new CapabilityTiers(T, store, NullLogger.Instance);
        if (attachSource)
        {
            tiers.AttachAgents(Source());
        }

        await tiers.ConfigureAsync(
            Agents(["skill:alpha:a", "agent:beta:critic"], [], agentCap),
            new FakeSkillGateway().For(T),
            CancellationToken.None
        );

        tiers.IsActive.Should().BeTrue("guard: skills are tiered either way");
        tiers.ListedTemplates(T).Should().BeNull();
    }

    [Fact]
    public async Task UntieredAgents_RootOffersNoAgentSearch_AndAnAgentSelectorWarns()
    {
        var logger = new RecordingLogger();
        var store = await StoreAsync(T);
        using var tiers = await TiersAsync(
            store,
            Source(),
            Agents(["skill:alpha:a", "agent:beta:critic"], [], agentCap: null),
            new FakeSkillGateway(),
            logger
        );

        tiers
            .RootProvider.GetFunctions()
            .Select(f => f.Contract.Name)
            .Should()
            .Contain(CapabilityTiers.SearchSkillToolName)
            .And.NotContain(SearchAgent)
            .And.NotContain(SearchPlugin);
        logger
            .Starting("Capabilities selector kind not enabled")
            .Should()
            .ContainSingle(r => r.Level == LogLevel.Warning && Equals(r.Props["Selector"], "agent:beta:critic"));
    }

    [Fact]
    public async Task UnknownAgentSelector_LogsOneWarn_AndLoadsNothing()
    {
        var logger = new RecordingLogger();
        var store = await StoreAsync(T);
        using var tiers = await TiersAsync(
            store,
            Source(),
            Agents(["agent:nope", "agent:beta:critic"], [], agentCap: 2),
            new FakeSkillGateway(),
            logger
        );

        tiers.ListedTemplates(T)!.Names.Should().BeEquivalentTo("beta:critic");
        logger
            .Starting("Capabilities selector unresolved")
            .Should()
            .ContainSingle()
            .Which.Props.Should()
            .Contain("Selector", "agent:nope")
            .And.Contain("Problem", SelectorProblem.Unknown);
    }

    [Fact]
    public async Task AgentKind_SeedsOncePerView_ARestartValidatesItAndReaddsPins()
    {
        var store = await StoreAsync(T);
        var gw = new FakeSkillGateway();
        using (
            var first = await TiersAsync(
                store,
                Source(),
                Agents(["agent:beta:critic", "agent:alpha:writer"], [], 3),
                gw
            )
        )
        {
            first.ListedTemplates(T)!.Names.Should().BeEquivalentTo("alpha:writer", "beta:critic");
        }

        var seeded = await ViewAsync(store, T);
        seeded!.SeededKinds.Should().Equal("agent", "skill");
        seeded.Loaded.Should().Equal("agent:alpha:writer", "agent:beta:critic");

        // A restart: alpha:writer is gone from the templates, and the mode now loads alpha:reviewer and pins
        // general-purpose.
        var logger = new RecordingLogger();
        using var second = await TiersAsync(
            store,
            Source(without: "alpha:writer"),
            Agents(["agent:alpha:reviewer"], ["agent:general-purpose"], 3),
            gw,
            logger
        );

        second
            .ListedTemplates(T)!
            .Names.Should()
            .BeEquivalentTo(
                ["beta:critic", "general-purpose"],
                "the seed ran once: a restart drops what is gone and re-adds the pins, but seeds nothing"
            );
        logger
            .Starting(
                "Capabilities view of {AgentKey} names {Key}, which this conversation's templates no longer offer"
            )
            .Should()
            .ContainSingle(r => r.Level == LogLevel.Warning && Equals(r.Props["Key"], "agent:alpha:writer"));
    }

    [Fact]
    public async Task SkillsOnlyView_SwitchedToAnAgentMode_SeedsTheAgentKind()
    {
        var store = await StoreAsync(T);
        var gw = new FakeSkillGateway();
        string[] loaded = ["skill:alpha:a", "agent:beta:critic"];
        using var tiers = await TiersAsync(store, Source(), Agents(loaded, [], agentCap: null), gw);
        tiers.ListedTemplates(T).Should().BeNull();
        (await ViewAsync(store, T))!.SeededKinds.Should().BeNull("a skills-only view is written exactly as before");

        await tiers.ConfigureAsync(Agents(loaded, [], agentCap: 2), gw.For(T), CancellationToken.None);

        tiers.ListedTemplates(T)!.Names.Should().BeEquivalentTo("beta:critic");
        var view = await ViewAsync(store, T);
        view!.SeededKinds.Should().Equal("agent", "skill");
        view.Loaded.Should().Equal("agent:beta:critic", FakeSkillGateway.KeyOf("alpha", "a"));
    }

    [Theory]
    [MemberData(nameof(AgentCapRows))]
    public async Task Rotation_CountsAgentUsesByExactNameOrUniqueSuffix_MostRecentUpToAgentCap(
        int? agentCap,
        string[] expectedAgentKeys
    )
    {
        var store = await StoreAsync(T);
        using var tiers = await TiersAsync(
            store,
            Source(),
            Agents([], ["agent:general-purpose"], agentCap),
            new FakeSkillGateway()
        );

        _ = await tiers.OnCheckpointActivatedAsync(
            Activation(
                T,
                10,
                9,
                AgentCall(3, "alpha:reviewer"), // the exact key
                AgentCall(5, "critic"), // the trailing segment only beta:critic has
                AgentCall(6, "general-purpose"), // pinned: kept, and never counted against the cap
                AgentCall(7, "nope") // no such template: ignored
            ),
            default
        );

        var view = await ViewAsync(store, T);
        view!.Loaded.Where(k => k.StartsWith("agent:", StringComparison.Ordinal)).Should().Equal(expectedAgentKeys);
        view.RotatedThroughSeq.Should().Be(10, "guard: the rotation ran");
    }

    [Fact]
    public async Task TemplateRegisteredAfterAttach_IsFoundBySearchAgent_WhileTheListingStaysTheSameInstance()
    {
        var logger = new RecordingLogger();
        var store = await StoreAsync(T);
        var source = Source();
        using var tiers = await TiersAsync(
            store,
            source,
            Agents(["agent:beta:critic"], [], 2),
            new FakeSkillGateway(),
            logger
        );
        var listing = tiers.ListedTemplates(T);
        (await SearchAsync(tiers, SearchAgent, """{"query":"klingon"}""")).Text.Should().StartWith("No sub-agents");

        source
            .TryRegister("beta:translator", Template("Translates beta release notes into Klingon."))
            .Should()
            .BeTrue();
        var text = (await SearchAsync(tiers, SearchAgent, """{"query":"klingon"}""")).Text;

        HitRows(text)
            .Should()
            .ContainSingle()
            .Which.Should()
            .StartWith("1. beta:translator (plugin: beta) ")
            .And.EndWith("[available]");
        text.Should().Contain("\n   Run: Agent(subagent_type=\"beta:translator\")");
        tiers.ListedTemplates(T).Should().BeSameAs(listing, "a discovered template is found without new Agent text");
        logger.Starting("Capabilities agent index refreshed").Should().ContainSingle();
    }

    [Fact]
    public async Task SearchAgent_MarksLoadedAvailableAndWeak_WithRunLines()
    {
        var store = await StoreAsync(T);
        using var tiers = await TiersAsync(
            store,
            Source(),
            Agents(["agent:alpha:reviewer"], [], 2),
            new FakeSkillGateway()
        );
        const string Legend =
            "\"loaded\" = already listed in your Agent tool; \"available\" = not listed, but runs the same way.\n";

        (await SearchAsync(tiers, SearchAgent, """{"query":"reviewer"}"""))
            .Text.Should()
            .Be(
                "1 sub-agent found.\n"
                    + Legend
                    + "\n1. alpha:reviewer (plugin: alpha) [loaded]\n"
                    + "   Reviews alpha changes for defects.\n"
                    + "   Run: Agent(subagent_type=\"alpha:reviewer\")"
            );
        (await SearchAsync(tiers, SearchAgent, """{"query":"documentation"}"""))
            .Text.Should()
            .Be(
                "1 sub-agent found.\n"
                    + Legend
                    + "\"weak\" = matches your words only loosely; check its description before you use it.\n"
                    + "\n1. alpha:writer (plugin: alpha) [weak] [available]\n"
                    + "   Writes alpha documentation pages.\n"
                    + "   Run: Agent(subagent_type=\"alpha:writer\")",
                "a description-only match scores under the weak line"
            );
        HitRows((await SearchAsync(tiers, SearchAgent, """{"query":"general-purpose"}""")).Text)
            .Should()
            .Equal("1. general-purpose (plugin: none) [available]");
    }

    /// <summary>
    /// T8 (SC8), on the search eval's 63 sub-agents and 36 plugins: a query made of words that most names in a
    /// code-review plugin share ("code", "review") scores its answer under the weak line, yet finds it first and names
    /// it by a word only that name has, so the answer is not weak; the hits that only share the common words stay weak,
    /// and so does every hit of an off-topic query. See <see cref="CapabilityTiers.WeakScore"/>.
    /// </summary>
    [Fact]
    public async Task AgentAndPluginSearch_OnTheEvalCorpus_AnAnswerItsOwnNameWordNamesIsNotWeak_OffTopicHitsAre()
    {
        var (source, gateway) = EvalCorpus();
        using var tiers = await TiersAsync(await StoreAsync(T), source, Agents([], [], 2), gateway);

        var agents = HitRows(
            (await SearchAsync(tiers, SearchAgent, """{"query":"code review correctness bugs specialist"}""")).Text
        );
        agents[0].Should().Be("1. code-reviewer:correctness-review (plugin: code-reviewer) [available]");
        agents.Skip(1).Should().NotBeEmpty().And.OnlyContain(row => row.Contains(" [weak] ", StringComparison.Ordinal));

        var plugins = HitRows(
            (await SearchAsync(tiers, SearchPlugin, """{"query":"code review specialist reviewer"}""")).Text
        );
        plugins[0].Should().Be("1. code-reviewer (marketplace: ClaudePlugins)");
        plugins.Skip(1).Should().NotBeEmpty().And.OnlyContain(row => row.EndsWith(" [weak]", StringComparison.Ordinal));

        foreach (
            var (tool, query) in new[]
            {
                // "agent" is a word of agent-contract-review's name alone, but it names the kind, not one sub-agent.
                (SearchAgent, "an agent that tunes Kubernetes autoscaling for my cluster"),
                (SearchAgent, "agent that tracks my workouts and builds a weekly diet plan"),
                (SearchPlugin, "write a bedtime story for my kids"),
            }
        )
        {
            var rows = HitRows((await SearchAsync(tiers, tool, $$"""{"query":"{{query}}"}""")).Text);
            rows.Should().NotBeEmpty("guard: the query must hit something for its hits to be weak");
            rows.Should().OnlyContain(row => row.Contains("[weak]", StringComparison.Ordinal), query);
        }
    }

    [Theory]
    [InlineData(SearchAgent, "No sub-agents matched \"zzqx qqqv\". Try different words")]
    [InlineData(SearchPlugin, "No plugins matched \"zzqx qqqv\". Try different words")]
    public async Task AgentAndPluginSearch_EmptyQueryIsInvalidArgs_NoMatchIsAnAnswer(string tool, string noMatch)
    {
        var store = await StoreAsync(T);
        using var tiers = await TiersAsync(store, Source(), Agents([], [], 2), new FakeSkillGateway());

        foreach (var bad in new[] { "", "{}", """{"query":"   "}""", """{"query":42}""", "{not json", "[1]" })
        {
            var payload = await SearchAsync(tiers, tool, bad);
            payload.IsError.Should().BeTrue(bad);
            payload.ErrorCode.Should().Be("invalid_args", bad);
        }

        var none = await SearchAsync(tiers, tool, """{"query":"zzqx qqqv"}""");
        none.IsError.Should().BeFalse("no match is an answer, not a failure");
        none.Text.Should().StartWith(noMatch);
    }

    [Fact]
    public async Task SearchPlugin_ListsThePluginsSkillsAndSubAgents_EachMarked()
    {
        var store = await StoreAsync(T);
        using var tiers = await TiersAsync(
            store,
            Source(),
            Agents(["skill:alpha:a", "agent:alpha:reviewer"], [], 2),
            new FakeSkillGateway()
        );

        var text = (await SearchAsync(tiers, SearchPlugin, """{"query":"alpha"}""")).Text;

        text.Should()
            .StartWith(
                "1 plugin found.\n\"loaded\" = already listed in your Skill or Agent tool; \"available\" = not listed, "
                    + "but works the same way.\n\n1. alpha (marketplace: mk)\n"
            );
        text.Should()
            .EndWith(
                "   Skills (4):\n"
                    + "   - alpha:a [loaded]: Skill(skill=\"alpha:a\")\n"
                    + "   - alpha:b [available]: Skill(skill=\"alpha:b\")\n"
                    + "   - alpha:dup [available]: Skill(skill=\"alpha:dup\")\n"
                    + "   - alpha:p [available]: Skill(skill=\"alpha:p\")\n"
                    + "   Sub-agents (2):\n"
                    + "   - alpha:reviewer [loaded]: Agent(subagent_type=\"alpha:reviewer\")\n"
                    + "   - alpha:writer [available]: Agent(subagent_type=\"alpha:writer\")"
            );
    }

    [Fact]
    public async Task SearchPlugin_ListsAtMostEightPerKind_AndPointsToThatKindsSearchForTheRest()
    {
        var store = await StoreAsync(T);
        var source = new MutableSubAgentTemplateSource(
            Enumerable
                .Range(1, 9)
                .ToDictionary(i => $"big:agent-{i:00}", i => Template($"Big agent number {i}."), StringComparer.Ordinal)
        );
        using var tiers = await TiersAsync(
            store,
            source,
            Agents([], [], 2),
            new FakeSkillGateway([.. Enumerable.Range(1, 10).Select(i => ("big", $"skill-{i:00}"))])
        );

        var lines = (await SearchAsync(tiers, SearchPlugin, """{"query":"big"}""")).Text.Split('\n');

        lines.Should().Contain("   Skills (10):").And.Contain("   +2 more: use SearchSkill");
        lines.Should().Contain("   Sub-agents (9):").And.Contain("   +1 more: use SearchAgent");
        lines.Where(l => l.StartsWith("   - big:skill-", StringComparison.Ordinal)).Should().HaveCount(8);
        lines.Where(l => l.StartsWith("   - big:agent-", StringComparison.Ordinal)).Should().HaveCount(8);
        lines.Should().NotContain(l => l.Contains("big:skill-09") || l.Contains("big:agent-09"), "ordinal first eight");
    }

    // Paper cut 1: marketplaces mk1 and mk2 both ship a plugin named "debugging". The template kept for
    // "debugging:x" came from mk1, so only mk1's hit lists it; mk2's hit has nothing of its own.
    [Fact]
    public async Task SearchPlugin_ListsAMarketplaceAgent_OnlyUnderItsOwnMarketplace()
    {
        var store = await StoreAsync(T);
        var source = new MutableSubAgentTemplateSource(
            new Dictionary<string, SubAgentTemplate>(StringComparer.Ordinal)
            {
                ["debugging:x"] = Template("Finds the root cause of a failure.") with { Marketplace = "mk1" },
            }
        );
        using var tiers = await TiersAsync(store, source, Agents([], [], 2), TwoMarketplaces("debugging"));

        var blocks = (await SearchAsync(tiers, SearchPlugin, """{"query":"debugging"}""")).Text.Split("\n\n");

        blocks.Should().HaveCount(3, "a header, then one block per plugin hit");
        blocks
            .Single(b => b.Contains("(marketplace: mk1)", StringComparison.Ordinal))
            .Should()
            .EndWith(
                "debugging (marketplace: mk1)\n   The debugging plugin of mk1.\n   Sub-agents (1):\n"
                    + "   - debugging:x [available]: Agent(subagent_type=\"debugging:x\")"
            );
        blocks
            .Single(b => b.Contains("(marketplace: mk2)", StringComparison.Ordinal))
            .Should()
            .EndWith(
                "debugging (marketplace: mk2)\n   The debugging plugin of mk2.\n   No skills or sub-agents.",
                "the agent kept from mk1 is not mk2's"
            );
    }

    private static TierSettings Agents(string[] loaded, string[] pinned, int? agentCap) =>
        new(loaded, pinned, [], [], 10, agentCap);

    /// <summary>
    /// Plugin alpha: reviewer and writer. Plugin beta: critic. And general-purpose, which belongs to no plugin.
    /// </summary>
    private static MutableSubAgentTemplateSource Source(string? without = null) =>
        new(
            new Dictionary<string, SubAgentTemplate>(StringComparer.Ordinal)
            {
                ["alpha:reviewer"] = Template("Reviews alpha changes for defects."),
                ["alpha:writer"] = Template("Writes alpha documentation pages."),
                ["beta:critic"] = Template("Challenges beta plans ahead of shipping."),
                ["general-purpose"] = Template("Handles any multi-step task."),
            }
                .Where(t => t.Key != without)
                .ToDictionary(t => t.Key, t => t.Value, StringComparer.Ordinal)
        );

    private static SubAgentTemplate Template(string description) =>
        new()
        {
            Description = description,
            SystemPrompt = "Tier tests never spawn.",
            AgentFactory = () => throw new NotSupportedException("Tier tests never spawn."),
        };

    /// <summary>One host build: attach the conversation's templates, then configure.</summary>
    private static Task<CapabilityTiers> TiersAsync(
        IConversationStore store,
        MutableSubAgentTemplateSource source,
        TierSettings settings,
        FakeSkillGateway gw,
        ILogger? logger = null
    ) => TiersAsync(store, source, settings, gw.For(T), logger);

    private static async Task<CapabilityTiers> TiersAsync(
        IConversationStore store,
        MutableSubAgentTemplateSource source,
        TierSettings settings,
        SkillTierGateway gateway,
        ILogger? logger = null
    )
    {
        var tiers = new CapabilityTiers(T, store, logger ?? NullLogger.Instance);
        tiers.AttachAgents(source);
        await tiers.ConfigureAsync(settings, gateway, CancellationToken.None);
        tiers.IsActive.Should().BeTrue("guard: the build applied");
        return tiers;
    }

    private static async Task<ToolHandlerResultPayload> SearchAsync(CapabilityTiers tiers, string tool, string args)
    {
        var search = tiers.RootProvider.GetFunctions().Single(f => f.Contract.Name == tool);
        var result = await search.Handler(args, new ToolCallContext(), CancellationToken.None);
        return result.Should().BeOfType<ToolHandlerResult.Resolved>().Subject.Payload;
    }

    private static string[] HitRows(string text) =>
        [.. text.Split('\n').Where(line => line.Length > 0 && char.IsAsciiDigit(line[0]) && line.Contains(". "))];

    private static async Task<InMemoryConversationStore> StoreAsync(string thread)
    {
        var store = new InMemoryConversationStore();
        await store.SaveMetadataAsync(thread, new ThreadMetadata { ThreadId = thread, LastUpdated = 1 });
        return store;
    }

    private static (long Seq, string Tool, string Args) AgentCall(long seq, string subagentType) =>
        (seq, SubAgentToolProvider.SpawnToolName, JsonSerializer.Serialize(new { subagent_type = subagentType }));

    private static CheckpointActivation Activation(
        string thread,
        long checkpointSeq,
        long lastMessageSeq,
        params (long Seq, string Tool, string Args)[] calls
    ) =>
        new(
            thread,
            "root",
            $"cp-{checkpointSeq}",
            checkpointSeq,
            lastMessageSeq,
            [
                .. calls.Select(c =>
                    (
                        c.Seq,
                        new ToolCall
                        {
                            FunctionName = c.Tool,
                            FunctionArgs = c.Args,
                            ToolCallId = $"call-{c.Seq}",
                        }
                    )
                ),
            ]
        );

    private static async Task<ViewJson?> ViewAsync(IConversationStore store, string thread)
    {
        var metadata = await store.LoadMetadataAsync(thread);
        return metadata?.Properties?.TryGetValue(CapabilityTiers.ViewPropertyKey, out var raw) == true
            ? JsonSerializer.Deserialize<ViewJson>((string)raw)
            : null;
    }

    private sealed record ViewJson(
        [property: JsonPropertyName("loaded")] string[] Loaded,
        [property: JsonPropertyName("revision")] int Revision,
        [property: JsonPropertyName("rotatedThroughSeq")] long RotatedThroughSeq,
        [property: JsonPropertyName("seededKinds")] string[]? SeededKinds
    );

    /// <summary>
    /// The search eval's corpus (SearchEval/THIRD-PARTY-NOTICES.md) at its real size: its sub-agents as the host's
    /// templates, and a gateway serving its plugins, with no skills.
    /// </summary>
    private static (MutableSubAgentTemplateSource Source, SkillTierGateway Gateway) EvalCorpus()
    {
        using var corpus = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Capabilities", "SearchEval", "corpus.json"))
        );
        var templates = new Dictionary<string, SubAgentTemplate>(StringComparer.Ordinal);
        var plugins = new List<(string Marketplace, SandboxSkillPlugin Plugin)>();
        foreach (var row in corpus.RootElement.EnumerateArray())
        {
            switch (Field(row, "kind"))
            {
                case CapabilityCatalog.AgentKind:
                    templates.Add(
                        Field(row, "plugin") + ":" + Field(row, "name"),
                        Template(Field(row, "description")) with
                        {
                            Marketplace = Field(row, "marketplace"),
                        }
                    );
                    break;
                case CapabilityCatalog.PluginKind:
                    plugins.Add(
                        (
                            Field(row, "marketplace"),
                            new SandboxSkillPlugin(Field(row, "name"), "1.0.0", Field(row, "description"), [])
                        )
                    );
                    break;
                default:
                    break;
            }
        }

        var catalog = new SandboxSkillCatalog(
            "sess-1",
            "",
            SandboxSkillTier.Included,
            new SandboxSkillCounts(0, 0, 0, 0),
            [
                .. plugins
                    .GroupBy(p => p.Marketplace, StringComparer.Ordinal)
                    .Select(g => new SandboxSkillMarketplace(g.Key, [.. g.Select(p => p.Plugin)])),
            ],
            [],
            false
        );
        var gateway = new SkillTierGateway(
            _ => Task.FromResult(catalog),
            (_, _, _) => Task.CompletedTask,
            _ =>
                Task.FromResult<FunctionContract?>(
                    new FunctionContract
                    {
                        Name = CapabilityTiers.SkillToolName,
                        Description = FakeSkillGateway.SkillText([]),
                        Parameters = [],
                    }
                )
        );
        return (new MutableSubAgentTemplateSource(templates), gateway);

        static string Field(JsonElement row, string name) => row.GetProperty(name).GetString()!;
    }

    /// <summary>A gateway whose marketplaces mk1 and mk2 each have a plugin named <paramref name="plugin"/>, with no skills.</summary>
    private static SkillTierGateway TwoMarketplaces(string plugin)
    {
        var catalog = new SandboxSkillCatalog(
            "sess-1",
            "",
            SandboxSkillTier.Included,
            new SandboxSkillCounts(0, 0, 0, 0),
            [
                .. new[] { "mk1", "mk2" }.Select(alias => new SandboxSkillMarketplace(
                    alias,
                    [new SandboxSkillPlugin(plugin, "1.0.0", $"The {plugin} plugin of {alias}.", [])]
                )),
            ],
            [],
            false
        );
        return new SkillTierGateway(
            _ => Task.FromResult(catalog),
            (_, _, _) => Task.CompletedTask,
            _ =>
                Task.FromResult<FunctionContract?>(
                    new FunctionContract
                    {
                        Name = CapabilityTiers.SkillToolName,
                        Description = FakeSkillGateway.SkillText([]),
                        Parameters = [],
                    }
                )
        );
    }
}
