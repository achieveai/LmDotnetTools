using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.LmAgentInfra.Agents;
using AchieveAi.LmDotnetTools.LmAgentInfra.Capabilities;
using AchieveAi.LmDotnetTools.LmAgentInfra.Context;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;
using AchieveAi.LmDotnetTools.McpServer.AspNetCore.Extensions;
using AchieveAi.LmDotnetTools.Sandbox;
using FluentAssertions;
using LmStreaming.Sample.Controllers;
using LmStreaming.Sample.E2E.Tests.Infrastructure;
using LmStreaming.Sample.Models;
using LmStreaming.Sample.Persistence;
using LmStreaming.Sample.Services;
using LmStreaming.Sample.Services.Discovery;
using LmStreaming.Sample.Services.RemoteMcp;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Serilog.Core;
using Serilog.Events;

namespace LmStreaming.Sample.E2E.Tests.Scenarios;

/// <summary>
/// Loaded-vs-available skill tiers through <c>Program.cs</c>'s real agent factory: the contracts the slice-1
/// manual run saw hold at the host (concurrent first turn, discovery notices, untiered parity). The only fakes
/// are the gateway's HTTP surface, its MCP endpoint, one plugin's remote MCP server and the LLM.
/// </summary>
/// <remarks>
/// One host serves the class. Every test provisions its own conversations, each on its own workspace and so
/// its own sandbox session, which attributes every gateway and MCP request to exactly one conversation.
/// </remarks>
public sealed class CapabilityTiersHostTests : IClassFixture<CapabilityTiersHostTests.TiersHost>
{
    private const string ReadPrefix = "please-read-the-notes ";
    private const string SwitchReadPrefix = "read-after-a-refused-switch ";

    /// <summary>The opening of the built-in summary prompt: only a compaction's summary call carries it.</summary>
    private const string SummaryMarker = "You compact an agent conversation into a checkpoint.";

    /// <summary>The smallest summary the checkpoint validator accepts: it recomputes the instructions from the rows.</summary>
    private const string SummaryJson =
        """{"instructions":[],"goals":[],"decisions":[],"tasks":[],"artifacts":[],"headlines":{},"agent_outcomes":{},"narrative":"Summarised."}""";

    /// <summary>A user message that makes the root call <c>Skill(beta:c)</c> once.</summary>
    private const string SkillUsePrefix = "use-skill-beta-then-compact ";

    /// <summary>A user message that makes the root spawn one <see cref="WorkerTemplate"/> sub-agent.</summary>
    private const string SpawnPrefix = "spawn-a-tiered-worker ";

    /// <summary>A user message that makes the root spawn one <see cref="OpenTemplate"/> sub-agent.</summary>
    private const string SpawnOpenPrefix = "spawn-an-open-worker ";

    /// <summary>A user message that makes the root spawn one <see cref="WorkerTemplate"/> with <c>add_tools: ExecuteTool</c>.</summary>
    private const string ExecuteSpawnPrefix = "spawn-a-tool-tiered-worker ";

    /// <summary>As <see cref="ExecuteSpawnPrefix"/>, with <c>remove_tools: SearchTool</c> as well.</summary>
    private const string ExecuteNoSearchSpawnPrefix = "spawn-a-worker-without-search ";

    /// <summary>The host's main sub-agent template: its tools list names Skill and Read, not SearchSkill.</summary>
    private const string WorkerTemplate = "tiers-worker";
    private const string WorkerPrompt = "You are the tiers worker.";

    /// <summary>A template with no tools list, so its sub-agent inherits every tool the root may hand down.</summary>
    private const string OpenTemplate = "tiers-open";

    /// <summary>Contains <see cref="WorkerPrompt"/>, so its requests never count as root requests.</summary>
    private const string OpenWorkerPrompt = WorkerPrompt + " It inherits every tool.";
    private const string ReadPath = "/workspace/notes/sub/data.txt";
    private const string ReadResult = "Read ok";

    /// <summary>SC6: a user message that makes the root read once where a new skill and a new sub-agent live.</summary>
    private const string CapabilityReadPrefix = "read-where-capabilities-live ";

    /// <summary>The workspace skill and sub-agent the gateway reports for a <see cref="CapabilityReadPrefix"/> read.</summary>
    private const string DemoSkillPath = "/workspace/s6/.claude/skills/s6-demo";
    private const string DemoAgent = "s6-agent";
    private const string DemoSkillDescription = "Use when the sixth scenario needs a demo. It shows discovery.";
    private const string DemoAgentDescription = "Runs the sixth scenario demo. It reports what it found.";

    /// <summary>SC2: a user message that makes a tool-tiered root run <see cref="AvailableTool"/> through <c>ExecuteTool</c>.</summary>
    private const string ExecuteAvailablePrefix = "run-an-available-tool ";

    /// <summary>A sandbox tool no mode here loads or pins, so a tool-tiered root has it available only.</summary>
    private const string AvailableTool = "Glob";

    /// <summary>SC7: a user message that makes a tool-tiered root find <see cref="RemoteTool"/>, then run it.</summary>
    private const string RemoteToolPrefix = "find-and-run-a-remote-tool ";

    /// <summary>The model-facing name of the tool the <c>acme</c> plugin's <c>api</c> server serves.</summary>
    private const string RemoteTool = "mcp_plugin_acme_api_lookup";

    /// <summary>The remote server's own name for <see cref="RemoteTool"/>, and its answer.</summary>
    private const string RemoteServerTool = "lookup";
    private const string RemoteResult = "acme lookup ok";

    /// <summary>The fake header values of two egress keys: one for <c>acme.test:443</c>, one for another host.</summary>
    private const string AcmeKeyValue = "Bearer acme-key-value-7c1e";
    private const string OtherKeyValue = "Bearer other-key-value-3f9a";

    /// <summary>
    /// The plugin MCP servers the gateway lists for a <see cref="RemoteToolPrefix"/> session, as authored: one with a key
    /// for its host, one whose credential has no key, and a stdio one.
    /// </summary>
    private const string RemoteRowsJson = """
        [{"name":"api","plugin":"acme","marketplace":"mk","type":"http","supported":true,"reason":null,
          "url":"https://acme.test/mcp",
          "headers":[{"name":"Authorization","value":"Bearer ${ACME_TOKEN}","placeholder":null},
                     {"name":"X-Trace","value":"on","placeholder":null}],
          "command":null,"args":[],"env":{},"extra":{}},
         {"name":"nokey","plugin":"acme","marketplace":"mk","type":"http","supported":true,"reason":null,
          "url":"https://nokey.test/mcp","headers":[{"name":"X-Api-Key","value":"${K}","placeholder":"K"}],
          "command":null,"args":[],"env":{},"extra":{}},
         {"name":"local","plugin":"acme","marketplace":"mk","type":"stdio","supported":false,
          "reason":"stdio servers are not supported","url":null,"headers":[],
          "command":"npx","args":["-y","@acme/mcp"],"env":{"ACME_TOKEN":"${ACME_TOKEN}"},"extra":{}}]
        """;

    private readonly TiersHost _host;

    public CapabilityTiersHostTests(TiersHost host)
    {
        _host = host;
    }

    /// <summary>
    /// Contract 10: callers racing the first turn of a fresh tiered conversation get one root, built once, whose
    /// tiers are applied once. A second build would mint a second tiers instance: a second gateway PATCH, a
    /// second "active" row and a second sandbox client presenting the thread id.
    /// </summary>
    [Fact]
    public async Task ConcurrentFirstTurn_BuildsAndAppliesOnce()
    {
        const int Callers = 8;
        var threadId = await _host.ProvisionAsync(_host.TieredModeId);
        var profile = (await _host.Modes.GetModeAsync(_host.TieredModeId))!.ToAgentProfile();
        var pool = _host.Services.GetRequiredService<MultiTurnAgentPool>();

        using var barrier = new Barrier(Callers);
        var agents = await Task.WhenAll(
            Enumerable
                .Range(0, Callers)
                .Select(_ =>
                    Task.Factory.StartNew(
                        () =>
                        {
                            barrier.SignalAndWait();
                            return pool.GetOrCreateAgent(
                                threadId,
                                profile,
                                requestedProviderId: null,
                                requestResponseDumpFileName: null
                            );
                        },
                        CancellationToken.None,
                        // Dedicated threads: barrier participants queued on a bounded pool would deadlock.
                        TaskCreationOptions.LongRunning,
                        TaskScheduler.Default
                    )
                )
        );

        agents.Distinct().Should().ContainSingle("every caller of the first turn gets the one pooled root");
        _host
            .Mcp.Calls.Where(c => c.Method == "initialize" && c.AgentId == threadId)
            .Should()
            .ContainSingle("one build connects one sandbox client, as the thread's gateway agent");
        _host
            .Gateway.Patches.Where(p => p.AgentId == threadId)
            .Should()
            .ContainSingle("one build applies the root's tiers once");
        _host.Gateway.Patches.Should().NotContain(p => p.AgentId == "", "the default agent never receives overrides");
        _host.Logs.Count("Capabilities active", "RootThreadId", threadId).Should().Be(1);
        _host.Logs.Count("Capabilities applied", "GatewayAgentId", threadId).Should().Be(1);
        // Guard: the view key this suite's untiered test asserts absent is the one a tiered build writes.
        (await _host.HasViewAsync(threadId))
            .Should()
            .BeTrue("a tiered first build seeds the persisted view");
    }

    /// <summary>
    /// Contract 11 (F6): the gateway attributes a read to the agent id the caller presented and reports the
    /// context file it found there while the read runs. A tiered root presents its thread id, so the notice joins
    /// its run and reaches its next request. An untiered root presents none: the discovery has no owner and is
    /// dropped.
    /// </summary>
    [Fact]
    public async Task DiscoveryNotice_TieredRootGetsOwnReadNotice_UntieredDoesNot()
    {
        var tiered = await _host.ProvisionAsync(_host.TieredModeId);
        var untiered = await _host.ProvisionAsync(_host.UntieredModeId);

        var tieredRun = await ReadThenDiscoverAsync(tiered, _host.TieredModeId, "tiered");
        var untieredRun = await ReadThenDiscoverAsync(untiered, _host.UntieredModeId, "untiered");

        tieredRun.ReadPresentedAs.Should().Be(tiered, "a tiered root reads as its own gateway agent");
        tieredRun.Delivered.Should().Be(1);
        tieredRun.NextRequestMessages.Should().Contain(tieredRun.Marker, "the notice reaches the root's next request");

        untieredRun.ReadPresentedAs.Should().BeNull("an untiered root reads as the default agent");
        untieredRun.Delivered.Should().Be(0);
        untieredRun.NextRequestMessages.Should().Contain(ReadResult, "guard: the request after the read was seen");
        untieredRun.NextRequestMessages.Should().NotContain(untieredRun.Marker);
    }

