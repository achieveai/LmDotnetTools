using AchieveAi.LmDotnetTools.LmAgentInfra.Capabilities;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using AchieveAi.LmDotnetTools.Sandbox;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AchieveAi.LmDotnetTools.LmAgentInfra.Tests.Capabilities;

/// <summary>
/// What the model reads back from <c>SearchSkill</c>, driven through the root provider exactly as a run calls it.
/// </summary>
public class CapabilityTiersSearchSkillTests
{
    // MT 1a item A2: a name two skills share used to print the PATH as the call form, which the gateway's Skill
    // tool rejects ("Skill not found"). The gateway name is the only form it accepts.
    [Fact]
    public async Task SearchSkill_TwoSkillsShareAName_LoadFormIsTheGatewayName_WithUnixLineEndings()
    {
        using var tiers = new CapabilityTiers("thread-1", new InMemoryConversationStore(), NullLogger.Instance);
        var catalog = new SandboxSkillCatalog(
            "sess-1",
            "default",
            SandboxSkillTier.Included,
            new SandboxSkillCounts(2, 0, 0, 0),
            [Marketplace("gb-plugins"), Marketplace("claude-plugins")],
            [],
            false
        );
        await tiers.ConfigureAsync(
            new TierSettings([], [], [], []),
            new SkillTierGateway(
                _ => Task.FromResult(catalog),
                (_, _, _) => Task.CompletedTask,
                _ => Task.FromResult<FunctionContract?>(null)
            ),
            CancellationToken.None
        );
        var search = tiers
            .RootProvider.GetFunctions()
            .Single(f => f.Contract.Name == CapabilityTiers.SearchSkillToolName);

        var result = await search.Handler("""{"query":"using hitl"}""", new ToolCallContext(), CancellationToken.None);

        var text = result.Should().BeOfType<ToolHandlerResult.Resolved>().Subject.Payload.Text;
        text.Should().Contain("Load: Skill(skill=\"hitl:using-hitl\")").And.NotContain("/marketplaces/");
        text.Should().NotContain("\r", "the model must read the same bytes on every OS");
    }

    // Contract 7: input rules, limit clamping, [loaded]/[available] from the root view, and the no-match hint.
    [Fact]
    public async Task SearchSkill_InputRules_LoadedMarks_AndNoMatchHint()
    {
        const string Thread = "thread-1";
        var gw = new FakeSkillGateway([.. Enumerable.Range(1, 25).Select(i => ("review", $"code-review-{i:00}"))]);
        var store = new InMemoryConversationStore();
        await store.SaveMetadataAsync(Thread, new ThreadMetadata { ThreadId = Thread, LastUpdated = 1 });
        using var tiers = new CapabilityTiers(Thread, store, NullLogger.Instance);
        await tiers.ConfigureAsync(
            new TierSettings(["skill:review:code-review-07"], [], [], []),
            gw.For(Thread),
            CancellationToken.None
        );
        var search = tiers
            .RootProvider.GetFunctions()
            .Single(f => f.Contract.Name == CapabilityTiers.SearchSkillToolName);

        async Task<ToolHandlerResultPayload> CallAsync(string args) =>
            (
                (ToolHandlerResult.Resolved)await search.Handler(args, new ToolCallContext(), CancellationToken.None)
            ).Payload;

        foreach (var bad in new[] { "", "{}", """{"query":"   "}""", """{"query":42}""", "{not json" })
        {
            var payload = await CallAsync(bad);
            payload.IsError.Should().BeTrue(bad);
            payload.ErrorCode.Should().Be("invalid_args", bad);
        }

        // FTS5 operators in the query are words, never syntax.
        (await CallAsync("""{"query":"\" OR * NEAR( - : ) AND review"}"""))
            .IsError.Should()
            .BeFalse();
        HitRows(await CallAsync("""{"query":"code review","limit":0}""")).Should().HaveCount(1);
        HitRows(await CallAsync("""{"query":"code review","limit":-5}""")).Should().HaveCount(1);
        var max = await CallAsync("""{"query":"code review","limit":99}""");
        HitRows(max).Should().HaveCount(20);
        max.Text.Should().StartWith("20 skills found.\n");
        HitRows(await CallAsync("""{"query":"code review"}""")).Should().HaveCount(5);
        HitRows(await CallAsync("""{"query":"code review","limit":"7"}""")).Should().HaveCount(5);

        HitRows(await CallAsync("""{"query":"code-review-07","limit":1}"""))
            .Should()
            .Equal("1. review:code-review-07 (plugin: review) [loaded]");
        HitRows(await CallAsync("""{"query":"code-review-08","limit":1}"""))
            .Should()
            .Equal("1. review:code-review-08 (plugin: review) [available]");

        var none = await CallAsync("""{"query":"zzqx qqqv"}""");
        none.IsError.Should().BeFalse("no match is an answer, not a failure");
        none.Text.Should().StartWith("No skills matched \"zzqx qqqv\". Try different words");
    }

