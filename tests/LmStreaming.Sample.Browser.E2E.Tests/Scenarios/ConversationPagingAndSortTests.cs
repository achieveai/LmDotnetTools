using System.Collections.Immutable;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;
using FluentAssertions;
using LmStreaming.Sample.Browser.E2E.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace LmStreaming.Sample.Browser.E2E.Tests.Scenarios;

/// <summary>
/// Browser regression for the conversation sidebar's paging and ordering — the "my old conversations
/// are being deleted every day" defect.
///
/// <para>
/// <b>What broke.</b> <c>ConversationsController.List</c> asked the store for a page of N threads
/// (Skip/Take over <c>LastUpdated</c> descending) and only THEN dropped agent-owned
/// (<c>subagent-*</c> / <c>workflow-*</c>) rows from that already-trimmed page. Filtering after Take
/// returns a page short by however many rows the filter removed. Because <c>LastUpdated</c> is bumped
/// on every completed run and background sub-agent runs are constant, agent-owned threads crowd the
/// entire FRONT of a last-used ordering: on a live store of 302 threads a 50-row page came back with
/// 45 agent-owned rows and the sidebar rendered five real conversations. Nothing had been deleted —
/// everything older was simply unreachable, with no signal that a page had been trimmed.
/// </para>
///
/// <para>
/// <b>Why a browser test.</b> The controller-level suite can prove the endpoint returns a full page,
/// but the user-visible failure is a SIDEBAR that stops at "today". Reaching an older conversation
/// needs the infinite-scroll paging, the single-flight guard and the sort-mode reset in
/// <c>useConversations.ts</c> to work together with the endpoint; happy-dom computes no layout, so a
/// scroll-driven pager cannot be exercised there at all.
/// </para>
///
/// <para>
/// <b>Why the seed is shaped the way it is.</b> Two properties carry every assertion here, and both
/// are load-bearing:
/// <list type="number">
///   <item>
///     Agent-owned threads ALONE outnumber a page (<see cref="SubAgentThreadCount"/> +
///     <see cref="WorkflowThreadCount"/> = 66 &gt; <see cref="PageSize"/>), and every one of them is
///     newer than every real conversation. Without that, the pre-fix filter-after-Take would still
///     have returned a full page and the first-page assertion would pass against the very bug it
///     exists to catch.
///   </item>
///   <item>
///     Creation order and last-used order DISAGREE — real conversation N is created at
///     <c>BASE_CREATED + N*step</c> but last used at <c>BASE_USED + (46-N)*step</c>, so the two
///     orderings are exact reverses. Were they to agree, the two sort modes would be
///     indistinguishable and every ordering assertion below would pass vacuously.
///   </item>
/// </list>
/// </para>
/// </summary>
[Collection(PlaywrightCollection.Name)]
public sealed class ConversationPagingAndSortTests
{
    /// <summary>Client page size (<c>CONVERSATIONS_PAGE_SIZE</c>) and the endpoint's default limit.</summary>
    private const int PageSize = 30;

    /// <summary>
    /// Real (human-started) conversations seeded. Deliberately more than one page and less than two,
    /// so paging must run exactly twice and the second page is SHORT — which is also the client's
    /// only exhaustion signal (the endpoint returns a bare array with no <c>hasMore</c>).
    /// </summary>
    private const int RealConversationCount = 45;

    /// <summary>Sub-agent-owned threads seeded. See the class remarks: 60 + 6 must exceed <see cref="PageSize"/>.</summary>
    private const int SubAgentThreadCount = 60;

    /// <summary>Workflow-controller-owned threads seeded.</summary>
    private const int WorkflowThreadCount = 6;

    /// <summary>Spacing between seeded timestamps. One minute is far larger than any clock skew.</summary>
    private const long StepMs = 60_000;

    /// <summary>2024-01-01T00:00:00Z. One step before the creation instant of the oldest real conversation.</summary>
    private const long BaseCreatedMs = 1_704_067_200_000;

    /// <summary>Base for real last-used timestamps — comfortably after every seeded creation instant.</summary>
    private const long BaseLastUsedMs = BaseCreatedMs + (100 * StepMs);