    /// <summary>
    /// SC6: a read finds a new workspace skill and a new sub-agent. A root that tiers both gets one notice per item
    /// on its next request, naming the kind, the key and the call, with the first sentence of the description;
    /// tools[] and the persisted view stay as they were. Another tiered conversation on the same workspace shares the
    /// gateway session, so its catalog takes the skill too, with no notice, and its next rotation PATCHes the skill
    /// available. An untiered root presents no agent id, so it gets none.
    /// </summary>
    [Fact]
    public async Task CapabilitiesFoundByARead_FinderGetsOneNoticePerItem_ToolsAndViewUnchanged_BystanderIndexesQuietly_UntieredNone()
    {
        const string SkillLabel = "label=\"skill:" + DemoSkillPath + "\"";
        const string AgentLabel = "label=\"agent:" + DemoAgent + "\"";
        var workspaceId = await _host.CreateWorkspaceAsync();
        var tiered = await _host.ProvisionAsync(_host.AgentTieredModeId, workspaceId);
        var bystander = await _host.ProvisionAsync(_host.TieredModeId, workspaceId);
        var untiered = await _host.ProvisionAsync(_host.UntieredModeId);

        // The bystander is built, and uses a skill, before the read: its catalog cannot list the skill yet.
        _ = await TurnAsync(bystander, SkillUsePrefix + bystander);
        var tieredRun = await ReadWhereCapabilitiesLiveAsync(tiered, _host.AgentTieredModeId, "tiered");
        var untieredRun = await ReadWhereCapabilitiesLiveAsync(untiered, _host.UntieredModeId, "untiered");
        _host.SessionOf(bystander).Should().Be(_host.SessionOf(tiered), "guard: one workspace, one gateway session");

        _host.ModelGate.AnswerRequestsContaining([SummaryMarker, SkillUsePrefix + bystander], SummaryJson);
        try
        {
            using var accepted = await _host.RequestCompactionAsync(bystander);
            accepted.StatusCode.Should().Be(HttpStatusCode.Accepted);
            _ = await TurnAsync(bystander, "plain after the checkpoint " + bystander);
        }
        finally
        {
            _host.ModelGate.AnswerRequestsContaining(null);
        }

        var bystanderPatches = _host.Gateway.Patches.Where(p => p.AgentId == bystander).ToList();
        bystanderPatches.Should().HaveCount(2, "guard: the build's PATCH, then the rotation's");
        using (var rotation = JsonDocument.Parse(bystanderPatches[^1].Body))
        {
            rotation
                .RootElement.GetProperty("set")
                .TryGetProperty(DemoSkillPath, out var demoTier)
                .Should()
                .BeTrue("a rotation PATCH that omits the found path leaves it included for the bystander");
            demoTier.GetString().Should().Be("available");
        }

        _host.Logs.Count("Capabilities discovered", "Owner", bystander).Should().Be(0, "a bystander gets no notice");

        var notices = TextsOf(tieredRun.Requests[^1].MessagesJson);
        notices.Sum(t => Occurrences(t, SkillLabel)).Should().Be(1, "one notice for the skill");
        notices.Sum(t => Occurrences(t, AgentLabel)).Should().Be(1, "one notice for the sub-agent");
        var all = string.Join('\n', notices);
        all.Should().Contain("Skill(skill=\"s6-demo\")").And.Contain("Use when the sixth scenario needs a demo.");
        all.Should().Contain($"Agent(subagent_type=\"{DemoAgent}\")").And.Contain("Runs the sixth scenario demo.");
        all.Should().NotContain("It shows discovery.").And.NotContain("It reports what it found.");
        tieredRun
            .Requests.Select(r => r.ToolsJson)
            .Distinct()
            .Should()
            .ContainSingle("tools[] is unchanged until a compaction");
        tieredRun
            .ViewAfter.Should()
            .NotBeNull()
            .And.Be(tieredRun.ViewBefore, "the view is unchanged until a compaction");

        var untieredTexts = string.Join('\n', TextsOf(untieredRun.Requests[^1].MessagesJson));
        untieredTexts.Should().Contain(ReadResult, "guard: the request after the read was seen");
        untieredTexts.Should().NotContain(SkillLabel).And.NotContain(AgentLabel);
    }

    /// <summary>
    /// Contract 1: a mode without tiers, never tiered or cleared, is today's Workspace Agent. The model receives
    /// byte-identical tools[] and the same system prompt (thread id masked); no capability row is logged, no
    /// view is written, and the root presents no gateway agent id.
    /// </summary>
    [Fact]
    public async Task Untiered_ToolsAndPromptByteIdentical_ToWorkspaceAgent()
    {
        var reference = _host.ReferenceThread;
        var expected = _host.Reference;
        string[] sandboxTools = ["Skill", "Read"];
        expected.ToolNames.Should().Contain(sandboxTools, "guard: the sandbox tools are connected");
        expected.ToolNames.Should().NotContain(CapabilityTiers.SearchSkillToolName);

        foreach (var modeId in new[] { _host.UntieredModeId, _host.ClearedModeId })
        {
            var threadId = await _host.ProvisionAsync(modeId);
            var actual = await _host.FirstRequestAsync(threadId, modeId);

            actual.ToolsJson.Should().Be(expected.ToolsJson, "mode {0} sends Workspace Agent's tools[]", modeId);
            actual
                .SystemPrompt.Replace(threadId, "<thread>", StringComparison.Ordinal)
                .Should()
                .Be(expected.SystemPrompt.Replace(reference, "<thread>", StringComparison.Ordinal));
            _host.Logs.Mentioning("Capabilities", threadId).Should().BeEmpty("mode {0} is untiered", modeId);
            (await _host.HasViewAsync(threadId)).Should().BeFalse();
            var mcp = _host.Mcp.Calls.Where(c => c.SessionId == _host.SessionOf(threadId)).ToList();
            mcp.Should().NotBeEmpty("guard: the root's sandbox client was attributed");
            mcp.Should().OnlyContain(c => c.AgentId == null, "an untiered root is the default gateway agent");
        }
    }

    /// <summary>
    /// A tiered → untiered switch that the factory refuses leaves the live root exactly as it was. The next request
    /// carries byte-identical tools, Skill and SearchSkill included; the root still reads as its own gateway agent;
    /// nothing went inert.
    /// </summary>
    [Fact]
    public async Task RefusedSwitch_TieredToUntiered_LiveRootKeepsItsTiers()
    {
        var threadId = await _host.ProvisionAsync(_host.TieredModeId);
        var before = await _host.FirstRequestAsync(threadId, _host.TieredModeId);
        before
            .ToolNames.Should()
            .Contain(["Skill", CapabilityTiers.SearchSkillToolName], "guard: the root starts tiered");

        await SwitchRefusedDuringACompactionAsync(threadId, _host.UntieredModeId);

        var (after, presentedAs) = await ReadAfterSwitchAsync(threadId, _host.TieredModeId, "tiered");
        after.ToolsJson.Should().Be(before.ToolsJson, "the refused switch changed nothing the model sees");
        presentedAs.Should().Be(threadId, "the root still reads as its own gateway agent");
        _host.Logs.Count("Capabilities inert", "RootThreadId", threadId).Should().Be(0);
    }

    /// <summary>
    /// An untiered → tiered switch that the factory refuses leaves the live root untiered. The next request carries
    /// byte-identical tools, the root reads as the default gateway agent, no view is persisted for the mode that
    /// never committed, and the live root sends no tier PATCH.
    /// </summary>
    [Fact]
    public async Task RefusedSwitch_UntieredToTiered_LiveRootStaysUntiered()
    {
        var threadId = await _host.ProvisionAsync(_host.UntieredModeId);
        var before = await _host.FirstRequestAsync(threadId, _host.UntieredModeId);

        await SwitchRefusedDuringACompactionAsync(threadId, _host.TieredModeId);
        var patchesAtRefusal = _host.Gateway.Patches.Count(p => p.AgentId == threadId);

        var (after, presentedAs) = await ReadAfterSwitchAsync(threadId, _host.UntieredModeId, "untiered");
        after.ToolsJson.Should().Be(before.ToolsJson, "the refused switch changed nothing the model sees");
        presentedAs.Should().BeNull("an untiered root reads as the default gateway agent");
        (await _host.HasViewAsync(threadId)).Should().BeFalse("a mode that never committed persists no view");
        _host.Logs.Count("Capabilities active", "RootThreadId", threadId).Should().Be(0);
        _host
            .Gateway.Patches.Count(p => p.AgentId == threadId)
            .Should()
            .Be(patchesAtRefusal, "the live untiered root sends no tier PATCH");
    }

    /// <summary>
    /// F-013: an in-place switch that applies moves the live root's tiers with it, both ways. Tiered → untiered: the
    /// root reads as the default gateway agent and sends Workspace Agent's tools[]. Untiered → tiered: the root
    /// reads as its own gateway agent and lists Skill and SearchSkill. Both switches answer <c>warning: null</c>.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AppliedInPlaceSwitch_MovesTheLiveRootsTiers_BothDirections(bool fromTiered)
    {
        var (fromMode, toMode) = fromTiered
            ? (_host.TieredModeId, _host.UntieredModeId)
            : (_host.UntieredModeId, _host.TieredModeId);
        var threadId = await _host.ProvisionAsync(fromMode);
        _ = await _host.FirstRequestAsync(threadId, fromMode);

        (await SwitchAppliedInPlaceAsync(threadId, toMode)).Should().BeNull("the tiers took");

        var (after, presentedAs) = await ReadAfterSwitchAsync(threadId, toMode, fromTiered ? "untiered" : "tiered");
        if (fromTiered)
        {
            presentedAs.Should().BeNull("the untiered root reads as the default gateway agent");
            after.ToolsJson.Should().Be(_host.Reference.ToolsJson, "the untiered root sends Workspace Agent's tools[]");
        }
        else
        {
            presentedAs.Should().Be(threadId, "the tiered root reads as its own gateway agent");
            after.ToolNames.Should().Contain(["Skill", CapabilityTiers.SearchSkillToolName]);
        }
    }

    /// <summary>
    /// R8: a switch into a tiered mode whose tier PATCH fails still applies, and says so: the response warns that the
    /// tiers were not applied and will be retried, with no skill names, agent ids or gateway error text.
    /// </summary>
    [Fact]
    public async Task AppliedSwitch_TierPatchFails_ResponseWarns()
    {
        var threadId = await _host.ProvisionAsync(_host.UntieredModeId);
        _ = await _host.FirstRequestAsync(threadId, _host.UntieredModeId);
        _host.Gateway.FailPatchesFor(threadId);

        var warning = await SwitchAppliedInPlaceAsync(threadId, _host.TieredModeId);

        warning
            .Should()
            .Be("Skill tiers could not be applied to the sandbox; they will be retried at the next compaction.");
        _host
            .Gateway.Patches.Should()
            .Contain(p => p.AgentId == threadId, "guard: the switch attempted the root's PATCH");
    }

    /// <summary>
    /// R8b: a tiered setup that goes inert says why on the switch that built it, here with the sandbox catalog down. A
    /// mode switch into the tiered mode and a provider switch on a tiered conversation both rebuild through the
    /// factory, and both answer with the plain warning: no skill names, agent ids or gateway error text.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AppliedSwitch_CatalogUnavailable_ResponseWarns_ModeAndProviderSwitch(bool providerSwitch)
    {
        var fromMode = providerSwitch ? _host.TieredModeId : _host.UntieredModeId;
        var threadId = await _host.ProvisionAsync(fromMode);
        _ = await _host.FirstRequestAsync(threadId, fromMode);
        _host.Gateway.FailCatalogFor(_host.SessionOf(threadId));

        using var switched = providerSwitch
            ? await _host.SwitchProviderAsync(threadId, "test-anthropic")
            : await _host.SwitchModeAsync(threadId, _host.TieredModeId);
        var warning = await AppliedInPlaceWarningAsync(threadId, switched);

        warning
            .Should()
            .Be(
                "Skill tiers could not be set up for this mode (the sandbox catalog was unavailable); this conversation runs without tiers until the agent is rebuilt."
            );
        _host
            .Logs.Count("Capabilities apply failed", "ThreadId", threadId)
            .Should()
            .Be(1, "guard: the switch's build could not read the catalog");
    }

    /// <summary>
    /// A tiered root that used a skill rotates at its next checkpoint, and the next request lists the new set. This
    /// reaches three host seams: the build registers the tiers provider (Skill and SearchSkill on the first request),
    /// the sandbox listing hands the root's Skill to the tiers instance (so a rotation can swap its text), and the
    /// compaction hook reaches the tiers instance (so a checkpoint rotates at all).
    /// </summary>
    [Fact]
    public async Task Tiered_SkillUsedThenCheckpoint_RotatesTheRoot_NextRequestListsTheNewSet()
    {
        var threadId = await _host.ProvisionAsync(_host.TieredModeId);
        var first = await TurnAsync(threadId, SkillUsePrefix + threadId);
        first.ToolNames.Should().Contain(["Skill", CapabilityTiers.SearchSkillToolName], "the root is tiered");
        DescriptionOf(first, "Skill").Should().Be(SandboxToolsProvider.SkillListing("alpha:a", "alpha:p"));

        _host.ModelGate.AnswerRequestsContaining([SummaryMarker, SkillUsePrefix + threadId], SummaryJson);
        try
        {
            using var accepted = await _host.RequestCompactionAsync(threadId);
            accepted.StatusCode.Should().Be(HttpStatusCode.Accepted);

            // The compaction runs on the loop's own task, so this turn starts after it ends.
            var next = await TurnAsync(threadId, "plain after the checkpoint " + threadId);

            _host.Logs.Count("Capabilities rotated", "ThreadId", threadId).Should().Be(1);
            var patches = _host.Gateway.Patches.Where(p => p.AgentId == threadId).ToList();
            patches.Should().HaveCount(2, "the build's PATCH, then the rotation's");
            _host.Gateway.IncludedNames(patches[^1]).Should().Equal("alpha:p", "beta:c");
            DescriptionOf(next, "Skill")
                .Should()
                .Be(
                    SandboxToolsProvider.SkillListing("alpha:p", "beta:c"),
                    "the rotation swapped the root's Skill text"
                );
        }
        finally
        {
            _host.ModelGate.AnswerRequestsContaining(null);
        }
    }