    // Probe defect (ws1 1b tests): JSON that parses but is not an object made the handler throw
    // InvalidOperationException instead of answering like a missing query.
    [Theory]
    [InlineData("[1,2]")]
    [InlineData("\"text\"")]
    [InlineData("42")]
    public async Task SearchSkill_NonObjectArgs_IsTheMissingQueryError_NotAThrow(string args)
    {
        var gw = new FakeSkillGateway();
        using var tiers = new CapabilityTiers("thread-1", new InMemoryConversationStore(), NullLogger.Instance);
        await tiers.ConfigureAsync(new TierSettings([], [], [], []), gw.For("thread-1"), CancellationToken.None);
        var search = tiers
            .RootProvider.GetFunctions()
            .Single(f => f.Contract.Name == CapabilityTiers.SearchSkillToolName);

        var nonObject = await search.Handler(args, new ToolCallContext(), CancellationToken.None);
        var missing = await search.Handler("{}", new ToolCallContext(), CancellationToken.None);

        var payload = nonObject.Should().BeOfType<ToolHandlerResult.Resolved>().Subject.Payload;
        payload.IsError.Should().BeTrue();
        payload.ErrorCode.Should().Be("invalid_args");
        payload.Should().Be(((ToolHandlerResult.Resolved)missing).Payload, "the same answer a missing query gets");
    }

    // Fails if a query's text reaches a log row at Information or above: it may carry user data. The Information
    // row keeps only its length and the hit count; the text and the hit keys go to Debug.
    [Fact]
    public async Task SearchSkill_Log_InformationHasLengthAndCount_QueryTextIsDebugOnly()
    {
        const string Thread = "thread-1";
        const string Query = "code review for acme payroll";
        var logger = new RecordingLogger();
        var gw = new FakeSkillGateway([("review", "code-review-01"), ("review", "code-review-02"), ("ops", "deploy")]);
        using var tiers = new CapabilityTiers(Thread, new InMemoryConversationStore(), logger);
        await tiers.ConfigureAsync(new TierSettings([], [], [], []), gw.For(Thread), CancellationToken.None);
        var search = tiers
            .RootProvider.GetFunctions()
            .Single(f => f.Contract.Name == CapabilityTiers.SearchSkillToolName);

        var result = await search.Handler($$"""{"query":"{{Query}}"}""", new ToolCallContext(), CancellationToken.None);

        var hitCount = HitRows(((ToolHandlerResult.Resolved)result).Payload).Length;
        hitCount.Should().BePositive("guard: the query must find something, or the count proves nothing");
        var info = logger
            .Starting("SearchSkill {")
            .Where(r => r.Level == LogLevel.Information)
            .Should()
            .ContainSingle()
            .Subject;
        info.Props.Should().Contain("ThreadId", Thread).And.Contain("QueryLength", Query.Length);
        info.Props.Should().Contain("HitCount", hitCount);
        logger
            .Rows.Where(r => r.Level >= LogLevel.Information)
            .SelectMany(r => r.Props.Values)
            .Select(v => v?.ToString() ?? "")
            .Should()
            .NotContain(v => v.Contains("payroll"), "the query text is Debug only");
        logger
            .Starting("SearchSkill query")
            .Should()
            .ContainSingle(r => r.Level == LogLevel.Debug && Equals(r.Props["Query"], Query));
    }

    private static string[] HitRows(ToolHandlerResultPayload payload) =>
        [
            .. payload
                .Text.Split('\n')
                .Where(line => line.Length > 0 && char.IsAsciiDigit(line[0]) && line.Contains(". ")),
        ];

    private static SandboxSkillMarketplace Marketplace(string alias) =>
        new(
            alias,
            [
                new SandboxSkillPlugin(
                    "hitl",
                    null,
                    "",
                    [
                        new SandboxSkill(
                            "hitl:using-hitl",
                            $"/marketplaces/{alias}/hitl/skills/using-hitl",
                            "Use when talking to the human through HITL.",
                            SandboxSkillTier.Included,
                            SandboxSkillTierOrigin.Default,
                            false,
                            null,
                            null,
                            false
                        ),
                    ]
                ),
            ]
        );
}
