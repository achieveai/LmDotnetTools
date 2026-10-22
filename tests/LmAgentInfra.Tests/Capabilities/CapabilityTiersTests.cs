using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AchieveAi.LmDotnetTools.LmAgentInfra.Capabilities;
using AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmMultiTurn.Compaction;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence.Sqlite;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using AchieveAi.LmDotnetTools.Sandbox;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AchieveAi.LmDotnetTools.LmAgentInfra.Tests.Capabilities;

/// <summary>
/// The root agent's skill tiers, driven the way the host drives them: configure on every build, list and attach
/// <c>Skill</c> as the root, then the compaction hook at each activation. A sub-agent's are driven through the
/// spawn override (<see cref="CapabilityTiers.ForInheritedToolAsync"/>) and the same hook. A <see cref="FakeSkillGateway"/> stands
/// in for one gateway session, so assertions read the PATCHes it received and the <c>Skill</c> text it renders.
/// </summary>
public class CapabilityTiersTests
{
    private const string T = "thread-1792610878575-63a7aa01";
    private const string T2 = "thread-1792611042672-cdb0bb02";
    private const string PinP = "skill:alpha:p";
    private const string SkillTool = CapabilityTiers.SkillToolName;

    private static readonly ToolHandler SkillHandler = (_, _, _) =>
        Task.FromResult<ToolHandlerResult>(ToolHandlerResult.FromText("skill body"));

    public static TheoryData<string[], string[], int, string[]> SeedRows =>
        new()
        {
            { ["skill:alpha:a"], [PinP], 10, ["alpha:a", "alpha:p"] },
            // Contract 6: {} tiers = active with an empty list; every skill stays loadable by name.
            { [], [], 10, [] },
            // Contract 5: the cap bounds rotations only, never the seed.
            { ["skill:alpha:a", "skill:alpha:b"], [PinP], 1, ["alpha:a", "alpha:b", "alpha:p"] },
        };

    public static TheoryData<string> UncarryableThreadIds => ["a/b", "has space", "thread-café", new string('t', 65)];

    // Contract 1 (inert half) and 9 (untier logged on a live change).
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public async Task Configure_NullSettingsOrGateway_StaysInert_NoGatewayCall_NoTools(
        bool withSettings,
        bool withGateway
    )
    {
        var store = await StoreAsync(T);
        var gw = new FakeSkillGateway();
        var logger = new RecordingLogger();
        using var tiers = new CapabilityTiers(T, store, logger);
        var settings = withSettings ? Tiers(["skill:alpha:a"], [PinP]) : null;
        var gateway = withGateway ? gw.For(T) : null;

        await tiers.ConfigureAsync(settings, gateway, CancellationToken.None);

        AssertInert(tiers, gw);
        gw.CatalogCalls.Should().Be(0);
        gw.Patches.Should().BeEmpty();
        (await RawViewAsync(store, T)).Should().BeNull("an untiered conversation never gets a view");
        logger.Rows.Should().BeEmpty("an untiered conversation writes no capability rows");
        (await tiers.OnCheckpointActivatedAsync(Activation(T, 5, 4, SkillCall(3, "beta:c")), default))
            .Should()
            .BeFalse("an inert hook must never make the loop re-estimate its tool tokens");

        // A live tiered conversation switched to an untiered mode: logged once, then nothing reaches the gateway.
        (await BuildAsync(tiers, Tiers(["skill:alpha:a"], [PinP]), gw, T))
            .Should()
            .BeTrue();
        var catalogCalls = gw.CatalogCalls;

        await tiers.ConfigureAsync(settings, gateway, CancellationToken.None);

        AssertInert(tiers, gw);
        logger
            .Starting("Capabilities inert")
            .Should()
            .ContainSingle()
            .Which.Props["Reason"]
            .Should()
            .Be("not configured");
        (await tiers.OnCheckpointActivatedAsync(Activation(T, 9, 8, SkillCall(7, "beta:c")), default))
            .Should()
            .BeFalse();
        gw.CatalogCalls.Should().Be(catalogCalls);
        gw.Patches.Should().ContainSingle("only the tiered build PATCHed");
    }

    // Contracts 2, 5 (cap not applied at seed) and 6.
    [Theory]
    [MemberData(nameof(SeedRows))]
    public async Task Configure_Tiered_PatchesTheThreadIdWithLoadedPlusPinnedIncluded_OthersAvailable(
        string[] loaded,
        string[] pinned,
        int cap,
        string[] expected
    )
    {
        var store = await StoreAsync(T);
        var gw = new FakeSkillGateway();
        var logger = new RecordingLogger();
        using var tiers = new CapabilityTiers(T, store, logger);

        (await BuildAsync(tiers, Tiers(loaded, pinned, cap), gw, T)).Should().BeTrue();

        tiers.IsActive.Should().BeTrue();
        tiers.RootGatewayAgentId.Should().Be(T);
        var patch = gw.Patches.Should().ContainSingle().Subject;
        patch.AgentId.Should().Be(T, "the root's own id; the default agent is shared by every conversation");
        patch
            .Set.Keys.Should()
            .BeEquivalentTo(
                gw.Skills.Select(s => s.Path),
                "a reset PATCH must name every path or the rest stay included"
            );
        IncludedNames(gw, patch.Set).Should().Equal(expected);
        FunctionNames(tiers).Should().Equal(CapabilityTiers.SkillToolName, CapabilityTiers.SearchSkillToolName);
        SkillDescription(tiers).Should().Be(FakeSkillGateway.SkillText(expected));
        await AssertViewAsync(store, T, expected, 1, 0);

        // Every turn configures again: the same PATCH, the same tool bytes.
        var bytes = ProviderBytes(tiers);
        (await BuildAsync(tiers, Tiers(loaded, pinned, cap), gw, T)).Should().BeTrue();

        gw.Patches.Should().HaveCount(2);
        gw.Patches[1].Set.Should().BeEquivalentTo(patch.Set);
        ProviderBytes(tiers).Should().Be(bytes);
        logger.Starting("Capabilities active").Should().ContainSingle().Which.Props["GatewayAgentId"].Should().Be(T);
        logger.Starting("Capabilities applied").Should().HaveCount(2);
        logger.Rows.Should().NotContain(r => r.Level >= LogLevel.Warning);
    }

    // Contract 3 (F4): the gateway's default agent never receives tiers.
    [Theory]
    [MemberData(nameof(UncarryableThreadIds))]
    public async Task Configure_InvalidThreadIdOrDefaultAgent_NeverPatchesTheDefaultAgent(string threadId)
    {
        var store = await StoreAsync(threadId);
        var gw = new FakeSkillGateway();
        var logger = new RecordingLogger();
        using var tiers = new CapabilityTiers(threadId, store, logger);

        await tiers.ConfigureAsync(Tiers(["skill:alpha:a"], [PinP]), gw.For(threadId), CancellationToken.None);

        AssertInert(tiers, gw);
        gw.CatalogCalls.Should().Be(0, "the id is checked before any gateway call");
        gw.Patches.Should().BeEmpty();
        (await RawViewAsync(store, threadId)).Should().BeNull();
        var warn = logger.Rows.Should().ContainSingle().Subject;
        warn.Level.Should().Be(LogLevel.Warning);
        warn.Props["RootThreadId"].Should().Be(threadId);
        // The default agent is selected by sending no id; no conversation can name it.
        FluentActions.Invoking(() => new CapabilityTiers(" ", store, logger)).Should().Throw<ArgumentException>();
    }

