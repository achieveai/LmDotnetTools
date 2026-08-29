using System.Globalization;
using System.Text;
using AchieveAi.LmDotnetTools.LmAgentInfra.Webhooks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace AchieveAi.LmDotnetTools.LmAgentInfra.Tests.Webhooks;

/// <summary>
/// ADR 0005 — the HTTP half of the webhook security layer. The verifier's decisions are pinned
/// elsewhere; what is pinned here is everything the middleware adds on top of them: which route it
/// guards at all, the status code each rejection class maps to, the shape of the reject body, that a
/// replay of an authenticated delivery id is a 409, and — the one a downstream controller actually
/// depends on — that an accepted request reaches the next term with its body still readable.
/// <para>
/// Driven against a hand-built <see cref="DefaultHttpContext"/> rather than a test server: the
/// middleware's contract is entirely expressible in terms of one request and one next-delegate, and
/// hosting it would add moving parts without adding coverage.
/// </para>
/// </summary>
public sealed class WebhookVerificationMiddlewareTests
{
    private const string Secret = "test-signing-secret-0123456789";
    private const string BodyJson = """{"provider_id":"github","session_id":"s-1"}""";
    private static readonly byte[] Body = Encoding.UTF8.GetBytes(BodyJson);

    /// <summary>What one pass through the middleware did, from the caller's and the downstream's view.</summary>
    private sealed record Outcome(int StatusCode, string ResponseBody, bool ReachedNext, string? BodySeenDownstream);

    private static async Task<Outcome> InvokeAsync(
        string path = "/api/auth/webhook/github",
        string method = "POST",
        string? contentType = "application/json",
        byte[]? body = null,
        string? signature = null,
        string? timestamp = null,
        string? deliveryId = "delivery-1",
        long? declaredContentLength = null,
        long maxBodyBytes = 1_048_576,
        DeliveryReplayCache? replayCache = null,
        bool signValidly = true
    )
    {
        body ??= Body;

        // The middleware reads DateTimeOffset.UtcNow itself, so the timestamp is stamped from the same
        // clock rather than a fixture constant — freshness is the verifier's concern, tested there.
        timestamp ??= DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var secret = new WebhookSigningSecret(Secret);
        signature ??= signValidly && deliveryId is not null ? secret.ComputeHex(timestamp, deliveryId, body) : "00";

        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        context.Request.ContentType = contentType;
        context.Request.Body = new MemoryStream(body);
        context.Request.ContentLength = declaredContentLength ?? body.Length;
        context.Request.Headers[WebhookHeaderNames.Signature] = signature;
        context.Request.Headers[WebhookHeaderNames.Timestamp] = timestamp;
        if (deliveryId is not null)
        {
            context.Request.Headers[WebhookHeaderNames.DeliveryId] = deliveryId;
        }

        var responseBody = new MemoryStream();
        context.Response.Body = responseBody;

        var reachedNext = false;
        string? bodySeenDownstream = null;
        var middleware = new WebhookVerificationMiddleware(
            async ctx =>
            {
                reachedNext = true;
                using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8, leaveOpen: true);
                bodySeenDownstream = await reader.ReadToEndAsync().ConfigureAwait(false);
            },
            new WebhookRequestVerifier(secret, ["github", "ado"], TimeSpan.FromMinutes(5), maxBodyBytes),
            replayCache ?? new DeliveryReplayCache(TimeSpan.FromMinutes(10)),
            new WebhookVerificationLimits { MaxBodyBytes = maxBodyBytes },
            NullLogger<WebhookVerificationMiddleware>.Instance
        );

        await middleware.InvokeAsync(context).ConfigureAwait(false);

