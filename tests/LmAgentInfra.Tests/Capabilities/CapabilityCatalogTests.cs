using AchieveAi.LmDotnetTools.LmAgentInfra.Capabilities;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using AchieveAi.LmDotnetTools.Sandbox;

namespace AchieveAi.LmDotnetTools.LmAgentInfra.Tests.Capabilities;

/// <summary>
/// Real in-memory FTS5: each ranking mechanism, per-kind isolation, query safety, skill resolution and
/// selector parsing per kind.
/// </summary>
public class CapabilityCatalogTests
{
    private static readonly CapabilityItem Pdf = Skill(
        "/m/official/document-skills/skills/pdf",
        "document-skills:pdf",
        "document-skills",
        "Merge, split and fill forms."
    );

    private static readonly CapabilityItem PdfHeavyDescription = Skill(
        "/m/official/office/skills/pdf-converter",
        "office:pdf-converter",
        "office",
        "Converts pdf files: pdf to docx, pdf to xlsx, pdf to images."
    );

    private static readonly CapabilityItem Debugging = Skill(
        "/m/gb/debugging/skills/systematic-debugging",
        "debugging:systematic-debugging",
        "debugging",
        "Use when encountering any bug, test failure, or unexpected behavior."
    );

    private static readonly CapabilityItem Standup = Skill(
        "/m/official/engineering/skills/standup",
        "engineering:standup",
        "engineering",
        "Writes a daily standup from recent commits."
    );

    [Theory]
    [InlineData("merging pdfs", "document-skills:pdf", null)] // porter: merging/merge, pdfs/pdf
    [InlineData("create issue", "jira:jiraCreateIssue", "jira:jiraCreateIssue")] // camelCase name words
    [InlineData("create issue", "jira:jira_create_issue", "jira:jira_create_issue")] // snake_case
    [InlineData("create issue", "jira:jira-create-issue", "jira:jira-create-issue")] // kebab-case
    [InlineData("systmatic", "debugging:systematic-debugging", null)] // trigram typo fallback
    [InlineData("pdf", "document-skills:pdf", null)] // exact name beats a pdf-named item whose description says pdf 4 times
    public void Search_FindsTheIntendedItemFirst(string query, string expectedName, string? jiraName)
    {
        List<CapabilityItem> items = [Pdf, PdfHeavyDescription, Debugging, Standup];
        if (jiraName is not null)
        {
            // The description shares no word with the query, so only the split name can match.
            items.Add(Skill("/m/official/jira/skills/" + jiraName, jiraName, "jira", "Opens a ticket in the tracker."));
        }

        using var catalog = CapabilityCatalog.Build(items);

        var hits = catalog.Search(query, CapabilityCatalog.SkillKind, 5);

        hits.Should().NotBeEmpty();
        hits[0].Name.Should().Be(expectedName);
    }

    [Theory]
    [InlineData("\"", true)]
    [InlineData("foo\"bar", false)]
    [InlineData("*", true)]
    [InlineData("NEAR(a b)", false)]
    [InlineData("a AND OR NOT b", false)]
    [InlineData("-x", false)]
    [InlineData("col:val", false)]
    [InlineData("^", true)]
    [InlineData("(", true)]
    [InlineData("''", true)]
    [InlineData("调试 🐛 debugging", false)]
    [InlineData("", true)]
    [InlineData("   ", true)]
    public void Search_HostileOrBlankQuery_NeverThrows_BlankReturnsNothing(string query, bool expectEmpty)
    {
        using var catalog = CapabilityCatalog.Build([Pdf, PdfHeavyDescription, Debugging, Standup]);

        var hits = catalog.Search(query, CapabilityCatalog.SkillKind, 5);

        if (expectEmpty)
        {
            hits.Should().BeEmpty();
        }
    }

