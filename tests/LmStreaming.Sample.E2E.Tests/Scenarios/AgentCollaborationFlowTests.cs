using System.Net;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;
using FluentAssertions;
using LmStreaming.Sample.E2E.Tests.Infrastructure;

namespace LmStreaming.Sample.E2E.Tests.Scenarios;

/// <summary>
/// End-to-end coverage of hierarchy-wide agent collaboration (#244) with the feature actually turned
/// on, against the real host: a real <c>SubAgentManager</c> per level, the real directory and ledger
/// the sample builds per conversation, the real HTTP projection, and a scripted provider standing in
/// for the model.
/// </summary>
/// <remarks>
/// <para>
/// Every other suite exercises collaboration with the pieces held apart — a directory in a unit test,
/// a tool contract in another, a host option bound in a third. Nothing ran the composed thing, and
/// <c>AgentCollaboration:Enabled</c> is <b>false</b> everywhere else in the repo, so the wiring that
/// only exists when it is true (the per-conversation root bundle, the widened tool surface, the
/// hierarchy projection, the transcript route) had no coverage at all. That gap is exactly where a
/// feature flag rots: each part is green, and the composition is never built.
/// </para>
/// <para>
/// <b>Why this shape is deterministic.</b> The scripted provider cannot read a runtime agent id, so
/// every tool call addresses agents by a name chosen at scripting time — except the root, whose agent
/// id IS the conversation's thread id (the sample deliberately reuses that identity), which the test
/// mints itself. And the spawn chain is SYNCHRONOUS: the parent blocks on the lead, the lead blocks on
/// the helper. The grandchild therefore provably exists while its ancestors are mid-call, without a
/// single sleep or a race against a background completion. The one background agent exists solely so
/// <c>WaitForAgents</c> has something real to block on.
/// </para>
/// <para>
/// The shape under test, top to bottom:
/// root (the conversation) → <c>lead</c> → <c>helper</c>, plus root → <c>notifier</c> in the
/// background. <c>helper</c> is at delegation depth 2, which is the whole point: pre-#244 the second
/// hop did not exist, and it only opens here because the root's bundle still has depth budget.
/// </para>
/// </remarks>
public sealed class AgentCollaborationFlowTests
{
    private const string LeadMarker = "You are the collaboration LEAD sub-agent";
    private const string HelperMarker = "You are the collaboration HELPER sub-agent";
    private const string NotifierMarker = "You are the collaboration NOTIFIER sub-agent";

    private const string HelperQuestion = "Which repo should I review first?";
    private const string HelperAnswer = "Helper finished its slice.";
    private const string LeadAnswer = "Lead finished, helper included.";
    private const string ParentAnswer = "All collaboration work is complete.";

    /// <summary>Turning the feature on is the entire premise, so it is configuration, not a mock.</summary>
    private static Dictionary<string, string?> CollaborationEnabled() =>
        new()
        {
            ["AgentCollaboration:Enabled"] = "true",
            // Two hops: root → lead → helper. The default of 1 would refuse the nested spawn.
            ["AgentCollaboration:MaxDelegationDepth"] = "2",
        };

