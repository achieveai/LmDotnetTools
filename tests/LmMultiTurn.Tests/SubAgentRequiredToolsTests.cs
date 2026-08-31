using System.Runtime.CompilerServices;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.ClientTools;
using AchieveAi.LmDotnetTools.LmMultiTurn.Collaboration;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using AchieveAi.LmDotnetTools.LmTestUtils;
using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace LmMultiTurn.Tests;

/// <summary>
/// Pins for #623: the mode-level required-tools union (<see cref="SubAgentOptions.RequiredToolNames"/>)
/// and the warning floor for board-referencing dispatches to templates whose restricted <c>tools:</c>
/// lists stripped the task tools. Each behavioural pin goes through the REAL spawn path, because the
/// defect was precisely that the per-template filter site silently dropped tools the dispatch prompt
/// ordered the agent to use.
/// </summary>
public sealed class SubAgentRequiredToolsTests : IAsyncLifetime
{
    private const string TaskTool = "claim-task";
    private const string SecondTaskTool = "list-tasks";
    private const string DomainTool = "get_weather";

    private readonly Mock<IMultiTurnAgent> _parentMock = new();
    private readonly List<SubAgentManager> _managers = [];

    public Task InitializeAsync()
    {
        _ = _parentMock
            .Setup(p =>
                p.SendAsync(
                    It.IsAny<List<IMessage>>(),
                    It.IsAny<string?>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(new SendReceipt("receipt-1", null, DateTimeOffset.UtcNow));
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        foreach (var manager in _managers)
        {
            await Wait.ForTeardownAsync(manager, "a sub-agent manager created by this test");
        }
    }

    #region BuildEnabledToolSet unit pins

    [Fact]
    public void BuildEnabledToolSet_UnionsRequiredToolsAfterTemplateFilter()
    {
        var result = SubAgentManager.BuildEnabledToolSet(
            templateEnabledTools: [DomainTool],
            addTools: null,
            removeTools: null,
            requiredTools: [TaskTool, SecondTaskTool]
        );

        result.Should().BeEquivalentTo([DomainTool, TaskTool, SecondTaskTool]);
    }

    [Fact]
    public void BuildEnabledToolSet_RequiredToolsWin_OverPerSpawnRemoveTools()
    {
        // The mode's enforcement is applied LAST: not even a spawn-time removeTools can strip a
        // required tool.
        var result = SubAgentManager.BuildEnabledToolSet(
            templateEnabledTools: [DomainTool, TaskTool],
            addTools: null,
            removeTools: [TaskTool],
            requiredTools: [TaskTool]
        );

        result.Should().BeEquivalentTo([DomainTool, TaskTool]);
    }

    [Fact]
    public void BuildEnabledToolSet_WithoutRequiredTools_KeepsTodayExactly()
    {
        SubAgentManager
            .BuildEnabledToolSet([DomainTool], addTools: null, removeTools: null)
            .Should()
            .BeEquivalentTo([DomainTool]);

        // No filtering at all stays "no filtering" — required tools cannot narrow an inherit-all
        // template into a list.
        SubAgentManager
            .BuildEnabledToolSet(
                templateEnabledTools: null,
                addTools: null,
                removeTools: null,
                requiredTools: [TaskTool]
            )
            .Should()
            .BeNull();
    }

    #endregion

    #region Spawn-path pins

    [Fact]
    public async Task RestrictedTemplate_WithModeRequiredTools_ReceivesTheUnion()
    {
        var (manager, _) = CreateManager(
            RestrictedTemplate(),
            options => options with { RequiredToolNames = [TaskTool, SecondTaskTool] }
        );

        var childLoop = await SpawnChildAsync(manager);

        childLoop.RegisteredToolNames.Should().Contain([DomainTool, TaskTool, SecondTaskTool]);
    }

    [Fact]
    public async Task RestrictedTemplate_WithoutTheProperty_StaysStripped()
    {
        // The opt-in pin: unset = today's behavior byte-for-byte.
        var (manager, _) = CreateManager(RestrictedTemplate());

        var childLoop = await SpawnChildAsync(manager);

        childLoop.RegisteredToolNames.Should().Contain(DomainTool);
        childLoop.RegisteredToolNames.Should().NotContain([TaskTool, SecondTaskTool]);
    }

    [Fact]
    public async Task RequiredTool_TheModeDoesNotExpose_IsNotGranted()
    {
        // "not-a-mode-tool" is required but absent from the parent's contracts (the mode's own
        // surface), so the union cannot materialize it: a mode cannot grant what it does not have.
        var (manager, _) = CreateManager(
            RestrictedTemplate(),
            options => options with { RequiredToolNames = [TaskTool, "not-a-mode-tool"] }
        );

        var childLoop = await SpawnChildAsync(manager);

        childLoop.RegisteredToolNames.Should().Contain(TaskTool);
        childLoop.RegisteredToolNames.Should().NotContain("not-a-mode-tool");
    }

    [Fact]
    public async Task DepthTwoSpawn_InheritsTheEnforcement()
    {
        // The #623 incident was observed one level down, so the pin goes two levels down: the child's
        // manager runs on ForChildLoop's options, and the grandchild's template is just as restricted
        // as the child's. Both must carry the required tool.
        var root = AgentCollaborationSetup.CreateRoot(new AgentCollaborationOptions { MaxDelegationDepth = 2 });
        _ = root.Directory.TryRegister(root.Context, root.Name, AgentCollaborationStatuses.Running, new NullEndpoint());

        var (manager, provider) = CreateManager(
            RestrictedTemplate(),
            options => options with { RequiredToolNames = [TaskTool] },
            collaboration: root
        );

        var childId = await SpawnCollaborativeAsync(provider, "child");
        var childLoop = ChildLoop(manager, childId);
        childLoop.RegisteredToolNames.Should().Contain([DomainTool, TaskTool]);

        var grandchildId = await SpawnCollaborativeAsync(childLoop.SubAgentTools!, "grandchild");
        var grandchildLoop = ChildLoop(childLoop.SubAgentManager!, grandchildId);

        grandchildLoop
            .RegisteredToolNames.Should()
            .Contain(
                [DomainTool, TaskTool],
                "the required-tools enforcement travels through ForChildLoop to every spawn depth"
            );
    }

    #endregion

    #region #635 — the all-tools wildcard in add_tools

    /// <summary>
    /// The defect: <c>add_tools: "*"</c> was added to the enabled set as a literal tool NAME, matched
    /// no parent contract at the intersection, and left the sub-agent with no inherited tools at all.
    /// The assertion is on the child's RESOLVED toolset, so it fails if resolution silently narrows.
    /// </summary>
    [Theory]
    [InlineData("*")]
    [InlineData("*:*")]
    [InlineData(" * ")]
    public async Task WildcardAddTools_InheritsEveryToolTheParentExposes(string wildcard)
    {
        var (manager, _) = CreateManager(RestrictedTemplate());

        var childLoop = await SpawnChildAsync(manager, addTools: [wildcard]);

        childLoop
            .RegisteredToolNames.Should()
            .Contain(
                [DomainTool, TaskTool, SecondTaskTool],
                "'{0}' means every tool the parent exposes, not a tool literally named that",
                wildcard
            );
        childLoop.RegisteredToolNames.Should().NotContain(wildcard.Trim());
    }

    /// <summary>
    /// Non-vacuity pin for the theory above: the SAME assertion shape, with a known-good explicit
    /// value, must discriminate. If the harness were reading a toolset it never observes, this would
    /// pass too — it does not, because the tool the spawn did NOT name is absent.
    /// </summary>
    [Fact]
    public async Task ExplicitAddTools_GrantsExactlyWhatItNames_AndNothingElse()
    {
        var (manager, _) = CreateManager(RestrictedTemplate());

        var childLoop = await SpawnChildAsync(manager, addTools: [TaskTool]);

        childLoop.RegisteredToolNames.Should().Contain([DomainTool, TaskTool]);
        childLoop
            .RegisteredToolNames.Should()
            .NotContain(
                SecondTaskTool,
                "the harness observes the real resolved toolset — an unnamed tool must be absent"
            );
    }

    /// <summary>
    /// Second half of the non-vacuity proof: a template that inherits everything really does show all
    /// three names through this same accessor, so "Contain(all three)" above is a reachable outcome
    /// rather than one the harness can never produce.
    /// </summary>
    [Fact]
    public async Task InheritAllTemplate_ShowsEveryParentToolThroughTheSameAccessor()
    {
        var (manager, _) = CreateManager(InheritAllTemplate());

        var childLoop = await SpawnChildAsync(manager);

        childLoop.RegisteredToolNames.Should().Contain([DomainTool, TaskTool, SecondTaskTool]);
    }

    /// <summary>
    /// The wildcard composes with <c>remove_tools</c>: "everything except X". This is only
    /// expressible because <c>*</c> supplies a base set — without one, removeTools throws.
    /// </summary>
    [Fact]
    public async Task WildcardAddTools_ComposesWithRemoveTools()
    {
        var (manager, _) = CreateManager(InheritAllTemplate());

        var childLoop = await SpawnChildAsync(manager, addTools: ["*"], removeTools: [SecondTaskTool]);

        childLoop.RegisteredToolNames.Should().Contain([DomainTool, TaskTool]);
        childLoop.RegisteredToolNames.Should().NotContain(SecondTaskTool);
    }

    /// <summary>
    /// #623's union applies on THIS path too: an add_tools list that names nothing the parent has
    /// still leaves the mode's required tools in place, so a "*"-style spawn mistake cannot strip
    /// what the mode guarantees. Pinned with an unmatched literal rather than the wildcard, because
    /// the wildcard now grants everything and would satisfy the assertion for the wrong reason.
    /// </summary>
    [Fact]
    public async Task UnmatchedAddTools_StillReceivesTheModeRequiredTools()
    {
        var (manager, _) = CreateManager(
            InheritAllTemplate(),
            options => options with { RequiredToolNames = [TaskTool] }
        );

        var childLoop = await SpawnChildAsync(manager, addTools: ["tasks:*"]);

        childLoop.RegisteredToolNames.Should().Contain(TaskTool);
        childLoop
            .RegisteredToolNames.Should()
            .NotContain(DomainTool, "an unmatched add list still narrows to the required union");
    }

    /// <summary>
    /// The parse-level sibling of the same hole, through the REAL <c>Agent</c> tool handler: a lone
    /// separator used to parse to an EMPTY (non-null) array, which the spawn path reads as a filter
    /// matching nothing rather than as "no filter".
    /// </summary>
    [Fact]
    public async Task AddToolsThatParsesToNoEntries_LeavesTheToolsetUnfiltered()
    {
        var (manager, provider) = CreateManager(InheritAllTemplate());

        var agentId = await SpawnViaToolAsync(provider, addTools: ",");
        var childLoop = ChildLoop(manager, agentId);

        childLoop.RegisteredToolNames.Should().Contain([DomainTool, TaskTool, SecondTaskTool]);
    }

    [Fact]
    public async Task WildcardAddTools_ThroughTheRealToolHandler_InheritsEverything()
    {
        var (manager, provider) = CreateManager(RestrictedTemplate());

        var agentId = await SpawnViaToolAsync(provider, addTools: "*");
        var childLoop = ChildLoop(manager, agentId);

        childLoop.RegisteredToolNames.Should().Contain([DomainTool, TaskTool, SecondTaskTool]);
    }

    [Fact]
    public void BuildEnabledToolSet_ExpandsTheWildcardFromTheInheritableRoster()
    {
        SubAgentManager
            .BuildEnabledToolSet(
                templateEnabledTools: [DomainTool],
                addTools: ["*"],
                removeTools: null,
                requiredTools: null,
                inheritableToolNames: [TaskTool, SecondTaskTool, DomainTool]
            )
            .Should()
            .BeEquivalentTo([DomainTool, TaskTool, SecondTaskTool]);
    }

    [Fact]
    public void IsAllToolsWildcard_AcceptsOnlyTheDocumentedSpellings()
    {
        SubAgentManager.IsAllToolsWildcard("*").Should().BeTrue();
        SubAgentManager.IsAllToolsWildcard("*:*").Should().BeTrue();
        SubAgentManager.IsAllToolsWildcard(" * ").Should().BeTrue();
        // Deliberately NOT wildcards: "all" is a legal tool name and add_tools has no group language.
        SubAgentManager.IsAllToolsWildcard("all").Should().BeFalse();
        SubAgentManager.IsAllToolsWildcard("tasks:*").Should().BeFalse();
        SubAgentManager.IsAllToolsWildcard(null).Should().BeFalse();
    }

    #endregion

    #region #635 — the "matched nothing" warnings

    private const string UnmatchedAddWarningMarker = "the parent does not expose";
    private const string EmptyToolsetWarningMarker = "resolved to an EMPTY inherited toolset";

    [Fact]
    public async Task AddToolsNamingNothingTheParentHas_LogsTheUnmatchedWarning()
    {
        var logger = new CapturingLogger<SubAgentManager>();
        var (manager, _) = CreateManager(InheritAllTemplate(), logger: logger);

        _ = await SpawnChildAsync(manager, addTools: ["tasks:*", "all"]);

        logger.CountAtLevel(LogLevel.Warning, UnmatchedAddWarningMarker).Should().Be(1);
    }

    [Fact]
    public async Task AddToolsNamingRealTools_DoesNotLogTheUnmatchedWarning()
    {
        var logger = new CapturingLogger<SubAgentManager>();
        var (manager, _) = CreateManager(InheritAllTemplate(), logger: logger);

        _ = await SpawnChildAsync(manager, addTools: [TaskTool, "*"]);

        logger.CountAtLevel(LogLevel.Warning, UnmatchedAddWarningMarker).Should().Be(0);
    }

    [Fact]
    public async Task ResolvingToNoInheritedToolAtAll_LogsTheEmptyToolsetWarning()
    {
        var logger = new CapturingLogger<SubAgentManager>();
        var (manager, _) = CreateManager(InheritAllTemplate(), logger: logger);

        _ = await SpawnChildAsync(manager, addTools: ["nothing-here"]);

        logger.CountAtLevel(LogLevel.Warning, EmptyToolsetWarningMarker).Should().Be(1);
    }

    [Fact]
    public async Task AWildcardSpawn_LeavesTheEmptyToolsetWarningSilent()
    {
        // The regression the warning exists for. The template restricts to a name the parent does not
        // have, so WITHOUT the wildcard expansion the resolved toolset is empty and the warning fires;
        // the silence asserted here is therefore evidence that "*" really expanded.
        var logger = new CapturingLogger<SubAgentManager>();
        var (manager, _) = CreateManager(Template(enabledTools: ["not-a-parent-tool"]), logger: logger);

        _ = await SpawnChildAsync(manager, addTools: ["*"]);

        logger.CountAtLevel(LogLevel.Warning, EmptyToolsetWarningMarker).Should().Be(0);
    }

    #endregion

    #region #638 F-001 — remove_tools that withholds nothing

    private const string RemoveWithheldNothingMarker = "not in its toolset to begin with";
    private const string RemoveRestoredMarker = "restored by the required-tools union";

    /// <summary>
    /// The headline #638 F-001 case. <c>add_tools: "*"</c> (which #636 taught) plus a group pattern on
    /// the remove side reads as "everything except the board tools" and silently produced the
    /// OPPOSITE: every board tool granted, no warning. Both halves are asserted here — the warning
    /// AND the over-grant it describes — so the test cannot pass on a spawn that never resolved.
    /// </summary>
    [Fact]
    public async Task RemoveToolsWithAGroupPattern_OverGrants_AndLogsTheWithheldNothingWarning()
    {
        var logger = new CapturingLogger<SubAgentManager>();
        var (manager, _) = CreateManager(InheritAllTemplate(), logger: logger);

        var childLoop = await SpawnChildAsync(manager, addTools: ["*"], removeTools: ["tasks:*"]);

        childLoop
            .RegisteredToolNames.Should()
            .Contain(
                [TaskTool, SecondTaskTool],
                "'tasks:*' is not group language on this side - the board tools are still granted"
            );
        logger.CountAtLevel(LogLevel.Warning, RemoveWithheldNothingMarker).Should().Be(1);
    }

    /// <summary>
    /// The paired positive for the silence below AND the discriminator for the warning above: SAME
    /// harness, SAME accessor, one variable changed (an exact tool name instead of a pattern). The
    /// removal really happens and the warning really stays quiet, so neither result is an artifact of
    /// the test never reaching the code.
    /// </summary>
    [Fact]
    public async Task RemoveToolsNamingAnExactTool_WithholdsIt_AndLeavesTheWarningSilent()
    {
        var logger = new CapturingLogger<SubAgentManager>();
        var (manager, _) = CreateManager(InheritAllTemplate(), logger: logger);

        var childLoop = await SpawnChildAsync(manager, addTools: ["*"], removeTools: [SecondTaskTool]);

        childLoop.RegisteredToolNames.Should().Contain([DomainTool, TaskTool]);
        childLoop
            .RegisteredToolNames.Should()
            .NotContain(SecondTaskTool, "an exact name really is withheld - the silence below is about a no-op");
        logger.CountAtLevel(LogLevel.Warning, RemoveWithheldNothingMarker).Should().Be(0);
    }

    /// <summary>
    /// <c>remove_tools: "*"</c> means "remove a tool literally named <c>*</c>" — a no-op, and
    /// deliberately still one: adding wildcard language to this side alone is the asymmetry that made
    /// F-001 reachable. It must not be a SILENT no-op.
    /// </summary>
    [Fact]
    public async Task RemoveToolsStarLiteral_IsANoOp_AndLogsTheWithheldNothingWarning()
    {
        var logger = new CapturingLogger<SubAgentManager>();
        var (manager, _) = CreateManager(InheritAllTemplate(), logger: logger);

        var childLoop = await SpawnChildAsync(manager, addTools: ["*"], removeTools: ["*"]);

        childLoop.RegisteredToolNames.Should().Contain([DomainTool, TaskTool, SecondTaskTool]);
        logger.CountAtLevel(LogLevel.Warning, RemoveWithheldNothingMarker).Should().Be(1);
    }

    /// <summary>
    /// The logged COUNT must be the number of entries that withheld nothing, not "some warning fired".
    /// Two of the three entries are no-ops; a line reporting 1 or 3 is wrong and fails here.
    /// </summary>
    [Fact]
    public async Task TheWithheldNothingWarning_CountsOnlyTheEntriesThatWithheldNothing()
    {
        var logger = new CapturingLogger<SubAgentManager>();
        var (manager, _) = CreateManager(InheritAllTemplate(), logger: logger);

        _ = await SpawnChildAsync(manager, addTools: ["*"], removeTools: ["tasks:*", "no-such-tool", SecondTaskTool]);

        var line = logger
            .MessagesAtLevel(LogLevel.Warning)
            .Should()
            .ContainSingle(message => message.Contains(RemoveWithheldNothingMarker, StringComparison.Ordinal))
            .Subject;

        line.Should().Contain("naming 2 tool(s)");
        line.Should().Contain("tasks:*").And.Contain("no-such-tool");
        line.Should().NotContain(SecondTaskTool, "the one entry that DID withhold a tool is not an unmatched entry");
    }

    /// <summary>
    /// Through the REAL <c>Agent</c> handler, so the <c>remove_tools</c> STRING is parsed by production
    /// code rather than handed in pre-split — the path a model actually takes.
    /// </summary>
    [Fact]
    public async Task RemoveToolsGroupPattern_ThroughTheRealToolHandler_OverGrantsAndWarns()
    {
        var logger = new CapturingLogger<SubAgentManager>();
        var (manager, provider) = CreateManager(InheritAllTemplate(), logger: logger);

        var agentId = await SpawnViaToolAsync(provider, addTools: "*", removeTools: "tasks:*");

        ChildLoop(manager, agentId).RegisteredToolNames.Should().Contain([TaskTool, SecondTaskTool]);
        logger.CountAtLevel(LogLevel.Warning, RemoveWithheldNothingMarker).Should().Be(1);
    }

    /// <summary>
    /// The second way "withhold these" produces the opposite: the entry MATCHES, is removed, and is
    /// then put straight back by the mode's required-tools union (#623). Deliberate precedence, but
    /// silent from the caller's seat until now — and it is a DIFFERENT line from the no-op warning,
    /// which must stay quiet here because the entry did match.
    /// </summary>
    [Fact]
    public async Task RemoveToolsNamingAModeRequiredTool_LogsTheRestoredWarning()
    {
        var logger = new CapturingLogger<SubAgentManager>();
        var (manager, _) = CreateManager(
            InheritAllTemplate(),
            options => options with { RequiredToolNames = [TaskTool] },
            logger: logger
        );

        var childLoop = await SpawnChildAsync(manager, addTools: ["*"], removeTools: [TaskTool]);

        childLoop.RegisteredToolNames.Should().Contain(TaskTool, "the required-tools union wins - that is the point");
        logger.CountAtLevel(LogLevel.Warning, RemoveRestoredMarker).Should().Be(1);
        logger.CountAtLevel(LogLevel.Warning, RemoveWithheldNothingMarker).Should().Be(0);
    }

    /// <summary>
    /// The two lines partition by OBSERVABLE outcome, so an entry that was never in the base set AND
    /// comes back through the required-tools union is reported as restored — the caller's question is
    /// "is it there?", and it is. It must not ALSO draw the no-op line, which would say the opposite.
    /// </summary>
    [Fact]
    public async Task RemoveToolsNamingARequiredToolTheTemplateNeverHad_ReportsItAsRestored_NotAsANoOp()
    {
        var logger = new CapturingLogger<SubAgentManager>();
        var (manager, _) = CreateManager(
            RestrictedTemplate(),
            options => options with { RequiredToolNames = [TaskTool] },
            logger: logger
        );

        var childLoop = await SpawnChildAsync(manager, removeTools: [TaskTool]);

        childLoop.RegisteredToolNames.Should().Contain(TaskTool);
        logger.CountAtLevel(LogLevel.Warning, RemoveRestoredMarker).Should().Be(1);
        logger.CountAtLevel(LogLevel.Warning, RemoveWithheldNothingMarker).Should().Be(0);
    }

    /// <summary>
    /// #641 F-001. The required-tools union re-adds a NAME; it cannot conjure a tool. On a parent that
    /// exposes no contract/handler for the mode-required tool, the name survives into the resolved set
    /// and the child still does not have it — so reporting it as "restored ... the sub-agent still has
    /// them" told the operator the OPPOSITE of the truth, and, the partition being exclusive,
    /// suppressed the accurate no-op line at the same time. This is the discriminator for
    /// <see cref="RemoveToolsNamingARequiredToolTheTemplateNeverHad_ReportsItAsRestored_NotAsANoOp"/>
    /// above: SAME removal, SAME required tool, one variable changed — the parent no longer exposes
    /// it — and the two must draw DIFFERENT lines. Reading the name set makes both draw the restored
    /// line, which is why the pre-fix predicate passed that test and fails this one.
    /// </summary>
    [Fact]
    public async Task RemoveToolsNamingARequiredToolTheParentCannotMaterialize_ReportsItAsANoOp_NotAsRestored()
    {
        var logger = new CapturingLogger<SubAgentManager>();
        var (manager, _) = CreateManager(
            InheritAllTemplate(),
            options => options with { RequiredToolNames = [TaskTool] },
            logger: logger,
            parentToolNames: [DomainTool]
        );

        var childLoop = await SpawnChildAsync(manager, addTools: ["*"], removeTools: [TaskTool]);

        childLoop
            .RegisteredToolNames.Should()
            .Contain(DomainTool, "the spawn really resolved - the assertions below are not about a failed spawn");
        childLoop
            .RegisteredToolNames.Should()
            .NotContain(TaskTool, "the union re-adds the NAME, but this parent exposes no tool to materialize");

        logger
            .CountAtLevel(LogLevel.Warning, RemoveRestoredMarker)
            .Should()
            .Be(0, "the sub-agent does NOT still have it - that line would be a false statement of fact");

        var line = logger
            .MessagesAtLevel(LogLevel.Warning)
            .Should()
            .ContainSingle(message => message.Contains(RemoveWithheldNothingMarker, StringComparison.Ordinal))
            .Subject;
        line.Should().Contain("naming 1 tool(s)").And.Contain(TaskTool);
    }

    /// <summary>
    /// Paired positive for the silence above: SAME harness, SAME spawn, one variable changed (the mode
    /// declares no required tools). The tool is genuinely withheld and the restored-warning is quiet,
    /// so the fired warning above is caused by the required-tools union and nothing else.
    /// </summary>
    [Fact]
    public async Task RemoveToolsWithoutModeRequiredTools_WithholdsTheTool_AndLeavesTheRestoredWarningSilent()
    {
        var logger = new CapturingLogger<SubAgentManager>();
        var (manager, _) = CreateManager(InheritAllTemplate(), logger: logger);

        var childLoop = await SpawnChildAsync(manager, addTools: ["*"], removeTools: [TaskTool]);

        childLoop.RegisteredToolNames.Should().NotContain(TaskTool);
        logger.CountAtLevel(LogLevel.Warning, RemoveRestoredMarker).Should().Be(0);
    }

    #endregion

    #region #638 F-002 — the empty-toolset warning's count and its deny-all false positive

    /// <summary>
    /// A deliberate <c>tools: []</c> deny-all is a CORRECT configuration, and a warning that fires on
    /// correct configuration trains people to ignore it. This is one half of the discriminator; the
    /// other half is <see cref="GenuineCollapse_FromANonEmptyToolsList_LogsTheEmptyToolsetWarning"/>,
    /// which resolves to the same empty toolset from a template that asked for something.
    /// </summary>
    [Fact]
    public async Task DeliberateDenyAllTemplate_LeavesTheEmptyToolsetWarningSilent()
    {
        var logger = new CapturingLogger<SubAgentManager>();
        var (manager, _) = CreateManager(DenyAllTemplate(), logger: logger);

        var childLoop = await SpawnChildAsync(manager);

        childLoop
            .RegisteredToolNames.Should()
            .NotContain(
                [DomainTool, TaskTool, SecondTaskTool],
                "deny-all really did deny all - the silence is about a REACHED empty toolset"
            );
        logger.CountAtLevel(LogLevel.Warning, EmptyToolsetWarningMarker).Should().Be(0);
    }

    /// <summary>
    /// The paired positive: the SAME empty resolved toolset, from a template that named a tool and got
    /// none of it. If the fix could not tell these two apart it would be incomplete, so this pair is
    /// the discriminator rather than either test alone.
    /// </summary>
    [Fact]
    public async Task GenuineCollapse_FromANonEmptyToolsList_LogsTheEmptyToolsetWarning()
    {
        var logger = new CapturingLogger<SubAgentManager>();
        var (manager, _) = CreateManager(Template(enabledTools: ["not-a-parent-tool"]), logger: logger);

        var childLoop = await SpawnChildAsync(manager);

        childLoop.RegisteredToolNames.Should().NotContain([DomainTool, TaskTool, SecondTaskTool]);
        logger.CountAtLevel(LogLevel.Warning, EmptyToolsetWarningMarker).Should().Be(1);
    }

    /// <summary>
    /// The deny-all suppression is not a blanket over the template: a spawn that ASKS for a tool on top
    /// of <c>tools: []</c> and receives none of it is a collapse, because the caller wanted something.
    /// </summary>
    [Fact]
    public async Task DenyAllTemplate_WithAnAddToolsThatMatchesNothing_StillLogsTheEmptyToolsetWarning()
    {
        var logger = new CapturingLogger<SubAgentManager>();
        var (manager, _) = CreateManager(DenyAllTemplate(), logger: logger);

        _ = await SpawnChildAsync(manager, addTools: ["no-such-tool"]);

        logger.CountAtLevel(LogLevel.Warning, EmptyToolsetWarningMarker).Should().Be(1);
    }

    /// <summary>
    /// The reported count must be the number of INHERITABLE parent tools. AskUserQuestion and
    /// NotifyClient sit in the parent's contracts but are structurally never inherited (#246), so a
    /// parent holding all five can hand a child at most three — a line saying five is simply wrong,
    /// and this asserts the number rather than merely that a line was logged.
    /// </summary>
    [Fact]
    public async Task TheEmptyToolsetWarning_CountsOnlyTheInheritableParentTools()
    {
        var logger = new CapturingLogger<SubAgentManager>();
        var (manager, _) = CreateManager(
            Template(enabledTools: ["not-a-parent-tool"]),
            logger: logger,
            parentToolNames:
            [
                TaskTool,
                SecondTaskTool,
                DomainTool,
                AskUserQuestionToolProvider.ToolName,
                NotifyClientToolProvider.ToolName,
            ]
        );

        _ = await SpawnChildAsync(manager);

        var line = logger
            .MessagesAtLevel(LogLevel.Warning)
            .Should()
            .ContainSingle(message => message.Contains(EmptyToolsetWarningMarker, StringComparison.Ordinal))
            .Subject;

        line.Should().Contain("exposing 3 inheritable tool(s)");
        line.Should().NotContain("exposing 5", "AskUserQuestion/NotifyClient can never be inherited");
    }

    /// <summary>
    /// A parent exposing ONLY the two never-inherited tools can hand a child nothing at all, so an
    /// empty child toolset is structurally unavoidable there and is not a narrowing to report.
    /// </summary>
    [Fact]
    public async Task ParentExposingOnlyNeverInheritedTools_LeavesTheEmptyToolsetWarningSilent()
    {
        var logger = new CapturingLogger<SubAgentManager>();
        var (manager, _) = CreateManager(
            InheritAllTemplate(),
            logger: logger,
            parentToolNames: [AskUserQuestionToolProvider.ToolName, NotifyClientToolProvider.ToolName]
        );

        _ = await SpawnChildAsync(manager, addTools: ["no-such-tool"]);

        logger.CountAtLevel(LogLevel.Warning, EmptyToolsetWarningMarker).Should().Be(0);
    }

    /// <summary>
    /// Paired positive for the silence above: the SAME template, the SAME spawn, the SAME accessor —
    /// one variable changed, a single inheritable tool added to the parent — and the warning fires.
    /// Without this the silence above would pass equally well if the spawn had failed outright.
    /// </summary>
    [Fact]
    public async Task ParentWithOneInheritableToolBesideThem_LogsTheEmptyToolsetWarning()
    {
        var logger = new CapturingLogger<SubAgentManager>();
        var (manager, _) = CreateManager(
            InheritAllTemplate(),
            logger: logger,
            parentToolNames: [AskUserQuestionToolProvider.ToolName, NotifyClientToolProvider.ToolName, DomainTool]
        );

        _ = await SpawnChildAsync(manager, addTools: ["no-such-tool"]);

        var line = logger
            .MessagesAtLevel(LogLevel.Warning)
            .Should()
            .ContainSingle(message => message.Contains(EmptyToolsetWarningMarker, StringComparison.Ordinal))
            .Subject;

        line.Should().Contain("exposing 1 inheritable tool(s)");
    }

    [Fact]
    public void IsNeverInheritedTool_NamesExactlyTheStructurallyExcludedTools()
    {
        SubAgentManager.IsNeverInheritedTool(AskUserQuestionToolProvider.ToolName).Should().BeTrue();
        SubAgentManager.IsNeverInheritedTool(NotifyClientToolProvider.ToolName).Should().BeTrue();
        SubAgentManager.IsNeverInheritedTool(TaskTool).Should().BeFalse();
        SubAgentManager.IsNeverInheritedTool(DomainTool).Should().BeFalse();
    }

    #endregion

    #region Warning floor

    private const string WarningMarker = "contains NONE of the task tools";

    [Fact]
    public async Task BoardReferencingDispatch_ToToollessTemplate_LogsTheWarning()
    {
        var logger = new CapturingLogger<SubAgentManager>();
        var (manager, _) = CreateManager(
            RestrictedTemplate(),
            options => options with { TaskToolNames = [TaskTool, SecondTaskTool] },
            logger: logger
        );

        _ = await SpawnChildAsync(manager, task: "Claim Todo 2.1 with claim-task under name correctness-reviewer.");

        logger.CountAtLevel(LogLevel.Warning, WarningMarker).Should().Be(1);
    }

    [Fact]
    public async Task NonBoardDispatch_ToTheSameToollessTemplate_DoesNotWarn()
    {
        var logger = new CapturingLogger<SubAgentManager>();
        var (manager, _) = CreateManager(
            RestrictedTemplate(),
            options => options with { TaskToolNames = [TaskTool, SecondTaskTool] },
            logger: logger
        );

        _ = await SpawnChildAsync(manager, task: "Summarize the README and report back.");

        logger.CountAtLevel(LogLevel.Warning, WarningMarker).Should().Be(0);
    }

    [Fact]
    public async Task BoardReferencingDispatch_WhenTaskToolsArePresent_DoesNotWarn()
    {
        var logger = new CapturingLogger<SubAgentManager>();
        var (manager, _) = CreateManager(
            InheritAllTemplate(),
            options => options with { TaskToolNames = [TaskTool, SecondTaskTool] },
            logger: logger
        );

        _ = await SpawnChildAsync(manager, task: "Claim Todo 2.1 with claim-task and work it.");

        logger.CountAtLevel(LogLevel.Warning, WarningMarker).Should().Be(0);
    }

    [Fact]
    public async Task NeutralDispatch_WithABoardTaskAssignedToTheSpawnName_LogsTheWarning()
    {
        // The second trigger: the prompt never mentions the board, but the primary already assigned a
        // task to this agent's name — the host's probe is what knows that.
        var logger = new CapturingLogger<SubAgentManager>();
        var (manager, _) = CreateManager(
            RestrictedTemplate(),
            options => options with { TaskToolNames = [TaskTool], TaskAssignmentProbe = name => name == "boardworker" },
            logger: logger
        );

        _ = await SpawnChildAsync(manager, task: "Do some research.", name: "boardworker");

        logger.CountAtLevel(LogLevel.Warning, WarningMarker).Should().Be(1);
    }

    /// <summary>
    /// PR #626 review F-003: the #623 incident's VERBATIM dispatch line cites no hyphenated tool
    /// name and no board phrase, so it must trigger the warning on the marker heuristic alone —
    /// no <see cref="SubAgentOptions.TaskAssignmentProbe"/> is wired here, pinning the ordering
    /// where the primary dispatches before assigning any board task.
    /// </summary>
    [Fact]
    public async Task VerbatimIncidentDispatchLine_LogsTheWarning_WithoutTheAssignmentProbe()
    {
        var logger = new CapturingLogger<SubAgentManager>();
        var (manager, _) = CreateManager(
            RestrictedTemplate(),
            options => options with { TaskToolNames = [TaskTool, SecondTaskTool] },
            logger: logger
        );

        _ = await SpawnChildAsync(manager, task: "Claim Todo 2.1 under name correctness-reviewer");

        logger.CountAtLevel(LogLevel.Warning, WarningMarker).Should().Be(1);
    }

    [Fact]
    public void ReferencesTodoBoard_MatchesTheDocumentedMarkers_CaseInsensitively()
    {
        SubAgentManager.ReferencesTodoBoard("Claim Todo 2.1 via CLAIM-TASK.").Should().BeTrue();
        SubAgentManager.ReferencesTodoBoard("Work the todo board top to bottom.").Should().BeTrue();
        // The incident's literal phrasing (F-003): spaced imperatives, no tool-name citation.
        SubAgentManager.ReferencesTodoBoard("Claim Todo 2.1 under name correctness-reviewer").Should().BeTrue();
        SubAgentManager.ReferencesTodoBoard("Please claim task 3 and start.").Should().BeTrue();
        SubAgentManager.ReferencesTodoBoard("Summarize the design doc.").Should().BeFalse();
        SubAgentManager.ReferencesTodoBoard(null).Should().BeFalse();
    }

    #endregion

    #region Harness

    private static FunctionContract Contract(string name) =>
        new()
        {
            Name = name,
            Description = name,
            Parameters = [],
        };

    private static ToolHandler OkHandler() =>
        (_, _, _) => Task.FromResult<ToolHandlerResult>(ToolHandlerResult.FromText("ok"));

    /// <summary>A template whose restricted <c>tools:</c> list strips the task tools — the #623 shape.</summary>
    private SubAgentTemplate RestrictedTemplate() => Template(enabledTools: [DomainTool]);

    private SubAgentTemplate InheritAllTemplate() => Template(enabledTools: null);

    /// <summary>
    /// A DELIBERATE deny-all template. <c>tools: []</c> in agent markdown reaches
    /// <c>SubAgentMarkdownParser.NormalizeTools</c>, which returns an EMPTY-but-not-null list — that is
    /// how "deny all" is spelled, and it is a correct configuration, not a collapse (#638 F-002).
    /// </summary>
    private SubAgentTemplate DenyAllTemplate() => Template(enabledTools: []);

    private SubAgentTemplate Template(IReadOnlyList<string>? enabledTools) =>
        new()
        {
            Name = "worker",
            SystemPrompt = "You are a worker.",
            EnabledTools = enabledTools,
            AgentFactory = () =>
            {
                var mock = new Mock<IStreamingAgent>();
                _ = mock.Setup(a =>
                        a.GenerateReplyStreamingAsync(
                            It.IsAny<IEnumerable<IMessage>>(),
                            It.IsAny<GenerateReplyOptions>(),
                            It.IsAny<CancellationToken>()
                        )
                    )
                    .Returns<IEnumerable<IMessage>, GenerateReplyOptions?, CancellationToken>(
                        (_, _, ct) => Task.FromResult(BlockingStream(ct))
                    );
                return mock.Object;
            },
        };

    /// <summary>
    /// The parent's tool surface — what the MODE exposes. It carries the task tools plus a domain
    /// tool, so a stripped child proves the template filter and a granted child proves the union.
    /// </summary>
    private (SubAgentManager Manager, SubAgentToolProvider Provider) CreateManager(
        SubAgentTemplate template,
        Func<SubAgentOptions, SubAgentOptions>? configure = null,
        AgentCollaborationSetup? collaboration = null,
        ILogger? logger = null,
        IReadOnlyList<string>? parentToolNames = null
    )
    {
        var options = new SubAgentOptions
        {
            Templates = new Dictionary<string, SubAgentTemplate> { ["worker"] = template },
            MaxConcurrentSubAgents = 5,
        };
        options = configure?.Invoke(options) ?? options;

        var toolNames = parentToolNames ?? [TaskTool, SecondTaskTool, DomainTool];
        var source = new MutableSubAgentTemplateSource(options.Templates);
        var manager = new SubAgentManager(
            parentAgent: _parentMock.Object,
            parentContracts: [.. toolNames.Select(Contract)],
            parentHandlers: toolNames.ToDictionary(name => name, _ => OkHandler(), StringComparer.Ordinal),
            options: options,
            source: source,
            logger: logger,
            collaboration: collaboration
        );

        _managers.Add(manager);
        return (manager, new SubAgentToolProvider(manager, source));
    }

    private async Task<MultiTurnAgentLoop> SpawnChildAsync(
        SubAgentManager manager,
        string task = "work",
        string? name = null,
        string[]? addTools = null,
        string[]? removeTools = null
    )
    {
        var receipt = await manager.SpawnAsync(
            "worker",
            task,
            name: name,
            runInBackground: true,
            addTools: addTools,
            removeTools: removeTools
        );
        using var doc = JsonDocument.Parse(receipt);
        var agentId = doc.RootElement.GetProperty("agent_id").GetString()!;
        return ChildLoop(manager, agentId);
    }

    /// <summary>
    /// Spawns through the REAL <c>Agent</c> tool handler, so the <c>add_tools</c> STRING is parsed by
    /// production code rather than handed in pre-split — the seam where a lone separator used to
    /// produce an empty filter (#635).
    /// </summary>
    private static async Task<string> SpawnViaToolAsync(
        SubAgentToolProvider provider,
        string addTools,
        string? removeTools = null
    )
    {
        var handler = provider.GetFunctions().First(f => f.Contract.Name == "Agent").Handler;
        var result = await handler(
            JsonSerializer.Serialize(
                new
                {
                    subagent_type = "worker",
                    prompt = "work",
                    run_in_background = true,
                    add_tools = addTools,
                    remove_tools = removeTools,
                }
            ),
            new ToolCallContext(),
            CancellationToken.None
        );

        var payload = result.Should().BeOfType<ToolHandlerResult.Resolved>().Subject.Payload;
        payload.IsError.Should().BeFalse(payload.Text);
        using var doc = JsonDocument.Parse(payload.Text);
        return doc.RootElement.GetProperty("agent_id").GetString()!;
    }

    private static async Task<string> SpawnCollaborativeAsync(SubAgentToolProvider provider, string name)
    {
        var handler = provider.GetFunctions().First(f => f.Contract.Name == "Agent").Handler;
        var result = await handler(
            JsonSerializer.Serialize(
                new
                {
                    subagent_type = "worker",
                    prompt = "work",
                    role = "worker role",
                    description = "Does a unit of work.",
                    name,
                    run_in_background = true,
                }
            ),
            new ToolCallContext(),
            CancellationToken.None
        );

        var payload = result.Should().BeOfType<ToolHandlerResult.Resolved>().Subject.Payload;
        payload.IsError.Should().BeFalse(payload.Text);
        using var doc = JsonDocument.Parse(payload.Text);
        return doc.RootElement.GetProperty("agent_id").GetString()!;
    }

    private static MultiTurnAgentLoop ChildLoop(SubAgentManager manager, string agentId)
    {
        manager.TryGetAgent(agentId, out var agent).Should().BeTrue();
        return agent.Should().BeOfType<MultiTurnAgentLoop>().Subject;
    }

    private static async IAsyncEnumerable<IMessage> BlockingStream([EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        yield break;
    }

    private sealed class NullEndpoint : IAgentWriteEndpoint
    {
        public ValueTask<AgentDeliveryOutcome> DeliverAsync(
            AgentMessage message,
            CancellationToken cancellationToken = default
        ) => ValueTask.FromResult(new AgentDeliveryOutcome(AgentDeliveryDisposition.Delivered));
    }

    #endregion
}