    [Fact]
    public void Search_VeryLongQuery_NeverThrows()
    {
        using var catalog = CapabilityCatalog.Build([Pdf, Debugging]);
        var query = string.Join(' ', Enumerable.Range(0, 800).Select(i => $"w{i}\"*"));

        var act = () => catalog.Search(query, CapabilityCatalog.SkillKind, 5);

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData("/m/gb/debugging/skills/systematic-debugging", "/m/gb/debugging/skills/systematic-debugging")] // path
    [InlineData("debugging:systematic-debugging", "/m/gb/debugging/skills/systematic-debugging")] // gateway name
    [InlineData("standup", "/m/official/engineering/skills/standup")] // bare name, unique
    [InlineData("systematic-debugging", null)] // bare name in two plugins
    [InlineData("nope", null)] // unknown
    public void TryResolveSkill_PathOrUniqueName_RefusesAmbiguousAndUnknown(string reference, string? expectedPath)
    {
        var duplicate = Skill(
            "/m/superpowers/superpowers/skills/systematic-debugging",
            "superpowers:systematic-debugging",
            "superpowers",
            "Same name, other plugin."
        );
        using var catalog = CapabilityCatalog.Build([Debugging, duplicate, Standup]);

        var resolved = catalog.TryResolveSkill(reference, out var item);

        resolved.Should().Be(expectedPath is not null);
        if (expectedPath is not null)
        {
            CapabilityCatalog.SkillPathOf(item.Key).Should().Be(expectedPath);
        }
    }

    [Fact]
    public void ResolveSelectors_ExpandsPluginsAndNames_ReportsUnknownAndAmbiguous()
    {
        var gateway = new SandboxSkillCatalog(
            "sess-1",
            "",
            SandboxSkillTier.Included,
            new SandboxSkillCounts(4, 0, 0, 0),
            [
                new SandboxSkillMarketplace(
                    "gb-plugins",
                    [
                        new SandboxSkillPlugin(
                            "debugging",
                            "1.0.0",
                            "",
                            [
                                GatewaySkill(
                                    "debugging:systematic-debugging",
                                    "/marketplaces/gb-plugins/debugging/skills/systematic-debugging"
                                ),
                                GatewaySkill(
                                    "debugging:debug-with-logs",
                                    "/marketplaces/gb-plugins/debugging/skills/debug-with-logs"
                                ),
                            ]
                        ),
                    ]
                ),
                new SandboxSkillMarketplace(
                    "superpowers",
                    [
                        new SandboxSkillPlugin(
                            "superpowers",
                            null,
                            "",
                            [
                                GatewaySkill(
                                    "superpowers:systematic-debugging",
                                    "/marketplaces/superpowers/superpowers/skills/systematic-debugging"
                                ),
                                GatewaySkill(
                                    "superpowers:brainstorming",
                                    "/marketplaces/superpowers/superpowers/skills/brainstorming"
                                ),
                            ]
                        ),
                    ]
                ),
            ],
            [GatewaySkill("greet", "/workspace/.claude/skills/greet")],
            false
        );
        using var catalog = CapabilityCatalog.Build(CapabilityCatalog.FromSkills(gateway));
        var issues = new List<SelectorIssue>();

        var keys = catalog.ResolveSelectors(
            [
                "skill:superpowers:systematic-debugging",
                "skill:/marketplaces/superpowers/superpowers/skills/brainstorming",
                "plugin:gb-plugins/debugging",
                "skill:greet",
                "skill:debugging:systematic-debugging", // already loaded by the plugin selector
                "skill:systematic-debugging",
                "skill:nope",
                "plugin:gb-plugins/nope",
                "tool:Bash", // the tool call's business
                "widget:Bash",
                "no-kind",
                "skill:",
            ],
            issues,
            CapabilityCatalog.SkillKind
        );

        keys.Should()
            .Equal(
                "skill:/marketplaces/superpowers/superpowers/skills/systematic-debugging",
                "skill:/marketplaces/superpowers/superpowers/skills/brainstorming",
                "skill:/marketplaces/gb-plugins/debugging/skills/systematic-debugging",
                "skill:/marketplaces/gb-plugins/debugging/skills/debug-with-logs",
                "skill:/workspace/.claude/skills/greet"
            );
        SelectorIssue[] expected =
        [
            new SelectorIssue(
                "skill:systematic-debugging",
                SelectorProblem.Ambiguous,
                ["debugging:systematic-debugging", "superpowers:systematic-debugging"]
            ),
            new SelectorIssue("skill:nope", SelectorProblem.Unknown, []),
            new SelectorIssue("plugin:gb-plugins/nope", SelectorProblem.Unknown, []),
            new SelectorIssue("widget:Bash", SelectorProblem.Malformed, []),
            new SelectorIssue("no-kind", SelectorProblem.Malformed, []),
            new SelectorIssue("skill:", SelectorProblem.Malformed, []),
        ];
        issues.Should().BeEquivalentTo(expected, options => options.WithStrictOrdering());
    }