    [Theory]
    [InlineData("test")]
    [InlineData("test-anthropic")]
    public async Task Collaboration_spawns_nests_messages_waits_and_publishes_the_hierarchy(string providerMode)
    {
        var threadId = $"collab-{providerMode}-{Guid.NewGuid():N}";

        var responder = ScriptedSseResponder
            .New()
            .ForRole("helper", ctx => ctx.SystemPromptContains(HelperMarker))
            // A grandchild addressing the ROOT is the claim collaboration makes and nesting alone
            // does not: these two are not parent and child, they are two nodes of one hierarchy,
            // and pre-#244 the helper could not have named the root at all.
            .Turn(t =>
                t.ToolCall(
                    "SendMessage",
                    new
                    {
                        target = threadId,
                        content = HelperQuestion,
                        msg_type = "question",
                    }
                )
            )
            .Turn(t => t.Text(HelperAnswer))
            .ForRole("lead", ctx => ctx.SystemPromptContains(LeadMarker))
            .Turn(t =>
                t.ToolCall(
                    "Agent",
                    new
                    {
                        subagent_type = "helper",
                        prompt = "review the second repo",
                        name = "helper",
                        role = "repo reviewer",
                        description = "Reviews one repository and reports findings back to the lead.",
                    }
                )
            )
            .Turn(t => t.Text(LeadAnswer))
            .ForRole("notifier", ctx => ctx.SystemPromptContains(NotifierMarker))
            .Turn(t => t.Text("Notifier done."))
            .ForRole("parent", ctx => ctx.SystemPromptContains("helpful assistant"))
            .Turn(t =>
                t.ToolCall(
                    "Agent",
                    new
                    {
                        subagent_type = "lead",
                        prompt = "run the migration review",
                        name = "lead",
                        role = "migration lead",
                        description = "Owns the auth migration review and delegates repositories.",
                    }
                )
            )
            .Turn(t =>
                t.ToolCall(
                    "Agent",
                    new
                    {
                        subagent_type = "notifier",
                        prompt = "post the summary",
                        name = "notifier",
                        role = "summary notifier",
                        description = "Posts the finished summary once the review lands.",
                        run_in_background = true,
                    }
                )
            )
            // WaitForAgents is the reason the notifier runs in the background: a blocking wait on a
            // synchronous child would be meaningless.
            .Turn(t =>
                t.ToolCall(
                    "WaitForAgents",
                    new
                    {
                        agent_ids = "notifier",
                        mode = "all",
                        timeout_seconds = 30,
                    }
                )
            )
            .Turn(t => t.ToolCall("CheckAgents", new { agent_ids = "lead, notifier" }))
            // GetAgents is hierarchy-wide: the helper is the lead's child, invisible to the root's
            // own manager, and must still be listed.
            .Turn(t => t.ToolCall("GetAgents", new { }))
            .Turn(t => t.Text(ParentAnswer))
            .Build();

        var builder = new ScriptedBuilder(
            responder,
            subAgentFactory: (_, providerAgentFactory) =>
                new SubAgentOptions
                {
                    Templates = new Dictionary<string, SubAgentTemplate>
                    {
                        ["lead"] = Template("Lead", LeadMarker, providerAgentFactory),
                        ["helper"] = Template("Helper", HelperMarker, providerAgentFactory),
                        ["notifier"] = Template("Notifier", NotifierMarker, providerAgentFactory),
                    },
                    MaxConcurrentSubAgents = 5,
                }
        );

        using var factory = new E2EWebAppFactory(providerMode, builder, CollaborationEnabled());

        var socket = await factory.ConnectWebSocketAsync(threadId);
        await using var client = new WebSocketTestClient(socket);

        await client.SendUserMessageAsync("run the collaboration");
        using var frames = await client.CollectUntilDoneAsync(TimeSpan.FromSeconds(60));

        // --- What the model was able to do ------------------------------------------------------
        var toolCalls = frames.ToolCallNames();
        toolCalls.Should().Contain("Agent");
        toolCalls
            .Should()
            .Contain("WaitForAgents", "the collaboration tool surface replaces CheckAgent with the plural tools");
        toolCalls.Should().Contain("CheckAgents");
        toolCalls.Should().Contain("GetAgents");

        frames.ConcatText().Should().Contain(ParentAnswer);

        // Both nested runs really ran: the lead's answer only exists because the helper's synchronous
        // spawn returned, and the helper's answer only exists because its SendMessage was accepted.
        var toolResults = frames.ToolCallResults();
        toolResults
            .Should()
            .Contain(
                r => r.Contains(LeadAnswer, StringComparison.Ordinal),
                "the lead's synchronous spawn returns its final answer to the root"
            );
        toolResults
            .Should()
            .Contain(
                r => r.Contains(HelperAnswer, StringComparison.Ordinal),
                "the helper is a SECOND delegation hop, which only exists under collaboration"
            );

        // The hierarchy roster the root asked for names the grandchild it never spawned, by the role
        // and description its own parent published for it.
        var roster = toolResults.FirstOrDefault(r => r.Contains("repo reviewer", StringComparison.Ordinal));
        roster
            .Should()
            .NotBeNull("GetAgents lists the whole collaboration, including agents owned by a manager further down");
        roster.Should().Contain("migration lead");

        responder.RemainingTurns["parent"].Should().Be(0);
        responder.RemainingTurns["lead"].Should().Be(0);
        responder.RemainingTurns["helper"].Should().Be(0);
        responder.RemainingTurns["notifier"].Should().Be(0);

        // --- What the human's client can read over HTTP ------------------------------------------
        using var http = factory.CreateClient();
        var rows = await ListAgentsAsync(http, threadId);

        var lead = Row(rows, "lead");
        var helper = Row(rows, "helper");

        lead.GetProperty("collaborationId")
            .GetString()
            .Should()
            .Be(threadId, "the conversation's own id is the collaboration id, so a reload rejoins it");
        lead.GetProperty("role").GetString().Should().Be("migration lead");
        helper.GetProperty("role").GetString().Should().Be("repo reviewer");
        helper.GetProperty("description").GetString().Should().Contain("Reviews one repository");

        helper
            .GetProperty("parentAgentId")
            .GetString()
            .Should()
            .Be(
                lead.GetProperty("agentId").GetString(),
                "the helper hangs off the lead, not off the conversation that never spawned it"
            );
        helper.GetProperty("delegationDepth").GetInt32().Should().Be(2);
        lead.GetProperty("delegationDepth").GetInt32().Should().Be(1);

        var ancestors = helper.GetProperty("ancestorAgentIds").EnumerateArray().Select(a => a.GetString()).ToList();
        ancestors
            .Should()
            .Contain(threadId, "the root must appear in the grandchild's lineage for an ancestor read to resolve");

        // --- The transcript boundary --------------------------------------------------------------
        // The root reads the grandchild it never spawned. Under the default Ancestors visibility this
        // is allowed precisely because of the lineage asserted above, so the two are one claim.
        var helperId = helper.GetProperty("agentId").GetString()!;
        using var transcript = await http.GetAsync($"/api/conversations/{threadId}/agents/{helperId}/transcript");
        transcript.StatusCode.Should().Be(HttpStatusCode.OK, "collaboration is enabled and the reader is an ancestor");

        var transcriptBody = await transcript.Content.ReadAsStringAsync();
        transcriptBody
            .Should()
            .Contain(
                HelperAnswer,
                "the transcript is the agent's real persisted history; body was: {0}",
                transcriptBody
            );

        // --- The grandchild's message actually reached the root ------------------------------------
        // Everything above would still pass if the SendMessage were silently dropped: the helper's next
        // turn runs either way, and a refused send returns a receipt just like an accepted one. So the
        // message is checked where it lands — in the ROOT's own conversation, addressed by an agent two
        // levels down that the root never spawned.
        //
        // Note this reads the persisted transcript, NOT the live socket, and that is a limitation of the
        // product rather than of the test: MultiTurnAgentLoop.PublishIfNotifyAsync publishes only
        // NotifyMessage, so an inbound AgentMessage is added to history and persisted but never streamed.
        // The human therefore sees the run it triggers, with the question that caused it invisible until
        // a reload. Asserting the live frame here is left out deliberately — see the accompanying report.
        var agentMessage = await WaitForAgentMessageAsync(http, threadId, TimeSpan.FromSeconds(30));

        var envelope = agentMessage.GetProperty("messageJson").GetString()!;
        envelope.Should().Contain(HelperQuestion, "the delivered message must carry the grandchild's own words");
        envelope
            .Should()
            .Contain(
                "\"from_name\":\"helper\"",
                "delivery is attributed to the sender itself, not to the parent that relayed it"
            );
    }