    /// <summary>
    /// Base for agent-owned last-used timestamps. Far beyond every real one, so agent-owned rows
    /// occupy the entire front of a last-used ordering — the production shape that made the
    /// filter-after-Take defect catastrophic.
    /// </summary>
    private const long AgentOwnedBaseLastUsedMs = BaseCreatedMs + (300 * StepMs);

    /// <summary>
    /// Pinned viewport. The sidebar list must OVERFLOW its container or the scroll handler never
    /// fires and the paging assertions would be testing nothing; 30 rows at ~59px each is ~1770px
    /// against a ~700px list container, so the overflow is not marginal.
    /// </summary>
    private const int ViewportWidth = 1280;

    private const int ViewportHeight = 800;

    private readonly PlaywrightFixture _fixture;

    public ConversationPagingAndSortTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// THE regression. The first page the sidebar renders must be a full page of REAL conversations,
    /// even though 66 agent-owned threads sit ahead of every one of them in the last-used ordering.
    /// </summary>
    /// <remarks>
    /// Goes red the moment the exclusion moves back after Skip/Take: the store's first 30 rows would
    /// then all be agent-owned and the controller would hand the sidebar an EMPTY page.
    /// </remarks>
    [Fact]
    public async Task First_page_is_full_of_real_conversations_though_agent_owned_threads_dominate_last_used()
    {
        // Guard the premise itself: if a later edit shrinks the agent-owned seed below a page, the
        // buggy code would return a full page too and this test would silently stop proving anything.
        (SubAgentThreadCount + WorkflowThreadCount)
            .Should()
            .BeGreaterThan(
                PageSize,
                "agent-owned threads must alone exceed one page, or filter-after-Take would still have returned a full page");

        var (session, _) = await OpenSeededSidebarAsync();
        await using var scope = session;
        var page = session.Page;

        await page.WaitForConversationCountAsync(PageSize);

        var titles = await page.ConversationTitlesAsync();
        titles
            .Should()
            .Equal(
                ExpectedLastUsedTitles().Take(PageSize),
                "the first page must be the 30 most recently used REAL conversations, in order — the store excludes agent-owned rows BEFORE it takes the page");

        await session.SaveSuccessScreenshotAsync(
            "ConversationPaging.first_page_is_full_of_real_conversations");
    }

    /// <summary>
    /// No agent-owned thread may reach the sidebar, on any page — the sub-agent panel is where those
    /// belong. Pages the list to exhaustion so the claim covers every row, not just the first page.
    /// </summary>
    [Fact]
    public async Task Agent_owned_threads_never_appear_in_the_sidebar()
    {
        var (session, _) = await OpenSeededSidebarAsync();
        await using var scope = session;
        var page = session.Page;

        await page.WaitForConversationCountAsync(PageSize);
        await page.LoadMoreConversationsAsync(RealConversationCount);
        await page.WaitForConversationCountAsync(RealConversationCount);

        var threadIds = await page.ConversationThreadIdsAsync();
        threadIds
            .Should()
            .OnlyContain(
                id => !id.StartsWith("subagent-", StringComparison.Ordinal)
                    && !id.StartsWith("workflow-", StringComparison.Ordinal),
                "agent-owned threads are surfaced only through the sub-agent panel");
        threadIds
            .Should()
            .HaveCount(
                RealConversationCount,
                "the exhausted list must hold every seeded real conversation — proving the check above swept all of them");
    }

    /// <summary>
    /// Scrolling loads the next page; every seeded conversation is reachable across the two pages and
    /// no row is rendered twice.
    /// </summary>
    [Fact]
    public async Task Scrolling_reaches_every_conversation_across_pages_without_duplicates()
    {
        var (session, listRequests) = await OpenSeededSidebarAsync();
        await using var scope = session;
        var page = session.Page;

        await page.WaitForConversationCountAsync(PageSize);
        await page.LoadMoreConversationsAsync(RealConversationCount);
        await page.WaitForConversationCountAsync(RealConversationCount);

        var threadIds = await page.ConversationThreadIdsAsync();
        threadIds.Should().OnlyHaveUniqueItems("offset paging must not render a conversation twice");
        threadIds
            .Should()
            .BeEquivalentTo(
                Enumerable.Range(1, RealConversationCount).Select(RealThreadId),
                "every seeded conversation must be reachable by scrolling — none may be stranded behind the page boundary");

        var titles = await page.ConversationTitlesAsync();
        titles
            .Should()
            .Equal(ExpectedLastUsedTitles(), "the paged list must stay in last-used order across the page boundary");

        listRequests
            .Snapshot()
            .Should()
            .HaveCount(2, "45 rows at a page size of 30 is exactly two page requests");
    }

