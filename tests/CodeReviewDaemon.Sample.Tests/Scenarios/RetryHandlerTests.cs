using System.Net;
using System.Text;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using CodeReviewDaemon.Sample.Workspace;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

/// <summary>
/// PR #121 M7 — the daemon's outbound HTTP resilience layer. A transient <c>429</c>/<c>5xx</c> is
/// retried with backoff for idempotent reads; a POST is retried only on <c>429</c> (server rejected it
/// before processing, so a retry cannot double-apply) and never on an ambiguous <c>5xx</c> — that case
/// is left to the ReviewPoster exactly-once backstop. Non-transient statuses (<c>4xx</c>) are returned
/// as-is without retry.
/// </summary>
public sealed class RetryHandlerTests
{
    private const string Url = "https://api.test/resource";

    private static HttpClient Client(FakeHttpMessageHandler inner) =>
        new(new RetryHandler(NullLogger<RetryHandler>.Instance, baseDelay: TimeSpan.Zero) { InnerHandler = inner });

    [Fact]
    public async Task A_get_that_is_rate_limited_then_succeeds_is_retried()
    {
        var fake = new FakeHttpMessageHandler()
            .OnSequence(HttpMethod.Get, "resource", (HttpStatusCode.TooManyRequests, "{}"), (HttpStatusCode.OK, "{\"ok\":true}"));

        var response = await Client(fake).GetAsync(Url);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        fake.CountRequests("resource").Should().Be(2, "the 429 was retried once and then succeeded");
    }

    [Fact]
    public async Task A_get_retries_5xx_up_to_the_cap_then_returns_the_last_response()
    {
        var fake = new FakeHttpMessageHandler().OnSequence(
            HttpMethod.Get,
            "resource",
            (HttpStatusCode.ServiceUnavailable, "{}"),
            (HttpStatusCode.BadGateway, "{}"),
            (HttpStatusCode.OK, "{\"ok\":true}"));

        var response = await Client(fake).GetAsync(Url);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        fake.CountRequests("resource").Should().Be(3);
    }

    [Fact]
    public async Task A_get_does_not_retry_a_4xx()
    {
        var fake = new FakeHttpMessageHandler().OnSequence(HttpMethod.Get, "resource", (HttpStatusCode.NotFound, "{}"));

        var response = await Client(fake).GetAsync(Url);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        fake.CountRequests("resource").Should().Be(1, "a 404 is not transient");
    }

    [Fact]
    public async Task A_post_is_retried_on_429()
    {
        var fake = new FakeHttpMessageHandler().OnSequence(
            HttpMethod.Post, "resource", (HttpStatusCode.TooManyRequests, "{}"), (HttpStatusCode.Created, "{\"id\":1}"));

        var response = await Client(fake).PostAsync(Url, new StringContent("{\"body\":true}", Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        fake.CountRequests("resource").Should().Be(2, "429 means the POST was rejected before processing — safe to retry");
    }

    [Fact]
    public async Task A_post_is_not_retried_on_5xx()
    {
        var fake = new FakeHttpMessageHandler().OnSequence(
            HttpMethod.Post, "resource", (HttpStatusCode.InternalServerError, "{}"), (HttpStatusCode.Created, "{\"id\":1}"));

        var response = await Client(fake).PostAsync(Url, new StringContent("{\"body\":true}", Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        fake.CountRequests("resource").Should()
            .Be(1, "an ambiguous 5xx POST could have applied server-side; retry is left to the ReviewPoster backstop");
    }
}
