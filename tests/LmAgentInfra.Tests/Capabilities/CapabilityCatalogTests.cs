using AchieveAi.LmDotnetTools.LmAgentInfra.Capabilities;
using AchieveAi.LmDotnetTools.Sandbox;

namespace AchieveAi.LmDotnetTools.LmAgentInfra.Tests.Capabilities;

/// <summary>
/// Real in-memory FTS5: each ranking mechanism, query safety, skill resolution and selector parsing.
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
                "tool:Bash",
                "no-kind",
                "skill:",
            ],
            issues
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
            new SelectorIssue("tool:Bash", SelectorProblem.Malformed, []),
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
            issues
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
