using System.Net;
using CodeReviewDaemon.Sample.Agents;
using CodeReviewDaemon.Sample.Orchestration;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeReviewDaemon.Sample.Tests.Orchestration;

/// <summary>
/// The deep-link retention ceiling: every posted comment carries
/// <c>{baseUrl}/?threadId={threadId}&amp;focus=1</c>, so a review's hosted conversation must stay reachable
/// for its whole window and be discarded once past it. Driven over a real <see cref="ReviewStore"/> on a temp
/// SQLite file (the ledger's SQL is part of what's under test), a scripted
/// <see cref="FakeHttpMessageHandler"/> for the review host, and a frozen clock.
/// <para>
/// The load-bearing fact is the negative one: a conversation minted <i>inside</i> the window survives a sweep.
/// A sweeper that discarded on review completion would 404 the link the moment the review it belongs to
/// finished — killing the feature it is meant to bound.
/// </para>
/// </summary>
public sealed class DeepLinkRetentionSweeperTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 7, 27, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Retention = TimeSpan.FromHours(24);

    private readonly TempSqliteDatabase _db = new();
    private readonly ReviewStore _store;
    private readonly List<HttpClient> _clients = [];

    public DeepLinkRetentionSweeperTests() => _store = new ReviewStore(_db.ConnectionString);

    public void Dispose()
    {
        foreach (var client in _clients)
        {
            client.Dispose();
        }

        _store.Dispose();
        _db.Dispose();
    }

    [Fact]
    public async Task A_conversation_past_the_window_is_deleted_on_the_host_and_dropped_from_the_ledger()
    {
        _store.RecordDeepLinkConversation("thread-old", "Review PR #222", Now - TimeSpan.FromHours(25));
        var handler = new FakeHttpMessageHandler()
            .On(req => req.Method == HttpMethod.Delete, _ => new HttpResponseMessage(HttpStatusCode.NoContent));

        await NewSweeper(handler).SweepAsync(CancellationToken.None);

        handler.Requests.Should().ContainSingle().Which.Uri.ToString().Should().EndWith("api/conversations/thread-old");
        AllLedgerRows().Should().BeEmpty();
    }

    [Fact]
    public async Task A_conversation_still_inside_the_window_is_left_alone()
    {
        // The whole point of the S2S path: the link outlives the review that minted it. Only age discards it.
        _store.RecordDeepLinkConversation("thread-young", "Review PR #222", Now - TimeSpan.FromHours(23));
        var handler = new FakeHttpMessageHandler()
            .On(req => req.Method == HttpMethod.Delete, _ => new HttpResponseMessage(HttpStatusCode.NoContent));

        await NewSweeper(handler).SweepAsync(CancellationToken.None);

        handler.Requests.Should().BeEmpty("nothing has aged out, so the host is never called");
        AllLedgerRows().Should().ContainSingle().Which.ThreadId.Should().Be("thread-young");
    }

    [Fact]
    public async Task A_failed_discard_keeps_its_row_for_the_next_sweep_without_stopping_the_batch()
    {
        _store.RecordDeepLinkConversation("thread-unreachable", "Review PR #222", Now - TimeSpan.FromHours(30));
        _store.RecordDeepLinkConversation("thread-ok", "Review PR #222 — judge", Now - TimeSpan.FromHours(29));
        var handler = new FakeHttpMessageHandler()
            .On(
                req => req.RequestUri!.ToString().Contains("thread-unreachable", StringComparison.Ordinal),
                _ => new HttpResponseMessage(HttpStatusCode.InternalServerError))
            .On(req => req.Method == HttpMethod.Delete, _ => new HttpResponseMessage(HttpStatusCode.NoContent));

        await NewSweeper(handler).SweepAsync(CancellationToken.None);

        // One unreachable conversation must not strand every other expired one behind it...
        handler.CountRequests("thread-ok").Should().Be(1);
        // ...and its own row survives, so the next poll cycle retries rather than leaking it forever.
        AllLedgerRows().Should().ContainSingle().Which.ThreadId.Should().Be("thread-unreachable");
    }

    [Fact]
    public async Task A_conversation_the_host_no_longer_has_is_dropped_from_the_ledger_anyway()
    {
        _store.RecordDeepLinkConversation("thread-gone", null, Now - TimeSpan.FromHours(48));
        var handler = new FakeHttpMessageHandler()
            .OnJson(HttpMethod.Delete, "api/conversations", "{}", HttpStatusCode.NotFound);

        await NewSweeper(handler).SweepAsync(CancellationToken.None);

        AllLedgerRows().Should().BeEmpty("a 404 is the state we wanted, reached by another route");
    }

    [Fact]
    public void Re_recording_a_thread_keeps_the_first_mint_so_the_clock_cannot_be_restarted()
    {
        var minted = Now - TimeSpan.FromHours(25);
        _store.RecordDeepLinkConversation("thread-1", "Review PR #222", minted);

        // A retry/reprovision against the same thread id must not push the conversation back inside the window.
        _store.RecordDeepLinkConversation("thread-1", "Review PR #222 (again)", Now);

        var row = AllLedgerRows().Should().ContainSingle().Subject;
        row.MintedAt.Should().Be(minted);
        row.Title.Should().Be("Review PR #222");
    }

    [Fact]
    public void A_non_positive_retention_window_is_rejected_at_construction()
    {
        // Zero would discard a conversation the instant it is recorded — the link would be dead before the
        // review that minted it answered. "Keep forever" is expressed by not registering the sweeper.
        var act = () => new DeepLinkRetentionSweeper(
            _store,
            NewClient(new FakeHttpMessageHandler()),
            TimeSpan.Zero,
            NullLogger<DeepLinkRetentionSweeper>.Instance);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    private DeepLinkRetentionSweeper NewSweeper(FakeHttpMessageHandler handler) =>
        new(
            _store,
            NewClient(handler),
            Retention,
            NullLogger<DeepLinkRetentionSweeper>.Instance,
            new FrozenTimeProvider(Now));

    private LmStreamingS2SClient NewClient(FakeHttpMessageHandler handler)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5051/") };
        _clients.Add(http);
        return new LmStreamingS2SClient(http, "s", "id", "key");
    }

    /// <summary>Every ledger row: the store only exposes an aged query, so ask it for everything ever minted.</summary>
    private IReadOnlyList<DeepLinkConversationRow> AllLedgerRows() =>
        _store.ListDeepLinkConversationsMintedBefore(Now + TimeSpan.FromDays(3650));

    private sealed class FrozenTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;

        public FrozenTimeProvider(DateTimeOffset now) => _now = now;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