    /// <summary>
    /// A tiered root's sub-agent gets its own tiers through the host's override: its own gateway agent, PATCHed with
    /// the mode's sub-agent set, and a Skill listing made as that agent. Its template lists Skill and Read only; the
    /// host's companion rule adds SearchSkill.
    /// </summary>
    [Fact]
    public async Task TieredChild_SpawnGetsItsOwnGatewayAgentAndListing_AndSearchSkill()
    {
        var threadId = await _host.ProvisionAsync(_host.TieredModeId);
        var before = _host.Llm.Count;

        _ = await TurnAsync(threadId, SpawnPrefix + threadId);

        var session = _host.SessionOf(threadId);
        var childPatch = _host
            .Gateway.Patches.Where(p =>
                p.SessionId == session && SandboxSubAgentIdentity.TryGetSubAgentId(p.AgentId, threadId, out _)
            )
            .Should()
            .ContainSingle("the child is its own gateway agent")
            .Subject;
        _host.Gateway.IncludedNames(childPatch).Should().Equal("beta:c");
        _host
            .Mcp.Calls.Should()
            .Contain(c => c.SessionId == session && c.Method == "tools/list" && c.AgentId == childPatch.AgentId);
        var child = _host
            .Llm.Skip(before)
            .Should()
            .ContainSingle(r => r.SystemPrompt.Contains(WorkerPrompt, StringComparison.Ordinal))
            .Subject;
        DescriptionOf(child, "Skill").Should().Be(SandboxToolsProvider.SkillListing("beta:c"));
        child.ToolNames.Should().Contain(CapabilityTiers.SearchSkillToolName);
    }

    /// <summary>
    /// With tools tiered, a sub-agent whose tools name <c>ExecuteTool</c> (here through <c>add_tools</c>) also gets
    /// <c>SearchTool</c>, as <c>Skill</c> brings <c>SearchSkill</c>: it runs its available tools through one and finds
    /// them with the other. A <c>remove_tools</c> naming <c>SearchTool</c> still wins.
    /// </summary>
    [Fact]
    public async Task ToolTieredChild_ExecuteToolBringsSearchTool_RemoveToolsStillWins()
    {
        var withSearch = await ToolTieredChildRequestAsync(ExecuteSpawnPrefix);
        var withoutSearch = await ToolTieredChildRequestAsync(ExecuteNoSearchSpawnPrefix);

        withSearch
            .ToolNames.Should()
            .Contain([CapabilityTiers.ExecuteToolToolName, CapabilityTiers.SearchToolToolName]);
        withoutSearch.ToolNames.Should().Contain(CapabilityTiers.ExecuteToolToolName);
        withoutSearch.ToolNames.Should().NotContain(CapabilityTiers.SearchToolToolName);
    }

    /// <summary>
    /// SC2: a tool-tiered root lists its loaded and pinned tools plus <c>SearchTool</c> and <c>ExecuteTool</c>, and no
    /// available tool. Its <c>ExecuteTool</c> call on an available tool runs that tool in the same run, in the sandbox
    /// as itself, and the UI gets the result as that tool's, under the call's own id. Program.cs is what hands the
    /// tiers' unwrap to the loop; without it the call would reach <c>ExecuteTool</c>'s own handler, which fails closed.
    /// </summary>
    [Fact]
    public async Task ToolTieredRoot_ExecuteToolRunsAnAvailableTool_InTheSameRun_ReportedAsThatTool()
    {
        _host.Reference.ToolNames.Should().Contain(AvailableTool, "guard: the sandbox serves the available tool");
        var threadId = await _host.ProvisionAsync(_host.ToolTieredModeId);

        await using var client = new WebSocketTestClient(await _host.ConnectAsync(threadId, _host.ToolTieredModeId));
        var before = _host.Llm.Count;
        await client.SendUserMessageAsync(ExecuteAvailablePrefix + threadId);
        using var frames = await client.CollectUntilDoneAsync(TimeSpan.FromSeconds(30));

        var requests = _host.RootRequestsSince(before).ToList();
        requests.Should().HaveCount(2, "one run: the ExecuteTool call, then the answer to its result");
        requests[0]
            .ToolNames.Should()
            .BeEquivalentTo(
                [
                    .. _host.Reference.ToolNames.Where(n => n != AvailableTool),
                    CapabilityTiers.SearchSkillToolName,
                    CapabilityTiers.SearchToolToolName,
                    CapabilityTiers.ExecuteToolToolName,
                ],
                "the root lists Workspace Agent's tools less the available one (Read is pinned), plus the searches "
                    + "and the runner"
            );

        var session = _host.SessionOf(threadId);
        _host
            .Mcp.Calls.Where(c => c.SessionId == session && c.Method == "tools/call")
            .Should()
            .Equal(
                [new McpCall(session, threadId, "tools/call", AvailableTool)],
                "the sandbox runs the available tool itself, for the root's gateway agent"
            );

        var wrapperCallId = frames
            .OfMessageType("tool_call_update", "tool_call", "tools_call")
            .Where(f =>
                f.TryGetProperty("function_name", out var name)
                && name.GetString() == CapabilityTiers.ExecuteToolToolName
            )
            .Select(f => f.GetProperty("tool_call_id").GetString())
            .Distinct()
            .Should()
            .ContainSingle("guard: the model called ExecuteTool once")
            .Subject;
        var result = frames.OfMessageType("tool_call_result").Should().ContainSingle().Subject;
        result.GetProperty("tool_name").GetString().Should().Be(AvailableTool, "the UI is told which tool ran");
        result.GetProperty("tool_call_id").GetString().Should().Be(wrapperCallId);
        result.GetProperty("result").GetString().Should().Be(AvailableTool + " ok");
        TextsOf(requests[1].MessagesJson)
            .Should()
            .Contain(AvailableTool + " ok", "the same run goes on with the tool's result");
    }

    /// <summary>
    /// SC7 (H1): a tool-tiered root lists none of a plugin's remote MCP tools, finds one with <c>SearchTool</c> and
    /// runs it through <c>ExecuteTool</c> in the same run. Every request reaches the server's own url with the headers
    /// of the egress key for its host (never the plugin's <c>${VAR}</c> credential header, never another host's key)
    /// plus the plugin's literal header. The server whose credential has no key is never contacted. No log line, model
    /// request or stored view holds a key value or a server url.
    /// </summary>
    [Fact]
    public async Task ToolTieredRoot_FindsAndRunsAPluginsRemoteMcpTool_WithTheTokenOfItsHostsEgressKey()
    {
        string threadId;
        List<LlmRequest> requests;
        List<(string? Tool, string? Result)> results;
        _host.Gateway.ServeMcpServersToNewSessions(RemoteRowsJson);
        try
        {
            threadId = await _host.ProvisionAsync(_host.ToolTieredModeId);
            await using var client = new WebSocketTestClient(
                await _host.ConnectAsync(threadId, _host.ToolTieredModeId)
            );
            var before = _host.Llm.Count;
            await client.SendUserMessageAsync(RemoteToolPrefix + threadId);
            using var frames = await client.CollectUntilDoneAsync(TimeSpan.FromSeconds(30));
            results =
            [
                .. frames
                    .OfMessageType("tool_call_result")
                    .Select(f => (f.GetProperty("tool_name").GetString(), f.GetProperty("result").GetString())),
            ];
            requests = [.. _host.RootRequestsSince(before)];
        }
        finally
        {
            _host.Gateway.ServeMcpServersToNewSessions(null);
        }

        requests.Should().HaveCount(3, "one run: SearchTool, then ExecuteTool, then the answer");
        requests
            .SelectMany(r => r.ToolNames)
            .Should()
            .NotContain(n => n.StartsWith("mcp", StringComparison.Ordinal), "a remote tool is available, never listed");
        results
            .Should()
            .ContainSingle(r => r.Tool == CapabilityTiers.SearchToolToolName)
            .Which.Result.Should()
            .Contain(RemoteTool, "SearchTool finds the plugin's remote tool");
        results
            .Should()
            .ContainSingle(r => r.Tool == RemoteTool, "the UI is told which tool ran")
            .Which.Result.Should()
            .Be(RemoteResult);
        TextsOf(requests[2].MessagesJson).Should().Contain(RemoteResult, "the same run goes on with the tool's result");

        var remote = _host.Remote.Requests;
        remote.Should().NotBeEmpty("guard: the build connected to the plugin's server");
        remote
            .Should()
            .AllSatisfy(r =>
            {
                r.Host.Should().Be("acme.test", "only the server with a key for its host is contacted");
                r.Path.Should().Be("/mcp");
                r.Authorization.Should().Be(AcmeKeyValue, "the egress key for acme.test:443 authenticates it");
                r.Trace.Should().Be("on", "a literal header is sent as authored");
                r.HeaderValues.Should()
                    .NotContain(v =>
                        v.Contains(OtherKeyValue, StringComparison.Ordinal)
                        || v.Contains("${", StringComparison.Ordinal)
                    );
            });
        remote
            .Where(r => r.Method == "tools/call")
            .Select(r => r.ToolName)
            .Should()
            .Equal([RemoteServerTool], "ExecuteTool ran the server's own tool once, by its own name");

        _host.Logs.Count("Remote MCP server {Marketplace}/{Plugin}/{Server} connected", "Server", "api").Should().Be(1);
        _host
            .Logs.Mentioning(
                "Remote MCP server {Marketplace}/{Plugin}/{Server} not connected",
                RemoteMcpServerPlan.NeedsEgressKey
            )
            .Should()
            .ContainSingle("the server whose credential has no key is named, with why");
        var view = await _host.ViewJsonAsync(threadId) ?? "";
        foreach (var secret in new[] { AcmeKeyValue, OtherKeyValue, "acme.test", "nokey.test", "${ACME_TOKEN}" })
        {
            _host.Logs.AnyContains(secret).Should().BeFalse("no log line holds a key value or a server url");
            requests
                .Should()
                .NotContain(
                    r => (r.SystemPrompt + r.ToolsJson + r.MessagesJson).Contains(secret, StringComparison.Ordinal),
                    "the model never sees a key value or a server url"
                );
            view.Should().NotContain(secret, "the stored view holds no row value");
        }
    }

    /// <summary>
    /// SC7 (H2): only a build that offers <c>ExecuteTool</c> reaches a plugin's remote MCP servers, so an untiered
    /// build and a skills-only one never ask the gateway for them. A tool-tiered build asks once.
    /// </summary>
    [Fact]
    public async Task UntieredAndSkillsOnlyBuilds_NeverFetchMcpServers()
    {
        foreach (var modeId in new[] { _host.UntieredModeId, _host.TieredModeId })
        {
            var threadId = await _host.ProvisionAsync(modeId);
            _ = await _host.FirstRequestAsync(threadId, modeId);

            _host
                .Gateway.McpServerFetches(_host.SessionOf(threadId))
                .Should()
                .Be(0, "mode {0} offers no ExecuteTool", modeId);
        }

        var toolTiered = await _host.ProvisionAsync(_host.ToolTieredModeId);
        _ = await _host.FirstRequestAsync(toolTiered, _host.ToolTieredModeId);
        _host
            .Gateway.McpServerFetches(_host.SessionOf(toolTiered))
            .Should()
            .Be(1, "guard: a tool-tiered build asks once");
    }

    /// <summary>
    /// T7 (SC8): a tool-tiered root lists only some tools, while Workspace Agent's prompt names more of them as
    /// mandatory. Its prompt is the reference prompt plus one fixed line that points at <c>SearchTool</c> and
    /// <c>ExecuteTool</c>. A skills-only root has no <c>ExecuteTool</c>, and its prompt is the reference prompt (the
    /// untiered one is pinned by <see cref="Untiered_ToolsAndPromptByteIdentical_ToWorkspaceAgent"/>).
    /// </summary>
    [Fact]
    public async Task ToolTieredRoot_PromptCarriesTheFixedExecuteToolLine_ASkillsOnlyRootsPromptDoesNot()
    {
        const string Line = CapabilityTiers.ExecuteToolPromptLine;
        var reference = _host.Reference.SystemPrompt.Replace(
            _host.ReferenceThread,
            "<thread>",
            StringComparison.Ordinal
        );
        reference.Should().NotContain(Line, "guard: Workspace Agent's own prompt");

        var (toolTiered, toolTieredTools) = await MaskedFirstRequestAsync(_host.ToolTieredModeId);
        toolTieredTools.Should().Contain(CapabilityTiers.ExecuteToolToolName, "guard: the root has ExecuteTool");
        Occurrences(toolTiered, Line).Should().Be(1, "the line is in the prompt once");
        toolTiered
            .Replace("\n\n" + Line, string.Empty, StringComparison.Ordinal)
            .Should()
            .Be(reference, "the line is the only change");

        var (skillsOnly, skillsOnlyTools) = await MaskedFirstRequestAsync(_host.TieredModeId);
        skillsOnlyTools.Should().NotContain(CapabilityTiers.ExecuteToolToolName, "guard: a skills-only root");
        skillsOnly.Should().Be(reference);

        async Task<(string Prompt, IReadOnlyList<string> Tools)> MaskedFirstRequestAsync(string modeId)
        {
            var threadId = await _host.ProvisionAsync(modeId);
            var request = await _host.FirstRequestAsync(threadId, modeId);
            return (request.SystemPrompt.Replace(threadId, "<thread>", StringComparison.Ordinal), request.ToolNames);
        }
    }