    // Contract 4: each conversation on one gateway session keeps its own tiers (and F7 routing by thread).
    [Fact]
    public async Task TwoConversationsOnOneGateway_EachPatchesAndRotatesOnlyItsOwnAgent()
    {
        var store = await StoreAsync(T, T2);
        var gw = new FakeSkillGateway();
        using var first = new CapabilityTiers(T, store, NullLogger.Instance);
        using var second = new CapabilityTiers(T2, store, NullLogger.Instance);
        (await BuildAsync(first, Tiers(["skill:alpha:a"], [PinP]), gw, T)).Should().BeTrue();
        (await BuildAsync(second, Tiers(["skill:beta:c"], [PinP]), gw, T2)).Should().BeTrue();
        var secondBytes = ProviderBytes(second);
        var secondView = await RawViewAsync(store, T2);

        gw.Patches.Select(p => p.AgentId).Should().Equal(T, T2);
        gw.ListedNames(T).Should().Equal("alpha:a", "alpha:p");
        gw.ListedNames(T2).Should().Equal("alpha:p", "beta:c");
        gw.ListedNames("").Should().HaveCount(gw.Skills.Count, "the shared default agent keeps every skill");

        // F7: another conversation's activation is not this instance's to rotate.
        (await first.OnCheckpointActivatedAsync(Activation(T2, 5, 4, SkillCall(3, "beta:d")), default))
            .Should()
            .BeFalse();
        gw.Patches.Should().HaveCount(2);

        (await first.OnCheckpointActivatedAsync(Activation(T, 5, 4, SkillCall(3, "beta:d")), default))
            .Should()
            .BeTrue();

        gw.Patches.Select(p => p.AgentId).Should().Equal(T, T2, T);
        gw.ListedNames(T).Should().Equal("alpha:p", "beta:d");
        SkillDescription(first).Should().Be(FakeSkillGateway.SkillText(["alpha:p", "beta:d"]));
        gw.ListedNames(T2).Should().Equal("alpha:p", "beta:c");
        ProviderBytes(second).Should().Be(secondBytes);
        (await RawViewAsync(store, T2)).Should().Be(secondView);
    }

    // Contract 5: ambiguous, unknown and malformed selectors load nothing and log one WARN each.
    [Fact]
    public async Task Configure_SelectorIssues_OneWarnEachWithCandidates_NotLoaded()
    {
        var store = await StoreAsync(T);
        var gw = new FakeSkillGateway();
        var logger = new RecordingLogger();
        using var tiers = new CapabilityTiers(T, store, logger);

        (
            await BuildAsync(
                tiers,
                Tiers(
                    ["skill:dup", "skill:nope", "nonsense", "plugin:mk/beta", "skill:alpha:a"],
                    // One skill named three ways: full name, path, unique short name.
                    [PinP, "skill:" + FakeSkillGateway.PathOf("alpha", "p"), "skill:p"]
                ),
                gw,
                T
            )
        ).Should().BeTrue();

        var warns = logger.Starting("Capabilities selector unresolved");
        warns.Should().HaveCount(3).And.OnlyContain(w => w.Level == LogLevel.Warning);
        warns.Should().OnlyContain(w => Equals(w.Props["ThreadId"], T) && Equals(w.Props["AgentKey"], "root"));
        warns
            .Select(w => (w.Props["Selector"], w.Props["Problem"]))
            .Should()
            .Equal(
                ("skill:dup", SelectorProblem.Ambiguous),
                ("skill:nope", SelectorProblem.Unknown),
                ("nonsense", SelectorProblem.Malformed)
            );
        ((IEnumerable<string>)warns[0].Props["Candidates"]!).Should().BeEquivalentTo("alpha:dup", "beta:dup");
        ((IEnumerable<string>)warns[1].Props["Candidates"]!).Should().BeEmpty();
        string[] expected = ["alpha:a", "alpha:p", "beta:c", "beta:d", "beta:dup"];
        gw.ListedNames(T).Should().Equal(expected, "alpha:dup stays out: only the plugin selector named beta:dup");
        (await ViewAsync(store, T))!.Loaded.Should().Equal(Keys(expected));
        logger.Rows.Where(r => r.Level >= LogLevel.Warning).Should().HaveCount(3);
    }

    // Contracts 8 and 9: the view is written once (rev 1) and reused after a re-tier and after a restart.
    [Fact]
    public async Task View_SeededOnce_ReusedAfterNewInstanceAndRetier_NoRevisionBump()
    {
        var store = await StoreAsync(T);
        var gw = new FakeSkillGateway();
        var logger = new RecordingLogger();
        var original = Tiers(["skill:alpha:a"], [PinP]);
        string? seeded;
        using (var tiers = new CapabilityTiers(T, store, logger))
        {
            (await BuildAsync(tiers, original, gw, T)).Should().BeTrue();
            seeded = await RawViewAsync(store, T);
            await AssertViewAsync(store, T, ["alpha:a", "alpha:p"], 1, 0);

            (await BuildAsync(tiers, original, gw, T)).Should().BeTrue();
            (await RawViewAsync(store, T)).Should().Be(seeded);

            await tiers.ConfigureAsync(null, gw.For(T), CancellationToken.None);
            tiers.IsActive.Should().BeFalse();
            logger.Starting("Capabilities inert").Should().ContainSingle();

            // Re-tier with a different Loaded: the persisted view wins over the config, with no revision bump.
            (await BuildAsync(tiers, Tiers(["skill:beta:c"], [PinP]), gw, T))
                .Should()
                .BeTrue();
            logger.Starting("Capabilities active").Should().HaveCount(2);
            gw.ListedNames(T).Should().Equal("alpha:a", "alpha:p");
            (await RawViewAsync(store, T)).Should().Be(seeded);
        }

        // Host restart: a new instance on a new gateway session reuses the view and applies it there.
        var restarted = new FakeSkillGateway();
        using var again = new CapabilityTiers(T, store, logger);
        (await BuildAsync(again, Tiers(["skill:beta:d"], [PinP]), restarted, T)).Should().BeTrue();

        restarted.Patches.Should().ContainSingle().Which.AgentId.Should().Be(T);
        restarted.ListedNames(T).Should().Equal("alpha:a", "alpha:p");
        SkillDescription(again).Should().Be(FakeSkillGateway.SkillText(["alpha:a", "alpha:p"]));
        (await RawViewAsync(store, T)).Should().Be(seeded);
        logger.Starting("Capabilities applied").Should().HaveCount(4);
    }

    // U3 (plan Proof): {A,B}+P, used C -> {P,C}; persisted, so a reload keeps it over the config.
    [Fact]
    public async Task Rotation_UsedSkill_BecomesLoaded_UnusedEvicted_AndSurvivesReload()
    {
        var store = await StoreAsync(T);
        var gw = new FakeSkillGateway();
        var logger = new RecordingLogger();
        var config = Tiers(["skill:alpha:a", "skill:alpha:b"], [PinP]);
        using (var tiers = new CapabilityTiers(T, store, logger))
        {
            (await BuildAsync(tiers, config, gw, T)).Should().BeTrue();
            var activation = Activation(
                T,
                10,
                9,
                // A search that found A is not a use of A, whatever its arguments carry.
                (4, CapabilityTiers.SearchSkillToolName, """{"query":"alpha a","skill":"alpha:a"}"""),
                SkillCall(6, "beta:c")
            );

            (await tiers.OnCheckpointActivatedAsync(activation, default)).Should().BeTrue();

            var patch = gw.Patches.Should().HaveCount(2).And.Subject.Last();
            patch.AgentId.Should().Be(T);
            IncludedNames(gw, patch.Set).Should().Equal("alpha:p", "beta:c");
            SkillDescription(tiers).Should().Be(FakeSkillGateway.SkillText(["alpha:p", "beta:c"]));
            await AssertViewAsync(store, T, ["alpha:p", "beta:c"], 2, 10);
            var rotated = logger.Starting("Capabilities rotated").Should().ContainSingle().Subject;
            rotated.Props["ThreadId"].Should().Be(T);
            rotated.Props["Revision"].Should().Be(2);
            ((IEnumerable<string>)rotated.Props["Used"]!).Should().Equal(Keys(["beta:c"]));
            ((IEnumerable<string>)rotated.Props["Promoted"]!).Should().Equal(Keys(["beta:c"]));
            ((IEnumerable<string>)rotated.Props["Evicted"]!).Should().Equal(Keys(["alpha:a", "alpha:b"]));
            logger.Starting("Capabilities relisted").Should().ContainSingle();
        }

        var reloadedGateway = new FakeSkillGateway();
        using var reloaded = new CapabilityTiers(T, store, NullLogger.Instance);
        (await BuildAsync(reloaded, config, reloadedGateway, T)).Should().BeTrue();

        reloadedGateway.ListedNames(T).Should().Equal("alpha:p", "beta:c");
    }