    /// <summary>
    /// Polls the root conversation until its persisted history contains an <c>AgentMessage</c>.
    /// Condition-based, not time-based: delivery is dispatched on a background task and the recipient
    /// only drains its input channel between runs, so the arrival cannot be pinned to any instant —
    /// but it can be waited FOR. Each attempt is a real awaited HTTP round-trip; <paramref name="timeout"/>
    /// is a safety net, not a sleep.
    /// </summary>
    private static async Task<JsonElement> WaitForAgentMessageAsync(HttpClient http, string threadId, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        var lastBody = "<none>";

        while (!cts.IsCancellationRequested)
        {
            using var response = await http.GetAsync($"/api/conversations/{threadId}/messages", cts.Token);
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            lastBody = await response.Content.ReadAsStringAsync(cts.Token);
            using var doc = JsonDocument.Parse(lastBody);

            foreach (var message in doc.RootElement.EnumerateArray())
            {
                if (message.TryGetProperty("messageType", out var type) && type.GetString() == "AgentMessage")
                {
                    return message.Clone();
                }
            }

            await Task.Yield();
        }

        throw new TimeoutException(
            $"No AgentMessage reached the root conversation within {timeout}. " + $"Last messages body: {lastBody}"
        );
    }

    private static SubAgentTemplate Template(
        string name,
        string systemPrompt,
        Func<IStreamingAgent> providerAgentFactory
    ) =>
        new()
        {
            Name = name,
            SystemPrompt = systemPrompt,
            AgentFactory = providerAgentFactory,
            MaxTurnsPerRun = 5,
        };

    /// <summary>The row for a named agent, failing with the whole roster when it is missing.</summary>
    private static JsonElement Row(IReadOnlyList<JsonElement> rows, string name)
    {
        var row = rows.FirstOrDefault(r =>
            r.TryGetProperty("name", out var n) && string.Equals(n.GetString(), name, StringComparison.Ordinal)
        );

        row.ValueKind.Should()
            .NotBe(
                JsonValueKind.Undefined,
                "'{0}' must appear in the hierarchy listing; it contained: {1}",
                name,
                string.Join(", ", rows.Select(r => r.ToString()))
            );

        return row;
    }

    /// <summary>
    /// Reads the hierarchy projection once. The run is already finished when this is called — the
    /// <c>done</c> sentinel arrived — so there is nothing to poll for and nothing to wait on.
    /// </summary>
    private static async Task<IReadOnlyList<JsonElement>> ListAgentsAsync(HttpClient http, string threadId)
    {
        using var response = await http.GetAsync($"/api/conversations/{threadId}/subagents");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        return [.. doc.RootElement.EnumerateArray().Select(e => e.Clone())];
    }
}