    [Fact]
    public void ResolveSelectors_PluginQualifiedRef_MatchesMarketplaceQualifiedGatewayNames()
    {
        // Seen on the live gateway: when two marketplaces carry the same plugin, every skill name gains the
        // marketplace as a third segment, so `hitl:using-hitl` is never an exact name there.
        using var catalog = CapabilityCatalog.Build([
            Skill("/marketplaces/gb-plugins/hitl/skills/using-hitl", "gb-plugins:hitl:using-hitl", "hitl", "."),
            Skill("/marketplaces/claude-plugins/hitl/skills/using-hitl", "claude-plugins:hitl:using-hitl", "hitl", "."),
            Skill(
                "/marketplaces/gb-plugins/clean-builds/skills/clean-builds",
                "gb-plugins:clean-builds:clean-builds",
                "clean-builds",
                "."
            ),
        ]);
        var issues = new List<SelectorIssue>();

        var keys = catalog.ResolveSelectors(
            ["skill:hitl:using-hitl", "skill:clean-builds:clean-builds", "skill:ns:clean-builds"],
            issues,
            CapabilityCatalog.SkillKind
        );

        keys.Should().Equal("skill:/marketplaces/gb-plugins/clean-builds/skills/clean-builds");
        SelectorIssue[] expected =
        [
            new SelectorIssue(
                "skill:hitl:using-hitl",
                SelectorProblem.Ambiguous,
                ["claude-plugins:hitl:using-hitl", "gb-plugins:hitl:using-hitl"]
            ),
            // Segment-aligned: `ns:clean-builds` is not a suffix of `gb-plugins:clean-builds:clean-builds`.
            new SelectorIssue("skill:ns:clean-builds", SelectorProblem.Unknown, []),
        ];
        issues.Should().BeEquivalentTo(expected, options => options.WithStrictOrdering());
    }

    // D-SCW1: with only one marketplace carrying the plugin, the gateway leaves the marketplace out of the name, so a
    // mode's marketplace-qualified selector must still find the skill by its item's own marketplace.
    [Fact]
    public void ResolveSelectors_MarketplaceQualifiedRef_MatchesAnUnprefixedNameInThatMarketplaceOnly()
    {
        var usingHitl = new CapabilityItem(
            "skill:/marketplaces/gb-plugins/hitl/skills/using-hitl",
            CapabilityCatalog.SkillKind,
            "hitl:using-hitl",
            "hitl",
            "gb-plugins",
            "."
        );
        using var catalog = CapabilityCatalog.Build([usingHitl]);
        var issues = new List<SelectorIssue>();

        var keys = catalog.ResolveSelectors(
            ["skill:gb-plugins:hitl:using-hitl", "skill:claude-plugins:hitl:using-hitl"],
            issues,
            CapabilityCatalog.SkillKind
        );

        keys.Should().Equal(usingHitl.Key);
        issues
            .Should()
            .BeEquivalentTo([new SelectorIssue("skill:claude-plugins:hitl:using-hitl", SelectorProblem.Unknown, [])]);
    }

