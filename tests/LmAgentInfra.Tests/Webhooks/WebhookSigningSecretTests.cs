using System.Text;
using AchieveAi.LmDotnetTools.LmAgentInfra.Webhooks;

namespace AchieveAi.LmDotnetTools.LmAgentInfra.Tests.Webhooks;

/// <summary>
/// ADR 0005 — the signing secret itself: the fail-closed random fallback, the tolerant handling of a
/// garbage signature header, and dual-key rotation. Rotation is net-new for this repository, so the
/// overlap window and the immediacy of revocation are pinned explicitly. The clock is driven by hand,
/// so an expiring overlap is a deterministic assertion rather than a wait.
/// </summary>
public sealed class WebhookSigningSecretTests
{
    private const string OldSecret = "test-signing-secret-0123456789";
    private const string NewSecret = "rotated-signing-secret-9876543210";
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_750_000_000);
    private static readonly byte[] Body = Encoding.UTF8.GetBytes("""{"event":"run.started"}""");
    private const string Timestamp = "1750000000";
    private const string DeliveryId = "delivery-1";

    private static string SignWith(string secret) =>
        new WebhookSigningSecret(secret).ComputeHex(Timestamp, DeliveryId, Body);

    private static bool Verifies(WebhookSigningSecret secret, string signature) =>
        secret.Matches(signature, Timestamp, DeliveryId, Body);

    [Fact]
    public void A_signature_from_the_configured_secret_verifies()
    {
        var secret = new WebhookSigningSecret(OldSecret);

        Verifies(secret, SignWith(OldSecret)).Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_unconfigured_secret_verifies_nothing(string? configured)
    {
        // Fail-closed: with no configured secret a random key is generated, so no peer can produce a
        // signature that verifies. The alternative — accepting everything — is the failure direction
        // this branch exists to avoid.
        var secret = new WebhookSigningSecret(configured);

        Verifies(secret, SignWith(OldSecret)).Should().BeFalse("no peer knows the generated key");
        secret.Value.Should().NotBeNullOrWhiteSpace("a key was still generated");
    }

    [Fact]
    public void Two_unconfigured_secrets_do_not_agree_with_each_other()
    {
        var a = new WebhookSigningSecret(null);
        var b = new WebhookSigningSecret(null);

        Verifies(b, a.ComputeHex(Timestamp, DeliveryId, Body)).Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("zzzz")]            // hex-length but not hex digits
    [InlineData("abc")]             // odd length
    [InlineData("not a signature")]
    public void A_missing_or_malformed_signature_is_false_rather_than_an_exception(string? presented)
    {
        // The "garbage header" case is attacker-controlled input, so it must be a rejection, not a
        // FormatException escaping into the pipeline.
        var secret = new WebhookSigningSecret(OldSecret);

        Verifies(secret, presented!).Should().BeFalse();
    }

    [Fact]
    public void ToString_never_discloses_key_material()
    {
        var secret = new WebhookSigningSecret(OldSecret);

        secret.ToString().Should().NotContain(OldSecret).And.Contain("[REDACTED]");
    }

    [Fact]
    public void Rotation_signs_with_the_new_key_and_still_accepts_the_previous_one_during_the_overlap()
    {
        var clock = new ManualTimeProvider(Now);
        var secret = new WebhookSigningSecret(OldSecret, clock);

        secret.Rotate(NewSecret, TimeSpan.FromMinutes(10));

        secret.Value.Should().Be(NewSecret, "signing always uses the current key");
        secret.ComputeHex(Timestamp, DeliveryId, Body).Should().Be(SignWith(NewSecret));
        secret.HasPreviousKey.Should().BeTrue();
        Verifies(secret, SignWith(NewSecret)).Should().BeTrue("the current key verifies");
        Verifies(secret, SignWith(OldSecret)).Should().BeTrue("a delivery already in flight is not rejected");
    }

    [Fact]
    public void The_previous_key_stops_verifying_once_the_overlap_lapses()
    {
        var clock = new ManualTimeProvider(Now);
        var secret = new WebhookSigningSecret(OldSecret, clock);
        secret.Rotate(NewSecret, TimeSpan.FromMinutes(10));

        clock.Advance(TimeSpan.FromMinutes(10));

        secret.HasPreviousKey.Should().BeFalse();
        Verifies(secret, SignWith(OldSecret)).Should().BeFalse("the overlap deadline has passed");
        Verifies(secret, SignWith(NewSecret)).Should().BeTrue("the current key is unaffected");
    }

    [Fact]
    public void Revoking_drops_the_previous_key_immediately()
    {
        // The compromise response: a leaked outgoing key must stop verifying at once, not at the end of
        // the window it was rotated out under.
        var clock = new ManualTimeProvider(Now);
        var secret = new WebhookSigningSecret(OldSecret, clock);
        secret.Rotate(NewSecret, TimeSpan.FromHours(24));

        secret.RevokePrevious();

        secret.HasPreviousKey.Should().BeFalse();
        Verifies(secret, SignWith(OldSecret)).Should().BeFalse("revocation takes effect without advancing the clock");
        Verifies(secret, SignWith(NewSecret)).Should().BeTrue();
    }

    [Fact]
    public void A_zero_overlap_is_a_hard_cutover()
    {
        var clock = new ManualTimeProvider(Now);
        var secret = new WebhookSigningSecret(OldSecret, clock);

        secret.Rotate(NewSecret, TimeSpan.Zero);

        secret.HasPreviousKey.Should().BeFalse();
        Verifies(secret, SignWith(OldSecret)).Should().BeFalse();
    }

    [Fact]
    public void Rotating_twice_inside_an_overlap_retains_only_the_immediately_previous_key()
    {
        // The active set is exactly two keys. A second rotation must not leave three keys verifying,
        // which would widen the window a stale key stays useful in.
        const string NewerSecret = "third-signing-secret-5555555555";
        var clock = new ManualTimeProvider(Now);
        var secret = new WebhookSigningSecret(OldSecret, clock);

        secret.Rotate(NewSecret, TimeSpan.FromMinutes(10));
        secret.Rotate(NewerSecret, TimeSpan.FromMinutes(10));

        Verifies(secret, SignWith(NewerSecret)).Should().BeTrue("current");
        Verifies(secret, SignWith(NewSecret)).Should().BeTrue("immediately previous");
        Verifies(secret, SignWith(OldSecret)).Should().BeFalse("dropped by the second rotation");
    }

    [Fact]
    public void A_never_configured_key_never_verifies_even_across_a_rotation()
    {
        const string StrangerSecret = "a-key-this-deployment-never-had";
        var clock = new ManualTimeProvider(Now);
        var secret = new WebhookSigningSecret(OldSecret, clock);
        secret.Rotate(NewSecret, TimeSpan.FromMinutes(10));

        Verifies(secret, SignWith(StrangerSecret)).Should().BeFalse();
    }

    [Fact]
    public void Rotation_rejects_a_blank_secret_or_a_negative_overlap()
    {
        var secret = new WebhookSigningSecret(OldSecret);

        var blank = () => secret.Rotate("  ", TimeSpan.FromMinutes(1));
        var negative = () => secret.Rotate(NewSecret, TimeSpan.FromMinutes(-1));

        blank.Should().Throw<ArgumentException>().WithParameterName("newSecret");
        negative.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("overlap");
    }

    [Fact]
    public void Revoking_without_a_rotation_is_a_no_op()
    {
        var secret = new WebhookSigningSecret(OldSecret);

        secret.RevokePrevious();

        Verifies(secret, SignWith(OldSecret)).Should().BeTrue("the current key is untouched");
    }
}
