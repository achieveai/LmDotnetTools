using System.Text;
using AchieveAi.LmDotnetTools.LmAgentInfra.Webhooks;

namespace AchieveAi.LmDotnetTools.LmAgentInfra.Tests.Webhooks;

/// <summary>
/// ADR 0005 — the outbound signer. The only claim that matters is the round trip: bytes this produces
/// must be accepted by <see cref="WebhookRequestVerifier"/>, because a signer that agrees with itself
/// but not with the receiver is worse than no signer at all.
/// </summary>
public sealed class WebhookRequestSignerTests
{
    private const string Secret = "test-signing-secret-0123456789";
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_750_000_000);
    private static readonly byte[] Body = Encoding.UTF8.GetBytes("""{"event":"run.started","run_id":"r-1"}""");

    private static WebhookRequestVerifier Verifier(WebhookSigningSecret secret) =>
        new(secret, ["github"], TimeSpan.FromMinutes(5), 1_048_576);

    private static WebhookVerificationResult VerifyOnTheWire(
        WebhookRequestVerifier verifier,
        WebhookSignatureHeaders headers,
        DateTimeOffset nowUtc,
        byte[]? body = null
    ) =>
        verifier.Verify(
            new WebhookVerificationInput(
                "github",
                "application/json",
                body ?? Body,
                headers.Signature,
                headers.Timestamp,
                headers.DeliveryId
            ),
            nowUtc
        );

    [Fact]
    public void What_the_signer_produces_is_what_the_verifier_accepts()
    {
        var secret = new WebhookSigningSecret(Secret);
        var headers = new WebhookRequestSigner(secret, new ManualTimeProvider(Now)).Sign(Body, "delivery-1");

        headers.Timestamp.Should().Be("1750000000", "the wire form is Unix seconds");
        headers.DeliveryId.Should().Be("delivery-1");
        VerifyOnTheWire(Verifier(secret), headers, Now).IsValid.Should().BeTrue();
    }

    [Fact]
    public void A_generated_delivery_id_is_unique_per_signature_and_still_round_trips()
    {
        var secret = new WebhookSigningSecret(Secret);
        var signer = new WebhookRequestSigner(secret, new ManualTimeProvider(Now));

        var first = signer.Sign(Body);
        var second = signer.Sign(Body);

        first.DeliveryId.Should().NotBe(second.DeliveryId, "each delivery needs its own replay-cache key");
        VerifyOnTheWire(Verifier(secret), first, Now).IsValid.Should().BeTrue();
        VerifyOnTheWire(Verifier(secret), second, Now).IsValid.Should().BeTrue();
    }

    [Fact]
    public void A_body_altered_after_signing_no_longer_verifies()
    {
        var secret = new WebhookSigningSecret(Secret);
        var headers = new WebhookRequestSigner(secret, new ManualTimeProvider(Now)).Sign(Body, "delivery-1");

        var tampered = Encoding.UTF8.GetBytes("""{"event":"run.started","run_id":"r-2"}""");

        VerifyOnTheWire(Verifier(secret), headers, Now, tampered)
            .Rejection.Should()
            .Be(WebhookRejection.InvalidSignature);
    }

    [Fact]
    public void The_signer_stamps_the_injected_clock_so_a_late_send_reads_as_stale()
    {
        // The timestamp is taken from the signer's clock, not the receiver's — which is what makes a
        // delayed delivery detectable at all.
        var secret = new WebhookSigningSecret(Secret);
        var clock = new ManualTimeProvider(Now);
        var headers = new WebhookRequestSigner(secret, clock).Sign(Body, "delivery-1");

        VerifyOnTheWire(Verifier(secret), headers, Now.AddMinutes(10))
            .Rejection.Should()
            .Be(WebhookRejection.StaleTimestamp);
    }

    [Fact]
    public void A_signature_survives_a_rotation_overlap_at_the_receiver()
    {
        // The sender rotates first; the receiver, still holding the old key as previous, must keep
        // accepting in-flight deliveries signed under it. This is the case dual-key rotation exists for.
        var clock = new ManualTimeProvider(Now);
        var senderSecret = new WebhookSigningSecret(Secret, clock);
        var inFlight = new WebhookRequestSigner(senderSecret, clock).Sign(Body, "delivery-1");

        var receiverSecret = new WebhookSigningSecret(Secret, clock);
        receiverSecret.Rotate("rotated-signing-secret-9876543210", TimeSpan.FromMinutes(10));

        VerifyOnTheWire(Verifier(receiverSecret), inFlight, Now).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Applying_the_headers_stamps_the_three_wire_header_names()
    {
        var headers = new WebhookSignatureHeaders("deadbeef", "1750000000", "delivery-1");
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://example.invalid/api/auth/webhook/github");

        headers.ApplyTo(request);

        request.Headers.GetValues(WebhookHeaderNames.Signature).Should().ContainSingle().Which.Should().Be("deadbeef");
        request
            .Headers.GetValues(WebhookHeaderNames.Timestamp)
            .Should()
            .ContainSingle()
            .Which.Should()
            .Be("1750000000");
        request
            .Headers.GetValues(WebhookHeaderNames.DeliveryId)
            .Should()
            .ContainSingle()
            .Which.Should()
            .Be("delivery-1");
    }

    [Fact]
    public void Applying_the_headers_twice_replaces_rather_than_appends()
    {
        // A retry re-stamps the same request object; appending would send two signature values, which no
        // receiver would resolve the way the sender intended.
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://example.invalid/api/auth/webhook/github");

        new WebhookSignatureHeaders("aaaa", "1750000000", "delivery-1").ApplyTo(request);
        new WebhookSignatureHeaders("bbbb", "1750000060", "delivery-1").ApplyTo(request);

        request.Headers.GetValues(WebhookHeaderNames.Signature).Should().ContainSingle().Which.Should().Be("bbbb");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_delivery_id_is_rejected(string? deliveryId)
    {
        var signer = new WebhookRequestSigner(new WebhookSigningSecret(Secret), new ManualTimeProvider(Now));

        var act = () => signer.Sign(Body, deliveryId!);

        act.Should().Throw<ArgumentException>().WithParameterName("deliveryId");
    }
}