    // C3: SearchSkill asks for skills only; agents and plugins that match the same words never fill its slots.
    [Fact]
    public void Search_ReturnsOnlyTheRequestedKind_AndAtMostLimit()
    {
        using var catalog = CapabilityCatalog.Build([
            Skill("/m/official/review/skills/code-review", "review:code-review", "review", "Reviews a pull request."),
            Skill("/m/official/review/skills/security-review", "review:security-review", "review", "Reviews code."),
            Skill("/m/official/review/skills/review-notes", "review:review-notes", "review", "Writes review notes."),
            new("agent:/m/official/review/agents/reviewer", "agent", "review", "review", "official", "Reviews code."),
            new("plugin:official/review", "plugin", "review", null, "official", "Review tools."),
        ]);

        var skills = catalog.Search("review", CapabilityCatalog.SkillKind, 2);

        skills.Should().HaveCount(2).And.OnlyContain(i => i.Kind == CapabilityCatalog.SkillKind);
        catalog
            .Search("review", "agent", 5)
            .Select(i => i.Key)
            .Should()
            .Equal("agent:/m/official/review/agents/reviewer");
        catalog.Search("review", "plugin", 5).Select(i => i.Key).Should().Equal("plugin:official/review");
    }

    // A kind with no items has no table of its own: its search is empty, never a failure (an empty sandbox, or an
    // index holding agents but no skills).
    [Fact]
    public void Search_KindWithNoItems_ReturnsEmpty()
    {
        using var empty = CapabilityCatalog.Build([]);
        using var agentsOnly = CapabilityCatalog.Build([Agent("documents:pdf-reviewer", "Reviews pdf files.")]);

        empty.Search("pdf", CapabilityCatalog.SkillKind, 5).Should().BeEmpty();
        agentsOnly.Search("pdf", CapabilityCatalog.SkillKind, 5).Should().BeEmpty();
        agentsOnly
            .Search("pdf", CapabilityCatalog.AgentKind, 5)
            .Select(i => i.Key)
            .Should()
            .Equal("agent:documents:pdf-reviewer");
    }

    [Fact]
    public void Search_FromEightThreads_NeverThrows_AndMatchesSerialResults()
    {
        using var catalog = CapabilityCatalog.Build([Pdf, PdfHeavyDescription, Debugging, Standup]);
        string[] queries = ["merging pdfs", "systmatic", "daily standup", "pdf", "bug in tests"];
        var serial = queries.Select(q => Names(catalog.Search(q, CapabilityCatalog.SkillKind, 5))).ToArray();

        var mismatches = 0;
        _ = Parallel.For(
            0,
            8,
            new ParallelOptions { MaxDegreeOfParallelism = 8 },
            _ =>
            {
                for (var round = 0; round < 20; round++)
                {
                    for (var i = 0; i < queries.Length; i++)
                    {
                        if (Names(catalog.Search(queries[i], CapabilityCatalog.SkillKind, 5)) != serial[i])
                        {
                            _ = Interlocked.Increment(ref mismatches);
                        }
                    }
                }
            }
        );

        mismatches.Should().Be(0);
    }

    // Per-kind tables: agents and plugins that repeat the skills' words change neither which skills a query
    // finds nor their scores, even when they come first and shift every row id.
    [Fact]
    public void Search_RanksEachKindAgainstItsOwnRows_OtherKindsNeverMoveASkillResult()
    {
        CapabilityItem[] skills = [Pdf, PdfHeavyDescription, Debugging, Standup];
        CapabilityItem[] others =
        [
            Agent("debugging:bug-hunter", "Debugging any bug or test failure, then merging the pdf fix."),
            Agent("engineering:standup-writer", "Writes the daily standup from commits: standup, standup, standup."),
            new(
                "plugin:official/debugging",
                CapabilityCatalog.PluginKind,
                "debugging",
                "debugging",
                "official",
                "Debugging skills and agents for any bug or test failure."
            ),
        ];
        using var skillsOnly = CapabilityCatalog.Build(skills);
        using var mixed = CapabilityCatalog.Build([.. others, .. skills]);

        foreach (var query in new[] { "bug in tests", "daily standup", "merging pdfs", "systmatic", "debugging" })
        {
            mixed
                .SearchScored(query, CapabilityCatalog.SkillKind, 5)
                .Should()
                .Equal(skillsOnly.SearchScored(query, CapabilityCatalog.SkillKind, 5), query);
        }

        mixed
            .Search("bug-hunter standup-writer", CapabilityCatalog.SkillKind, 5)
            .Select(i => i.Key)
            .Should()
            .BeEquivalentTo(Debugging.Key, Standup.Key);
        mixed
            .Search("pdf debugging standup", CapabilityCatalog.AgentKind, 5)
            .Select(i => i.Key)
            .Should()
            .BeEquivalentTo("agent:debugging:bug-hunter", "agent:engineering:standup-writer");
    }

