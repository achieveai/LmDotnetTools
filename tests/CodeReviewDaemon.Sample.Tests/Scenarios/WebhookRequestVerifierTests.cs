using System.Text;
using CodeReviewDaemon.Sample.Auth;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

/// <summary>
/// Plan §9 — the deterministic webhook verifier + its signing-secret and replay-cache collaborators.
/// These pin the security contract the daemon enforces in front of the auth webhook: a callback is
/// accepted only when it targets a known provider, is <c>application/json</c>, is within the size cap,
/// carries the signature/timestamp/delivery headers, has a fresh timestamp, and bears an HMAC signature
/// over the exact body under that timestamp. The threat cases (tampered body, swapped timestamp, replay)
/// are the point of the layer, so they are exercised explicitly.
/// </summary>
public sealed class WebhookRequestVerifierTests
{
    private const string Secret = "test-signing-secret-0123456789";
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_750_000_000);
    private static readonly byte[] Body = Encoding.UTF8.GetBytes("""{"provider_id":"github","destination_host":"api.github.com"}""");

    private static WebhookRequestVerifier Verifier() =>
        new(new WebhookSigningSecret(Secret), ["github", "ado"], TimeSpan.FromMinutes(5), 1_048_576);

    private static WebhookVerificationInput SignedInput(
        string provider = "github",
        string? contentType = "application/json",
        byte[]? body = null,
        DateTimeOffset? timestamp = null,
        string deliveryId = "delivery-1")
    {
        body ??= Body;
        var ts = (timestamp ?? Now).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        var sig = new WebhookSigningSecret(Secret).ComputeHex(ts, deliveryId, body);
        return new WebhookVerificationInput(provider, contentType, body, sig, ts, deliveryId);
    }

    [Fact]
    public void A_correctly_signed_request_is_accepted()
    {
        Verifier().Verify(SignedInput(), Now).IsValid.Should().BeTrue();
    }

    [Fact]
    public void A_tampered_body_is_rejected_even_with_a_valid_looking_signature()
    {
        var input = SignedInput();
        // The signature was computed over the original Body; the gateway-claimed body is different.
        var tampered = input with { Body = Encoding.UTF8.GetBytes("""{"provider_id":"github","destination_host":"evil.example"}""") };

        Verifier().Verify(tampered, Now).Rejection.Should().Be(WebhookRejection.InvalidSignature);
    }

    [Fact]
    public void A_replayed_body_under_a_fresh_timestamp_is_rejected()
    {
        // Capture a valid request, then swap only the timestamp to dodge the staleness check. Because the
        // timestamp is bound into the signed payload, the signature no longer matches.
        var original = SignedInput(timestamp: Now.AddMinutes(-10));
        var swapped = original with { Timestamp = Now.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture) };

        Verifier().Verify(swapped, Now).Rejection.Should().Be(WebhookRejection.InvalidSignature);
    }

    [Fact]
    public void A_replay_with_a_changed_delivery_id_is_rejected()
    {
        // Capture a valid callback, then resend it verbatim except for a fresh delivery id (the vector
        // that would dodge the replay cache). Because the delivery id is bound into the signed payload,
        // the signature no longer matches — the swap is caught before the cache is even consulted.
        var captured = SignedInput(deliveryId: "delivery-1");
        var replayed = captured with { DeliveryId = "delivery-2" };

        Verifier().Verify(replayed, Now).Rejection.Should().Be(WebhookRejection.InvalidSignature);
    }

    [Fact]
    public void An_unknown_provider_is_rejected()
    {
        Verifier().Verify(SignedInput(provider: "gitlab"), Now).Rejection.Should().Be(WebhookRejection.UnknownProvider);
    }

    [Theory]
    [InlineData("text/plain")]
    [InlineData("")]
    [InlineData(null)]
    public void A_non_json_content_type_is_rejected(string? contentType)
    {
        Verifier().Verify(SignedInput(contentType: contentType), Now).Rejection
            .Should().Be(WebhookRejection.UnsupportedContentType);
    }

    [Fact]
    public void A_json_content_type_with_charset_parameter_is_accepted()
    {
        Verifier().Verify(SignedInput(contentType: "application/json; charset=utf-8"), Now).IsValid.Should().BeTrue();
    }

    [Fact]
    public void An_oversized_body_is_rejected()
    {
        var big = new byte[8];
        var verifier = new WebhookRequestVerifier(new WebhookSigningSecret(Secret), ["github"], TimeSpan.FromMinutes(5), maxBodyBytes: 4);
        var ts = Now.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        var input = new WebhookVerificationInput("github", "application/json", big, new WebhookSigningSecret(Secret).ComputeHex(ts, "d", big), ts, "d");

        verifier.Verify(input, Now).Rejection.Should().Be(WebhookRejection.BodyTooLarge);
    }

    [Theory]
    [InlineData(null, "1750000000", "d")]
    [InlineData("sig", null, "d")]
    [InlineData("sig", "1750000000", null)]
    public void A_missing_signature_timestamp_or_delivery_header_is_rejected(string? sig, string? ts, string? delivery)
    {
        var input = new WebhookVerificationInput("github", "application/json", Body, sig, ts, delivery);

        Verifier().Verify(input, Now).Rejection.Should().Be(WebhookRejection.MissingHeaders);
    }

    [Fact]
    public void A_stale_timestamp_outside_the_tolerance_is_rejected()
    {
        Verifier().Verify(SignedInput(timestamp: Now.AddMinutes(-10)), Now).Rejection
            .Should().Be(WebhookRejection.StaleTimestamp);
    }

    [Fact]
    public void A_future_timestamp_outside_the_tolerance_is_rejected()
    {
        Verifier().Verify(SignedInput(timestamp: Now.AddMinutes(10)), Now).Rejection
            .Should().Be(WebhookRejection.StaleTimestamp);
    }

    [Fact]
    public void An_unparseable_timestamp_is_rejected_as_stale()
    {
        var input = new WebhookVerificationInput("github", "application/json", Body, "deadbeef", "not-a-timestamp", "d");

        Verifier().Verify(input, Now).Rejection.Should().Be(WebhookRejection.StaleTimestamp);
    }
}

/// <summary>Plan §9 — the delivery-id replay cache that rejects a duplicate callback within its TTL.</summary>
public sealed class DeliveryReplayCacheTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_750_000_000);

    [Fact]
    public void A_fresh_delivery_id_registers_and_a_duplicate_is_rejected()
    {
        var cache = new DeliveryReplayCache(TimeSpan.FromMinutes(10));

        cache.TryRegister("abc", Now).Should().BeTrue("first sighting");
        cache.TryRegister("abc", Now.AddSeconds(30)).Should().BeFalse("replay within TTL");
    }

    [Fact]
    public void Distinct_delivery_ids_each_register()
    {
        var cache = new DeliveryReplayCache(TimeSpan.FromMinutes(10));

        cache.TryRegister("a", Now).Should().BeTrue();
        cache.TryRegister("b", Now).Should().BeTrue();
    }

    [Fact]
    public void A_delivery_id_is_accepted_again_after_the_ttl_lapses()
    {
        var cache = new DeliveryReplayCache(TimeSpan.FromMinutes(5));

        cache.TryRegister("abc", Now).Should().BeTrue();
        cache.TryRegister("abc", Now.AddMinutes(6)).Should().BeTrue("the prior sighting has expired");
    }
}
