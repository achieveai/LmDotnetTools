using System.Net;
using CodeReviewDaemon.Sample.Agents;
using CodeReviewDaemon.Sample.Orchestration;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeReviewDaemon.Sample.Tests.Orchestration;

/// <summary>
/// Pins the daemon's startup assertion that the LmStreaming review host runs with
/// <c>AgentCollaboration__Enabled=true</c>.
/// <para>
/// The probe itself is covered against the wire in <c>LmStreamingS2SClientTests</c>. What is covered HERE is
/// the hosted-service wrapper, and it is worth its own file because both of its behaviours are ones a
/// well-meaning edit silently reverses. Making it swallow turns the guard into decoration — and the
/// misconfiguration it guards against announces itself NOWHERE else: reviews still complete, notes are still
/// committed, and only the delegate reviewers' transcripts are quietly replaced by placeholder stubs. Making
/// it strict is the opposite failure: an unreachable host at boot would make the daemon refuse to start for a
/// reason that has nothing to do with the setting being asserted.
/// </para>
/// <para>
/// Neither test touches the network. Every request is answered by <see cref="FakeHttpMessageHandler"/>;
/// <c>localhost:5051</c> appears only as a <see cref="HttpClient.BaseAddress"/> and is never dialled — which
/// matters more than usual here, because a real 5051 is often listening on the machines this suite runs on,
/// and a test that reached it would pass or fail according to how someone else's host was configured.
/// </para>
/// </summary>
public sealed class ReviewHostCollaborationPreflightTests
{
    private const string TranscriptRoute = "/agents/";

    private static ReviewHostCollaborationPreflight Preflight(FakeHttpMessageHandler handler)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5051/") };
        return new ReviewHostCollaborationPreflight(
            new LmStreamingS2SClient(http, "s", "id", "key"),
            NullLogger<ReviewHostCollaborationPreflight>.Instance);
    }

    /// <summary>
    /// The reason the service exists. Throwing from <c>IHostedService.StartAsync</c> aborts host startup and
    /// surfaces the cause directly; anything softer (log-and-continue) would let the daemon run a full night
    /// of reviews that all look successful and all lose their sub-agent reasoning.
    /// </summary>
    [Fact]
    public async Task StartAsync_refuses_to_start_the_daemon_when_the_host_has_collaboration_off()
    {
        var handler = new FakeHttpMessageHandler().On(
            req => req.RequestUri!.ToString().Contains(TranscriptRoute, StringComparison.Ordinal),
            _ => new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent(
                    "{\"error\":\"Agent collaboration is disabled.\",\"code\":\"collaboration_unavailable\"}"),
            });

        var act = () => Preflight(handler).StartAsync(CancellationToken.None);

        var thrown = (await act.Should().ThrowAsync<ReviewHostContractException>()).Which;
        // The operator has to be able to act on this without reading the daemon's source, so the message must
        // carry the exact setting to change — not merely that "collaboration is unavailable".
        thrown.Message.Should().Contain(
            "AgentCollaboration__Enabled=true", "the message names the exact setting the operator must set");
        thrown.Message.Should().Contain(
            "not retroactive",
            "restarting the host does not repair the PRs already reviewed against it, and an operator who "
                + "assumes otherwise will not go back and re-review them");
    }

    /// <summary>
    /// The counterweight. The probe is bounded and fails OPEN on purpose: a transport failure establishes
    /// nothing about the setting, and a precondition check that cannot answer must not be the reason the
    /// daemon will not boot. This is the behaviour that regressed once already — an unbounded probe took host
    /// startup from 15 seconds to over ten minutes — so it is pinned at the layer where the cost lands.
    /// </summary>
    [Fact]
    public async Task StartAsync_still_starts_the_daemon_when_the_review_host_cannot_be_reached()
    {
        var handler = new FakeHttpMessageHandler().On(
            req => req.RequestUri!.ToString().Contains(TranscriptRoute, StringComparison.Ordinal),
            _ => throw new HttpRequestException("Connection refused"));

        var act = () => Preflight(handler).StartAsync(CancellationToken.None);

        await act.Should().NotThrowAsync(
            "an unreachable host is a real problem with its own diagnosis elsewhere, but it is not evidence "
                + "that collaboration is disabled, and blocking startup on it would make an unrelated outage "
                + "look like a misconfiguration");
    }

    /// <summary>The ordinary path: the route answers normally, so the setting is on and startup proceeds.</summary>
    [Fact]
    public async Task StartAsync_completes_when_the_transcript_route_answers_normally()
    {
        var handler = new FakeHttpMessageHandler().OnJson(
            HttpMethod.Get, TranscriptRoute, "{\"entries\":[]}");

        var act = () => Preflight(handler).StartAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
        handler.Requests.Should().ContainSingle(
            "the assertion is made once at startup, not polled — a second call would mean it moved into the "
                + "review path, where it would cost a round trip per PR");
    }

    /// <summary>Stopping is not a place to do work: the daemon may be shutting down because startup itself
    /// failed, and a StopAsync that reached the network would turn a clean exit into a second failure.</summary>
    [Fact]
    public async Task StopAsync_does_nothing_and_talks_to_nobody()
    {
        var handler = new FakeHttpMessageHandler();

        await Preflight(handler).StopAsync(CancellationToken.None);

        handler.Requests.Should().BeEmpty();
    }
}