    // Typo fallback reads its own kind's vocabulary: an agent's word must not hide a skill-word correction.
    [Fact]
    public void Search_CorrectsATypoFromItsOwnKindsWordsOnly()
    {
        var skill = Skill("/m/official/ops/skills/cluster", "ops:cluster", "ops", "Scales kubernetes deployments.");
        var agent = Agent("ops:helper", "Spells it kubernetis.");
        using var catalog = CapabilityCatalog.Build([skill, agent]);

        catalog.Search("kubernetis", CapabilityCatalog.SkillKind, 5).Should().Equal(skill);
        catalog.Search("kubernetis", CapabilityCatalog.AgentKind, 5).Should().Equal(agent);
    }

    [Fact]
    public void ResolveSelectors_AgentKind_ResolvesTemplateKeysAndSegments_SkillCallSkipsThem()
    {
        var templates = new Dictionary<string, SubAgentTemplate>(StringComparer.Ordinal)
        {
            ["review:code-reviewer"] = Template("Reviews a finished branch."),
            ["progress:architect"] = Template("Records accepted architecture changes."),
            ["development:architect"] = Template("Designs the smallest testable slice."),
            ["general-purpose"] = Template(null),
        };

        var items = CapabilityCatalog.FromTemplates(templates);

        items
            .Should()
            .Equal(
                new CapabilityItem(
                    "agent:development:architect",
                    CapabilityCatalog.AgentKind,
                    "development:architect",
                    "development",
                    null,
                    "Designs the smallest testable slice."
                ),
                new CapabilityItem(
                    "agent:general-purpose",
                    CapabilityCatalog.AgentKind,
                    "general-purpose",
                    null,
                    null,
                    ""
                ),
                new CapabilityItem(
                    "agent:progress:architect",
                    CapabilityCatalog.AgentKind,
                    "progress:architect",
                    "progress",
                    null,
                    "Records accepted architecture changes."
                ),
                new CapabilityItem(
                    "agent:review:code-reviewer",
                    CapabilityCatalog.AgentKind,
                    "review:code-reviewer",
                    "review",
                    null,
                    "Reviews a finished branch."
                )
            );
        CapabilityCatalog
            .FromTemplates(templates.Reverse().ToDictionary(StringComparer.Ordinal))
            .Should()
            .Equal(items, "the same templates give the same items whatever the dictionary order");

        using var catalog = CapabilityCatalog.Build([.. items, Debugging]);
        string[] selectors =
        [
            "agent:development:architect",
            "agent:code-reviewer", // the only template whose last segment is code-reviewer
            "agent:architect", // two templates end with :architect
            "agent:nope",
            "skill:debugging:systematic-debugging", // the skill call's business
            "tool:Bash", // the tool call's business
        ];

        var (agentKeys, agentIssues) = Resolve(catalog, CapabilityCatalog.AgentKind, selectors);
        var (skillKeys, skillIssues) = Resolve(catalog, CapabilityCatalog.SkillKind, selectors);

        agentKeys.Should().Equal("agent:development:architect", "agent:review:code-reviewer");
        SelectorIssue[] expectedAgentIssues =
        [
            new("agent:architect", SelectorProblem.Ambiguous, ["development:architect", "progress:architect"]),
            new("agent:nope", SelectorProblem.Unknown, []),
        ];
        agentIssues.Should().BeEquivalentTo(expectedAgentIssues, options => options.WithStrictOrdering());
        skillKeys.Should().Equal(Debugging.Key);
        skillIssues.Should().BeEmpty();
    }

