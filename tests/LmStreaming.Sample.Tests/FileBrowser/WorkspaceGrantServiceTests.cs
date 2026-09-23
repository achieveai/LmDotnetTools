using System.Security.Cryptography;
using AchieveAi.LmDotnetTools.LmCore.Identity;
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
/// <remarks>
/// Under <c>Identity:Enforce</c> the grant is also the CREDENTIAL on the raw route, so the payload carries
/// the whole principal and <c>Open</c> hands it back. Those tests are the second half of this file: a field
/// dropped in the round trip is an access decision made against a principal that is not the caller's.
/// </remarks>
public class WorkspaceGrantServiceTests
{
    private const string ThreadId = "t1";

    /// <summary>The signed-in caller most tests mint for.</summary>
    private static Principal User =>
        new()
        {
            TenantId = "tnt_a",
            Actor = new PrincipalRef(PrincipalKind.EndUser, "tnt_a:oid_b"),
            Source = PrincipalSource.Interactive,
        };

    private static Principal OtherUser =>
        new()
        {
            TenantId = "tnt_a",
            Actor = new PrincipalRef(PrincipalKind.EndUser, "tnt_a:someone_else"),
            Source = PrincipalSource.Interactive,
        };

    private static WorkspaceGrantService Build(FakeTimeProvider? time = null) =>
        new(new EphemeralDataProtectionProvider(), time ?? new FakeTimeProvider(DateTimeOffset.UtcNow));

    [Fact]
    public void Mint_ThenValidate_SameThreadAndPrincipal_Accepted()
    {
        var service = Build();
        var grant = service.Mint(ThreadId, User);
        service.Validate(grant.Token, ThreadId, User).Should().Be(WorkspaceGrantFailure.None);
    }

