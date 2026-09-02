using System.Globalization;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;
using FluentAssertions;
using LmStreaming.Sample.Browser.E2E.Tests.Infrastructure;

namespace LmStreaming.Sample.Browser.E2E.Tests.Scenarios;

/// <summary>
/// Collaboration (#244) as the HUMAN sees it: a real browser, a real host with
/// <c>AgentCollaboration:Enabled</c> actually on, a two-hop hierarchy the model builds itself, and the
/// three things the client is supposed to do with it — render the tree, name a grandchild it never
/// spawned, and refuse a machine caller that goes after a raw agent thread.
/// </summary>
/// <remarks>
/// <para>
/// The vitest suite already covers <c>SubAgentListPanel</c> against hand-written props, which proves
/// the component's mapping and nothing about where those props come from. Everything between the
/// directory and the DOM — the hierarchy projection, the depth arithmetic, the JSON contract, the
/// panel's poll — was only ever exercised in halves. This scenario runs the whole path once.
/// </para>
/// <para>
/// <b>Deterministic by construction.</b> The scripted provider cannot read a runtime agent id, so the
/// grandchild addresses the root by the NAME the sample registers it under (<c>conversation</c>) rather
/// than by an id no script could know. The spawn chain is synchronous — the root blocks on the lead,
/// the lead blocks on the helper — so the tree provably exists by the time the stream goes idle, with
/// no sleep and no race against a background completion.
/// </para>
/// <para>
/// <b>What is deliberately NOT asserted live.</b> The grandchild's message reaches the human's
/// conversation, but it is never STREAMED there: <c>MultiTurnAgentLoop.PublishIfNotifyAsync</c>
/// publishes only <c>NotifyMessage</c>, so an inbound <c>AgentMessage</c> is added to history and
/// persisted while the socket says nothing. The pill is therefore asserted after a reload, which is
/// exactly what a human would have to do — a product limitation this test pins rather than papers over.
/// </para>
/// </remarks>
[Collection(PlaywrightCollection.Name)]
public sealed class SubAgentHierarchyRenderTests
{
    private const string LeadMarker = "You are the collaboration LEAD sub-agent";
    private const string HelperMarker = "You are the collaboration HELPER sub-agent";

    private const string LeadRole = "migration lead";
    private const string HelperRole = "repo reviewer";
    private const string HelperDescription = "Reviews one repository and reports findings back to the lead.";

    private const string HelperQuestion = "Which repo should I review first?";
    private const string HelperAnswer = "Helper finished its slice.";
    private const string LeadAnswer = "Lead finished, helper included.";
    private const string ParentAnswer = "All collaboration work is complete.";

    /// <summary>The name <c>Program.cs</c> registers the conversation root under.</summary>
    private const string RootName = "conversation";

    private readonly PlaywrightFixture _fixture;

    public SubAgentHierarchyRenderTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>Turning the feature on is the premise of the scenario, so it is configuration, not a mock.</summary>
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
    public async Task Hierarchy_renders_its_depths_and_refuses_a_machine_read_of_a_raw_thread(string providerMode)
    {
        var responder = ScriptedSseResponder
            .New()
            .ForRole("helper", ctx => ctx.SystemPromptContains(HelperMarker))
            // Addressing the root BY NAME is what makes this scriptable at all, and it is also the
            // claim collaboration makes that plain nesting does not: these two are not parent and
            // child, they are two members of one hierarchy.
            .Turn(t =>
                t.ToolCall(
                    "SendMessage",
                    new
                    {
                        target = RootName,
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
                        role = HelperRole,
                        description = HelperDescription,
                    }
                )
            )
            .Turn(t => t.Text(LeadAnswer))
            .ForRole("parent", ctx => ctx.SystemPromptContains("helpful assistant"))
            .Turn(t =>
                t.ToolCall(
                    "Agent",
                    new
                    {
                        subagent_type = "lead",
                        prompt = "run the migration review",
                        name = "lead",
                        role = LeadRole,
                        description = "Owns the auth migration review and delegates repositories.",
                    }
                )
            )
            .Turn(t => t.Text(ParentAnswer))
            .Build();

        await using var session = await _fixture.OpenAsync(
            providerMode,
            responder.HandlerFor(providerMode),
            subAgentFactory: (_, providerAgentFactory) =>
                new SubAgentOptions
                {
                    Templates = new Dictionary<string, SubAgentTemplate>
                    {
                        ["lead"] = Template("Lead", LeadMarker, providerAgentFactory),
                        ["helper"] = Template("Helper", HelperMarker, providerAgentFactory),
                    },
                    MaxConcurrentSubAgents = 5,
                },
            settings: CollaborationEnabled()
        );
        var page = session.Page;

        await page.SendMessageAsync("run the collaboration");
        await page.WaitForStreamIdleAsync(timeoutMs: 60_000);
        await page.AssistantText().WaitForTextContainsAsync(ParentAnswer, timeoutMs: 30_000);

        // --- The tree the human sees ------------------------------------------------------------
        await page.GetByTestId("subagent-panel-toggle").ClickAsync();
        // Two rows is the whole point: the panel polls a HIERARCHY-wide listing, so it must show the
        // helper even though this conversation's own manager never spawned it and cannot see it.
        await page.GetByTestId("subagent-item").WaitForCountAtLeastAsync(2, timeoutMs: 30_000);

        var rows = await ReadPanelRowsAsync(page);
        var lead = Row(rows, "lead");
        var helper = Row(rows, "helper");

        lead.StructuralDepth.Should().Be(1);
        lead.DelegationDepth.Should().Be(1);
        helper.StructuralDepth.Should().Be(2);
        helper
            .DelegationDepth.Should()
            .Be(2, "the helper is a SECOND delegation hop, which only exists under collaboration");

        lead.Badge.Should().Be("· L1/D1");
        helper.Badge.Should().Be("· L2/D2");
        helper
            .PaddingLeft.Should()
            .BeGreaterThan(
                lead.PaddingLeft,
                "a deeper agent is indented further, which is what makes the tree readable"
            );

        // A row for an agent this conversation does not own has no task of its own, so the panel shows
        // the ROLE its parent published for it — the only place a human ever reads that text.
        helper.Task.Should().Contain(HelperRole);

        // Every row is readable here (the human reads as the root, and the root is an ancestor of
        // everything), so no row may claim otherwise or show the lock.
        rows.Should().OnlyContain(row => row.Readable == "true");
        (await page.GetByTestId("subagent-transcript-locked").CountAsync()).Should().Be(0);

        // The sidebar only lists what it loaded at startup, so the thread this session just created is
        // read from the listing the client itself uses. Agent-owned threads are excluded from it by
        // design, which is why the single entry is unambiguously the human's conversation.
        var threadId = await page.EvaluateAsync<string>(
            """
            async () => {
              const list = await (await fetch('/api/conversations?limit=50')).json();
              return list.length === 1 ? list[0].threadId : '';
            }
            """
        );
        threadId.Should().NotBeEmpty("exactly one non-agent conversation exists in this session");

        // The helper's own transcript thread: subagent-{scope}-{agentId}, scoped to the human's
        // conversation (#705) — the raw id a bypass would have to name.
        var helperThreadId = SubAgentThreadIds.For(threadId, helper.AgentId);

        // --- The raw-thread guard, from the client that would be used to bypass it -----------------
        var refusal = await FetchAsync(page, $"/api/conversations/{helperThreadId}/messages?viewer={helper.AgentId}");

        refusal
            .Status.Should()
            .Be(
                403,
                "a caller naming the agent it reads as is a machine, and raw agent threads are closed to machines"
            );
        refusal
            .Body.Should()
            .NotContainAny(
                [HelperRole, HelperDescription, HelperAnswer, HelperQuestion],
                "a refusal must not disclose the name, task, or content of what it refuses"
            );

        // Control: the SAME url without the machine identity keeps its legacy behaviour, which proves
        // the refusal above is about who asked rather than about the route being broken.
        var legacy = await FetchAsync(page, $"/api/conversations/{helperThreadId}/messages");
        legacy.Status.Should().Be(200);

        // --- The grandchild's message, in the human's own conversation -----------------------------
        // Persisted, never streamed (see the class remarks), so it is waited FOR over HTTP and then
        // read off the reloaded DOM.
        await WaitForPersistedAgentMessageAsync(page, threadId, TimeSpan.FromSeconds(30));
        await page.ReloadAsync();
        await page.Textarea().WaitForAsync();

        var agentPills = page.Locator("[data-testid='notification-pill'][data-notify-kind='agent-message']");
        await agentPills.WaitForCountAtLeastAsync(1, timeoutMs: 30_000);

        var pillText = await agentPills.First.InnerTextAsync();
        pillText.Should().Contain("Agent asked", "the pill names what the sender was doing");
        pillText
            .Should()
            .Contain("helper", "delivery is attributed to the grandchild itself, not to the parent that relayed it");

        await session.SaveSuccessScreenshotAsync($"SubAgentHierarchyRender.Depths_and_raw_thread_guard_{providerMode}");
    }

