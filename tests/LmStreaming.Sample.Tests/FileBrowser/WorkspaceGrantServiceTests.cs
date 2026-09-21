using System.Security.Cryptography;
using LmStreaming.Sample.FileBrowser;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Time.Testing;

namespace LmStreaming.Sample.Tests.FileBrowser;

/// <summary>
/// Pins the opaque, signed, time-limited READ grant that lets a header-less browser fetch — an
/// <c>&lt;iframe src&gt;</c>, an <c>&lt;img src&gt;</c>, a relative <c>&lt;link&gt;</c> inside a rendered
/// workspace page — be recognised as coming from this app (Bug#15). The grant is a bearer, so every one of
/// its four binding properties (signature, expiry, thread, principal) is a separate test: a grant that
/// validates for the wrong thread or the wrong principal is a cross-conversation read.
/// </summary>
public class WorkspaceGrantServiceTests
{
    private const string ThreadId = "t1";
    private const string Principal = "EndUser:tnt_a:oid_b";

    private static WorkspaceGrantService Build(FakeTimeProvider? time = null) =>
        new(new EphemeralDataProtectionProvider(), time ?? new FakeTimeProvider(DateTimeOffset.UtcNow));

    [Fact]
    public void Mint_ThenValidate_SameThreadAndPrincipal_Accepted()
    {
        var service = Build();
        var grant = service.Mint(ThreadId, Principal);
        service.Validate(grant.Token, ThreadId, Principal).Should().Be(WorkspaceGrantFailure.None);
    }

    [Fact]
    public void Mint_ExpiresAt_IsLifetimeAfterTheProvidersNow()
    {
        var now = new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);
        var service = Build(new FakeTimeProvider(now));
        service.Mint(ThreadId, Principal).ExpiresAt.Should().Be(now + FileBrowserLimits.WorkspaceGrantLifetime);
    }

    /// <summary>
    /// An expired grant is refused. Minted from a clock two lifetimes in the past, so its expiry is genuinely
    /// behind the real <c>UtcNow</c> the time-limited protector compares against — no waiting, no fake clock
    /// inside the protector.
    /// </summary>
    [Fact]
    public void Validate_ExpiredGrant_Refused()
    {
        var past = DateTimeOffset.UtcNow - (FileBrowserLimits.WorkspaceGrantLifetime * 2);
        var service = Build(new FakeTimeProvider(past));
        var grant = service.Mint(ThreadId, Principal);
        service.Validate(grant.Token, ThreadId, Principal).Should().Be(WorkspaceGrantFailure.Invalid);
    }

    [Fact]
    public void Validate_DifferentThread_RefusedAsThreadMismatch()
    {
        var service = Build();
        var grant = service.Mint(ThreadId, Principal);
        service.Validate(grant.Token, "t2", Principal).Should().Be(WorkspaceGrantFailure.ThreadMismatch);
    }

    [Fact]
    public void Validate_DifferentPrincipal_RefusedAsPrincipalMismatch()
    {
        var service = Build();
        var grant = service.Mint(ThreadId, Principal);
        service
            .Validate(grant.Token, ThreadId, "EndUser:tnt_a:someone_else")
            .Should()
            .Be(WorkspaceGrantFailure.PrincipalMismatch);
    }

    /// <summary>
    /// A grant minted while signed out must not validate for a signed-in principal, nor the reverse: the
    /// anonymous case is a principal value like any other, not a wildcard.
    /// </summary>
    [Fact]
    public void Validate_AnonymousGrantAgainstNamedPrincipal_Refused()
    {
        var service = Build();
        var grant = service.Mint(ThreadId, principalId: null);
        service.Validate(grant.Token, ThreadId, Principal).Should().Be(WorkspaceGrantFailure.PrincipalMismatch);
    }

    [Fact]
    public void Validate_AnonymousGrantAgainstAnonymousRequest_Accepted()
    {
        var service = Build();
        var grant = service.Mint(ThreadId, principalId: null);
        service.Validate(grant.Token, ThreadId, principalId: null).Should().Be(WorkspaceGrantFailure.None);
    }

    [Fact]
    public void Validate_TamperedToken_Refused()
    {
        var service = Build();
        var token = service.Mint(ThreadId, Principal).Token;
        // Flip one character of the payload; the signature no longer covers it.
        var tampered = token[..^2] + (token[^2] == 'A' ? 'B' : 'A') + token[^1];
        service.Validate(tampered, ThreadId, Principal).Should().Be(WorkspaceGrantFailure.Invalid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-grant")]
    public void Validate_MalformedToken_Refused(string token) =>
        Build().Validate(token, ThreadId, Principal).Should().Be(WorkspaceGrantFailure.Invalid);

    /// <summary>
    /// A token protected under a DIFFERENT purpose string (or a different key ring) is refused. This is what
    /// stops some other signed blob this app mints — an unsubscribe link, a share token — from being replayed
    /// as a workspace grant.
    /// </summary>
    [Fact]
    public void Validate_TokenFromAnotherPurpose_Refused()
    {
        var provider = new EphemeralDataProtectionProvider();
        var service = new WorkspaceGrantService(provider, new FakeTimeProvider(DateTimeOffset.UtcNow));
        var foreign = provider
            .CreateProtector("some.other.purpose")
            .ToTimeLimitedDataProtector()
            .Protect($"v1|{ThreadId}|{Principal}", DateTimeOffset.UtcNow.AddHours(1));
        service.Validate(foreign, ThreadId, Principal).Should().Be(WorkspaceGrantFailure.Invalid);
    }

    /// <summary>
    /// Two grants for the same inputs are different strings. Data Protection payloads carry random key
    /// material, so a grant is not a stable, guessable function of (thread, principal) that could be
    /// precomputed or recognised across users.
    /// </summary>
    [Fact]
    public void Mint_TwiceForSameInputs_ProducesDifferentTokens()
    {
        var service = Build();
        service.Mint(ThreadId, Principal).Token.Should().NotBe(service.Mint(ThreadId, Principal).Token);
    }

    /// <summary>
    /// The payload is escaped, so a thread id that itself contains the field delimiter cannot forge the
    /// principal field (or vice versa).
    /// </summary>
    [Fact]
    public void Validate_DelimiterInThreadId_DoesNotForgeThePrincipalField()
    {
        var service = Build();
        var grant = service.Mint($"{ThreadId}|{Principal}", principalId: null);
        service.Validate(grant.Token, ThreadId, Principal).Should().Be(WorkspaceGrantFailure.ThreadMismatch);
    }

    [Fact]
    public void Mint_NeverThrowsForAnEmptyPrincipal()
    {
        var act = () => Build().Mint(ThreadId, string.Empty);
        act.Should().NotThrow<CryptographicException>();
    }
}