    [Fact]
    public void Mint_ExpiresAt_IsLifetimeAfterTheProvidersNow()
    {
        var now = new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);
        var service = Build(new FakeTimeProvider(now));
        service.Mint(ThreadId, User).ExpiresAt.Should().Be(now + FileBrowserLimits.WorkspaceGrantLifetime);
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
        var grant = service.Mint(ThreadId, User);
        service.Validate(grant.Token, ThreadId, User).Should().Be(WorkspaceGrantFailure.Invalid);
    }

    [Fact]
    public void Validate_DifferentThread_RefusedAsThreadMismatch()
    {
        var service = Build();
        var grant = service.Mint(ThreadId, User);
        service.Validate(grant.Token, "t2", User).Should().Be(WorkspaceGrantFailure.ThreadMismatch);
    }

    [Fact]
    public void Validate_DifferentPrincipal_RefusedAsPrincipalMismatch()
    {
        var service = Build();
        var grant = service.Mint(ThreadId, User);
        service.Validate(grant.Token, ThreadId, OtherUser).Should().Be(WorkspaceGrantFailure.PrincipalMismatch);
    }

    /// <summary>
    /// Two parties of DIFFERENT kinds may carry the same id string, and a binding that ignored kind would let
    /// one validate as the other. The identity a grant binds to is <c>{Kind}:{Id}</c> for exactly this reason.
    /// </summary>
    [Fact]
    public void Validate_SameIdDifferentKind_RefusedAsPrincipalMismatch()
    {
        var service = Build();
        var asUser = new Principal
        {
            TenantId = "tnt_a",
            Actor = new PrincipalRef(PrincipalKind.EndUser, "same-id"),
            Source = PrincipalSource.Interactive,
        };
        var asApp = new Principal
        {
            TenantId = "tnt_a",
            Actor = new PrincipalRef(PrincipalKind.App, "same-id"),
            Source = PrincipalSource.AppOnly,
        };

        service
            .Validate(service.Mint(ThreadId, asUser).Token, ThreadId, asApp)
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
        var grant = service.Mint(ThreadId, principal: null);
        service.Validate(grant.Token, ThreadId, User).Should().Be(WorkspaceGrantFailure.PrincipalMismatch);
    }

    [Fact]
    public void Validate_AnonymousGrantAgainstAnonymousRequest_Accepted()
    {
        var service = Build();
        var grant = service.Mint(ThreadId, principal: null);
        service.Validate(grant.Token, ThreadId, principal: null).Should().Be(WorkspaceGrantFailure.None);
    }

    [Fact]
    public void Validate_TamperedToken_Refused()
    {
        var service = Build();
        var token = service.Mint(ThreadId, User).Token;
        // Flip one character of the payload; the signature no longer covers it.
        var tampered = token[..^2] + (token[^2] == 'A' ? 'B' : 'A') + token[^1];
        service.Validate(tampered, ThreadId, User).Should().Be(WorkspaceGrantFailure.Invalid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-grant")]
    public void Validate_MalformedToken_Refused(string token) =>
        Build().Validate(token, ThreadId, User).Should().Be(WorkspaceGrantFailure.Invalid);

    /// <summary>
    /// A token protected under a DIFFERENT purpose string (or a different key ring) is refused. This is what
    /// stops some other signed blob this app mints — an unsubscribe link, a share token — from being replayed
    /// as a workspace grant. The purpose is versioned, so this also covers a v1 token presented to a v2 build.
    /// </summary>
    [Fact]
    public void Validate_TokenFromAnotherPurpose_Refused()
    {
        var provider = new EphemeralDataProtectionProvider();
        var service = new WorkspaceGrantService(provider, new FakeTimeProvider(DateTimeOffset.UtcNow));
        var foreign = provider
            .CreateProtector("some.other.purpose")
            .ToTimeLimitedDataProtector()
            .Protect($"v1|{ThreadId}|EndUser:tnt_a:oid_b", DateTimeOffset.UtcNow.AddHours(1));
        service.Validate(foreign, ThreadId, User).Should().Be(WorkspaceGrantFailure.Invalid);
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
        service.Mint(ThreadId, User).Token.Should().NotBe(service.Mint(ThreadId, User).Token);
    }

    /// <summary>
    /// The thread id is a JSON VALUE in the payload, not a field in a delimited string, so no character it
    /// contains can be read as a separator and shift the principal field.
    /// </summary>
    [Fact]
    public void Validate_DelimiterInThreadId_DoesNotForgeThePrincipalField()
    {
        var service = Build();
        var grant = service.Mint($"{ThreadId}\",\"principal\":\"EndUser:tnt_a:oid_b", principal: null);
        service.Validate(grant.Token, ThreadId, User).Should().Be(WorkspaceGrantFailure.ThreadMismatch);
    }

    [Fact]
    public void Mint_NeverThrowsForAPrincipalCarryingNoScopesOrRoles()
    {
        var act = () => Build().Mint(ThreadId, User);
        act.Should().NotThrow<CryptographicException>();
    }

    // -------- Opening: the grant as a credential (Identity:Enforce) --------

    /// <summary>
    /// Every field an access decision reads must survive the round trip. A dropped tenant, role or
    /// on-behalf-of is not a cosmetic loss: it is <c>ResourceAccessPolicy</c> deciding against a principal
    /// that is not the caller's.
    /// </summary>
    [Fact]
    public void Open_RoundTripsEveryFieldAnAccessDecisionReads()
    {
        var service = Build();
        var minted = new Principal
        {
            TenantId = "tnt_a",
            Actor = new PrincipalRef(PrincipalKind.App, "app-7"),
            OnBehalfOf = new PrincipalRef(PrincipalKind.EndUser, "tnt_a:oid_b"),
            AppId = "app-7",
            Scopes = new HashSet<string>(["files.read", "chat.write"], StringComparer.Ordinal),
            Roles = new HashSet<string>(["admin"], StringComparer.Ordinal),
            Source = PrincipalSource.HostAsserted,
        };

        var opened = service.Open(service.Mint(ThreadId, minted).Token, ThreadId);

        opened.Failure.Should().Be(WorkspaceGrantFailure.None);
        var reconstructed = opened.Principal.Should().NotBeNull().And.BeOfType<Principal>().Subject;
        reconstructed.TenantId.Should().Be("tnt_a");
        reconstructed.Actor.Should().Be(new PrincipalRef(PrincipalKind.App, "app-7"));
        reconstructed.OnBehalfOf.Should().Be(new PrincipalRef(PrincipalKind.EndUser, "tnt_a:oid_b"));
        reconstructed.AppId.Should().Be("app-7");
        reconstructed.Scopes.Should().BeEquivalentTo(["files.read", "chat.write"]);
        reconstructed.Roles.Should().BeEquivalentTo(["admin"]);
        reconstructed.Source.Should().Be(PrincipalSource.HostAsserted);

        // The derived value every owner column and usage record is written from.
        reconstructed.EffectiveUserId.Should().Be("tnt_a:oid_b");
    }

    /// <summary>
    /// <c>DelegationChain</c> is deliberately NOT carried: its own contract says it is audit-only and never
    /// consulted for an access decision, so a reconstructed principal decides every question identically
    /// without it. Pinned so the omission stays a decision rather than becoming a silent bug.
    /// </summary>
    [Fact]
    public void Open_DoesNotCarryTheAuditOnlyDelegationChain()
    {
        var service = Build();
        var minted = new Principal
        {
            TenantId = "tnt_a",
            Actor = new PrincipalRef(PrincipalKind.Agent, "agent-1"),
            OnBehalfOf = new PrincipalRef(PrincipalKind.EndUser, "tnt_a:oid_b"),
            DelegationChain = [new PrincipalRef(PrincipalKind.App, "app-7")],
            Source = PrincipalSource.HostAsserted,
        };

        var opened = service.Open(service.Mint(ThreadId, minted).Token, ThreadId);

        opened.Principal!.DelegationChain.Should().BeEmpty();
        // …and the grant still validates, i.e. the omission does not change the identity it binds to.
        service
            .Validate(service.Mint(ThreadId, minted).Token, ThreadId, minted)
            .Should()
            .Be(WorkspaceGrantFailure.None);
    }

    [Fact]
    public void Open_AnonymousGrant_SucceedsWithNoPrincipal()
    {
        var service = Build();
        var opened = service.Open(service.Mint(ThreadId, principal: null).Token, ThreadId);
        opened.Failure.Should().Be(WorkspaceGrantFailure.None);
        opened.Principal.Should().BeNull();
    }

    /// <summary>
    /// Opening answers the thread question but never the principal one — it does not know who is asking.
    /// A grant pointed at another conversation yields no principal, so a caller cannot reach for one anyway.
    /// </summary>
    [Fact]
    public void Open_GrantForAnotherThread_ReportsThreadMismatchAndNoPrincipal()
    {
        var service = Build();
        var opened = service.Open(service.Mint(ThreadId, User).Token, "t2");
        opened.Failure.Should().Be(WorkspaceGrantFailure.ThreadMismatch);
        opened.Principal.Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-grant")]
    public void Open_MalformedToken_IsInvalidAndYieldsNoPrincipal(string token)
    {
        var opened = Build().Open(token, ThreadId);
        opened.Failure.Should().Be(WorkspaceGrantFailure.Invalid);
        opened.Principal.Should().BeNull();
    }

    [Fact]
    public void IdentityOf_NoPrincipal_IsTheAnonymousValue() =>
        WorkspaceGrantService.IdentityOf(null).Should().Be(WorkspaceGrantService.AnonymousPrincipalId);

    [Fact]
    public void IdentityOf_NamesTheActorsKindAndId() =>
        WorkspaceGrantService.IdentityOf(User).Should().Be("EndUser:tnt_a:oid_b");
}