    /// <summary>
    /// The "Loading more..." affordance must clear once the list is exhausted, and a further scroll
    /// must not ask for a page that cannot exist.
    /// </summary>
    /// <remarks>
    /// The DOM affordance alone is a weak claim — it is transient, so "not present" is also what a
    /// pager that never ran looks like. The request log is the real assertion: after the short second
    /// page proved exhaustion, scrolling again must issue NO third request.
    /// </remarks>
    [Fact]
    public async Task Loading_affordance_clears_once_the_list_is_exhausted()
    {
        var (session, listRequests) = await OpenSeededSidebarAsync();
        await using var scope = session;
        var page = session.Page;

        await page.WaitForConversationCountAsync(PageSize);
        await page.LoadMoreConversationsAsync(RealConversationCount);
        await page.WaitForConversationCountAsync(RealConversationCount);

        await page.ConversationsLoadingMore()
            .WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Detached, Timeout = 10_000 });

        // Scroll once more against an exhausted list. The handler still fires; the loader must not.
        await page.ScrollSidebarToEndAsync();
        await page.WaitForConversationCountAsync(RealConversationCount);

        (await page.ConversationsLoadingMore().CountAsync())
            .Should()
            .Be(0, "the list is exhausted, so no further page is in flight");
        listRequests
            .Snapshot()
            .Should()
            .HaveCount(2, "a short page proved exhaustion — scrolling again must not ask for a third page");
    }

    /// <summary>
    /// The two sort modes must actually order differently. Seeded so creation order is the exact
    /// reverse of last-used order, the head row is the newest-used conversation under
    /// <c>lastUsed</c> and the newest-created one under <c>created</c>.
    /// </summary>
    /// <remarks>
    /// Goes red if <c>created</c> resolves to the last-used ordering anywhere along the path —
    /// controller parse, options plumbing, or store ordering.
    /// </remarks>
    [Fact]
    public async Task Last_used_and_created_sorts_produce_different_head_rows()
    {
        var (session, _) = await OpenSeededSidebarAsync();
        await using var scope = session;
        var page = session.Page;

        await page.WaitForConversationCountAsync(PageSize);
        var lastUsedTitles = await page.ConversationTitlesAsync();
        lastUsedTitles[0].Should().Be(RealTitle(1), "conversation 01 was used most recently");

        await page.SelectSortModeAsync("created");
        await page.WaitForConversationCountAsync(PageSize);
        var createdTitles = await page.ConversationTitlesAsync();
        createdTitles[0]
            .Should()
            .Be(RealTitle(RealConversationCount), "conversation 45 was created most recently");
        createdTitles[0]
            .Should()
            .NotBe(
                lastUsedTitles[0],
                "the two sort modes must order the list differently, or one of them is not being applied");
        createdTitles
            .Should()
            .Equal(
                ExpectedCreatedTitles().Take(PageSize),
                "created order is newest-created first, which here is the exact reverse of last-used");
    }

    /// <summary>
    /// Switching sort mode must RESET paging to a single page. Pages fetched under two different
    /// orderings cannot be concatenated — the result would be an incoherent list with duplicates.
    /// </summary>
    [Fact]
    public async Task Switching_sort_mode_resets_paging_to_a_single_page()
    {
        var (session, listRequests) = await OpenSeededSidebarAsync();
        await using var scope = session;
        var page = session.Page;

        // Page the list right out to exhaustion FIRST, so the switch has two pages' worth of
        // last-used-ordered rows to (incorrectly) merge with if the reset is missing.
        await page.WaitForConversationCountAsync(PageSize);
        await page.LoadMoreConversationsAsync(RealConversationCount);
        await page.WaitForConversationCountAsync(RealConversationCount);

        await page.SelectSortModeAsync("created");

        await page.WaitForConversationCountAsync(PageSize);
        var titles = await page.ConversationTitlesAsync();
        titles
            .Should()
            .Equal(
                ExpectedCreatedTitles().Take(PageSize),
                "the switch must start a fresh single page in the new order, never merge the pages fetched under the old one");

        var requests = listRequests.Snapshot();
        requests.Should().HaveCount(3, "two pages under last-used, then one fresh page under created");
        requests[^1]
            .Should()
            .Contain("offset=0")
            .And.Contain("sort=created", "the refetch after a sort switch restarts at offset 0 in the new order");
    }

    /// <summary>
    /// An unrecognised <c>sort</c> is rejected with 400 and a machine-readable code, never silently
    /// ignored — a silently defaulted sort is indistinguishable from a working one, so a client that
    /// misspells it would render a plausible list in the wrong order forever.
    /// </summary>
    [Fact]
    public async Task Unrecognised_sort_is_rejected_with_400()
    {
        var (session, _) = await OpenSeededSidebarAsync();
        await using var scope = session;
        var page = session.Page;

        await page.WaitForConversationCountAsync(PageSize);

        // Non-vacuity: the same same-origin fetch with a KNOWN sort must succeed, so the 400 below
        // cannot be blamed on the probe itself.
        var accepted = await FetchListAsync(page, "created");
        accepted.GetProperty("status").GetInt32().Should().Be(200, "'created' is an accepted sort");

        var rejected = await FetchListAsync(page, "bogus");
        rejected
            .GetProperty("status")
            .GetInt32()
            .Should()
            .Be(400, "an unknown sort must be rejected, not silently defaulted");
        rejected
            .GetProperty("body")
            .GetString()
            .Should()
            .Contain("invalid_sort", "the rejection must carry a machine-readable code the client can act on");
    }

    /// <summary>
    /// Issues <c>GET /api/conversations?...&amp;sort={sort}</c> from the page's own origin and returns
    /// <c>{ status, body }</c>. Driven through the browser rather than an out-of-band HttpClient so
    /// the request travels the same path the SPA's own list fetch does.
    /// </summary>
    private static Task<JsonElement> FetchListAsync(IPage page, string sort)
    {
        return page.EvaluateAsync<JsonElement>(
            @"async (sort) => {
                const response = await fetch(
                    '/api/conversations?limit=30&offset=0&sort=' + encodeURIComponent(sort));
                return { status: response.status, body: await response.text() };
            }",
            sort);
    }

    /// <summary>
    /// Boots the sample, seeds the conversation store behind it, and reloads so the SPA's on-mount
    /// list fetch sees the seeded rows.
    /// </summary>
    /// <remarks>
    /// The seed has to happen AFTER the host starts (the store instance lives in its container) but
    /// BEFORE the list is fetched — hence boot, seed, reload. The request log is attached before the
    /// reload so the first page request is counted like every later one.
    /// </remarks>
    private async Task<(ScenarioSession Session, ConversationListRequestLog ListRequests)> OpenSeededSidebarAsync()
    {
        // The scripted responder is never exercised: these tests only read the sidebar. One turn is
        // declared because a role with no turns would be a misleading script, not because it runs.
        var responder = ScriptedSseResponder
            .New()
            .ForRole("parent", ctx => ctx.SystemPromptContains("helpful assistant"))
            .Turn(t => t.Text("unused - this scenario never sends a message."))
            .Build();

        var session = await _fixture.OpenAsync("test", responder.HandlerFor("test"));
        try
        {
            var page = session.Page;
            await page.SetViewportSizeAsync(ViewportWidth, ViewportHeight);
            await SeedConversationStoreAsync(session.Factory);

            var listRequests = new ConversationListRequestLog(page);
            await page.ReloadAsync();
            await page.Textarea().WaitForAsync();

            return (session, listRequests);
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Writes the seed described in the class remarks straight into the store the running host
    /// resolved — the same seam <see cref="BrowserWebAppFactory.AppServices"/> exists for.
    /// </summary>
    private static async Task SeedConversationStoreAsync(BrowserWebAppFactory factory)
    {
        var store = factory.AppServices.GetRequiredService<IConversationStore>();

        for (var n = 1; n <= RealConversationCount; n++)
        {
            var threadId = RealThreadId(n);
            await store.SaveMetadataAsync(
                threadId,
                new ThreadMetadata
                {
                    ThreadId = threadId,
                    LastUpdated = RealLastUsedMs(n),
                    Properties = ImmutableDictionary<string, object>.Empty.Add("title", RealTitle(n)),
                });
        }

        // Agent-owned threads: every one newer than every real conversation, so they crowd the whole
        // front of the last-used ordering. Their ids carry no minted timestamp, which is exactly the
        // production shape — they are excluded by prefix, not by their creation time.
        var agentOwned = Enumerable
            .Range(1, SubAgentThreadCount)
            .Select(i => $"subagent-agent-{i:D2}")
            .Concat(Enumerable.Range(1, WorkflowThreadCount).Select(i => $"workflow-run-{i:D2}"))
            .ToList();

        for (var i = 0; i < agentOwned.Count; i++)
        {
            var threadId = agentOwned[i];
            await store.SaveMetadataAsync(
                threadId,
                new ThreadMetadata
                {
                    ThreadId = threadId,
                    LastUpdated = AgentOwnedBaseLastUsedMs + ((i + 1) * StepMs),
                    Properties = ImmutableDictionary<string, object>.Empty.Add(
                        "title",
                        $"Agent thread {threadId}"),
                });
        }
    }

    /// <summary>
    /// Id of real conversation <paramref name="n"/>. The middle segment IS the creation instant —
    /// <c>ConversationListOptions.CreationTimestampOf</c> reads it, because <c>ThreadMetadata</c>
    /// records no creation time of its own — so a higher <paramref name="n"/> is created later.
    /// </summary>
    private static string RealThreadId(int n)
    {
        return $"thread-{BaseCreatedMs + (n * StepMs)}-r{n:D2}";
    }

    /// <summary>
    /// Last-used instant of real conversation <paramref name="n"/>, deliberately the REVERSE of its
    /// creation order: conversation 01 is the oldest created and the most recently used.
    /// </summary>
    private static long RealLastUsedMs(int n)
    {
        return BaseLastUsedMs + ((RealConversationCount + 1 - n) * StepMs);
    }

    private static string RealTitle(int n)
    {
        return $"Conversation {n:D2}";
    }

    /// <summary>Titles in last-used order: 01 first (used most recently), 45 last.</summary>
    private static IEnumerable<string> ExpectedLastUsedTitles()
    {
        return Enumerable.Range(1, RealConversationCount).Select(RealTitle);
    }

    /// <summary>Titles in creation order: 45 first (created most recently), 01 last.</summary>
    private static IEnumerable<string> ExpectedCreatedTitles()
    {
        return Enumerable.Range(1, RealConversationCount).Reverse().Select(RealTitle);
    }

    /// <summary>
    /// Records every sidebar PAGE request (<c>GET /api/conversations?...</c>) the client issues.
    /// </summary>
    /// <remarks>
    /// Needed because two of the claims here are about a request that must NOT happen — no third page
    /// once the list is exhausted, and no page appended across a sort switch. Those are invisible in
    /// the DOM: a pager that correctly declined to fetch and a pager that never ran look identical.
    /// The per-conversation routes (<c>/api/conversations/{id}/...</c>) carry a path segment where
    /// this one carries a query string, so matching on <c>"/api/conversations?"</c> tells them apart.
    /// </remarks>
    private sealed class ConversationListRequestLog
    {
        private readonly List<string> _urls = [];

        public ConversationListRequestLog(IPage page)
        {
            ArgumentNullException.ThrowIfNull(page);
            page.Request += OnRequest;
        }

        /// <summary>The recorded page-request URLs so far, in issue order.</summary>
        public IReadOnlyList<string> Snapshot()
        {
            lock (_urls)
            {
                return [.. _urls];
            }
        }

        private void OnRequest(object? sender, IRequest request)
        {
            if (!request.Url.Contains("/api/conversations?", StringComparison.Ordinal))
            {
                return;
            }

            lock (_urls)
            {
                _urls.Add(request.Url);
            }
        }
    }
}