    // The production store: SQLite hands the view back as a JsonElement string, not the string written. A break in
    // that read re-seeds revision 1 from the config on every build and overwrites the rotated view.
    [Fact]
    public async Task SqliteStore_RotatedView_SurvivesReload_NotReseeded()
    {
        await using var sqlite = new TempSqliteStore();
        var store = sqlite.Store;
        await store.SaveMetadataAsync(T, new ThreadMetadata { ThreadId = T, LastUpdated = 1 });
        var config = Tiers(["skill:alpha:a", "skill:alpha:b"], [PinP]);
        using (var tiers = new CapabilityTiers(T, store, NullLogger.Instance))
        {
            (await BuildAsync(tiers, config, new FakeSkillGateway(), T)).Should().BeTrue();
            (await tiers.OnCheckpointActivatedAsync(Activation(T, 10, 9, SkillCall(6, "beta:c")), default))
                .Should()
                .BeTrue();
        }

        await AssertViewAsync(store, T, ["alpha:p", "beta:c"], 2, 10);
        var rotated = await RawViewAsync(store, T);

        var reloadedGateway = new FakeSkillGateway();
        using var reloaded = new CapabilityTiers(T, store, NullLogger.Instance);
        (await BuildAsync(reloaded, config, reloadedGateway, T)).Should().BeTrue();

        reloadedGateway.ListedNames(T).Should().Equal("alpha:p", "beta:c");
        (await RawViewAsync(store, T)).Should().Be(rotated, "a reload reuses the view; it does not re-seed it");
    }

    // A view a newer build wrote is left byte-for-byte as it is (after a downgrade, an overwrite cannot be undone),
    // and the mode stays inert: no PATCH, no Skill or SearchSkill of its own.
    [Fact]
    public async Task SqliteStore_NewerSchemaView_InertNoPatch_ViewUntouched()
    {
        const string Newer = """{"schemaVersion":2,"loaded":["skill:alpha:a"],"revision":7,"rotatedThroughSeq":40}""";
        await using var sqlite = new TempSqliteStore();
        var store = sqlite.Store;
        await store.SaveMetadataAsync(
            T,
            new ThreadMetadata
            {
                ThreadId = T,
                LastUpdated = 1,
                Properties = ImmutableDictionary<string, object>.Empty.Add(CapabilityTiers.ViewPropertyKey, Newer),
            }
        );
        var gw = new FakeSkillGateway();
        var logger = new RecordingLogger();
        using var tiers = new CapabilityTiers(T, store, logger);

        (await BuildAsync(tiers, Tiers(["skill:alpha:a"], [PinP]), gw, T)).Should().BeFalse();

        AssertInert(tiers, gw);
        gw.Patches.Should().BeEmpty();
        (await RawViewAsync(store, T)).Should().Be(Newer);
        logger.Starting("Capabilities view of {AgentKey} has a newer schema").Should().ContainSingle();
    }

    // U4: a rotation that keeps the same list sends nothing and leaves the tool bytes alone (prompt cache).
    [Fact]
    public async Task Rotation_WithUnchangedLoaded_NoPatchNoRelist_ProviderBytesIdentical()
    {
        var store = await StoreAsync(T);
        var gw = new FakeSkillGateway();
        using var tiers = new CapabilityTiers(T, store, NullLogger.Instance);
        (await BuildAsync(tiers, Tiers(["skill:alpha:a"], [PinP]), gw, T)).Should().BeTrue();
        var bytes = ProviderBytes(tiers);
        var listCalls = gw.ListCalls;

        (
            await tiers.OnCheckpointActivatedAsync(
                Activation(T, 6, 5, SkillCall(3, "alpha:a"), SkillCall(4, "alpha:p")),
                default
            )
        )
            .Should()
            .BeFalse();

        gw.Patches.Should().ContainSingle("only the seed PATCHed");
        gw.ListCalls.Should().Be(listCalls);
        ProviderBytes(tiers).Should().Be(bytes);
        await AssertViewAsync(store, T, ["alpha:a", "alpha:p"], 1, 6);
    }

    // U6: the cap keeps the most recently used; pinned is never counted against it.
    [Fact]
    public async Task Rotation_OverCap_KeepsMostRecentlyUsed_PinnedNotCounted()
    {
        var store = await StoreAsync(T);
        var gw = new FakeSkillGateway();
        using var tiers = new CapabilityTiers(T, store, NullLogger.Instance);
        (await BuildAsync(tiers, Tiers([], [PinP], cap: 2), gw, T)).Should().BeTrue();

        (
            await tiers.OnCheckpointActivatedAsync(
                Activation(
                    T,
                    6,
                    5,
                    SkillCall(1, "alpha:b"),
                    SkillCall(2, "beta:c"),
                    SkillCall(3, "beta:d"),
                    SkillCall(4, "alpha:p")
                ),
                default
            )
        )
            .Should()
            .BeTrue();

        gw.ListedNames(T).Should().Equal("alpha:p", "beta:c", "beta:d");
        SkillDescription(tiers).Should().Be(FakeSkillGateway.SkillText(["alpha:p", "beta:c", "beta:d"]));
    }

    // U7: a failed PATCH keeps the old contract; the next activation retries even though the view is unchanged.
    [Fact]
    public async Task ApplyFailure_KeepsOldContract_NextActivationRetries()
    {
        var store = await StoreAsync(T);
        var gw = new FakeSkillGateway();
        var logger = new RecordingLogger();
        using var tiers = new CapabilityTiers(T, store, logger);
        (await BuildAsync(tiers, Tiers(["skill:alpha:a"], [PinP]), gw, T)).Should().BeTrue();
        var before = ProviderBytes(tiers);
        gw.FailPatches = 1;

        (await tiers.OnCheckpointActivatedAsync(Activation(T, 7, 6, SkillCall(5, "beta:c")), default))
            .Should()
            .BeFalse();

        ProviderBytes(tiers).Should().Be(before, "the Skill text must not list skills the gateway still hides");
        gw.ListedNames(T).Should().Equal("alpha:a", "alpha:p");
        logger.Starting("Capabilities apply failed").Should().ContainSingle().Which.Level.Should().Be(LogLevel.Warning);
        logger.Starting("Capabilities relisted").Should().BeEmpty();
        // The view is persisted before the PATCH, so the next activation knows what to retry.
        await AssertViewAsync(store, T, ["alpha:p", "beta:c"], 2, 7);

        (await tiers.OnCheckpointActivatedAsync(Activation(T, 11, 10, SkillCall(9, "beta:c")), default))
            .Should()
            .BeTrue();

        gw.Patches.Should().HaveCount(3, "seed, the failed PATCH, the retry");
        gw.ListedNames(T).Should().Equal("alpha:p", "beta:c");
        SkillDescription(tiers).Should().Be(FakeSkillGateway.SkillText(["alpha:p", "beta:c"]));
        await AssertViewAsync(store, T, ["alpha:p", "beta:c"], 2, 11);
    }