    /// <summary>One rendered sub-agent row, as the browser actually laid it out.</summary>
    /// <remarks>
    /// The depths are kept as the RAW attribute text and parsed on demand: a row that omitted one
    /// (collaboration off, or a pre-#244 persisted row) must fail saying the attribute was missing,
    /// not throw out of the extraction before any assertion has run.
    /// </remarks>
    private sealed record PanelRow(
        string AgentId,
        string Name,
        string Task,
        string Badge,
        string Structural,
        string Delegation,
        string Readable,
        double PaddingLeft
    )
    {
        public int StructuralDepth => Depth(Structural, nameof(Structural));

        public int DelegationDepth => Depth(Delegation, nameof(Delegation));

        private int Depth(string raw, string which) =>
            int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var depth)
                ? depth
                : throw new InvalidOperationException($"Row '{Name}' published no usable {which} depth (was '{raw}').");
    }

    /// <summary>
    /// Extracts every rendered row in ONE round-trip, including the computed indent — which only the
    /// browser can answer, and which is the whole reason this assertion lives here rather than in vitest.
    /// </summary>
    private static async Task<IReadOnlyList<PanelRow>> ReadPanelRowsAsync(IPage page)
    {
        var json = await page.EvaluateAsync<string>(
            """
            () => JSON.stringify(
              Array.from(document.querySelectorAll('[data-testid="subagent-item"]')).map(li => {
                const row = li.querySelector('[data-testid="subagent-focus-button"]');
                return {
                  agentId: li.getAttribute('data-agent-id') ?? '',
                  name: li.querySelector('.subagent-name')?.textContent?.trim() ?? '',
                  task: li.querySelector('.subagent-task')?.textContent?.trim() ?? '',
                  badge: li.querySelector('[data-testid="subagent-depth"]')?.textContent?.trim() ?? '',
                  structural: li.getAttribute('data-structural-depth') ?? '',
                  delegation: li.getAttribute('data-delegation-depth') ?? '',
                  readable: li.getAttribute('data-transcript-readable') ?? '',
                  paddingLeft: row ? parseFloat(getComputedStyle(row).paddingLeft) : 0,
                };
              }))
            """
        );

        using var doc = JsonDocument.Parse(json);
        return
        [
            .. doc
                .RootElement.EnumerateArray()
                .Select(e => new PanelRow(
                    e.GetProperty("agentId").GetString()!,
                    e.GetProperty("name").GetString()!,
                    e.GetProperty("task").GetString()!,
                    e.GetProperty("badge").GetString()!,
                    e.GetProperty("structural").GetString()!,
                    e.GetProperty("delegation").GetString()!,
                    e.GetProperty("readable").GetString()!,
                    e.GetProperty("paddingLeft").GetDouble()
                )),
        ];
    }

    /// <summary>The row whose rendered name starts with <paramref name="name"/>, failing with all of them.</summary>
    private static PanelRow Row(IReadOnlyList<PanelRow> rows, string name)
    {
        var row = rows.FirstOrDefault(r => r.Name.StartsWith(name, StringComparison.Ordinal));

        row.Should()
            .NotBeNull(
                "'{0}' must be rendered; the panel showed: {1}",
                name,
                string.Join(" | ", rows.Select(r => $"{r.Name}/{r.Structural}/{r.Delegation}"))
            );

        return row!;
    }

    private sealed record FetchResult(int Status, string Body);

    /// <summary>Issues a same-origin request from the PAGE, so the guard sees a real browser request.</summary>
    private static async Task<FetchResult> FetchAsync(IPage page, string url)
    {
        var json = await page.EvaluateAsync<string>(
            """
            async (url) => {
              const response = await fetch(url);
              return JSON.stringify({ status: response.status, body: await response.text() });
            }
            """,
            url
        );

        using var doc = JsonDocument.Parse(json);
        return new FetchResult(
            doc.RootElement.GetProperty("status").GetInt32(),
            doc.RootElement.GetProperty("body").GetString()!
        );
    }

    /// <summary>
    /// Waits until the root conversation's persisted history contains an <c>AgentMessage</c>. Delivery
    /// is dispatched on a background task and the recipient only drains its inbox between runs, so the
    /// arrival cannot be pinned to an instant — but it can be waited FOR. Each attempt is a real awaited
    /// request; the timeout is a safety net, not a sleep.
    /// </summary>
    private static async Task WaitForPersistedAgentMessageAsync(IPage page, string threadId, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        var last = "<none>";

        while (!cts.IsCancellationRequested)
        {
            var result = await FetchAsync(page, $"/api/conversations/{threadId}/messages");
            last = result.Body;
            if (last.Contains("\"AgentMessage\"", StringComparison.Ordinal))
            {
                return;
            }

            await Task.Yield();
        }

        throw new TimeoutException(
            $"No AgentMessage reached the root conversation within {timeout}. Last body: {last}"
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
}
