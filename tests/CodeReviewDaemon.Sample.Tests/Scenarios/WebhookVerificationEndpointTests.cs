using System.Globalization;
using System.Net;
using System.Text;
using CodeReviewDaemon.Sample.Auth;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

/// <summary>
/// Plan §9 (AC#4 webhook contract) — the end-to-end matrix the re-review asked for, exercised through
/// the real daemon host + the <c>WebhookVerificationMiddleware</c> sitting in front of the shared
/// controller. A fully-valid signed callback reaches the controller (which denies, as no token is
/// signed in); every malformed/forged/replayed variant is rejected at the edge before token resolution.
/// </summary>
public sealed class WebhookVerificationEndpointTests
{
    private const string SigningSecret = "endpoint-test-signing-secret";
    private const string SharedSecret = "endpoint-test-shared-secret";
    private const string Body = """{"provider_id":"github","destination_host":"api.github.com","required_scopes":[]}""";

    private static WebApplicationFactory<Program> Factory(DaemonWebAppFactory baseFactory) =>
        baseFactory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Auth:Webhook:SigningSecret", SigningSecret);
            b.UseSetting("Auth:Webhook:GatewaySharedSecret", SharedSecret);
        });

    private static HttpRequestMessage Signed(
        string provider = "github",
        string body = Body,
        string contentType = "application/json",
        string? signatureBody = null,
        DateTimeOffset? timestamp = null,
        string deliveryId = "delivery-1",
        bool withSharedSecret = true)
    {
        var ts = (timestamp ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        // Sign over signatureBody when provided (to forge a body/signature mismatch), else over body.
        var sig = new WebhookSigningSecret(SigningSecret).ComputeHex(ts, deliveryId, Encoding.UTF8.GetBytes(signatureBody ?? body));

        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/auth/webhook/{provider}")
        {
            Content = new StringContent(body, Encoding.UTF8, contentType),
        };
        request.Headers.TryAddWithoutValidation(WebhookVerificationMiddleware.SignatureHeader, sig);
        request.Headers.TryAddWithoutValidation(WebhookVerificationMiddleware.TimestampHeader, ts);
        request.Headers.TryAddWithoutValidation(WebhookVerificationMiddleware.DeliveryHeader, deliveryId);
        if (withSharedSecret)
        {
            request.Headers.TryAddWithoutValidation("Authorization", SharedSecret);
        }

        return request;
    }

    [Fact]
    public async Task A_valid_signed_callback_passes_the_middleware_and_reaches_the_controller()
    {
        using var baseFactory = new DaemonWebAppFactory();
        using var factory = Factory(baseFactory);
        using var client = factory.CreateClient();

        var response = await client.SendAsync(Signed());

        // The middleware accepted it; the controller then denies (no token signed in) — a clean 200,
        // never a 401/4xx from the verification layer.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain("deny");
    }

    [Fact]
    public async Task An_invalid_signature_is_rejected_401()
    {
        using var baseFactory = new DaemonWebAppFactory();
        using var factory = Factory(baseFactory);
        using var client = factory.CreateClient();

        var request = Signed();
        request.Headers.Remove(WebhookVerificationMiddleware.SignatureHeader);
        request.Headers.TryAddWithoutValidation(WebhookVerificationMiddleware.SignatureHeader, "00deadbeef");

        (await client.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_valid_signature_over_an_altered_body_is_rejected_401()
    {
        using var baseFactory = new DaemonWebAppFactory();
        using var factory = Factory(baseFactory);
        using var client = factory.CreateClient();

        // Signature computed over the original body, but a different body is sent.
        var request = Signed(body: """{"provider_id":"github","destination_host":"evil.example"}""", signatureBody: Body);

        (await client.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_stale_timestamp_is_rejected_401()
    {
        using var baseFactory = new DaemonWebAppFactory();
        using var factory = Factory(baseFactory);
        using var client = factory.CreateClient();

        (await client.SendAsync(Signed(timestamp: DateTimeOffset.UtcNow.AddMinutes(-10)))).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_missing_signature_header_is_rejected_400()
    {
        using var baseFactory = new DaemonWebAppFactory();
        using var factory = Factory(baseFactory);
        using var client = factory.CreateClient();

        var request = Signed();
        request.Headers.Remove(WebhookVerificationMiddleware.SignatureHeader);

        (await client.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_non_json_content_type_is_rejected_415()
    {
        using var baseFactory = new DaemonWebAppFactory();
        using var factory = Factory(baseFactory);
        using var client = factory.CreateClient();

        (await client.SendAsync(Signed(contentType: "text/plain"))).StatusCode
            .Should().Be(HttpStatusCode.UnsupportedMediaType);
    }

    [Fact]
    public async Task An_unknown_provider_path_is_rejected_404()
    {
        using var baseFactory = new DaemonWebAppFactory();
        using var factory = Factory(baseFactory);
        using var client = factory.CreateClient();

        (await client.SendAsync(Signed(provider: "gitlab"))).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_duplicate_delivery_id_is_rejected_409_on_replay()
    {
        using var baseFactory = new DaemonWebAppFactory();
        using var factory = Factory(baseFactory);
        using var client = factory.CreateClient();

        (await client.SendAsync(Signed(deliveryId: "dup-1"))).StatusCode.Should().Be(HttpStatusCode.OK);
        // Re-send the identical callback (a fresh request object, same delivery id) — a replay.
        (await client.SendAsync(Signed(deliveryId: "dup-1"))).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task A_replay_with_a_changed_delivery_id_is_rejected_401()
    {
        using var baseFactory = new DaemonWebAppFactory();
        using var factory = Factory(baseFactory);
        using var client = factory.CreateClient();

        // Accept a valid callback, then resend the SAME signed body/timestamp/signature but swap the
        // delivery id (the vector that would dodge the cache if the id were not part of the signature).
        var captured = Signed(deliveryId: "orig");
        var capturedSig = captured.Headers.GetValues(WebhookVerificationMiddleware.SignatureHeader).Single();
        var capturedTs = captured.Headers.GetValues(WebhookVerificationMiddleware.TimestampHeader).Single();
        (await client.SendAsync(captured)).StatusCode.Should().Be(HttpStatusCode.OK);

        var replay = new HttpRequestMessage(HttpMethod.Post, "/api/auth/webhook/github")
        {
            Content = new StringContent(Body, Encoding.UTF8, "application/json"),
        };
        replay.Headers.TryAddWithoutValidation(WebhookVerificationMiddleware.SignatureHeader, capturedSig);
        replay.Headers.TryAddWithoutValidation(WebhookVerificationMiddleware.TimestampHeader, capturedTs);
        replay.Headers.TryAddWithoutValidation(WebhookVerificationMiddleware.DeliveryHeader, "swapped");
        replay.Headers.TryAddWithoutValidation("Authorization", SharedSecret);

        (await client.SendAsync(replay)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task An_oversized_body_is_rejected_413()
    {
        using var baseFactory = new DaemonWebAppFactory();
        using var factory = Factory(baseFactory);
        using var client = factory.CreateClient();

        // Just over the 1 MiB default cap. The middleware rejects on the declared Content-Length before
        // reading or verifying, so no valid signature is needed — this pins the real buffering/413 path.
        var oversized = new string('x', 1_048_577);
        var request = Signed(body: oversized);

        (await client.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
    }

    [Fact]
    public async Task A_suffix_path_under_the_webhook_prefix_is_not_verified_and_404s()
    {
        using var baseFactory = new DaemonWebAppFactory();
        using var factory = Factory(baseFactory);
        using var client = factory.CreateClient();

        // A path with an extra segment is outside the exact webhook route: the verifier branch must not
        // run (so no delivery id is consumed) and MVC has no matching route → 404, never a 4xx from the
        // verification layer.
        var request = Signed(provider: "github/extra");

        (await client.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Malformed_json_with_a_valid_signature_passes_the_edge_then_fails_model_binding_400()
    {
        using var baseFactory = new DaemonWebAppFactory();
        using var factory = Factory(baseFactory);
        using var client = factory.CreateClient();

        // The body is signed correctly (so the middleware accepts it) but is not valid JSON, so MVC's
        // [FromBody] binding rejects it — proving the body still reaches the controller intact.
        var response = await client.SendAsync(Signed(body: "{ not valid json "));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