    // F-004: a build whose PATCH failed leaves the gateway behind a view whose revision did not move (a newly pinned
    // key joins at the same revision). The next activation re-PATCHes and re-lists, although Loaded is unchanged.
    [Fact]
    public async Task BuildApplyFailed_SameRevision_NextActivationWithSameLoaded_PatchesAndRelists()
    {
        var store = await StoreAsync(T);
        var gw = new FakeSkillGateway();
        using var tiers = new CapabilityTiers(T, store, NullLogger.Instance);
        (await BuildAsync(tiers, Tiers(["skill:alpha:a"], [PinP]), gw, T)).Should().BeTrue();
        gw.FailPatches = 1;
        (await BuildAsync(tiers, Tiers(["skill:alpha:a"], [PinP, "skill:alpha:b"]), gw, T)).Should().BeTrue();
        gw.ListedNames(T).Should().Equal(["alpha:a", "alpha:p"], "guard: the failed PATCH left the old set");
        var patches = gw.Patches.Count;
        var listCalls = gw.ListCalls;

        (await tiers.OnCheckpointActivatedAsync(Activation(T, 6, 5, SkillCall(3, "alpha:a")), default))
            .Should()
            .BeTrue("the gateway and the Skill text are behind the view");

        gw.Patches.Should().HaveCount(patches + 1);
        IncludedNames(gw, gw.Patches[^1].Set).Should().Equal("alpha:a", "alpha:b", "alpha:p");
        gw.ListCalls.Should().Be(listCalls + 1);
        SkillDescription(tiers).Should().Be(FakeSkillGateway.SkillText(["alpha:a", "alpha:b", "alpha:p"]));
    }

    // F-001: a build that never commits (a refused switch, or a sandbox connect that throws) changes nothing live, in
    // either direction. Its Skill attaches to the stage, and only its own flow lists as the staged agent.
    [Fact]
    public async Task StageDisposedUncommitted_LiveTiersUntouched_BothDirections()
    {
        var store = await StoreAsync(T);
        var gw = new FakeSkillGateway();
        var logger = new RecordingLogger();
        using var tiers = new CapabilityTiers(T, store, logger);

        using (var stage = await tiers.StageAsync(Tiers(["skill:alpha:a"], [PinP]), gw.For(T), default))
        {
            stage.IsActive.Should().BeTrue("guard: the staged mode is tiered");
            stage.TryAttachRootSkill(gw.SkillContract(T), SkillHandler).Should().BeTrue();
            stage
                .RootProvider.GetFunctions()
                .Select(f => f.Contract.Name)
                .Should()
                .Equal(CapabilityTiers.SkillToolName, CapabilityTiers.SearchSkillToolName);
            using (stage.EnterBuildFlow())
            {
                tiers.RootGatewayAgentId.Should().Be(T, "the build lists Skill as the agent it stages");
            }
        }

        AssertInert(tiers, gw);
        (await RawViewAsync(store, T)).Should().BeNull("a mode that never committed persists no view");
        logger.Starting("Capabilities active").Should().BeEmpty();

        (await BuildAsync(tiers, Tiers(["skill:alpha:a"], [PinP]), gw, T)).Should().BeTrue();
        var bytes = ProviderBytes(tiers);
        using (var stage = await tiers.StageAsync(null, null, default))
        {
            stage.IsActive.Should().BeFalse("guard: the staged mode is untiered");
            using (stage.EnterBuildFlow())
            {
                tiers.RootGatewayAgentId.Should().BeNull("the untiered build lists as the default agent");
            }

            stage.RootProvider.GetFunctions().Should().BeEmpty();
        }

        tiers.IsActive.Should().BeTrue();
        tiers.RootGatewayAgentId.Should().Be(T);
        ProviderBytes(tiers).Should().Be(bytes, "the live root keeps its Skill and SearchSkill");
        logger.Starting("Capabilities inert").Should().BeEmpty();
    }

    // F-016: a root rotation that lands between a build's stage and its commit (a compaction that finished while a
    // switch was building) is not undone by the commit. The next window starts after that rotation's checkpoint, the
    // persisted revision never goes down, and the build's newly pinned key is still listed.
    [Fact]
    public async Task RootRotationBetweenStageAndCommit_KeepsTheRotatedWindowAndRevision_AddsTheNewPin()
    {
        var store = await StoreAsync(T);
        var gw = new FakeSkillGateway();
        var logger = new RecordingLogger();
        using var tiers = new CapabilityTiers(T, store, logger);
        (await BuildAsync(tiers, Tiers([], [PinP]), gw, T)).Should().BeTrue();

        using var stage = await tiers.StageAsync(Tiers([], [PinP, "skill:alpha:b"]), gw.For(T), default);
        stage.IsActive.Should().BeTrue("guard: the staged mode is tiered");
        stage.TryAttachRootSkill(gw.SkillContract(T), SkillHandler).Should().BeTrue();
        (await tiers.OnCheckpointActivatedAsync(Activation(T, 10, 9, SkillCall(6, "beta:c")), default))
            .Should()
            .BeTrue();
        await AssertViewAsync(store, T, ["alpha:p", "beta:c"], 2, 10);

        await stage.CommitAsync(default);
        // The thread's rows still hold the call at 6, which the first rotation already counted.
        (await tiers.OnCheckpointActivatedAsync(Activation(T, 20, 19, SkillCall(6, "beta:c")), default))
            .Should()
            .BeTrue();

        var second = logger.Starting("Capabilities rotated").Should().HaveCount(2).And.Subject.Last();
        ((IEnumerable<string>)second.Props["Used"]!).Should().BeEmpty("the window starts after checkpoint 10");
        var persisted = await ViewAsync(store, T);
        persisted!.Revision.Should().BeGreaterThan(2, "the persisted revision never goes down, and the view changed");
        persisted.RotatedThroughSeq.Should().Be(20);
        gw.ListedNames(T).Should().Equal(["alpha:b", "alpha:p"], "the stage's new pinned key is listed");
    }

    // R8, R8b: a build whose tiers did not fully take carries a plain warning for the switch that asked for it: a
    // tiered setup that went inert says why (it runs untiered), and a failed PATCH says the gateway retries at the next
    // rotation. An untiered build and an applied one carry none.
    [Fact]
    public async Task StageSetupWarning_OnlyWhenTheTieredSetupDidNotTake()
    {
        var gw = new FakeSkillGateway();
        using var tiers = new CapabilityTiers(T, await StoreAsync(T), NullLogger.Instance)
        {
            ConfigureBudget = TimeSpan.FromMilliseconds(50),
        };
        var settings = Tiers(["skill:alpha:a"], [PinP]);

        using (var untiered = await tiers.StageAsync(null, null, default))
        {
            untiered.SetupWarning.Should().BeNull();
        }

        using (var applied = await tiers.StageAsync(settings, gw.For(T), default))
        {
            applied.SetupWarning.Should().BeNull();
        }

        gw.FailPatches = 1;
        using (var patchFailed = await tiers.StageAsync(settings, gw.For(T), default))
        {
            patchFailed.IsActive.Should().BeTrue("guard: a failed PATCH leaves the build tiered");
            patchFailed
                .SetupWarning.Should()
                .Be("Skill tiers could not be applied to the sandbox; they will be retried at the next compaction.");
        }

        var hanging = gw.For(T) with { PatchTiers = (_, _, ct) => Task.Delay(Timeout.Infinite, ct) };
        using (var timedOut = await tiers.StageAsync(settings, hanging, default).WaitAsync(TimeSpan.FromSeconds(5)))
        {
            timedOut.IsActive.Should().BeFalse("guard: past the budget the build is inert");
            timedOut.SetupWarning.Should().Be(NotSetUp("setup timed out"));
        }

        var catalogDown = gw.For(T) with
        {
            GetCatalog = _ => throw new HttpRequestException("catalog down at 10.0.0.7"),
        };
        using (var noCatalog = await tiers.StageAsync(settings, catalogDown, default))
        {
            noCatalog.IsActive.Should().BeFalse("guard: without a catalog the build is inert");
            noCatalog.SetupWarning.Should().Be(NotSetUp("the sandbox catalog was unavailable"));
        }

        var newerStore = new InMemoryConversationStore();
        await newerStore.SaveMetadataAsync(
            T,
            new ThreadMetadata
            {
                ThreadId = T,
                LastUpdated = 1,
                Properties = ImmutableDictionary<string, object>.Empty.Add(
                    CapabilityTiers.ViewPropertyKey,
                    """{"schemaVersion":2,"loaded":["skill:alpha:a"],"revision":7,"rotatedThroughSeq":40}"""
                ),
            }
        );
        using (var newerTiers = new CapabilityTiers(T, newerStore, NullLogger.Instance))
        using (var newer = await newerTiers.StageAsync(settings, gw.For(T), default))
        {
            newer.IsActive.Should().BeFalse("guard: a newer saved view leaves the build inert");
            newer.SetupWarning.Should().Be(NotSetUp("the saved tiers come from a newer version"));
        }

        using var badIdTiers = new CapabilityTiers("a/b", await StoreAsync("a/b"), NullLogger.Instance);
        using var badId = await badIdTiers.StageAsync(settings, gw.For("a/b"), default);
        badId.IsActive.Should().BeFalse("guard: an invalid thread id leaves the build inert");
        badId.SetupWarning.Should().Be(NotSetUp("the thread id is not a valid agent id"));

        static string NotSetUp(string cause) =>
            $"Skill tiers could not be set up for this mode ({cause}); this conversation runs without tiers until its next mode switch.";
    }