    /// <summary>
    /// A mode that tiers skills only, and one that names an <c>agent:</c> selector without <c>caps.agent</c>, both send
    /// Workspace Agent's <c>Agent</c> tool byte for byte: every template listed, no hint.
    /// </summary>
    [Fact]
    public async Task SkillsOnlyAndUncappedAgentSelectorModes_KeepTheUntieredAgentTool()
    {
        var expected = ToolJsonOf(_host.Reference, "Agent");

        foreach (var modeId in new[] { _host.TieredModeId, _host.UncappedAgentSelectorModeId })
        {
            var threadId = await _host.ProvisionAsync(modeId);
            var actual = await _host.FirstRequestAsync(threadId, modeId);

            actual
                .ToolNames.Should()
                .Contain(CapabilityTiers.SearchSkillToolName, "guard: mode {0} tiers skills", modeId);
            actual.ToolNames.Should().NotContain(CapabilityTiers.SearchAgentToolName);
            actual.ToolNames.Should().NotContain(CapabilityTiers.SearchPluginToolName);
            ToolJsonOf(actual, "Agent").Should().Be(expected, "mode {0} does not tier sub-agents", modeId);
        }
    }

    /// <summary>
    /// A skills-only root switched in place to a mode with <c>caps.agent</c> seeds the agent kind at that switch: the
    /// next request's <c>Agent</c> tool lists the mode's loaded template and the hint, and the root can search
    /// agents and plugins.
    /// </summary>
    [Fact]
    public async Task SkillsOnlyRoot_SwitchedInPlaceToAnAgentMode_ListsTheSeededTemplate()
    {
        var threadId = await _host.ProvisionAsync(_host.TieredModeId);
        var before = await _host.FirstRequestAsync(threadId, _host.TieredModeId);
        AgentTypeDescriptionOf(before).Should().StartWith("Which sub-agent to spawn. One of: ");

        (await SwitchAppliedInPlaceAsync(threadId, _host.AgentTieredModeId)).Should().BeNull("the tiers took");
        var after = await _host.FirstRequestAsync(threadId, _host.AgentTieredModeId);

        AgentTypeDescriptionOf(after)
            .Should()
            .Be($"Which sub-agent to spawn. Listed: {WorkerTemplate}. {CapabilityTiers.UnlistedAgentsHint}");
        after.ToolNames.Should().Contain([CapabilityTiers.SearchAgentToolName, CapabilityTiers.SearchPluginToolName]);
    }

    /// <summary>
    /// The root's agent searches answer from the root's own view, so a sub-agent never inherits them, even from a
    /// template that inherits every other tool.
    /// </summary>
    [Fact]
    public async Task AgentTieredRoot_SearchesAgentsAndPlugins_ItsChildInheritsNeither()
    {
        var threadId = await _host.ProvisionAsync(_host.AgentTieredModeId);
        var before = _host.Llm.Count;

        var root = await TurnAsync(threadId, SpawnOpenPrefix + threadId, _host.AgentTieredModeId);

        root.ToolNames.Should().Contain([CapabilityTiers.SearchAgentToolName, CapabilityTiers.SearchPluginToolName]);
        var child = _host
            .Llm.Skip(before)
            .Should()
            .ContainSingle(r => r.SystemPrompt.Contains(OpenWorkerPrompt, StringComparison.Ordinal))
            .Subject;
        child.ToolNames.Should().Contain("Read", "guard: the open template inherits the root's tools");
        child.ToolNames.Should().NotContain(CapabilityTiers.SearchAgentToolName);
        child.ToolNames.Should().NotContain(CapabilityTiers.SearchPluginToolName);
    }

    /// <summary>
    /// A workspace's <c>tiers.pinned</c>, set through <c>PUT /api/workspaces/{id}</c>, joins a tiered mode's root
    /// list; an explicit <c>null</c> clears it for the next build. An untiered mode on that workspace stays
    /// Workspace Agent's tools[] byte for byte: the workspace never switches tiers on.
    /// </summary>
    [Fact]
    public async Task WorkspacePinned_JoinsATieredRoot_NullClearsIt_AnUntieredModeIsUnchanged()
    {
        var workspaceId = await _host.CreateWorkspaceAsync();
        using (var pinned = await _host.UpdateWorkspaceAsync(workspaceId, """{"tiers":{"pinned":["skill:beta:c"]}}"""))
        {
            pinned.StatusCode.Should().Be(HttpStatusCode.OK);
            (await pinned.Content.ReadAsStringAsync()).Should().Contain("skill:beta:c", "the view shows the tiers");
        }

        var tiered = await _host.ProvisionAsync(_host.TieredModeId, workspaceId);
        var withPin = await _host.FirstRequestAsync(tiered, _host.TieredModeId);
        DescriptionOf(withPin, "Skill")
            .Should()
            .Be(
                SandboxToolsProvider.SkillListing("alpha:a", "alpha:p", "beta:c"),
                "the workspace pin joins the mode's loaded and pinned skills"
            );

        var untiered = await _host.ProvisionAsync(_host.UntieredModeId, workspaceId);
        (await _host.FirstRequestAsync(untiered, _host.UntieredModeId))
            .ToolsJson.Should()
            .Be(_host.Reference.ToolsJson, "an untiered mode ignores the workspace's tiers");
        _host.Logs.Mentioning("Capabilities", untiered).Should().BeEmpty();

        using (var cleared = await _host.UpdateWorkspaceAsync(workspaceId, """{"tiers":null}"""))
        {
            cleared.StatusCode.Should().Be(HttpStatusCode.OK);
            (await cleared.Content.ReadAsStringAsync()).Should().NotContain("skill:beta:c");
        }

        var after = await _host.ProvisionAsync(_host.TieredModeId, workspaceId);
        DescriptionOf(await _host.FirstRequestAsync(after, _host.TieredModeId), "Skill")
            .Should()
            .Be(SandboxToolsProvider.SkillListing("alpha:a", "alpha:p"), "null cleared the workspace pin");
    }

    /// <summary>One turn on its own connection (in the tiered mode unless named); the first root request it sent.</summary>
    private async Task<LlmRequest> TurnAsync(string threadId, string message, string? modeId = null)
    {
        await using var client = new WebSocketTestClient(
            await _host.ConnectAsync(threadId, modeId ?? _host.TieredModeId)
        );
        var before = _host.Llm.Count;
        await TakeTurnAsync(client, message);
        return _host.RootRequestsSince(before).First();
    }

    /// <summary>The first request of the one <see cref="WorkerTemplate"/> sub-agent a fresh tool-tiered root spawns.</summary>
    private async Task<LlmRequest> ToolTieredChildRequestAsync(string prefix)
    {
        var threadId = await _host.ProvisionAsync(_host.ToolTieredModeId);
        var before = _host.Llm.Count;
        _ = await TurnAsync(threadId, prefix + threadId, _host.ToolTieredModeId);
        return _host
            .Llm.Skip(before)
            .Should()
            .ContainSingle(r => r.SystemPrompt.Contains(WorkerPrompt, StringComparison.Ordinal))
            .Subject;
    }

    private static string? DescriptionOf(LlmRequest request, string tool)
    {
        using var doc = JsonDocument.Parse(request.ToolsJson);
        return doc
            .RootElement.EnumerateArray()
            .Where(t => t.GetProperty("name").GetString() == tool)
            .Select(t => t.GetProperty("description").GetString())
            .Single();
    }

    /// <summary>One tool's JSON exactly as the request carried it.</summary>
    private static string ToolJsonOf(LlmRequest request, string tool)
    {
        using var doc = JsonDocument.Parse(request.ToolsJson);
        return doc
            .RootElement.EnumerateArray()
            .Where(t => t.GetProperty("name").GetString() == tool)
            .Select(t => t.GetRawText())
            .Single();
    }

    /// <summary>The description of the <c>Agent</c> tool's <c>subagent_type</c> (Anthropic's <c>input_schema</c>).</summary>
    private static string? AgentTypeDescriptionOf(LlmRequest request)
    {
        using var doc = JsonDocument.Parse(ToolJsonOf(request, "Agent"));
        return doc
            .RootElement.GetProperty("input_schema")
            .GetProperty("properties")
            .GetProperty("subagent_type")
            .GetProperty("description")
            .GetString();
    }

    /// <summary>
    /// Holds an operator's manual compaction at its summary call, then switches mode. The pool's busy check does not
    /// see a manual compaction, so the factory builds the target mode (tiers included) and reaches
    /// <c>Reconfigure</c>, which refuses: 409. The compaction then finishes on the loop's own task, ahead of any
    /// later turn.
    /// </summary>
    private async Task SwitchRefusedDuringACompactionAsync(string threadId, string targetModeId)
    {
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var inSummary = _host.ModelGate.HoldNextContaining(SummaryMarker);
        try
        {
            using var accepted = await _host.RequestCompactionAsync(threadId);
            accepted
                .StatusCode.Should()
                .Be(
                    HttpStatusCode.Accepted,
                    "guard: the compaction must run; body was: {0}",
                    await accepted.Content.ReadAsStringAsync()
                );
            await inSummary.Entered.WaitAsync(bound.Token);

            using var refused = await _host.SwitchModeAsync(threadId, targetModeId).WaitAsync(bound.Token);
            refused.StatusCode.Should().Be(HttpStatusCode.Conflict, "the loop refuses a switch while it compacts");
        }
        finally
        {
            inSummary.Release();
        }

        _host
            .Logs.Count("Refusing to reconfigure thread {ThreadId}: a manual compaction", "ThreadId", threadId)
            .Should()
            .Be(1, "guard: refused by the live loop, after the factory built the target mode");
    }

    /// <summary>Switches an idle conversation, which the factory serves in place; the response's <c>warning</c>.</summary>
    private async Task<string?> SwitchAppliedInPlaceAsync(string threadId, string targetModeId)
    {
        using var switched = await _host.SwitchModeAsync(threadId, targetModeId);
        return await AppliedInPlaceWarningAsync(threadId, switched);
    }

    /// <summary>The <c>warning</c> of a mode or provider switch that must have applied, in place.</summary>
    private async Task<string?> AppliedInPlaceWarningAsync(string threadId, HttpResponseMessage switched)
    {
        var body = await switched.Content.ReadAsStringAsync();
        switched.StatusCode.Should().Be(HttpStatusCode.OK, "the switch must apply; body was: {0}", body);
        _host
            .Logs.Count("Reconfigured the live agent for thread {ThreadId} in place", "ThreadId", threadId)
            .Should()
            .Be(1, "guard: the switch was served in place");
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("warning").GetString();
    }

    /// <summary>
    /// One turn that makes one sandbox <c>Read</c>: the turn's first root request, and the gateway agent id the root
    /// presented on the read.
    /// </summary>
    private async Task<(LlmRequest First, string? PresentedAs)> ReadAfterSwitchAsync(
        string threadId,
        string modeId,
        string tag
    )
    {
        await using var client = new WebSocketTestClient(await _host.ConnectAsync(threadId, modeId));
        var before = _host.Llm.Count;
        await TakeTurnAsync(client, SwitchReadPrefix + tag);
        var session = _host.SessionOf(threadId);
        var read = _host
            .Mcp.Calls.Should()
            .ContainSingle(c => c.SessionId == session && c.Method == "tools/call" && c.ToolName == "Read")
            .Subject;
        return (_host.RootRequestsSince(before).First(), read.AgentId);
    }

    private async Task<DiscoveryRun> ReadThenDiscoverAsync(string threadId, string modeId, string tag)
    {
        await using var client = new WebSocketTestClient(await _host.ConnectAsync(threadId, modeId));
        var before = _host.Llm.Count;
        await TakeTurnAsync(client, ReadPrefix + tag);
        var session = _host.SessionOf(threadId);
        var discovery = _host.Discoveries.Should().ContainSingle(d => d.SessionId == session).Subject;
        // The run's requests: the one that asked for the read, then the one after the read's result.
        var requests = _host.RootRequestsSince(before).ToList();
        requests.Should().HaveCount(2, "guard: one read, then one answer");
        return new DiscoveryRun(discovery.Owner, discovery.Delivered, discovery.Marker, requests[1].MessagesJson);
    }