    [Fact]
    public void ResolveSelectors_PluginSelector_ExpandsToItemsOfTheRequestedKind()
    {
        var gateway = new SandboxSkillCatalog(
            "sess-1",
            "",
            SandboxSkillTier.Included,
            new SandboxSkillCounts(1, 0, 0, 0),
            [
                new SandboxSkillMarketplace(
                    "gb-plugins",
                    [
                        new SandboxSkillPlugin(
                            "debugging",
                            "1.0.0",
                            "Systematic debugging.",
                            [
                                GatewaySkill(
                                    "debugging:systematic-debugging",
                                    "/marketplaces/gb-plugins/debugging/skills/systematic-debugging"
                                ),
                            ]
                        ),
                        new SandboxSkillPlugin("clean-builds", null, "Zero-warning builds.", []),
                    ]
                ),
            ],
            [],
            false
        );
        var plugins = CapabilityCatalog.FromPlugins(gateway);
        var agents = CapabilityCatalog.FromTemplates(
            new Dictionary<string, SubAgentTemplate>(StringComparer.Ordinal)
            {
                ["debugging:logging-review"] = Template("Reviews logging."), // no marketplace: matched by plugin name
                ["review:debugging"] = Template("Named debugging, in plugin review."),
            }
        );
        using var catalog = CapabilityCatalog.Build([.. CapabilityCatalog.FromSkills(gateway), .. plugins, .. agents]);
        string[] selectors =
        [
            "plugin:gb-plugins/debugging",
            "plugin:gb-plugins/clean-builds",
            "plugin:gb-plugins/nope",
        ];
        var unknown = new SelectorIssue("plugin:gb-plugins/nope", SelectorProblem.Unknown, []);

        var (skillKeys, skillIssues) = Resolve(catalog, CapabilityCatalog.SkillKind, selectors);
        var (agentKeys, agentIssues) = Resolve(catalog, CapabilityCatalog.AgentKind, selectors);
        var (pluginKeys, pluginIssues) = Resolve(catalog, CapabilityCatalog.PluginKind, selectors);

        plugins
            .Should()
            .Equal(
                new CapabilityItem(
                    "plugin:gb-plugins/debugging",
                    CapabilityCatalog.PluginKind,
                    "debugging",
                    "debugging",
                    "gb-plugins",
                    "Systematic debugging."
                ),
                new CapabilityItem(
                    "plugin:gb-plugins/clean-builds",
                    CapabilityCatalog.PluginKind,
                    "clean-builds",
                    "clean-builds",
                    "gb-plugins",
                    "Zero-warning builds."
                )
            );
        // clean-builds has a plugin row but no skill or agent: nothing to load, and not an issue.
        skillKeys.Should().Equal("skill:/marketplaces/gb-plugins/debugging/skills/systematic-debugging");
        skillIssues.Should().BeEquivalentTo([unknown]);
        agentKeys.Should().Equal("agent:debugging:logging-review");
        agentIssues.Should().BeEquivalentTo([unknown]);
        pluginKeys.Should().Equal("plugin:gb-plugins/debugging", "plugin:gb-plugins/clean-builds");
        pluginIssues.Should().BeEquivalentTo([unknown]);
    }

    // F-017: a plugin selector names a marketplace + plugin pair, and is Unknown only when no item of any kind
    // carries that exact pair. An agent with no marketplace never makes a misspelt marketplace real.
    [Fact]
    public void ResolveSelectors_PluginSelector_UnknownOnlyWhenNoItemHasThatMarketplaceAndPlugin()
    {
        var debuggingSkill = Skill(
            "/marketplaces/official/debugging/skills/systematic-debugging",
            "debugging:systematic-debugging",
            "debugging",
            "Find the root cause of a bug."
        );
        var pdfSkill = Skill("/marketplaces/official/pdf/skills/pdf", "pdf:pdf", "pdf", "Fill pdf forms.");
        var debuggingAgent = Agent("debugging:x", "Debugs.");
        using var catalog = CapabilityCatalog.Build([debuggingSkill, pdfSkill, debuggingAgent]);
        var typo = new SelectorIssue("plugin:typo/debugging", SelectorProblem.Unknown, []);

        // A misspelt marketplace: Unknown on both passes, although an agent carries the plugin name.
        foreach (var kind in new[] { CapabilityCatalog.SkillKind, CapabilityCatalog.AgentKind })
        {
            var (keys, issues) = Resolve(catalog, kind, ["plugin:typo/debugging"]);
            keys.Should().BeEmpty(kind);
            issues.Should().BeEquivalentTo([typo], kind);
        }

        // The real pair: each pass gets its own kind of that plugin (the agent by plugin name), with no issue.
        var (skillKeys, skillIssues) = Resolve(catalog, CapabilityCatalog.SkillKind, ["plugin:official/debugging"]);
        var (agentKeys, agentIssues) = Resolve(catalog, CapabilityCatalog.AgentKind, ["plugin:official/debugging"]);
        skillKeys.Should().Equal(debuggingSkill.Key);
        skillIssues.Should().BeEmpty();
        agentKeys.Should().Equal(debuggingAgent.Key);
        agentIssues.Should().BeEmpty();

        // A skills-only plugin pinned in a mode that also tiers agents: the agent pass lists nothing and warns nothing.
        var (pdfAgentKeys, pdfAgentIssues) = Resolve(catalog, CapabilityCatalog.AgentKind, ["plugin:official/pdf"]);
        pdfAgentKeys.Should().BeEmpty();
        pdfAgentIssues.Should().BeEmpty();
    }