    // R3: one budget bounds a build's tiers. A gateway that hangs leaves the build inert with one WARN naming the
    // thread, and nothing persisted.
    [Fact]
    public async Task Configure_GatewayHangsPastTheBudget_InertWithOneWarn_NoView()
    {
        var store = await StoreAsync(T);
        var gw = new FakeSkillGateway();
        var logger = new RecordingLogger();
        using var tiers = new CapabilityTiers(T, store, logger) { ConfigureBudget = TimeSpan.FromMilliseconds(50) };
        var hanging = gw.For(T) with { PatchTiers = (_, _, ct) => Task.Delay(Timeout.Infinite, ct) };

        Func<Task> configure = () =>
            tiers.ConfigureAsync(Tiers(["skill:alpha:a"], [PinP]), hanging, CancellationToken.None);

        // The 50 ms budget ends the build; without it the PATCH never returns and this bound fails the test.
        await configure.Should().CompleteWithinAsync(TimeSpan.FromSeconds(5));
        AssertInert(tiers, gw);
        var failed = logger.Starting("Capabilities apply failed").Should().ContainSingle().Subject;
        failed.Level.Should().Be(LogLevel.Warning);
        failed.Props["ThreadId"].Should().Be(T);
        (await RawViewAsync(store, T)).Should().BeNull("a build that timed out persists no view");
    }

    // MT 1b: a failure row the operator cannot tie to a conversation hides behind any per-thread log filter.
    [Fact]
    public async Task ApplyFailed_OnEveryPath_CarriesTheThreadId()
    {
        // Configure: the catalog cannot be read.
        var gw = new FakeSkillGateway();
        var catalogDown = new RecordingLogger();
        using (var tiers = new CapabilityTiers(T, await StoreAsync(T), catalogDown))
        {
            await tiers.ConfigureAsync(
                Tiers([], [PinP]),
                gw.For(T) with
                {
                    GetCatalog = _ => throw new HttpRequestException("catalog down"),
                },
                CancellationToken.None
            );
        }

        // Rotation: the PATCH fails.
        var patchDown = new RecordingLogger();
        using (var tiers = new CapabilityTiers(T, await StoreAsync(T), patchDown))
        {
            (await BuildAsync(tiers, Tiers([], [PinP]), gw, T)).Should().BeTrue();
            gw.FailPatches = 1;
            (await tiers.OnCheckpointActivatedAsync(Activation(T, 5, 4, SkillCall(3, "beta:c")), default))
                .Should()
                .BeFalse();
        }

        // Rotation: the PATCH lands but the re-list finds no Skill tool.
        var listDown = new RecordingLogger();
        using (var tiers = new CapabilityTiers(T, await StoreAsync(T), listDown))
        {
            await tiers.ConfigureAsync(
                Tiers([], [PinP]),
                gw.For(T) with
                {
                    ListSkillContract = _ => Task.FromResult<FunctionContract?>(null),
                },
                CancellationToken.None
            );
            tiers.TryAttachRootSkill(gw.SkillContract(T), SkillHandler).Should().BeTrue();
            (await tiers.OnCheckpointActivatedAsync(Activation(T, 5, 4, SkillCall(3, "beta:c")), default))
                .Should()
                .BeFalse();
        }

        foreach (var logger in new[] { catalogDown, patchDown, listDown })
        {
            var failed = logger.Starting("Capabilities apply failed").Should().ContainSingle().Subject;
            failed.Level.Should().Be(LogLevel.Warning);
            failed.Props["ThreadId"].Should().Be(T);
            failed.Props["AgentKey"].Should().Be("root");
        }
    }

    // MT 1c D-1c-1: a sub-agent whose own setup failed holds the PARENT's Skill tool, so its compaction must not
    // rotate its view or PATCH its gateway id. The view seeded before the failed PATCH is kept.
    [Fact]
    public async Task SubAgentSetupFailed_ItsCompaction_DoesNotRotateOrPatch()
    {
        var child = SubAgentThreadIds.For(T, "agent-1");
        var store = await StoreAsync(T);
        var gw = new FakeSkillGateway();
        var logger = new RecordingLogger();
        using var tiers = new CapabilityTiers(T, store, logger);
        (await BuildAsync(tiers, SubAgentTiers, gw, T)).Should().BeTrue();
        gw.FailPatches = 1;

        (await SpawnAsync(tiers, gw, "agent-1")).Should().AllSatisfy(p => p.Should().BeNull());

        var patches = gw.Patches.Count;
        await AssertViewAsync(store, child, ["alpha:p", "beta:c"], 1, 0);
        var seeded = await RawViewAsync(store, child);

        (await tiers.OnCheckpointActivatedAsync(Activation(child, 9, 8, SkillCall(3, "beta:d")), default))
            .Should()
            .BeFalse();

        gw.Patches.Should().HaveCount(patches, "the sub-agent still serves the parent's Skill tool");
        (await RawViewAsync(store, child)).Should().Be(seeded);
        logger.Starting("Capabilities rotated").Should().BeEmpty();
    }

    // MT 1c D-1c-1: a later build does not make the failed sub-agent's tools its own; only a spawn that installs
    // them (a reconstruct) does. The root rotates as before throughout.
    [Fact]
    public async Task SubAgentSetupFailed_LaterBuild_StillNotRotated_UntilASpawnInstallsItsOwnTools()
    {
        var child = SubAgentThreadIds.For(T, "agent-1");
        var store = await StoreAsync(T);
        var gw = new FakeSkillGateway();
        using var tiers = new CapabilityTiers(T, store, NullLogger.Instance);
        (await BuildAsync(tiers, SubAgentTiers, gw, T)).Should().BeTrue();
        gw.FailPatches = 1;
        (await SpawnAsync(tiers, gw, "agent-1")).Should().AllSatisfy(p => p.Should().BeNull());

        (await BuildAsync(tiers, SubAgentTiers, gw, T)).Should().BeTrue();
        var patches = gw.Patches.Count;
        var seeded = await RawViewAsync(store, child);

        (await tiers.OnCheckpointActivatedAsync(Activation(child, 9, 8, SkillCall(3, "beta:d")), default))
            .Should()
            .BeFalse();

        gw.Patches.Should().HaveCount(patches);
        (await RawViewAsync(store, child)).Should().Be(seeded);
        (await tiers.OnCheckpointActivatedAsync(Activation(T, 9, 8, SkillCall(3, "beta:d")), default))
            .Should()
            .BeTrue("the root's rotation does not depend on any sub-agent");

        (await SpawnAsync(tiers, gw, "agent-1")).Should().AllSatisfy(p => p.Should().NotBeNull());
        (await tiers.OnCheckpointActivatedAsync(Activation(child, 13, 12, SkillCall(11, "beta:d")), default))
            .Should()
            .BeTrue();

        gw.Patches.Last().AgentId.Should().Be(SandboxSubAgentIdentity.ForSubAgent(T, "agent-1"));
        await AssertViewAsync(store, child, ["alpha:p", "beta:d"], 2, 13);
    }

