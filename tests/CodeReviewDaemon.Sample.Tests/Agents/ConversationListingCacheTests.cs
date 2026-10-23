using System.Net;
using CodeReviewDaemon.Sample.Agents;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using Row = CodeReviewDaemon.Sample.Agents.ConversationListingCache.Row;

namespace CodeReviewDaemon.Sample.Tests.Agents;

/// <summary>
/// The shared conversation listing that keeps a burst of review launches from each paging the host's whole list
/// (2026-10-22: 13k conversations, list calls went from 2s to 120s timeouts).
/// </summary>
public class ConversationListingCacheTests
{
    private const string Listing = """
        [
          {"threadId":"t1","title":"Nova-17-aaaaaaaa","workspace":"ws","mode":"m","provider":"p","lastUpdated":100,"clientToolsDisabled":true},
          {"threadId":"t2","title":"Nova-18-bbbbbbbb","workspace":"ws","mode":"m","provider":"p","lastUpdated":200}
        ]
        """;

    private DateTimeOffset _now = DateTimeOffset.UnixEpoch;

    [Fact]
    public async Task Lookups_by_every_client_in_the_process_share_one_listing_fetch()
    {
        var handler = new FakeHttpMessageHandler()
            .OnJson(HttpMethod.Get, "/api/conversations?", Listing)
            .OnJson(HttpMethod.Post, "/provider", "{}");
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var cache = NewCache();
        var launch = new LmStreamingS2SClient(http, null, null, null, cache);
        var another = new LmStreamingS2SClient(http, null, null, null, cache);

        (await launch.GetClientToolsDisabledAsync("t1", CancellationToken.None)).Should().BeTrue();
        (await launch.FindConversationByTitlePrefixAsync("Nova-17-", "ws", "m", CancellationToken.None))
            .Should()
            .Be(new S2SConversationMatch("t1", "p", true));
        await launch.EnsureConversationReusableAsync("t1", "ws", "p2", "m", CancellationToken.None);
        // The provider switch above shows in the copy the other client reads.
        await another.EnsureConversationBindingAsync("t1", "ws", "p2", "m", CancellationToken.None);
        (await another.FindConversationByTitlePrefixAsync("Nova-99-", "ws", "m", CancellationToken.None))
            .Should()
            .BeNull();

        handler.CountRequests("/api/conversations?").Should().Be(1);
        handler.Requests[0].Uri.Query.Should().Be("?limit=1000&offset=0&sort=created");
    }