    // Paper cut 1: a marketplace agent carries its own marketplace. A plugin: selector loads it only from there, and
    // a plugin that ships only sub-agents is a known plugin in that marketplace.
    [Fact]
    public void ResolveSelectors_PluginSelector_LoadsAMarketplaceAgentOnlyFromItsOwnMarketplace()
    {
        var mk2Skill = new CapabilityItem(
            CapabilityCatalog.SkillKeyPrefix + "/marketplaces/mk2/debugging/skills/trace",
            CapabilityCatalog.SkillKind,
            "debugging:trace",
            "debugging",
            "mk2",
            "Trace a failure."
        );
        var mk1Agent = CapabilityCatalog.FromTemplates(
            new Dictionary<string, SubAgentTemplate>(StringComparer.Ordinal)
            {
                ["debugging:x"] = Template("Debugs.") with { Marketplace = "mk1" },
            }
        )[0];
        using var catalog = CapabilityCatalog.Build([mk2Skill, mk1Agent]);

        var (mk2Keys, mk2Issues) = Resolve(catalog, CapabilityCatalog.AgentKind, ["plugin:mk2/debugging"]);
        mk2Keys.Should().BeEmpty("the agent was kept from mk1, so mk2's debugging plugin does not hold it");
        mk2Issues.Should().BeEmpty("mk2/debugging is a real plugin");

        var (mk1Keys, mk1Issues) = Resolve(catalog, CapabilityCatalog.AgentKind, ["plugin:mk1/debugging"]);
        mk1Keys.Should().Equal(mk1Agent.Key);
        mk1Issues.Should().BeEmpty("a plugin that ships only sub-agents is known in its own marketplace");
    }

    private static (IReadOnlyList<string> Keys, List<SelectorIssue> Issues) Resolve(
        CapabilityCatalog catalog,
        string kind,
        string[] selectors
    )
    {
        var issues = new List<SelectorIssue>();
        var keys = catalog.ResolveSelectors(selectors, issues, kind);
        return (keys, issues);
    }

    private static CapabilityItem Agent(string templateKey, string description) =>
        CapabilityCatalog.FromTemplates(
            new Dictionary<string, SubAgentTemplate>(StringComparer.Ordinal) { [templateKey] = Template(description) }
        )[0];

    private static SubAgentTemplate Template(string? description) =>
        new()
        {
            Description = description,
            SystemPrompt = "Test template.",
            AgentFactory = () => throw new NotSupportedException("Catalog tests never spawn."),
        };

    private static string Names(IReadOnlyList<CapabilityItem> items) => string.Join('|', items.Select(i => i.Name));

    private static CapabilityItem Skill(string path, string name, string? plugin, string description) =>
        new(
            CapabilityCatalog.SkillKeyPrefix + path,
            CapabilityCatalog.SkillKind,
            name,
            plugin,
            "official",
            description
        );

    private static SandboxSkill GatewaySkill(string name, string path) =>
        new(name, path, "", SandboxSkillTier.Included, SandboxSkillTierOrigin.Default, false, null, null, false);
}