    // 1c: only a failure falls back to the parent's tool. The caller's own cancellation propagates, logs no WARN
    // and does not mark the build failed, so the next spawn still sets the sub-agent up.
    [Fact]
    public async Task SubAgentSpawn_CallerCancelled_Propagates_NoWarn_NextSpawnSetsUp()
    {
        var store = await StoreAsync(T);
        var gw = new FakeSkillGateway();
        var logger = new RecordingLogger();
        using var tiers = new CapabilityTiers(T, store, logger);
        (await BuildAsync(tiers, SubAgentTiers, gw, T)).Should().BeTrue();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await FluentActions
            .Awaiting(() =>
                tiers
                    .ForInheritedToolAsync(
                        new InheritedTool("agent-1", gw.SkillContract(T), SkillHandler),
                        cancelled.Token
                    )
                    .AsTask()
            )
            .Should()
            .ThrowAsync<OperationCanceledException>();

        logger.Starting("Capabilities apply failed").Should().BeEmpty();
        (await SpawnAsync(tiers, gw, "agent-1")).Should().AllSatisfy(p => p.Should().NotBeNull());
    }

    // 1c: a tiered sub-agent's two providers each serve exactly the tool they replace, so its registry holds one
    // Skill and one SearchSkill (a duplicate name would fail the child's registry build).
    [Fact]
    public async Task SubAgentProviders_EachServeExactlyTheToolTheyReplace()
    {
        var store = await StoreAsync(T);
        var gw = new FakeSkillGateway();
        using var tiers = new CapabilityTiers(T, store, NullLogger.Instance);
        (await BuildAsync(tiers, SubAgentTiers, gw, T)).Should().BeTrue();

        var providers = await SpawnAsync(tiers, gw, "agent-1");

        providers
            .Select(p => string.Join(',', p!.GetFunctions().Select(f => f.Contract.Name)))
            .Should()
            .Equal(SkillTool, CapabilityTiers.SearchSkillToolName);
    }

    // 1c: a sub-agent's compaction moves only its own view, revision and gateway tiers; the root's compaction
    // moves only the root's.
    [Fact]
    public async Task SubAgentAndRootRotations_EachLeaveTheOthersViewRevisionAndToolsUnchanged()
    {
        var child = SubAgentThreadIds.For(T, "agent-1");
        var childId = SandboxSubAgentIdentity.ForSubAgent(T, "agent-1");
        var store = await StoreAsync(T);
        var gw = new FakeSkillGateway();
        using var tiers = new CapabilityTiers(T, store, NullLogger.Instance);
        (await BuildAsync(tiers, SubAgentTiers, gw, T)).Should().BeTrue();
        var childTools = await SpawnAsync(tiers, gw, "agent-1");
        childTools.Should().AllSatisfy(p => p.Should().NotBeNull());
        var rootView = await RawViewAsync(store, T);
        var rootBytes = ProviderBytes(tiers);

        // The child's own Skill text and SearchSkill marks: its view, not the root's (alpha:a, alpha:p).
        ChildSkillDescription(childTools).Should().Be(FakeSkillGateway.SkillText(["alpha:p", "beta:c"]));
        var marks = await ChildSearchMarksAsync(childTools);
        marks["beta:c"].Should().Be("loaded");
        marks["alpha:a"].Should().Be("available");

        (await tiers.OnCheckpointActivatedAsync(Activation(child, 9, 8, SkillCall(3, "beta:d")), default))
            .Should()
            .BeTrue();

        gw.Patches.Last().AgentId.Should().Be(childId);
        gw.ListedNames(childId).Should().Equal("alpha:p", "beta:d");
        ChildSkillDescription(childTools).Should().Be(FakeSkillGateway.SkillText(["alpha:p", "beta:d"]));
        await AssertViewAsync(store, child, ["alpha:p", "beta:d"], 2, 9);
        (await RawViewAsync(store, T)).Should().Be(rootView);
        gw.ListedNames(T).Should().Equal("alpha:a", "alpha:p");
        ProviderBytes(tiers).Should().Be(rootBytes);

        var childView = await RawViewAsync(store, child);
        (await tiers.OnCheckpointActivatedAsync(Activation(T, 9, 8, SkillCall(3, "beta:c")), default))
            .Should()
            .BeTrue();

        gw.Patches.Last().AgentId.Should().Be(T);
        await AssertViewAsync(store, T, ["alpha:p", "beta:c"], 2, 9);
        (await RawViewAsync(store, child)).Should().Be(childView);
        gw.ListedNames(childId).Should().Equal("alpha:p", "beta:d");
    }