    /// <summary>
    /// A plain turn (it builds the conversation and its session), then a turn whose one read finds the demo skill and
    /// sub-agent: every root request of both turns, and the persisted view before and after the read turn.
    /// </summary>
    private async Task<CapabilityReadRun> ReadWhereCapabilitiesLiveAsync(string threadId, string modeId, string tag)
    {
        await using var client = new WebSocketTestClient(await _host.ConnectAsync(threadId, modeId));
        var before = _host.Llm.Count;
        await TakeTurnAsync(client, "plain " + threadId);
        _host.ReportCapabilitiesOn(_host.SessionOf(threadId));
        var viewBefore = await _host.ViewJsonAsync(threadId);
        await TakeTurnAsync(client, CapabilityReadPrefix + tag);
        var requests = _host.RootRequestsSince(before).ToList();
        requests.Should().HaveCount(3, "guard: one plain answer, one read, then one answer");
        return new CapabilityReadRun(requests, viewBefore, await _host.ViewJsonAsync(threadId));
    }

    /// <summary>Every string in a request's messages JSON (texts, tool results), in order.</summary>
    private static IReadOnlyList<string> TextsOf(string messagesJson)
    {
        var texts = new List<string>();
        using var doc = JsonDocument.Parse(messagesJson);
        Collect(doc.RootElement);
        return texts;

        void Collect(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.String:
                    texts.Add(element.GetString()!);
                    break;
                case JsonValueKind.Array:
                    foreach (var item in element.EnumerateArray())
                    {
                        Collect(item);
                    }

                    break;
                case JsonValueKind.Object:
                    foreach (var property in element.EnumerateObject())
                    {
                        Collect(property.Value);
                    }

                    break;
                case JsonValueKind.Undefined:
                case JsonValueKind.Number:
                case JsonValueKind.True:
                case JsonValueKind.False:
                case JsonValueKind.Null:
                default:
                    break;
            }
        }
    }

    private static int Occurrences(string text, string value)
    {
        var count = 0;
        for (
            var i = text.IndexOf(value, StringComparison.Ordinal);
            i >= 0;
            i = text.IndexOf(value, i + 1, StringComparison.Ordinal)
        )
        {
            count++;
        }

        return count;
    }

    private sealed record CapabilityReadRun(IReadOnlyList<LlmRequest> Requests, string? ViewBefore, string? ViewAfter);

    private static async Task TakeTurnAsync(WebSocketTestClient client, string message)
    {
        await client.SendUserMessageAsync(message);
        using var frames = await client.CollectUntilDoneAsync(TimeSpan.FromSeconds(30));
        frames.Should().NotBeEmpty("the turn must complete");
    }

    private sealed record DiscoveryRun(
        string? ReadPresentedAs,
        int Delivered,
        string Marker,
        string NextRequestMessages
    );

    /// <summary>One provider request as the model received it.</summary>
    public sealed record LlmRequest(
        string SystemPrompt,
        string ToolsJson,
        IReadOnlyList<string> ToolNames,
        string MessagesJson
    );

    /// <summary>One request to the sandbox MCP endpoint: its session, presented agent id and JSON-RPC call.</summary>
    public sealed record McpCall(string? SessionId, string? AgentId, string? Method, string? ToolName);

    /// <summary>
    /// One context file the fake gateway reported for a read: the owner it named (the read's presented agent id,
    /// "" for none), the marker it carried and what the host's injector answered.
    /// </summary>
    public sealed record Discovery(string SessionId, string? Owner, string Marker, int Delivered);

    /// <summary>One <c>PATCH …/skills</c>: the agent it targeted and the body sent.</summary>
    public sealed record SkillPatch(string SessionId, string AgentId, string Body);

    /// <summary>The booted host and every fake it talks to.</summary>
    public sealed class TiersHost : IAsyncLifetime
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "lmstreaming-tiers-" + Guid.NewGuid().ToString("N")
        );
        private readonly ConcurrentQueue<LlmRequest> _llm = new();
        private readonly ConcurrentQueue<Discovery> _discoveries = new();
        private readonly ConcurrentDictionary<string, byte> _capabilitySessions = new(StringComparer.Ordinal);
        private WebApplication? _mcpApp;
        private WebApplication? _remoteApp;
        private E2EWebAppFactory? _factory;
        private HttpClient? _http;
        private FileWorkspaceStore? _workspaces;
        private FileConversationStore? _conversations;
        private int _workspaceCount;

        public FakeGateway Gateway { get; } = new();

        public LogCapture Logs { get; } = new();

        public McpRecorder Mcp { get; private set; } = null!;

        /// <summary>SC7: every plugin MCP server url the host connects to reaches this one fake server.</summary>
        public RemoteMcpRecorder Remote { get; private set; } = null!;

        /// <summary>Sits in front of the scripted model; a test can hold one request there.</summary>
        public LlmGate ModelGate { get; private set; } = null!;

        public FileChatModeStore Modes { get; private set; } = null!;

        public IReadOnlyList<LlmRequest> Llm => [.. _llm];

        public IReadOnlyList<Discovery> Discoveries => [.. _discoveries];

        public IServiceProvider Services => _factory!.Services;

        /// <summary>
        /// Root requests captured after <paramref name="index"/>: the ones carrying the sandbox tools, less the
        /// worker's.
        /// </summary>
        public IEnumerable<LlmRequest> RootRequestsSince(int index) =>
            Llm.Skip(index)
                .Where(r =>
                    r.ToolNames.Contains("Read") && !r.SystemPrompt.Contains(WorkerPrompt, StringComparison.Ordinal)
                );

        /// <summary>A copy of Workspace Agent with one loaded and one pinned skill.</summary>
        public string TieredModeId { get; private set; } = null!;

        /// <summary>A copy of Workspace Agent that was never tiered.</summary>
        public string UntieredModeId { get; private set; } = null!;

        /// <summary>A copy of Workspace Agent that was tiered and then cleared (explicit null).</summary>
        public string ClearedModeId { get; private set; } = null!;

        /// <summary><see cref="TieredModeId"/>'s tiers plus <c>caps.agent</c>, loading <see cref="WorkerTemplate"/>.</summary>
        public string AgentTieredModeId { get; private set; } = null!;

        /// <summary><see cref="TieredModeId"/>'s tiers plus an <c>agent:</c> selector, without <c>caps.agent</c>.</summary>
        public string UncappedAgentSelectorModeId { get; private set; } = null!;

        /// <summary><see cref="TieredModeId"/>'s tiers plus <c>caps.tool</c>, pinning <c>Read</c>.</summary>
        public string ToolTieredModeId { get; private set; } = null!;

        /// <summary>A Workspace Agent conversation built at boot: the parity reference, and the host's warm-up.</summary>
        public string ReferenceThread { get; private set; } = null!;

        /// <summary>The first request <see cref="ReferenceThread"/> sent.</summary>
        public LlmRequest Reference { get; private set; } = null!;

        public async Task InitializeAsync()
        {
            Modes = new FileChatModeStore(Path.Combine(_root, "modes"));
            _workspaces = new FileWorkspaceStore(Path.Combine(_root, "workspaces"), "default-leaf");
            _conversations = new FileConversationStore(Path.Combine(_root, "conversations"));

            var tiers = new ModeTiers
            {
                Loaded = ["skill:alpha:a"],
                Pinned = ["skill:alpha:p"],
                SubAgent = new ModeSubAgentTiers { Loaded = ["skill:beta:c"] },
            };
            TieredModeId = (await CopyAsync("Tiers host tiered", tiers)).Id;
            UntieredModeId = (await Modes.CopyModeAsync(SystemChatModes.WorkspaceAgentModeId, "Tiers host plain")).Id;
            var cleared = await CopyAsync("Tiers host cleared", tiers);
            cleared = await Modes.UpdateModeAsync(
                cleared.Id,
                new ChatModeCreateUpdate
                {
                    Name = cleared.Name,
                    SystemPrompt = cleared.SystemPrompt,
                    Tiers = null,
                }
            );
            cleared.Tiers.Should().BeNull("guard: an explicit null clears the tiers");
            ClearedModeId = cleared.Id;
            AgentTieredModeId = (
                await CopyAsync(
                    "Tiers host agents",
                    tiers with
                    {
                        Loaded = ["skill:alpha:a", $"agent:{WorkerTemplate}"],
                        Caps = new ModeTierCaps { Agent = 2 },
                    }
                )
            ).Id;
            UncappedAgentSelectorModeId = (
                await CopyAsync(
                    "Tiers host uncapped agent selector",
                    tiers with
                    {
                        Loaded = ["skill:alpha:a", $"agent:{WorkerTemplate}"],
                    }
                )
            ).Id;

            ToolTieredModeId = (
                await CopyAsync(
                    "Tiers host tools",
                    tiers with
                    {
                        Pinned = ["skill:alpha:p", "tool:Read"],
                        Caps = new ModeTierCaps { Tool = 5 },
                    }
                )
            ).Id;

            var responder = ScriptedSseResponder
                .New()
                .ForRole(
                    "capture",
                    ctx =>
                    {
                        _llm.Enqueue(Capture(ctx));
                        return false;
                    }
                )
                .ForRole(
                    "read",
                    ctx =>
                        !ctx.HasToolResult
                        && (ctx.LatestUserMessage?.Contains(ReadPrefix, StringComparison.Ordinal) ?? false)
                )
                .Turn(t => t.ToolCall("Read", new { file_path = ReadPath }))
                .Turn(t => t.ToolCall("Read", new { file_path = ReadPath }))
                .ForRole(
                    "capability-read",
                    ctx =>
                        !ctx.HasToolResult
                        && (ctx.LatestUserMessage?.Contains(CapabilityReadPrefix, StringComparison.Ordinal) ?? false)
                )
                .Turn(t => t.ToolCall("Read", new { file_path = ReadPath }))
                .Turn(t => t.ToolCall("Read", new { file_path = ReadPath }))
                .ForRole(
                    "switch-read",
                    ctx =>
                        !ctx.HasToolResult
                        && (ctx.LatestUserMessage?.Contains(SwitchReadPrefix, StringComparison.Ordinal) ?? false)
                )
                // One turn per ReadAfterSwitchAsync in the class: two refused switches, two applied ones.
                .Turn(t => t.ToolCall("Read", new { file_path = ReadPath }))
                .Turn(t => t.ToolCall("Read", new { file_path = ReadPath }))
                .Turn(t => t.ToolCall("Read", new { file_path = ReadPath }))
                .Turn(t => t.ToolCall("Read", new { file_path = ReadPath }))
                .ForRole(
                    "skill-use",
                    ctx =>
                        !ctx.HasToolResult
                        && (ctx.LatestUserMessage?.Contains(SkillUsePrefix, StringComparison.Ordinal) ?? false)
                )
                .Turn(t => t.ToolCall("Skill", new { skill = "beta:c" }))
                .ForRole(
                    "spawn",
                    ctx =>
                        !ctx.HasToolResult
                        && (ctx.LatestUserMessage?.Contains(SpawnPrefix, StringComparison.Ordinal) ?? false)
                )
                // Collaboration is on in this host, so a spawn names its role and description.
                .Turn(t =>
                    t.ToolCall(
                        "Agent",
                        new
                        {
                            subagent_type = WorkerTemplate,
                            prompt = "check your skills",
                            role = "tiers worker",
                            description = "Checks which skills it lists.",
                        }
                    )
                )
                .ForRole(
                    "spawn-open",
                    ctx =>
                        !ctx.HasToolResult
                        && (ctx.LatestUserMessage?.Contains(SpawnOpenPrefix, StringComparison.Ordinal) ?? false)
                )
                .Turn(t =>
                    t.ToolCall(
                        "Agent",
                        new
                        {
                            subagent_type = OpenTemplate,
                            prompt = "list your tools",
                            role = "open worker",
                            description = "Lists the tools it inherited.",
                        }
                    )
                )
                .ForRole(
                    "spawn-execute",
                    ctx =>
                        !ctx.HasToolResult
                        && (ctx.LatestUserMessage?.Contains(ExecuteSpawnPrefix, StringComparison.Ordinal) ?? false)
                )
                .Turn(t =>
                    t.ToolCall(
                        "Agent",
                        new
                        {
                            subagent_type = WorkerTemplate,
                            prompt = "list your tools",
                            role = "tiers worker",
                            description = "Lists the tools it got.",
                            add_tools = "ExecuteTool",
                        }
                    )
                )
                .ForRole(
                    "spawn-execute-no-search",
                    ctx =>
                        !ctx.HasToolResult
                        && (
                            ctx.LatestUserMessage?.Contains(ExecuteNoSearchSpawnPrefix, StringComparison.Ordinal)
                            ?? false
                        )
                )
                .Turn(t =>
                    t.ToolCall(
                        "Agent",
                        new
                        {
                            subagent_type = WorkerTemplate,
                            prompt = "list your tools",
                            role = "tiers worker",
                            description = "Lists the tools it got.",
                            add_tools = "ExecuteTool",
                            remove_tools = "SearchTool",
                        }
                    )
                )
                .ForRole(
                    "execute-available",
                    ctx =>
                        !ctx.HasToolResult
                        && (ctx.LatestUserMessage?.Contains(ExecuteAvailablePrefix, StringComparison.Ordinal) ?? false)
                )
                .Turn(t =>
                    t.ToolCall(
                        CapabilityTiers.ExecuteToolToolName,
                        new { tool = AvailableTool, arguments = new { pattern = "*.md" } }
                    )
                )
                .ForRole(
                    "remote-search",
                    ctx =>
                        !ctx.HasToolResult
                        && (ctx.LatestUserMessage?.Contains(RemoteToolPrefix, StringComparison.Ordinal) ?? false)
                )
                .Turn(t => t.ToolCall(CapabilityTiers.SearchToolToolName, new { query = "acme lookup" }))
                // A tool result's request carries no user text: the user message is found in the body.
                .ForRole(
                    "remote-execute",
                    ctx =>
                        ctx.HasToolResult
                        && ctx.RequestBody.GetRawText().Contains(RemoteToolPrefix, StringComparison.Ordinal)
                        && !ctx.RequestBody.GetRawText().Contains(RemoteResult, StringComparison.Ordinal)
                )
                .Turn(t =>
                    t.ToolCall(
                        CapabilityTiers.ExecuteToolToolName,
                        new { tool = RemoteTool, arguments = new { query = "order-7" } }
                    )
                )
                .Build();
            ModelGate = new LlmGate(responder.AsAnthropicHandler());

            var gatewayOptions = new SandboxGatewayOptions
            {
                BaseUrl = "http://127.0.0.1:39919",
                WorkspaceBasePath = null,
                Workspace = "default-leaf",
            };
            var gatewayLifetime = new SandboxGatewayLifetime(
                gatewayOptions,
                NullLogger<SandboxGatewayLifetime>.Instance,
                new HttpClient(Gateway)
            );
            (_mcpApp, var mcpHandler) = await StartInMemoryMcpServerAsync(new SandboxToolsProvider());
            Mcp = new McpRecorder(mcpHandler);
            (_remoteApp, var remoteHandler) = await StartInMemoryMcpServerAsync(new RemoteToolsProvider());
            Remote = new RemoteMcpRecorder(remoteHandler);
            var keysDirectory = Path.Combine(_root, "keys");

            _factory = new E2EWebAppFactory(
                "test-anthropic",
                new ScriptedBuilder(
                    ModelGate,
                    subAgentFactory: (_, providerAgentFactory) =>
                        new SubAgentOptions
                        {
                            Templates = new Dictionary<string, SubAgentTemplate>(StringComparer.Ordinal)
                            {
                                [WorkerTemplate] = new SubAgentTemplate
                                {
                                    Name = "Tiers worker",
                                    SystemPrompt = WorkerPrompt,
                                    EnabledTools = ["Skill", "Read"],
                                    AgentFactory = providerAgentFactory,
                                },
                                [OpenTemplate] = new SubAgentTemplate
                                {
                                    Name = "Tiers open worker",
                                    SystemPrompt = OpenWorkerPrompt,
                                    AgentFactory = providerAgentFactory,
                                },
                            },
                        }
                ),
                settings: new Dictionary<string, string?>
                {
                    ["RemoteMcp:AllowedOrigins:0"] = "https://acme.test",
                    ["RemoteMcp:AllowedOrigins:1"] = "https://nokey.test",
                    ["ContextDiscovery:RouteToOpeningSubAgent"] = "true",
                    // A one-turn conversation has a legal cut, so a test can hold an operator's compaction open.
                    ["Compaction:MinTailTokens"] = "1",
                },
                configureServices: services =>
                {
                    services.RemoveAll<IChatModeStore>();
                    services.AddSingleton<IChatModeStore>(Modes);
                    services.RemoveAll<IWorkspaceStore>();
                    services.AddSingleton<IWorkspaceStore>(_workspaces);
                    services.RemoveAll<IConversationStore>();
                    services.AddSingleton<IConversationStore>(_conversations);
                    services.RemoveAll<IRunLedgerStore>();
                    services.AddSingleton<IRunLedgerStore>(_conversations);
                    services.RemoveAll<SandboxGatewayLifetime>();
                    services.AddSingleton(gatewayLifetime);
                    services.RemoveAll<SandboxSessionRegistry>();
                    services.AddSingleton(sp => new SandboxSessionRegistry(
                        gatewayLifetime,
                        gatewayOptions,
                        sp.GetRequiredService<ILogger<SandboxSessionRegistry>>(),
                        new HttpClient(Gateway),
                        new AuthOptions(),
                        new SessionSecretStore(Path.Combine(_root, "secrets"), NullLogger<SessionSecretStore>.Instance)
                    ));
                    services.RemoveAll<IMarketplaceCatalogClient>();
                    services.AddSingleton<IMarketplaceCatalogClient>(new EmptyMarketplaceCatalogClient());
                    services.AddKeyedSingleton<HttpMessageHandler>(Program.SandboxMcpTransportHandlerKey, Mcp);
                    services.AddKeyedSingleton<HttpMessageHandler>(Program.RemoteMcpTransportHandlerKey, Remote);
                    // The host's egress keys live in this run's own directory.
                    services.RemoveAll<PredefinedKeyRegistry>();
                    services.AddSingleton(sp => new PredefinedKeyRegistry(
                        keysDirectory,
                        new FileOAuthTokenStore(keysDirectory, NullLogger<FileOAuthTokenStore>.Instance),
                        new HttpClient(),
                        sp.GetRequiredService<ILoggerFactory>()
                    ));
                    // Program.cs's Serilog pipeline reads sinks from DI (ReadFrom.Services).
                    services.AddSingleton<ILogEventSink>(Logs);
                }
            );
            _http = _factory.CreateClient();
            var keys = Services.GetRequiredService<PredefinedKeyRegistry>();
            await keys.UpsertAsync(EgressKey("acme-key", "acme.test", AcmeKeyValue));
            await keys.UpsertAsync(EgressKey("other-key", "other.test", OtherKeyValue));
            Mcp.OnRead = async read =>
            {
                await ReportContextFileAsync(read);
                await ReportCapabilitiesAsync(read);
            };
            // The gateway lists Skill per agent: an agent it holds tiers for sees its included skills.
            Mcp.RewriteToolsList = (call, body) =>
                call is { SessionId: { } session, AgentId: { } agent }
                && Gateway.IncludedNames(session, agent) is { } names
                    ? body.Replace(
                        SandboxToolsProvider.SkillListing("alpha:a", "alpha:p"),
                        SandboxToolsProvider.SkillListing([.. names]),
                        StringComparison.Ordinal
                    )
                    : body;

            // The first build and turn on a fresh host pays its one-time start-up; the reference pays it here.
            ReferenceThread = await ProvisionAsync(SystemChatModes.WorkspaceAgentModeId);
            Reference = await FirstRequestAsync(ReferenceThread, SystemChatModes.WorkspaceAgentModeId);
            // So do the first tiered build, the first manual compaction and the first mode switch; a warm-up
            // conversation pays them here. Its compaction fails at the summary call (the model answers 400), as the
            // refused-switch tests' do; the turn after it starts once it ends.
            var warmUp = await ProvisionAsync(TieredModeId);
            _ = await FirstRequestAsync(warmUp, TieredModeId);
            using (var compaction = await RequestCompactionAsync(warmUp))
            {
                compaction.StatusCode.Should().Be(HttpStatusCode.Accepted, "the warm-up compaction must run");
            }

            _ = await FirstRequestAsync(warmUp, TieredModeId);
            using (var switched = await SwitchModeAsync(warmUp, UntieredModeId))
            {
                switched.StatusCode.Should().Be(HttpStatusCode.OK, "the warm-up switch must apply");
            }

            // Guard: the host's loggers reach the capture. The suite's zero-count log assertions prove nothing on a
            // host whose logging went silent at start-up.
            Logs.Count("Capabilities active", "RootThreadId", warmUp)
                .Should()
                .BePositive("guard: the warm-up's tiered build logs through the capture");
        }

        public async Task DisposeAsync()
        {
            _http?.Dispose();
            if (_factory is not null)
            {
                await _factory.DisposeAsync();
            }

            if (_mcpApp is not null)
            {
                await _mcpApp.DisposeAsync();
            }

            if (_remoteApp is not null)
            {
                await _remoteApp.DisposeAsync();
            }

            Mcp?.Dispose();
            Remote?.Dispose();
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort temp cleanup.
            }
        }

        /// <summary>A fresh conversation on the named workspace, else on its own (and so its own sandbox session).</summary>
        public async Task<string> ProvisionAsync(string modeId, string? workspaceId = null)
        {
            workspaceId ??= await CreateWorkspaceAsync();
            using var response = await _http!.PostAsJsonAsync(
                "/api/conversations",
                new
                {
                    workspaceId,
                    providerId = "test-anthropic",
                    modeId,
                }
            );
            var body = await response.Content.ReadAsStringAsync();
            response.StatusCode.Should().Be(HttpStatusCode.OK, "provision must succeed; body was: {0}", body);
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.GetProperty("threadId").GetString()!;
        }

        /// <summary>A fresh user workspace; returns its id.</summary>
        public async Task<string> CreateWorkspaceAsync() =>
            (
                await _workspaces!.CreateAsync(
                    new WorkspaceCreate { Name = $"tiers-host-{Interlocked.Increment(ref _workspaceCount)}" }
                )
            ).Id;

        /// <summary>A <c>PUT /api/workspaces/{id}</c> with the body as given.</summary>
        public Task<HttpResponseMessage> UpdateWorkspaceAsync(string workspaceId, string json) =>
            _http!.PutAsync(
                $"/api/workspaces/{workspaceId}",
                new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            );

        public Task<System.Net.WebSockets.WebSocket> ConnectAsync(string threadId, string modeId) =>
            _factory!.ConnectWebSocketAsync(threadId, modeId);

        public Task<HttpResponseMessage> SwitchModeAsync(string threadId, string modeId) =>
            _http!.PostAsJsonAsync($"/api/conversations/{threadId}/mode", new { modeId });

        public Task<HttpResponseMessage> SwitchProviderAsync(string threadId, string providerId) =>
            _http!.PostAsJsonAsync($"/api/conversations/{threadId}/provider", new { providerId });

        public Task<HttpResponseMessage> RequestCompactionAsync(string threadId) =>
            _http!.PostAsJsonAsync($"/api/conversations/{threadId}/compaction", new { });

        /// <summary>Takes one plain turn on a conversation and returns the first root request it sent.</summary>
        public async Task<LlmRequest> FirstRequestAsync(string threadId, string modeId)
        {
            await using var client = new WebSocketTestClient(await ConnectAsync(threadId, modeId));
            var before = _llm.Count;
            await TakeTurnAsync(client, "plain " + threadId);
            return RootRequestsSince(before).First();
        }

        /// <summary>The one sandbox session this conversation is registered on.</summary>
        public string SessionOf(string threadId)
        {
            var registry = Services.GetRequiredService<SandboxSessionRegistry>();
            return Gateway.Sessions.Should().ContainSingle(s => registry.GetThreads(s).Contains(threadId)).Subject;
        }

        /// <summary>SC6: from now on a read on <paramref name="sessionId"/> also finds the demo skill and sub-agent.</summary>
        public void ReportCapabilitiesOn(string sessionId) => _capabilitySessions[sessionId] = 0;

        /// <summary>The persisted capability view of a thread, as stored; null when it has none.</summary>
        public async Task<string?> ViewJsonAsync(string threadId)
        {
            var metadata = await _conversations!.LoadMetadataAsync(threadId);
            return metadata?.Properties?.TryGetValue(CapabilityTiers.ViewPropertyKey, out var raw) == true
                ? raw is JsonElement element
                    ? element.GetRawText()
                    : raw.ToString()
                : null;
        }

        public async Task<bool> HasViewAsync(string threadId)
        {
            var metadata = await _conversations!.LoadMetadataAsync(threadId);
            return metadata?.Properties?.ContainsKey(CapabilityTiers.ViewPropertyKey) ?? false;
        }

        /// <summary>
        /// The gateway's side of F6: a read under a directory with a CLAUDE.md delivers that file, while the read
        /// runs, to the agent id the read presented ("" when none), through the webhook's own entry point.
        /// </summary>
        private async Task ReportContextFileAsync(McpCall read)
        {
            var marker = $"DISCOVERY-MARKER-{Guid.NewGuid():N}";
            var delivered = await Services
                .GetRequiredService<ContextDiscoveryInjector>()
                .InjectAsync(
                    new ContextDiscoveryPayload
                    {
                        SessionId = read.SessionId,
                        Kind = "context_file",
                        Path = "/workspace/notes/CLAUDE.md",
                        Content = marker,
                        AgentId = read.AgentId ?? "",
                    },
                    CancellationToken.None
                );
            _discoveries.Enqueue(new Discovery(read.SessionId!, read.AgentId, marker, delivered));
        }

        /// <summary>
        /// SC6, the gateway's side: a read on a registered session walks up to a new workspace skill and sub-agent.
        /// The skill joins the session's catalog; then one batch for the read's agent id reaches the webhook's handler
        /// while the read runs, as the gateway posts it.
        /// </summary>
        private async Task ReportCapabilitiesAsync(McpCall read)
        {
            if (read.SessionId is not { } session || !_capabilitySessions.ContainsKey(session))
            {
                return;
            }

            Gateway.DiscoverWorkspaceSkill(session, "s6-demo", DemoSkillPath, DemoSkillDescription);
            // As the gateway does, the batch names a workspace sub-agent by its path only; its file is in the sandbox.
            Gateway.WriteWorkspaceFile(
                session,
                $"s6/.claude/agents/{DemoAgent}.md",
                $"---\nname: {DemoAgent}\ndescription: {DemoAgentDescription}\n---\nYou are the s6 demo agent.\n"
            );
            var handled = await Services
                .GetRequiredService<ContextDiscoveryBatchHandler>()
                .HandleAsync(
                    new ContextDiscoveryEnvelope
                    {
                        SessionId = session,
                        AgentId = read.AgentId ?? "",
                        Discoveries =
                        [
                            new ContextDiscoveryItem
                            {
                                Kind = "skill",
                                Name = "s6-demo",
                                Description = DemoSkillDescription,
                                Path = DemoSkillPath + "/SKILL.md",
                            },
                            new ContextDiscoveryItem
                            {
                                Kind = "subagent",
                                Name = DemoAgent,
                                Description = DemoAgentDescription,
                                Path = $"/workspace/s6/.claude/agents/{DemoAgent}.md",
                            },
                        ],
                    },
                    CancellationToken.None
                );
            handled.Should().BeTrue("guard: the batch is well formed");
        }

        private async Task<ChatMode> CopyAsync(string name, ModeTiers tiers)
        {
            var copy = await Modes.CopyModeAsync(SystemChatModes.WorkspaceAgentModeId, name);
            return await Modes.UpdateModeAsync(
                copy.Id,
                new ChatModeCreateUpdate
                {
                    Name = copy.Name,
                    SystemPrompt = copy.SystemPrompt,
                    Tiers = tiers,
                }
            );
        }

        /// <summary>A custom-headers egress key for <paramref name="host"/>:443 that sends <paramref name="authorization"/>.</summary>
        private static PredefinedKeyEntry EgressKey(string id, string host, string authorization) =>
            new()
            {
                Id = id,
                Host = host,
                Kind = PredefinedKeyKind.CustomHeaders,
                Headers = [new PredefinedHeader("Authorization", authorization)],
            };

        private static LlmRequest Capture(ScriptedRequestContext ctx) =>
            new(
                ctx.SystemPrompt,
                ctx.RequestBody.TryGetProperty("tools", out var tools) ? tools.GetRawText() : "",
                [.. ctx.Tools],
                ctx.RequestBody.TryGetProperty("messages", out var messages) ? messages.GetRawText() : ""
            );

        private static async Task<(WebApplication App, HttpMessageHandler Handler)> StartInMemoryMcpServerAsync(
            IFunctionProvider provider
        )
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            builder.Services.AddSingleton(provider);
            builder.Services.AddMcpServerFromFunctionProviders();

            var app = builder.Build();
            app.MapMcpFunctionProviders();
            await app.StartAsync();

            var testServer = (TestServer)app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>();
            return (app, testServer.CreateHandler());
        }
    }

    /// <summary>
    /// The gateway's lifecycle, env and skills surface. Every create mints a new session; the skills routes
    /// answer one fixed catalog (marketplace <c>mk</c>: <c>alpha:a</c>, <c>alpha:p</c>, <c>beta:c</c>) for the
    /// agent asked about and record each PATCH. Unmodelled routes fail closed.
    /// </summary>
    public sealed class FakeGateway : HttpMessageHandler
    {
        private readonly object _gate = new();
        private readonly List<string> _sessions = [];
        private readonly List<SkillPatch> _patches = [];
        private readonly HashSet<string> _failingPatchAgents = new(StringComparer.Ordinal);
        private readonly HashSet<string> _failingCatalogSessions = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<(string Name, string Path, string Description)>> _workspaceSkills =
            new(StringComparer.Ordinal);
        private readonly Dictionary<(string SessionId, string Path), string> _workspaceFiles = [];
        private readonly Dictionary<string, string> _mcpServers = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _mcpServerFetches = new(StringComparer.Ordinal);
        private string? _mcpServersForNewSessions;

        /// <summary>
        /// SC7: each session created from now on lists the plugin MCP servers <paramref name="rowsJson"/> (a JSON
        /// array); null stops. Every other session lists none.
        /// </summary>
        public void ServeMcpServersToNewSessions(string? rowsJson)
        {
            lock (_gate)
            {
                _mcpServersForNewSessions = rowsJson;
            }
        }

        /// <summary>How many times <paramref name="sessionId"/>'s plugin MCP servers were asked for.</summary>
        public int McpServerFetches(string sessionId)
        {
            lock (_gate)
            {
                return _mcpServerFetches.GetValueOrDefault(sessionId);
            }
        }

        /// <summary>From now on the files API of <paramref name="sessionId"/> serves <paramref name="text"/> at the
        /// workspace-relative <paramref name="path"/>.</summary>
        public void WriteWorkspaceFile(string sessionId, string path, string text)
        {
            lock (_gate)
            {
                _workspaceFiles[(sessionId, path)] = text;
            }
        }

        /// <summary>From now on the catalog of <paramref name="sessionId"/> lists this workspace skill, untiered.</summary>
        public void DiscoverWorkspaceSkill(string sessionId, string name, string path, string description)
        {
            lock (_gate)
            {
                if (!_workspaceSkills.TryGetValue(sessionId, out var skills))
                {
                    _workspaceSkills[sessionId] = skills = [];
                }

                if (!skills.Any(s => s.Path == path))
                {
                    skills.Add((name, path, description));
                }
            }
        }

        /// <summary>From now on, a PATCH for <paramref name="agentId"/> is recorded and answered with a 500.</summary>
        public void FailPatchesFor(string agentId)
        {
            lock (_gate)
            {
                _ = _failingPatchAgents.Add(agentId);
            }
        }

        /// <summary>From now on, a skills catalog GET on <paramref name="sessionId"/> is answered with a 500.</summary>
        public void FailCatalogFor(string sessionId)
        {
            lock (_gate)
            {
                _ = _failingCatalogSessions.Add(sessionId);
            }
        }

        public IReadOnlyList<string> Sessions
        {
            get
            {
                lock (_gate)
                {
                    return [.. _sessions];
                }
            }
        }

        public IReadOnlyList<SkillPatch> Patches
        {
            get
            {
                lock (_gate)
                {
                    return [.. _patches];
                }
            }
        }

        /// <summary>The skills <paramref name="agentId"/> lists on a session after its last PATCH; null before one.</summary>
        public IReadOnlyList<string>? IncludedNames(string sessionId, string agentId) =>
            Patches.LastOrDefault(p => p.SessionId == sessionId && p.AgentId == agentId) is { } last
                ? IncludedNames(last)
                : null;

        /// <summary>
        /// The skills a PATCH includes, as gateway names: <c>/marketplaces/mk/alpha/skills/a</c> is <c>alpha:a</c>.
        /// </summary>
        public IReadOnlyList<string> IncludedNames(SkillPatch patch)
        {
            using var doc = JsonDocument.Parse(patch.Body);
            return
            [
                .. doc
                    .RootElement.GetProperty("set")
                    .EnumerateObject()
                    .Where(e => e.Value.GetString() == "included")
                    .Select(e => e.Name.Split('/', StringSplitOptions.RemoveEmptyEntries))
                    .Select(parts => $"{parts[2]}:{parts[4]}")
                    .Order(StringComparer.Ordinal),
            ];
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            lock (_gate)
            {
                if (path.EndsWith("/health", StringComparison.Ordinal))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK);
                }

                if (request.Method == HttpMethod.Post && path.EndsWith("/api/v1/sandboxes", StringComparison.Ordinal))
                {
                    var created = "sess-" + Guid.NewGuid().ToString("N");
                    _sessions.Add(created);
                    if (_mcpServersForNewSessions is { } rows)
                    {
                        _mcpServers[created] = rows;
                    }

                    return SessionResponse(created);
                }

                foreach (var session in _sessions)
                {
                    var prefix = $"/api/v1/sandboxes/{session}";
                    if (path.EndsWith(prefix + "/skills", StringComparison.Ordinal))
                    {
                        var agent = AgentIdOf(request.RequestUri.Query);
                        var patch = request.Method == HttpMethod.Patch;
                        if (!patch && _failingCatalogSessions.Contains(session))
                        {
                            return new HttpResponseMessage(HttpStatusCode.InternalServerError)
                            {
                                Content = new StringContent(
                                    """{"error":"catalog store down"}""",
                                    System.Text.Encoding.UTF8,
                                    "application/json"
                                ),
                            };
                        }

                        if (patch)
                        {
                            _patches.Add(new SkillPatch(session, agent, body ?? ""));
                            if (_failingPatchAgents.Contains(agent))
                            {
                                return new HttpResponseMessage(HttpStatusCode.InternalServerError)
                                {
                                    Content = new StringContent(
                                        """{"error":"tier store down"}""",
                                        System.Text.Encoding.UTF8,
                                        "application/json"
                                    ),
                                };
                            }
                        }

                        return Json(Catalog(session, agent, patch, _workspaceSkills.GetValueOrDefault(session) ?? []));
                    }

                    if (path.EndsWith(prefix + "/env", StringComparison.Ordinal))
                    {
                        return Json(new JsonObject { ["env"] = new JsonObject() }.ToJsonString());
                    }

                    if (
                        request.Method == HttpMethod.Get
                        && path.EndsWith(prefix + "/mcp-servers", StringComparison.Ordinal)
                    )
                    {
                        _mcpServerFetches[session] = _mcpServerFetches.GetValueOrDefault(session) + 1;
                        return Json(
                            new JsonObject
                            {
                                ["session_id"] = session,
                                ["mcp_servers"] = JsonNode.Parse(_mcpServers.GetValueOrDefault(session) ?? "[]"),
                            }.ToJsonString()
                        );
                    }

                    if (request.Method == HttpMethod.Get && path.Contains(prefix + "/files/", StringComparison.Ordinal))
                    {
                        var file = Uri.UnescapeDataString(request.RequestUri.Query.TrimStart('?')["path=".Length..]);
                        return _workspaceFiles.TryGetValue((session, file), out var text)
                            ? new HttpResponseMessage(HttpStatusCode.OK)
                            {
                                Content = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(text)),
                            }
                            : new HttpResponseMessage(HttpStatusCode.NotFound)
                            {
                                Content = new StringContent(
                                    """{"error":"path_not_found","code":404,"error_code":"path_not_found","retryable":false}""",
                                    System.Text.Encoding.UTF8,
                                    "application/json"
                                ),
                            };
                    }

                    if (request.Method == HttpMethod.Get && path.EndsWith(prefix, StringComparison.Ordinal))
                    {
                        return SessionResponse(session);
                    }
                }
            }

            throw new HttpRequestException($"Unhandled fake gateway request: {request.Method} {path}");
        }

        private static string AgentIdOf(string query)
        {
            foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                if (pair.StartsWith("agent_id=", StringComparison.Ordinal))
                {
                    return Uri.UnescapeDataString(pair["agent_id=".Length..]);
                }
            }

            return "";
        }

        private static string Catalog(
            string sessionId,
            string agentId,
            bool patch,
            IReadOnlyList<(string Name, string Path, string Description)> workspaceSkills
        )
        {
            static JsonObject Skill(string plugin, string skill) =>
                new()
                {
                    ["name"] = $"{plugin}:{skill}",
                    ["path"] = $"/marketplaces/mk/{plugin}/skills/{skill}",
                    ["description"] = $"Use when the task needs {skill} from {plugin}.",
                    ["tier"] = "available",
                    ["tier_origin"] = "default",
                    ["loaded"] = false,
                };

            static JsonObject Plugin(string name, params JsonObject[] skills) =>
                new()
                {
                    ["name"] = name,
                    ["version"] = null,
                    ["description"] = $"The {name} plugin.",
                    ["skills"] = new JsonArray(skills),
                };

            var catalog = new JsonObject
            {
                ["session_id"] = sessionId,
                ["agent_id"] = agentId,
                ["default_tier"] = "available",
                ["counts"] = new JsonObject
                {
                    ["included"] = 0,
                    ["available"] = 3,
                    ["unavailable"] = 0,
                    ["loaded"] = 0,
                },
                ["marketplaces"] = new JsonArray(
                    new JsonObject
                    {
                        ["alias"] = "mk",
                        ["plugins"] = new JsonArray(
                            Plugin("alpha", Skill("alpha", "a"), Skill("alpha", "p")),
                            Plugin("beta", Skill("beta", "c"))
                        ),
                    }
                ),
                ["workspace"] = new JsonObject
                {
                    ["skills"] = new JsonArray([
                        .. workspaceSkills.Select(s => new JsonObject
                        {
                            ["name"] = s.Name,
                            ["path"] = s.Path,
                            ["description"] = s.Description,
                            ["tier"] = "included",
                            ["tier_origin"] = "default",
                            ["loaded"] = false,
                            ["notified"] = true,
                        }),
                    ]),
                },
            };
            if (patch)
            {
                catalog["tools_list_changed"] = true;
            }

            return catalog.ToJsonString();
        }

        private static HttpResponseMessage Json(string json) =>
            new(HttpStatusCode.OK) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };

        private static HttpResponseMessage SessionResponse(string sessionId) =>
            Json(
                new JsonObject
                {
                    ["session_id"] = sessionId,
                    ["container_id"] = "container-1",
                    ["volumes"] = new JsonObject
                    {
                        ["workspace"] = new JsonObject
                        {
                            ["container_path"] = "/workspace",
                            ["read_only"] = false,
                            ["id"] = 7,
                        },
                    },
                }.ToJsonString()
            );
    }

    /// <summary>Sits in front of the in-memory MCP server and records who called what, per session.</summary>
    public sealed class McpRecorder(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        private readonly ConcurrentQueue<McpCall> _calls = new();

        public IReadOnlyList<McpCall> Calls => [.. _calls];

        /// <summary>Runs for every <c>tools/call</c> of <c>Read</c> before it reaches the server, as the gateway would.</summary>
        public Func<McpCall, Task>? OnRead { get; set; }

        /// <summary>Rewrites each <c>tools/list</c> answer for the caller that asked; null leaves answers as served.</summary>
        public Func<McpCall, string, string>? RewriteToolsList { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            string? method = null;
            string? tool = null;
            if (request.Content is not null)
            {
                await request.Content.LoadIntoBufferAsync(cancellationToken).ConfigureAwait(false);
                var body = await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                (method, tool) = JsonRpcCallOf(body);
            }

            var call = new McpCall(
                HeaderOf(request, "X-Session-ID"),
                HeaderOf(request, SandboxAgentId.HeaderName),
                method,
                tool
            );
            _calls.Enqueue(call);
            if (method == "tools/call" && tool == "Read" && OnRead is { } onRead)
            {
                await onRead(call).ConfigureAwait(false);
            }

            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (method == "tools/list" && RewriteToolsList is { } rewrite)
            {
                var served = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var content = new StringContent(rewrite(call, served), System.Text.Encoding.UTF8);
                content.Headers.ContentType = response.Content.Headers.ContentType;
                response.Content = content;
            }

            return response;
        }

        private static string? HeaderOf(HttpRequestMessage request, string name) =>
            request.Headers.TryGetValues(name, out var values) ? values.Single() : null;

        internal static (string? Method, string? Tool) JsonRpcCallOf(string body)
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    return (null, null);
                }

                var method = root.TryGetProperty("method", out var m) ? m.GetString() : null;
                var tool =
                    root.TryGetProperty("params", out var p)
                    && p.ValueKind == JsonValueKind.Object
                    && p.TryGetProperty("name", out var n)
                        ? n.GetString()
                        : null;
                return (method, tool);
            }
            catch (JsonException)
            {
                return (null, null);
            }
        }
    }

    /// <summary>One request a plugin's remote MCP server got: where it was sent, its credential headers, every header value and its call.</summary>
    public sealed record RemoteRequest(
        string Host,
        string Path,
        string? Authorization,
        string? Trace,
        IReadOnlyList<string> HeaderValues,
        string? Method,
        string? ToolName
    );

    /// <summary>SC7: sits in front of the fake remote MCP server (every plugin url reaches it) and records each request.</summary>
    public sealed class RemoteMcpRecorder(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        private readonly ConcurrentQueue<RemoteRequest> _requests = new();

        public IReadOnlyList<RemoteRequest> Requests => [.. _requests];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            string? method = null;
            string? tool = null;
            if (request.Content is not null)
            {
                await request.Content.LoadIntoBufferAsync(cancellationToken).ConfigureAwait(false);
                (method, tool) = McpRecorder.JsonRpcCallOf(
                    await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)
                );
            }

            _requests.Enqueue(
                new RemoteRequest(
                    request.RequestUri!.Host,
                    request.RequestUri.AbsolutePath,
                    HeaderOf(request, "Authorization"),
                    HeaderOf(request, "X-Trace"),
                    [
                        .. request.Headers.SelectMany(h => h.Value),
                        .. request.Content?.Headers.SelectMany(h => h.Value) ?? [],
                    ],
                    method,
                    tool
                )
            );
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        private static string? HeaderOf(HttpRequestMessage request, string name) =>
            request.Headers.TryGetValues(name, out var values) ? string.Join(", ", values) : null;
    }

    /// <summary>Sits in front of the scripted model and can hold the next request whose body names a marker.</summary>
    public sealed class LlmGate(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        private readonly object _gate = new();
        private (string Marker, Hold Hold)? _armed;
        private (string[] Markers, string Text)? _answer;

        /// <summary>
        /// Answers every later request whose body holds all <paramref name="markers"/> here, with
        /// <paramref name="text"/> as one non-streaming Anthropic message (a summary call is not streamed). Null
        /// stops.
        /// </summary>
        public void AnswerRequestsContaining(string[]? markers, string text = "")
        {
            lock (_gate)
            {
                _answer = markers is null ? null : (markers, text);
            }
        }

        public Hold HoldNextContaining(string marker)
        {
            var hold = new Hold();
            lock (_gate)
            {
                _armed = (marker, hold);
            }

            return hold;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            Hold? hold = null;
            string? answerText = null;
            bool lookNow;
            lock (_gate)
            {
                lookNow = _armed is not null || _answer is not null;
            }

            if (request.Content is not null && lookNow)
            {
                await request.Content.LoadIntoBufferAsync(cancellationToken).ConfigureAwait(false);
                var body = await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                lock (_gate)
                {
                    if (_armed is { } armed && body.Contains(armed.Marker, StringComparison.Ordinal))
                    {
                        hold = armed.Hold;
                        _armed = null;
                    }

                    if (_answer is { } answer && answer.Markers.All(m => body.Contains(m, StringComparison.Ordinal)))
                    {
                        answerText = answer.Text;
                    }
                }
            }

            if (hold is not null)
            {
                await hold.PassAsync(cancellationToken).ConfigureAwait(false);
            }

            return answerText is null
                ? await base.SendAsync(request, cancellationToken).ConfigureAwait(false)
                : AnthropicMessage(answerText);
        }
    }

    private static HttpResponseMessage AnthropicMessage(string text) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                new JsonObject
                {
                    ["id"] = "msg_answered",
                    ["type"] = "message",
                    ["role"] = "assistant",
                    ["model"] = "claude-scripted",
                    ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
                    ["stop_reason"] = "end_turn",
                    ["stop_sequence"] = null,
                    ["usage"] = new JsonObject { ["input_tokens"] = 1, ["output_tokens"] = 1 },
                }.ToJsonString(),
                System.Text.Encoding.UTF8,
                "application/json"
            ),
        };

    /// <summary>One armed pause: the request it caught has <see cref="Entered"/> and waits for <see cref="Release"/>.</summary>
    public sealed class Hold
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => _entered.Task;

        public void Release() => _released.TrySetResult();

        internal async Task PassAsync(CancellationToken ct)
        {
            _entered.TrySetResult();
            await _released.Task.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>Every Serilog event the host writes: its template and its scalar properties.</summary>
    public sealed class LogCapture : ILogEventSink
    {
        private readonly ConcurrentQueue<(
            string Template,
            IReadOnlyDictionary<string, string?> Props,
            string Text
        )> _events = new();

        public void Emit(LogEvent logEvent) =>
            _events.Enqueue(
                (
                    logEvent.MessageTemplate.Text,
                    logEvent.Properties.ToDictionary(
                        p => p.Key,
                        p => p.Value is ScalarValue scalar ? scalar.Value?.ToString() : p.Value.ToString()
                    ),
                    logEvent.RenderMessage(System.Globalization.CultureInfo.InvariantCulture)
                        + "\n"
                        + logEvent.Exception
                )
            );

        /// <summary>True when any event's rendered message, exception or property holds <paramref name="text"/>.</summary>
        public bool AnyContains(string text) =>
            _events.Any(e =>
                e.Text.Contains(text, StringComparison.Ordinal)
                || e.Props.Values.Any(v => v?.Contains(text, StringComparison.Ordinal) == true)
            );

        /// <summary>Events whose template starts with <paramref name="templatePrefix"/> and whose property matches.</summary>
        public int Count(string templatePrefix, string property, string value) =>
            _events.Count(e =>
                e.Template.StartsWith(templatePrefix, StringComparison.Ordinal)
                && e.Props.TryGetValue(property, out var v)
                && v == value
            );

        /// <summary>Templates starting with <paramref name="templatePrefix"/> with any property equal to <paramref name="value"/>.</summary>
        public IReadOnlyList<string> Mentioning(string templatePrefix, string value) =>
            [
                .. _events
                    .Where(e =>
                        e.Template.StartsWith(templatePrefix, StringComparison.Ordinal)
                        && e.Props.Values.Any(v => v == value)
                    )
                    .Select(e => e.Template),
            ];
    }

    /// <summary>
    /// The sandbox MCP server's tools: a <c>Skill</c> to list, a <c>Read</c> to call and <see cref="AvailableTool"/>
    /// for a tool-tiered root to run through <c>ExecuteTool</c>.
    /// </summary>
    private sealed class SandboxToolsProvider : IFunctionProvider
    {
        /// <summary>The Skill description the gateway serves an agent that lists <paramref name="names"/>.</summary>
        public static string SkillListing(params string[] names) =>
            "Loads a skill. Skills: " + string.Join(", ", names);

        public string ProviderName => "fake-sandbox";

        public int Priority => 0;

        public IEnumerable<FunctionDescriptor> GetFunctions() =>
            [
                Tool("Skill", SkillListing("alpha:a", "alpha:p"), "skill"),
                Tool("Read", "Reads a file from the workspace.", "file_path"),
                Tool(AvailableTool, "Finds workspace files by pattern.", "pattern"),
            ];

        private FunctionDescriptor Tool(string name, string description, string parameter) =>
            new()
            {
                ProviderName = ProviderName,
                Contract = new FunctionContract
                {
                    Name = name,
                    Description = description,
                    Parameters =
                    [
                        new FunctionParameterContract
                        {
                            Name = parameter,
                            ParameterType = JsonSchemaObject.String(parameter),
                            IsRequired = true,
                        },
                    ],
                },
                Handler = (_, _, _) => Task.FromResult<ToolHandlerResult>(ToolHandlerResult.FromText(name + " ok")),
            };
    }

    /// <summary>SC7: the fake remote MCP server's one tool, <see cref="RemoteServerTool"/>.</summary>
    private sealed class RemoteToolsProvider : IFunctionProvider
    {
        public string ProviderName => "fake-remote";

        public int Priority => 0;

        public IEnumerable<FunctionDescriptor> GetFunctions() =>
            [
                new()
                {
                    ProviderName = ProviderName,
                    Contract = new FunctionContract
                    {
                        Name = RemoteServerTool,
                        Description = "Looks up an order in the acme ledger.",
                        Parameters =
                        [
                            new FunctionParameterContract
                            {
                                Name = "query",
                                ParameterType = JsonSchemaObject.String("query"),
                                IsRequired = true,
                            },
                        ],
                    },
                    Handler = (_, _, _) => Task.FromResult<ToolHandlerResult>(ToolHandlerResult.FromText(RemoteResult)),
                },
            ];
    }

    /// <summary>Empty, available marketplace catalog: compatible with a workspace that selects none.</summary>
    private sealed class EmptyMarketplaceCatalogClient : IMarketplaceCatalogClient
    {
        public Task<MarketplaceCatalog> GetCatalogAsync(
            IReadOnlyList<string>? marketplaces = null,
            CancellationToken ct = default
        ) => Task.FromResult(new MarketplaceCatalog(Selected: [], Marketplaces: []));
    }
}
