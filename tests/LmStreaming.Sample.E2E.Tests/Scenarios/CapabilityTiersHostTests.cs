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
using LmStreaming.Sample.E2E.Tests.Infrastructure;
using LmStreaming.Sample.Models;
using LmStreaming.Sample.Persistence;
using LmStreaming.Sample.Services;
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
/// are the gateway's HTTP surface, its MCP endpoint and the LLM.
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

    /// <summary>The host's one sub-agent template: its tools list names Skill and Read, not SearchSkill.</summary>
    private const string WorkerTemplate = "tiers-worker";
    private const string WorkerPrompt = "You are the tiers worker.";
    private const string ReadPath = "/workspace/notes/sub/data.txt";
    private const string ReadResult = "Read ok";

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
                "Skill tiers could not be set up for this mode (the sandbox catalog was unavailable); this conversation runs without tiers until its next mode switch."
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

    /// <summary>One turn on its own connection; the first root request it sent.</summary>
    private async Task<LlmRequest> TurnAsync(string threadId, string message)
    {
        await using var client = new WebSocketTestClient(await _host.ConnectAsync(threadId, _host.TieredModeId));
        var before = _host.Llm.Count;
        await TakeTurnAsync(client, message);
        return _host.RootRequestsSince(before).First();
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
        private WebApplication? _mcpApp;
        private E2EWebAppFactory? _factory;
        private HttpClient? _http;
        private FileWorkspaceStore? _workspaces;
        private FileConversationStore? _conversations;
        private int _workspaceCount;

        public FakeGateway Gateway { get; } = new();

        public LogCapture Logs { get; } = new();

        public McpRecorder Mcp { get; private set; } = null!;

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
                            },
                        }
                ),
                settings: new Dictionary<string, string?>
                {
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
                    // Program.cs's Serilog pipeline reads sinks from DI (ReadFrom.Services).
                    services.AddSingleton<ILogEventSink>(Logs);
                }
            );
            _http = _factory.CreateClient();
            Mcp.OnRead = ReportContextFileAsync;
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

            Mcp?.Dispose();
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort temp cleanup.
            }
        }

        /// <summary>A fresh conversation on its own workspace (and so its own sandbox session).</summary>
        public async Task<string> ProvisionAsync(string modeId)
        {
            var workspace = await _workspaces!.CreateAsync(
                new WorkspaceCreate { Name = $"tiers-host-{Interlocked.Increment(ref _workspaceCount)}" }
            );
            using var response = await _http!.PostAsJsonAsync(
                "/api/conversations",
                new
                {
                    workspaceId = workspace.Id,
                    providerId = "test-anthropic",
                    modeId,
                }
            );
            var body = await response.Content.ReadAsStringAsync();
            response.StatusCode.Should().Be(HttpStatusCode.OK, "provision must succeed; body was: {0}", body);
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.GetProperty("threadId").GetString()!;
        }

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

                        return Json(Catalog(session, agent, patch));
                    }

                    if (path.EndsWith(prefix + "/env", StringComparison.Ordinal))
                    {
                        return Json(new JsonObject { ["env"] = new JsonObject() }.ToJsonString());
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

        private static string Catalog(string sessionId, string agentId, bool patch)
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
                ["workspace"] = new JsonObject { ["skills"] = new JsonArray() },
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
                        ["workspace"] = new JsonObject { ["container_path"] = "/workspace", ["read_only"] = false },
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

        private static (string? Method, string? Tool) JsonRpcCallOf(string body)
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
        private readonly ConcurrentQueue<(string Template, IReadOnlyDictionary<string, string?> Props)> _events = new();

        public void Emit(LogEvent logEvent) =>
            _events.Enqueue(
                (
                    logEvent.MessageTemplate.Text,
                    logEvent.Properties.ToDictionary(
                        p => p.Key,
                        p => p.Value is ScalarValue scalar ? scalar.Value?.ToString() : p.Value.ToString()
                    )
                )
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

    /// <summary>The sandbox MCP server's tools: a <c>Skill</c> to list and a <c>Read</c> to call.</summary>
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

    /// <summary>Empty, available marketplace catalog: compatible with a workspace that selects none.</summary>
    private sealed class EmptyMarketplaceCatalogClient : IMarketplaceCatalogClient
    {
        public Task<MarketplaceCatalog> GetCatalogAsync(
            IReadOnlyList<string>? marketplaces = null,
            CancellationToken ct = default
        ) => Task.FromResult(new MarketplaceCatalog(Selected: [], Marketplaces: []));
    }
}