    // F8: each agent has its own gate. A root rotation held inside its PATCH does not hold up a sub-agent's
    // spawn, and a second root rotation waits for the first instead of interleaving with it.
    [Fact]
    public async Task RootRotationHeldInPatch_SubAgentSpawnCompletes_SecondRootRotationWaits()
    {
        var childId = SandboxSubAgentIdentity.ForSubAgent(T, "agent-1");
        var store = await StoreAsync(T);
        var gw = new FakeSkillGateway();
        var inner = gw.For(T);
        var calls = new ConcurrentQueue<string>();
        var rootHold = Task.CompletedTask;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateway = inner with
        {
            PatchTiers = async (id, set, ct) =>
            {
                calls.Enqueue($"patch {id}");
                if (id == T)
                {
                    await rootHold.WaitAsync(ct);
                }

                await inner.PatchTiers(id, set, ct);
                calls.Enqueue($"done {id}");
            },
        };
        using var tiers = new CapabilityTiers(T, store, NullLogger.Instance);
        await tiers.ConfigureAsync(SubAgentTiers, gateway, CancellationToken.None);
        tiers.TryAttachRootSkill(gw.SkillContract(T), SkillHandler).Should().BeTrue();
        rootHold = release.Task;

        // The in-memory store completes at once, so each call below runs until it waits on a gate or the hold.
        var first = tiers.OnCheckpointActivatedAsync(Activation(T, 9, 8, SkillCall(3, "beta:c")), default).AsTask();
        var spawned = await SpawnAsync(tiers, gw, "agent-1").WaitAsync(TimeSpan.FromSeconds(5));
        var second = tiers.OnCheckpointActivatedAsync(Activation(T, 13, 12, SkillCall(11, "beta:d")), default).AsTask();

        spawned.Should().AllSatisfy(p => p.Should().NotBeNull(), "the root's gate does not hold a sub-agent");
        first.IsCompleted.Should().BeFalse("its PATCH is held");
        second.IsCompleted.Should().BeFalse("it waits for the root's gate");
        calls.Should().Equal($"patch {T}", $"done {T}", $"patch {T}", $"patch {childId}", $"done {childId}");

        release.SetResult();
        (await first.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();
        (await second.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();

        calls
            .Where(c => c.EndsWith(T, StringComparison.Ordinal))
            .Should()
            .Equal($"patch {T}", $"done {T}", $"patch {T}", $"done {T}", $"patch {T}", $"done {T}");
        gw.ListedNames(T).Should().Equal("alpha:p", "beta:d");
        SkillDescription(tiers).Should().Be(FakeSkillGateway.SkillText(["alpha:p", "beta:d"]));
        await AssertViewAsync(store, T, ["alpha:p", "beta:d"], 3, 13);
    }

    // M7: after a host restart (a new instance over the same store) a respawn loads the sub-agent's saved view
    // at its revision and PATCHes that set: no seed write, no revision bump.
    [Fact]
    public async Task SubAgentRespawn_AfterRestart_LoadsTheSavedView_NoSeedNoBump()
    {
        var child = SubAgentThreadIds.For(T, "agent-1");
        var childId = SandboxSubAgentIdentity.ForSubAgent(T, "agent-1");
        var store = await StoreAsync(T);
        using (var tiers = new CapabilityTiers(T, store, NullLogger.Instance))
        {
            var gw = new FakeSkillGateway();
            (await BuildAsync(tiers, SubAgentTiers, gw, T)).Should().BeTrue();
            (await SpawnAsync(tiers, gw, "agent-1")).Should().AllSatisfy(p => p.Should().NotBeNull());
            (await tiers.OnCheckpointActivatedAsync(Activation(child, 9, 8, SkillCall(3, "beta:d")), default))
                .Should()
                .BeTrue();
        }

        await AssertViewAsync(store, child, ["alpha:p", "beta:d"], 2, 9);
        var saved = await RawViewAsync(store, child);
        var restarted = new FakeSkillGateway();
        var logger = new RecordingLogger();
        using var again = new CapabilityTiers(T, store, logger);
        (await BuildAsync(again, SubAgentTiers, restarted, T)).Should().BeTrue();

        (await SpawnAsync(again, restarted, "agent-1")).Should().AllSatisfy(p => p.Should().NotBeNull());

        var patch = restarted.Patches.Should().HaveCount(2).And.Subject.Last();
        patch.AgentId.Should().Be(childId);
        IncludedNames(restarted, patch.Set).Should().Equal("alpha:p", "beta:d");
        (await RawViewAsync(store, child)).Should().Be(saved);
        logger
            .Starting("Capabilities relisted")
            .Should()
            .ContainSingle(r => Equals(r.Props["AgentKey"], "agent-1"))
            .Which.Props["Revision"]
            .Should()
            .Be(2);
    }

    // D-1c-1 marker: a respawn while the tiers are inert hands the sub-agent the parent's Skill, so a compaction of
    // it after the tiers come back must not rotate it, although an earlier spawn had installed its own tools.
    [Fact]
    public async Task SubAgentRespawnedWhileInert_LaterCompaction_DoesNotRotate()
    {
        var child = SubAgentThreadIds.For(T, "agent-1");
        var store = await StoreAsync(T);
        var gw = new FakeSkillGateway();
        using var tiers = new CapabilityTiers(T, store, NullLogger.Instance);
        (await BuildAsync(tiers, SubAgentTiers, gw, T)).Should().BeTrue();
        (await SpawnAsync(tiers, gw, "agent-1")).Should().AllSatisfy(p => p.Should().NotBeNull());

        await tiers.ConfigureAsync(null, gw.For(T), CancellationToken.None);
        // An inert parent has no SearchSkill, so the spawn offers only Skill.
        (await tiers.ForInheritedToolAsync(new InheritedTool("agent-1", gw.SkillContract(T), SkillHandler), default))
            .Should()
            .BeNull();
        (await BuildAsync(tiers, SubAgentTiers, gw, T)).Should().BeTrue();
        var patches = gw.Patches.Count;
        var view = await RawViewAsync(store, child);

        (await tiers.OnCheckpointActivatedAsync(Activation(child, 9, 8, SkillCall(3, "beta:d")), default))
            .Should()
            .BeFalse();

        gw.Patches.Should().HaveCount(patches);
        (await RawViewAsync(store, child)).Should().Be(view);
    }

    // D-1c-1 marker: a respawn whose setup fails in the same build hands the sub-agent the parent's tools again,
    // so its next compaction does not rotate it, although its first spawn had installed its own.
    [Fact]
    public async Task SubAgentRespawnSetupFailed_SameBuild_LaterCompaction_DoesNotRotate()
    {
        var child = SubAgentThreadIds.For(T, "agent-1");
        var store = await StoreAsync(T);
        var gw = new FakeSkillGateway();
        using var tiers = new CapabilityTiers(T, store, NullLogger.Instance);
        (await BuildAsync(tiers, SubAgentTiers, gw, T)).Should().BeTrue();
        (await SpawnAsync(tiers, gw, "agent-1")).Should().AllSatisfy(p => p.Should().NotBeNull());
        // A rotation whose PATCH fails leaves the applied revision behind the view, so the respawn must apply.
        gw.FailPatches = 1;
        (await tiers.OnCheckpointActivatedAsync(Activation(child, 9, 8, SkillCall(3, "beta:d")), default))
            .Should()
            .BeFalse();

        gw.FailPatches = 1;
        (await SpawnAsync(tiers, gw, "agent-1")).Should().AllSatisfy(p => p.Should().BeNull());
        var patches = gw.Patches.Count;
        var view = await RawViewAsync(store, child);

        (await tiers.OnCheckpointActivatedAsync(Activation(child, 13, 12, SkillCall(11, "beta:c")), default))
            .Should()
            .BeFalse();

        gw.Patches.Should().HaveCount(patches);
        (await RawViewAsync(store, child)).Should().Be(view);
    }

    // U8: only a Skill call that names exactly one skill is a use; junk never breaks the rotation.
    [Fact]
    public async Task UsageMapping_IgnoresAmbiguousUnknownAndMalformedArgs()
    {
        var store = await StoreAsync(T);
        var gw = new FakeSkillGateway();
        var logger = new RecordingLogger();
        using var tiers = new CapabilityTiers(T, store, logger);
        (await BuildAsync(tiers, Tiers([], [PinP]), gw, T)).Should().BeTrue();

        (
            await tiers.OnCheckpointActivatedAsync(
                Activation(
                    T,
                    14,
                    13,
                    SkillCall(1, FakeSkillGateway.PathOf("alpha", "b")), // path
                    SkillCall(2, "beta:c"), // full gateway name
                    SkillCall(3, "a"), // trailing segment only one skill has
                    SkillCall(4, "dup"), // two skills: ambiguous
                    SkillCall(5, "nope"),
                    SkillCall(6, "  "),
                    (7, SkillTool, "{not json"),
                    (8, SkillTool, """{"skill":42}"""),
                    (9, SkillTool, """["beta:d"]"""),
                    (10, SkillTool, null),
                    (11, "Read", """{"skill":"beta:d"}"""),
                    (12, CapabilityTiers.SearchSkillToolName, """{"query":"d","skill":"beta:d"}""")
                ),
                default
            )
        )
            .Should()
            .BeTrue();

        gw.ListedNames(T).Should().Equal("alpha:a", "alpha:b", "alpha:p", "beta:c");
        var rotated = logger.Starting("Capabilities rotated").Should().ContainSingle().Subject;
        ((IEnumerable<string>)rotated.Props["Used"]!).Should().Equal(Keys(["alpha:a", "alpha:b", "beta:c"]));
        logger.Starting("Capabilities apply failed").Should().BeEmpty();
    }

    // U12 (F1): the usage window is (rotatedThroughSeq, checkpoint], owned by the host's persisted watermark.
    [Fact]
    public async Task UsageWindow_IsTheHostWatermark()
    {
        var store = await StoreAsync(T);
        var gw = new FakeSkillGateway();
        var logger = new RecordingLogger();
        using var tiers = new CapabilityTiers(T, store, logger);
        (await BuildAsync(tiers, Tiers([], [PinP]), gw, T)).Should().BeTrue();

        (await tiers.OnCheckpointActivatedAsync(Activation(T, 5, 4, SkillCall(3, "beta:c")), default))
            .Should()
            .BeTrue();
        var afterFirst = await RawViewAsync(store, T);
        (await ViewAsync(store, T))!.RotatedThroughSeq.Should().Be(5);

        // No row after the watermark (5): skipped, so the promoted set and the watermark stay.
        (await tiers.OnCheckpointActivatedAsync(Activation(T, 6, 5, SkillCall(3, "beta:d")), default))
            .Should()
            .BeFalse();

        (await RawViewAsync(store, T)).Should().Be(afterFirst);
        gw.Patches.Should().HaveCount(2);
        var skipped = logger.Starting("Capabilities rotation skipped").Should().ContainSingle().Subject;
        skipped.Props["CheckpointSeq"].Should().Be(6L);
        skipped.Props["RotatedThroughSeq"].Should().Be(5L);

        // A call at or below the watermark was counted by an earlier rotation and is not counted again.
        (
            await tiers.OnCheckpointActivatedAsync(
                Activation(T, 11, 10, SkillCall(3, "beta:c"), SkillCall(7, "beta:d"), SkillCall(9, "alpha:b")),
                default
            )
        )
            .Should()
            .BeTrue();

        gw.ListedNames(T).Should().Equal("alpha:b", "alpha:p", "beta:d");
        await AssertViewAsync(store, T, ["alpha:b", "alpha:p", "beta:d"], 3, 11);
    }

    private static TierSettings Tiers(string[] loaded, string[] pinned, int cap = 10) =>
        new(loaded, pinned, [], [], cap);

    // Root: alpha:a + pinned alpha:p. Each sub-agent: beta:c + pinned alpha:p.
    private static TierSettings SubAgentTiers => new(["skill:alpha:a"], [PinP], ["skill:beta:c"], [PinP]);

    /// <summary>
    /// A spawn as the library drives it: the override for each inherited tool, the parent's Skill then its
    /// SearchSkill. Null = the sub-agent inherits the parent's tool.
    /// </summary>
    private static async Task<IFunctionProvider?[]> SpawnAsync(
        CapabilityTiers tiers,
        FakeSkillGateway gw,
        string agentId
    )
    {
        var search = tiers
            .RootProvider.GetFunctions()
            .Single(f => f.Contract.Name == CapabilityTiers.SearchSkillToolName);
        return
        [
            await tiers.ForInheritedToolAsync(new InheritedTool(agentId, gw.SkillContract(T), SkillHandler), default),
            await tiers.ForInheritedToolAsync(new InheritedTool(agentId, search.Contract, search.Handler), default),
        ];
    }

    /// <summary>The description of the <c>Skill</c> a spawned sub-agent is served (its first provider).</summary>
    private static string? ChildSkillDescription(IFunctionProvider?[] childTools) =>
        childTools[0]!.GetFunctions().Single(f => f.Contract.Name == SkillTool).Contract.Description;

    /// <summary>Each skill's mark (<c>loaded</c> or <c>available</c>) in one search from the sub-agent's SearchSkill.</summary>
    private static async Task<IReadOnlyDictionary<string, string>> ChildSearchMarksAsync(
        IFunctionProvider?[] childTools
    )
    {
        var search = childTools[1]!.GetFunctions().Single(f => f.Contract.Name == CapabilityTiers.SearchSkillToolName);
        var result = await search.Handler(
            """{"query":"alpha beta","limit":20}""",
            new ToolCallContext(),
            CancellationToken.None
        );
        var text = ((ToolHandlerResult.Resolved)result).Payload.Text;
        // Hit rows read "1. beta:c (plugin: beta) [loaded]".
        return text.Split('\n')
            .Select(line => Regex.Match(line, @"^\d+\. (\S+) .*\[(loaded|available)\]$"))
            .Where(m => m.Success)
            .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value, StringComparer.Ordinal);
    }

    private static async Task<InMemoryConversationStore> StoreAsync(params string[] threads)
    {
        var store = new InMemoryConversationStore();
        foreach (var thread in threads)
        {
            await store.SaveMetadataAsync(thread, new ThreadMetadata { ThreadId = thread, LastUpdated = 1 });
        }

        return store;
    }

    /// <summary>One host build: configure, then list <c>Skill</c> as the root and attach it.</summary>
    private static async Task<bool> BuildAsync(
        CapabilityTiers tiers,
        TierSettings settings,
        FakeSkillGateway gw,
        string agentId
    )
    {
        var gateway = gw.For(agentId);
        await tiers.ConfigureAsync(settings, gateway, CancellationToken.None);
        if (!tiers.IsActive)
        {
            return false;
        }

        var skill = await gateway.ListSkillContract(CancellationToken.None);
        return tiers.TryAttachRootSkill(skill!, SkillHandler);
    }

    private static void AssertInert(CapabilityTiers tiers, FakeSkillGateway gw)
    {
        tiers.IsActive.Should().BeFalse();
        tiers.RootGatewayAgentId.Should().BeNull("the root sends no agent id");
        tiers.RootProvider.GetFunctions().Should().BeEmpty("the host's own Skill registration stays in charge");
        tiers.TryAttachRootSkill(gw.SkillContract(T), SkillHandler).Should().BeFalse();
    }

    private static CheckpointActivation Activation(
        string thread,
        long checkpointSeq,
        long lastMessageSeq,
        params (long Seq, string Tool, string? Args)[] calls
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

    private static (long Seq, string Tool, string? Args) SkillCall(long seq, string reference) =>
        (seq, CapabilityTiers.SkillToolName, JsonSerializer.Serialize(new { skill = reference }));

    private static string[] Keys(string[] names) =>
        [
            .. names
                .Select(n => n.Split(':', 2))
                .Select(parts => FakeSkillGateway.KeyOf(parts[0], parts[1]))
                .Order(StringComparer.Ordinal),
        ];

    private static IReadOnlyList<string> IncludedNames(
        FakeSkillGateway gw,
        IReadOnlyDictionary<string, SandboxSkillTier> set
    ) =>
        [
            .. gw
                .Skills.Where(s =>
                    set.GetValueOrDefault(s.Path, SandboxSkillTier.Available) == SandboxSkillTier.Included
                )
                .Select(s => s.Name)
                .Order(StringComparer.Ordinal),
        ];

    private static IReadOnlyList<string> FunctionNames(CapabilityTiers tiers) =>
        [.. tiers.RootProvider.GetFunctions().Select(f => f.Contract.Name)];

    private static string? SkillDescription(CapabilityTiers tiers) =>
        tiers
            .RootProvider.GetFunctions()
            .Single(f => f.Contract.Name == CapabilityTiers.SkillToolName)
            .Contract.Description;

    /// <summary>What the model is sent for the provider's tools: name, description and parameter names, in order.</summary>
    private static string ProviderBytes(CapabilityTiers tiers) =>
        string.Join(
            '\n',
            tiers
                .RootProvider.GetFunctions()
                .Select(f =>
                    $"{f.Contract.Name}|{f.Contract.Description}|{string.Join(',', (f.Contract.Parameters ?? []).Select(p => p.Name))}"
                )
        );

    private static async Task<string?> RawViewAsync(IConversationStore store, string thread)
    {
        var metadata = await store.LoadMetadataAsync(thread);
        // An in-memory store hands back the string written; SQLite a JsonElement holding it.
        return metadata?.Properties?.TryGetValue(CapabilityTiers.ViewPropertyKey, out var raw) == true
            ? raw is JsonElement element
                ? element.GetString()
                : (string)raw
            : null;
    }

    /// <summary>The production conversation store on a temp file, deleted on dispose.</summary>
    private sealed class TempSqliteStore : IAsyncDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), $"tiers-{Guid.NewGuid():N}.db");