        return new Outcome(
            context.Response.StatusCode,
            Encoding.UTF8.GetString(responseBody.ToArray()),
            reachedNext,
            bodySeenDownstream
        );
    }

    [Fact]
    public async Task A_valid_request_reaches_the_next_term_with_the_body_rewound()
    {
        // The middleware consumes the body to compute the HMAC over the raw bytes; if it did not rewind,
        // every downstream [FromBody] bind would see an empty stream. That rewind is the assertion.
        var outcome = await InvokeAsync();

        outcome.ReachedNext.Should().BeTrue();
        outcome.BodySeenDownstream.Should().Be(BodyJson, "the body must still be readable downstream");
        outcome.ResponseBody.Should().BeEmpty();
    }

    [Fact]
    public async Task A_request_outside_the_guarded_route_passes_straight_through_unverified()
    {
        // Guarding only the exact route is what keeps this from becoming an app-wide signature demand.
        var outcome = await InvokeAsync(path: "/api/conversations", signature: "garbage");

        outcome.ReachedNext.Should().BeTrue();
        outcome.BodySeenDownstream.Should().Be(BodyJson);
    }

    [Theory]
    [InlineData("/api/auth/webhook")] // no provider segment
    [InlineData("/api/auth/webhook/")] // empty provider segment
    [InlineData("/api/auth/webhook/github/extra")] // suffix path — must not consume a delivery id
    public async Task A_near_miss_route_is_not_verified(string path)
    {
        var outcome = await InvokeAsync(path: path, signature: "garbage");

        outcome.ReachedNext.Should().BeTrue();
    }

    [Fact]
    public async Task A_non_post_on_the_guarded_route_passes_through()
    {
        var outcome = await InvokeAsync(method: "GET", signature: "garbage");

        outcome.ReachedNext.Should().BeTrue();
    }

    [Fact]
    public async Task An_unknown_provider_is_a_404()
    {
        var outcome = await InvokeAsync(path: "/api/auth/webhook/gitlab");

        outcome.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        outcome.ReachedNext.Should().BeFalse();
        outcome.ResponseBody.Should().Be($"webhook rejected: {WebhookRejection.UnknownProvider}");
    }

    [Fact]
    public async Task A_non_json_content_type_is_a_415()
    {
        var outcome = await InvokeAsync(contentType: "text/plain");

        outcome.StatusCode.Should().Be(StatusCodes.Status415UnsupportedMediaType);
        outcome.ResponseBody.Should().Be($"webhook rejected: {WebhookRejection.UnsupportedContentType}");
    }

    [Fact]
    public async Task A_missing_delivery_header_is_a_400()
    {
        var outcome = await InvokeAsync(deliveryId: null, signature: "00");

        outcome.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        outcome.ResponseBody.Should().Be($"webhook rejected: {WebhookRejection.MissingHeaders}");
    }

    [Fact]
    public async Task An_invalid_signature_is_a_401()
    {
        // 401 is the default arm of the status map: an authentication failure, not a routing or shape
        // failure, and deliberately indistinguishable from a stale timestamp to a caller.
        var outcome = await InvokeAsync(signValidly: false);

        outcome.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        outcome.ReachedNext.Should().BeFalse();
        outcome.ResponseBody.Should().Be($"webhook rejected: {WebhookRejection.InvalidSignature}");
    }

    [Fact]
    public async Task A_stale_timestamp_is_also_a_401()
    {
        var stale = DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

        var outcome = await InvokeAsync(timestamp: stale);

        outcome.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        outcome.ResponseBody.Should().Be($"webhook rejected: {WebhookRejection.StaleTimestamp}");
    }

    [Fact]
    public async Task A_declared_content_length_over_the_cap_is_a_413_before_the_body_is_read()
    {
        var outcome = await InvokeAsync(declaredContentLength: 10_000, maxBodyBytes: 16);

        outcome.StatusCode.Should().Be(StatusCodes.Status413PayloadTooLarge);
        outcome.ReachedNext.Should().BeFalse();
        outcome.ResponseBody.Should().Be("webhook rejected: body too large");
    }

    [Fact]
    public async Task A_body_that_overruns_the_cap_while_streaming_is_a_413()
    {
        // A lying (or absent) Content-Length must not get past the cap: the read itself is bounded.
        var outcome = await InvokeAsync(declaredContentLength: 1, maxBodyBytes: 16);

        outcome.StatusCode.Should().Be(StatusCodes.Status413PayloadTooLarge);
        outcome.ReachedNext.Should().BeFalse();
        outcome.ResponseBody.Should().Be("webhook rejected: body too large");
    }

    [Fact]
    public async Task A_replayed_delivery_id_is_a_409()
    {
        // The signature is valid both times — only the replay cache separates them, which is why the
        // delivery id has to be inside the signed payload for this rejection to mean anything.
        var replayCache = new DeliveryReplayCache(TimeSpan.FromMinutes(10));

        var first = await InvokeAsync(replayCache: replayCache);
        var second = await InvokeAsync(replayCache: replayCache);

        first.ReachedNext.Should().BeTrue();
        second.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        second.ReachedNext.Should().BeFalse();
        second.ResponseBody.Should().Be("webhook rejected: duplicate delivery");
    }

    [Fact]
    public async Task A_rejection_body_is_plain_text()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = "/api/auth/webhook/gitlab";
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Body);
        context.Response.Body = new MemoryStream();

        var middleware = new WebhookVerificationMiddleware(
            _ => Task.CompletedTask,
            new WebhookRequestVerifier(
                new WebhookSigningSecret(Secret),
                ["github"],
                TimeSpan.FromMinutes(5),
                1_048_576
            ),
            new DeliveryReplayCache(TimeSpan.FromMinutes(10)),
            new WebhookVerificationLimits(),
            NullLogger<WebhookVerificationMiddleware>.Instance
        );

        await middleware.InvokeAsync(context);

        context.Response.ContentType.Should().Be("text/plain");
    }

    [Fact]
    public void The_route_predicate_matches_exactly_one_provider_segment_on_post()
    {
        static HttpRequest Request(string method, string path)
        {
            var context = new DefaultHttpContext();
            context.Request.Method = method;
            context.Request.Path = path;
            return context.Request;
        }

        WebhookVerificationMiddleware.IsWebhookRoute(Request("POST", "/api/auth/webhook/github")).Should().BeTrue();
        WebhookVerificationMiddleware
            .IsWebhookRoute(Request("POST", "/API/Auth/Webhook/github"))
            .Should()
            .BeTrue("the prefix match is case-insensitive");
        WebhookVerificationMiddleware.IsWebhookRoute(Request("GET", "/api/auth/webhook/github")).Should().BeFalse();
        WebhookVerificationMiddleware.IsWebhookRoute(Request("POST", "/api/auth/webhook")).Should().BeFalse();
        WebhookVerificationMiddleware
            .IsWebhookRoute(Request("POST", "/api/auth/webhook/github/extra"))
            .Should()
            .BeFalse();
    }

    [Fact]
    public void The_middleware_rejects_null_collaborators()
    {
        var verifier = new WebhookRequestVerifier(
            new WebhookSigningSecret(Secret),
            ["github"],
            TimeSpan.FromMinutes(5),
            16
        );
        var cache = new DeliveryReplayCache(TimeSpan.FromMinutes(10));
        var limits = new WebhookVerificationLimits();
        var logger = NullLogger<WebhookVerificationMiddleware>.Instance;

        var nullNext = () => new WebhookVerificationMiddleware(null!, verifier, cache, limits, logger);
        var nullLimits = () =>
            new WebhookVerificationMiddleware(_ => Task.CompletedTask, verifier, cache, null!, logger);

        nullNext.Should().Throw<ArgumentNullException>().WithParameterName("next");
        nullLimits.Should().Throw<ArgumentNullException>().WithParameterName("limits");
    }
}