    [Fact]
    public async Task The_clients_own_writes_show_in_the_copy_without_a_refetch()
    {
        var handler = new FakeHttpMessageHandler()
            .OnJson(HttpMethod.Get, "/api/conversations?", Listing)
            .On(
                r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath == "/api/conversations",
                _ => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"threadId":"fresh"}"""),
                }
            )
            .OnJson(HttpMethod.Put, "/metadata", "{}")
            .On(r => r.Method == HttpMethod.Delete, _ => new HttpResponseMessage(HttpStatusCode.NoContent));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var client = new LmStreamingS2SClient(http, null, null, null, NewCache());
        _ = await client.FindConversationByTitlePrefixAsync("Nova-17-", "ws", "m", CancellationToken.None);

        var fresh = await client.ProvisionAsync(
            "ws",
            "p",
            "m",
            null,
            null,
            null,
            null,
            disableClientTools: true,
            CancellationToken.None
        );
        await client.UpdateMetadataAsync(fresh, "Nova-17-cccccccc", null, CancellationToken.None);
        _ = await client.DeleteConversationAsync("t2", CancellationToken.None);

        (await client.FindConversationByTitlePrefixAsync("Nova-17-", "ws", "m", CancellationToken.None))
            .Should()
            .Be(new S2SConversationMatch("fresh", "p", true));
        (await client.GetClientToolsDisabledAsync("t2", CancellationToken.None)).Should().BeNull();
        handler.CountRequests("/api/conversations?").Should().Be(1);
    }

    [Fact]
    public async Task A_copy_is_fetched_again_after_its_max_age_and_for_a_missing_thread_after_the_miss_age()
    {
        var cache = NewCache();
        var fetches = 0;
        Task<IReadOnlyList<Row>> Fetch(CancellationToken ct)
        {
            fetches++;
            return Task.FromResult<IReadOnlyList<Row>>([Listed("t1")]);
        }

        _ = await cache.RowsAsync(Fetch, "t1", CancellationToken.None);
        _now += TimeSpan.FromSeconds(29);
        _ = await cache.RowsAsync(Fetch, "missing", CancellationToken.None);
        fetches.Should().Be(1, "a young copy is trusted to lack the thread");

        _now += TimeSpan.FromSeconds(1);
        _ = await cache.RowsAsync(Fetch, "t1", CancellationToken.None);
        _ = await cache.RowsAsync(Fetch, null, CancellationToken.None);
        fetches.Should().Be(1, "a copy that has the thread, or a whole-listing read, is trusted until the max age");
        _ = await cache.RowsAsync(Fetch, "missing", CancellationToken.None);
        fetches.Should().Be(2);

        _now += TimeSpan.FromMinutes(10);
        _ = await cache.RowsAsync(Fetch, "t1", CancellationToken.None);
        fetches.Should().Be(3);
    }

    [Fact]
    public async Task Concurrent_lookups_wait_for_one_fetch_and_writes_made_during_it_survive()
    {
        var cache = NewCache();
        var pages = new TaskCompletionSource<IReadOnlyList<Row>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fetches = 0;
        Task<IReadOnlyList<Row>> Fetch(CancellationToken ct)
        {
            _ = Interlocked.Increment(ref fetches);
            return pages.Task;
        }

        var first = cache.RowsAsync(Fetch, "t1", CancellationToken.None);
        var second = cache.RowsAsync(Fetch, null, CancellationToken.None);
        // The pages in flight predate both writes.
        cache.Apply("fresh", _ => Listed("fresh"));
        cache.Apply("t1", _ => null);
        pages.SetResult([Listed("t1"), Listed("t2")]);

        (await first).Select(row => row.ThreadId).Should().Equal("fresh", "t2");
        (await second).Select(row => row.ThreadId).Should().Equal("fresh", "t2");
        fetches.Should().Be(1);
    }

    [Fact]
    public async Task A_failed_fetch_leaves_nothing_cached()
    {
        var cache = NewCache();

        var failing = () =>
            cache.RowsAsync(_ => throw new HttpRequestException("timed out"), null, CancellationToken.None);

        _ = await failing.Should().ThrowAsync<HttpRequestException>();
        (await cache.RowsAsync(_ => Task.FromResult<IReadOnlyList<Row>>([Listed("t1")]), null, CancellationToken.None))
            .Should()
            .ContainSingle();
    }

    [Fact]
    public async Task Without_a_copy_a_thread_lookup_pages_by_1000_and_stops_at_the_page_holding_it()
    {
        var full = "[" + string.Join(",", Enumerable.Range(0, 1000).Select(i => $$"""{"threadId":"n{{i}}"}""")) + "]";
        var handler = new FakeHttpMessageHandler()
            .OnJson(HttpMethod.Get, "offset=0&", full)
            .OnJson(HttpMethod.Get, "offset=1000&", """[{"threadId":"old","clientToolsDisabled":true}]""");
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var client = new LmStreamingS2SClient(http, null, null, null);

        (await client.GetClientToolsDisabledAsync("n5", CancellationToken.None)).Should().BeFalse();
        (await client.GetClientToolsDisabledAsync("old", CancellationToken.None)).Should().BeTrue();

        handler
            .Requests.Select(r => r.Uri.Query)
            .Should()
            .Equal(
                "?limit=1000&offset=0&sort=created",
                "?limit=1000&offset=0&sort=created",
                "?limit=1000&offset=1000&sort=created"
            );
    }

    private ConversationListingCache NewCache() => new(TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(30), () => _now);

    private static Row Listed(string threadId) => new(threadId, null, "ws", "m", "p", 0, false);
}