        public TempSqliteStore() => Store = new SqliteConversationStore(_path);

        public SqliteConversationStore Store { get; }

        public async ValueTask DisposeAsync()
        {
            await Store.DisposeAsync();
            SqliteConnection.ClearAllPools(); // releases the pooled file handles so the file can go
            foreach (var file in new[] { _path, _path + "-wal", _path + "-shm" })
            {
                try
                {
                    File.Delete(file);
                }
                catch (IOException) { }
            }
        }
    }

    private static async Task<ViewJson?> ViewAsync(IConversationStore store, string thread) =>
        await RawViewAsync(store, thread) is { } json ? JsonSerializer.Deserialize<ViewJson>(json) : null;

    private static async Task AssertViewAsync(
        IConversationStore store,
        string thread,
        string[] names,
        int revision,
        long rotatedThroughSeq
    )
    {
        var view = await ViewAsync(store, thread);
        view.Should().NotBeNull("the view is persisted in the thread metadata");
        view!.SchemaVersion.Should().Be(1);
        view.Loaded.Should().Equal(Keys(names));
        view.Revision.Should().Be(revision);
        view.RotatedThroughSeq.Should().Be(rotatedThroughSeq);
    }

    private sealed record ViewJson(
        [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
        [property: JsonPropertyName("loaded")] string[] Loaded,
        [property: JsonPropertyName("revision")] int Revision,
        [property: JsonPropertyName("rotatedThroughSeq")] long RotatedThroughSeq
    );
}
