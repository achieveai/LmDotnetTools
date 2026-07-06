namespace ConversationDaemon.Sample.Tests;

/// <summary>
/// AC7 — the daemon's poll/resume sequencing. <see cref="ConversationScript"/> observes everything
/// through the injected <see cref="DaemonRestClient"/>, so a scripted <see cref="FakeHttpMessageHandler"/>
/// returning a sequence of bodies fully exercises the polling loops: they must poll past transient
/// not-yet states, return once the awaited condition is observed, and throw
/// <see cref="TimeoutException"/> when it never arrives.
/// </summary>
public sealed class ConversationScriptTests
{
    private const string ThreadId = "thread-1";

    private static readonly string NoMarkerMessages =
        "[{\"role\":\"assistant\",\"content\":\"still thinking\"}]";

    // A deferred Wait as it surfaces on the (hand-built) wire: the messages endpoint carries the marker.
    private static readonly string DeferredMessages = "[{\"is_deferred\":true}]";

    private static readonly string RunInProgress =
        "{\"isInProgress\":true,\"currentRunId\":\"run-1\"}";

    private static readonly string RunIdle = "{\"isInProgress\":false,\"currentRunId\":\"run-1\"}";

    [Theory]
    [InlineData("[{\"content\":\"...is_deferred\\\":true...\"}]", true)] // escaped wire form
    [InlineData("{\"is_deferred\":true}", true)] // unescaped (hand-built) form
    [InlineData("{\"is_deferred\":false}", false)] // marker present but not deferred
    [InlineData("[{\"role\":\"assistant\"}]", false)] // no marker at all
    public void ContainsDeferredWait_matches_both_wire_and_unescaped_forms(string body, bool expected)
    {
        ConversationScript.ContainsDeferredWait(body).Should().Be(expected);
    }

    [Fact]
    public async Task WaitForDeferredWaitAsync_polls_past_a_no_marker_body_then_returns()
    {
        var handler = new FakeHttpMessageHandler().OnJsonSequence(
            HttpMethod.Get,
            "/messages",
            NoMarkerMessages,
            DeferredMessages);
        var client = ClientOver(handler);

        var act = () =>
            ConversationScript.WaitForDeferredWaitAsync(
                client,
                ThreadId,
                TimeSpan.FromSeconds(5),
                CancellationToken.None);

        await act.Should().NotThrowAsync();
        handler
            .CountRequests("/messages")
            .Should()
            .BeGreaterThanOrEqualTo(2, "it must poll again after the first body carried no marker");
    }

    [Fact]
    public async Task WaitForDeferredWaitAsync_throws_TimeoutException_when_no_marker_ever_appears()
    {
        var handler = new FakeHttpMessageHandler().OnJsonSequence(
            HttpMethod.Get,
            "/messages",
            NoMarkerMessages);
        var client = ClientOver(handler);

        // 800ms exceeds one 400ms poll interval but keeps the test fast.
        var act = () =>
            ConversationScript.WaitForDeferredWaitAsync(
                client,
                ThreadId,
                TimeSpan.FromMilliseconds(800),
                CancellationToken.None);

        await act.Should().ThrowAsync<TimeoutException>();
    }

    [Fact]
    public async Task WaitForRunToCompleteAsync_returns_once_the_run_transitions_to_idle()
    {
        var handler = new FakeHttpMessageHandler().OnJsonSequence(
            HttpMethod.Get,
            "/run-state",
            RunInProgress,
            RunIdle);
        var client = ClientOver(handler);

        var act = () =>
            ConversationScript.WaitForRunToCompleteAsync(
                client,
                ThreadId,
                TimeSpan.FromSeconds(5),
                CancellationToken.None);

        await act.Should().NotThrowAsync();
        handler
            .CountRequests("/run-state")
            .Should()
            .BeGreaterThanOrEqualTo(2, "it must observe in-progress and then idle across successive polls");
    }

    [Fact]
    public async Task WaitForRunToCompleteAsync_throws_TimeoutException_when_the_run_never_idles()
    {
        var handler = new FakeHttpMessageHandler().OnJsonSequence(
            HttpMethod.Get,
            "/run-state",
            RunInProgress);
        var client = ClientOver(handler);

        var act = () =>
            ConversationScript.WaitForRunToCompleteAsync(
                client,
                ThreadId,
                TimeSpan.FromMilliseconds(800),
                CancellationToken.None);

        await act.Should().ThrowAsync<TimeoutException>();
    }

    private static DaemonRestClient ClientOver(FakeHttpMessageHandler handler)
    {
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5000") };
        return new DaemonRestClient(httpClient);
    }
}
