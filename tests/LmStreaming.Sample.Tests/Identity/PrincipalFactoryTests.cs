using System.Globalization;
using System.Security.Claims;
using AchieveAi.LmDotnetTools.LmCore.Identity;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence.Sqlite;
using LmStreaming.Sample.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace LmStreaming.Sample.Tests.Identity;

/// <summary>
/// Records everything written to it, so a test can assert on the audit trail rather than on a log
/// string.
/// </summary>
internal sealed class RecordingAuditSink : IAuditSink
{
    public List<AuthenticationAuditRecord> Authentications { get; } = [];

    public List<AuthorizationAuditRecord> Authorizations { get; } = [];

    public List<AdministrationAuditRecord> Administrations { get; } = [];

    public void Write(AuthenticationAuditRecord record) => Authentications.Add(record);

    public void Write(AuthorizationAuditRecord record) => Authorizations.Add(record);

    public void Write(AdministrationAuditRecord record) => Administrations.Add(record);
}

/// <summary>
/// A tenant store whose backing database is unavailable. Every member throws, because an outage
/// does not politely restrict itself to the one call a test happens to exercise.
/// </summary>
internal sealed class UnavailableTenantStore : ITenantStore
{
    public const string Message = "database is locked";

    private static InvalidOperationException Fail() => new(Message);

    public Task<TenantRecord?> FindByEntraTenantIdAsync(string entraTenantId, CancellationToken ct = default) =>
        throw Fail();

    public Task<TenantRecord?> FindByTenantIdAsync(string tenantId, CancellationToken ct = default) =>
        throw Fail();

    public Task<TenantProvisionOutcome> ProvisionAsync(
        TenantRecord tenant,
        string firstAdminUpn,
        CancellationToken ct = default) => throw Fail();

    public Task<bool> TryBindFirstAdminAsync(
        string tenantId,
        string upn,
        string userId,
        DateTimeOffset boundAt,
        CancellationToken ct = default) => throw Fail();

    public Task<bool> IsTenantAdminAsync(string tenantId, string userId, CancellationToken ct = default) =>
        throw Fail();

    public Task<int> NormalizeEntraTenantIdsAsync(CancellationToken ct = default) => throw Fail();

    public Task<bool> TryEnsureQuarantineTenantAsync(
        string tenantId,
        DateTimeOffset createdAt,
        CancellationToken ct = default) => throw Fail();
}

/// <summary>
/// Pins <see cref="PrincipalFactory"/> against the sign-in acceptance criteria of issue #301.
/// </summary>
/// <remarks>
/// Runs against a REAL <see cref="SqliteTenantStore"/> over a temp file rather than a mock. The
/// clause that matters most - "a rejected sign-in does not create a tenant" - is a claim about
/// rows, and a mocked store can only prove that a method nobody called was not called. Here the
/// count comes from SQL.
/// </remarks>
public sealed class PrincipalFactoryTests : IAsyncLifetime
{
    private const string EntraTenant = "11111111-1111-1111-1111-111111111111";
    private const string ObjectId = "22222222-2222-2222-2222-222222222222";
    private const string InternalTenant = "tnt_acme";
    private const string AdminUpn = "ada@acme.example";

    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private string _databasePath = null!;
    private SqliteConnectionFactory _factory = null!;
    private SqliteTenantStore _store = null!;
    private RecordingAuditSink _audit = null!;
    private FakeTimeProvider _time = null!;

    public async Task InitializeAsync()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"identity_{Guid.NewGuid():N}.db");
        _factory = new SqliteConnectionFactory(_databasePath);

        // Applied up front rather than left to the store's lazy initialization, so that the
        // "no tenant was created" assertion can read the table BEFORE the code under test has run.
        // Without this, the before-count would throw "no such table" and the test could only ever
        // observe the after-state.
        await SqliteSchemaInitializer.InitializeSchemaAsync(_factory);

        _store = new SqliteTenantStore(_factory);
        _audit = new RecordingAuditSink();
        _time = new FakeTimeProvider(T0);
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        SqliteConnection.ClearAllPools();
        await Task.Delay(50);
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try
            {
                if (File.Exists(_databasePath + suffix))
                {
                    File.Delete(_databasePath + suffix);
                }
            }
            catch (IOException)
            {
                // A leaked temp file is not a test failure.
            }
        }
    }

    private PrincipalFactory CreateFactory(IdentityOptions? options = null) =>
        CreateFactory(_store, options);

    private PrincipalFactory CreateFactory(ITenantStore store, IdentityOptions? options = null) =>
        new(
            store,
            _audit,
            Options.Create(options ?? new IdentityOptions()),
            _time,
            NullLogger<PrincipalFactory>.Instance);

    private static ClaimsPrincipal Token(
        string? tid = EntraTenant,
        string? oid = ObjectId,
        string? upn = AdminUpn,
        string? jti = "jti-1")
    {
        List<Claim> claims = [];
        if (tid is not null)
        {
            claims.Add(new Claim("tid", tid));
        }

        if (oid is not null)
        {
            claims.Add(new Claim("oid", oid));
        }

        if (upn is not null)
        {
            claims.Add(new Claim("preferred_username", upn));
        }

        if (jti is not null)
        {
            claims.Add(new Claim("jti", jti));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "TestBearer"));
    }

    private Task<TenantProvisionOutcome> ProvisionAsync(
        TenantStatus status = TenantStatus.Active,
        string firstAdminUpn = AdminUpn) =>
        _store.ProvisionAsync(
            new TenantRecord
            {
                TenantId = InternalTenant,
                EntraTenantId = EntraTenant,
                DisplayName = "Acme Corp",
                Status = status,
                CreatedAt = T0,
                CreatedBy = "operator",
            },
            firstAdminUpn);

    /// <summary>Counts tenant rows straight from SQL, so the assertion does not depend on the store's API.</summary>
    private async Task<long> CountTenantsAsync()
    {
        await using var connection = await _factory.GetConnectionAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM tenants;";
        return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    [Fact]
    public async Task ASignedInUserFromAProvisionedTenant_GetsAnInteractiveEndUserPrincipal()
    {
        _ = await ProvisionAsync();

        var resolution = await CreateFactory().ResolveInteractiveAsync(Token(), "corr-1");

        _ = resolution.IsRejected.Should().BeFalse();
        var principal = resolution.Principal!;
        _ = principal.Source.Should().Be(PrincipalSource.Interactive);
        _ = principal.Actor.Kind.Should().Be(PrincipalKind.EndUser);

        // The namespaced pair, not the bare oid: an oid is unique only within its directory.
        _ = principal.Actor.Id.Should().Be($"{EntraTenant}:{ObjectId}");

        // Our internal id, NOT the Entra tid. Every downstream scope check reads this value, so a
        // raw tid leaking through here would key tenant data by the customer's directory id.
        _ = principal.TenantId.Should().Be(InternalTenant);
        _ = principal.OnBehalfOf.Should().BeNull();
        _ = principal.EffectiveUserId.Should().Be($"{EntraTenant}:{ObjectId}");
    }

    [Fact]
    public async Task AValidTokenFromAnUnprovisionedTenant_IsRefusedAndCreatesNoTenant()
    {
        var before = await CountTenantsAsync();

        var resolution = await CreateFactory().ResolveInteractiveAsync(Token(), "corr-1");

        _ = resolution.IsRejected.Should().BeTrue();
        _ = resolution.Code.Should().Be(PrincipalResolution.TenantNotProvisioned);
        _ = resolution.Principal.Should().BeNull();

        // 403, not 401. A 401 is what tells a browser to sign in again, and signing in again cannot
        // conjure a provisioned tenant - so answering 401 here is an infinite loop.
        _ = resolution.StatusCode.Should().Be(403);

        // The load-bearing half of this clause. Counted from SQL, not from the store's API, and
        // both before and after, so a store that auto-created on read could not hide behind an
        // already-nonzero baseline.
        _ = before.Should().Be(0);
        _ = (await CountTenantsAsync()).Should().Be(0);
    }

    [Fact]
    public async Task ARefusedSignIn_IsAuditedAsASecurityEvent()
    {
        _ = await CreateFactory().ResolveInteractiveAsync(Token(), "corr-7");

        var record = _audit.Authentications.Should().ContainSingle().Subject;
        _ = record.Outcome.Should().Be(AuthenticationOutcome.Rejected);
        _ = record.Reason.Should().Be(PrincipalResolution.TenantNotProvisioned);
        _ = record.EventClass.Should().Be(AuditEventClass.Security);
        _ = record.ClaimedEntraTenantId.Should().Be(EntraTenant);
        _ = record.ResolvedTenantId.Should().BeNull();
        _ = record.CorrelationId.Should().Be("corr-7");
        _ = record.FrontDoor.Should().Be(AuditFrontDoor.Interactive);
    }

    [Fact]
    public async Task AnAcceptedSignIn_IsAuditedToo()
    {
        _ = await ProvisionAsync();

        _ = await CreateFactory().ResolveInteractiveAsync(Token(), "corr-8");

        // A deny-only trail cannot answer "did this ever succeed, and when?".
        var record = _audit.Authentications.Should().ContainSingle().Subject;
        _ = record.Outcome.Should().Be(AuthenticationOutcome.Accepted);
        _ = record.EventClass.Should().Be(AuditEventClass.Routine);
        _ = record.ResolvedTenantId.Should().Be(InternalTenant);
        _ = record.Jti.Should().Be("jti-1");
    }

    [Fact]
    public async Task ARejectedUpn_IsNotAuditedUnlessTheDeploymentAsksForIt()
    {
        _ = await CreateFactory().ResolveInteractiveAsync(Token(), "corr-9");
        _ = _audit.Authentications.Should().ContainSingle().Subject.ClaimedUpn.Should().BeNull();

        _audit.Authentications.Clear();

        var withUpn = CreateFactory(new IdentityOptions { Audit = new IdentityAuditOptions { IncludeUpn = true } });
        _ = await withUpn.ResolveInteractiveAsync(Token(), "corr-9");
        _ = _audit.Authentications.Should().ContainSingle().Subject.ClaimedUpn.Should().Be(AdminUpn);
    }

    [Fact]
    public async Task ASuspendedTenant_IsRefusedWithItsOwnCode()
    {
        _ = await ProvisionAsync(TenantStatus.Suspended);

        var resolution = await CreateFactory().ResolveInteractiveAsync(Token(), "corr-2");

        _ = resolution.IsRejected.Should().BeTrue();
        _ = resolution.Code.Should().Be(PrincipalResolution.TenantSuspended);
        _ = resolution.StatusCode.Should().Be(403);

        // Distinguishable from not-provisioned in the trail, which is the whole reason the two
        // codes are separate: support answers them differently.
        var record = _audit.Authentications.Should().ContainSingle().Subject;
        _ = record.Reason.Should().Be(PrincipalResolution.TenantSuspended);
        _ = record.ResolvedTenantId.Should().Be(InternalTenant);
    }

    [Theory]
    [InlineData(null, ObjectId)]
    [InlineData(EntraTenant, null)]
    [InlineData(null, null)]
    public async Task ATokenMissingEitherHalfOfTheUserKey_IsRefusedAsInvalidAndMayBeRetried(
        string? tid,
        string? oid)
    {
        _ = await ProvisionAsync();

        var resolution = await CreateFactory().ResolveInteractiveAsync(Token(tid, oid), "corr-3");

        _ = resolution.IsRejected.Should().BeTrue();
        _ = resolution.Code.Should().Be(PrincipalResolution.InvalidToken);

        // 401 here, unlike the tenant refusals: the token never established a usable identity, so a
        // better token genuinely could succeed.
        _ = resolution.StatusCode.Should().Be(401);
    }

    [Fact]
    public async Task TheNamedFirstAdmin_BindsOnFirstSignInAndIsNeverReboundAfterwards()
    {
        _ = await ProvisionAsync();
        var factory = CreateFactory();

        var first = await factory.ResolveInteractiveAsync(Token(), "corr-4");
        _ = first.Principal!.Roles.Should().Contain("admin");
        _ = first.Principal.Roles.Should().Contain("member");

        var boundAt = await ReadBoundAtAsync();
        _ = boundAt.Should().Be(T0.ToUnixTimeMilliseconds());

        // A SECOND sign-in, by a DIFFERENT user whose UPN happens to match the seeded one. If the
        // bind were re-applied, the row would move to this impostor's id and hand them admin.
        _time.Advance(TimeSpan.FromDays(30));
        const string ImpostorOid = "33333333-3333-3333-3333-333333333333";
        var second = await factory.ResolveInteractiveAsync(Token(oid: ImpostorOid), "corr-5");

        _ = second.Principal!.Roles.Should().NotContain("admin");
        _ = (await ReadBoundUserIdAsync()).Should().Be($"{EntraTenant}:{ObjectId}");
        _ = (await ReadBoundAtAsync()).Should().Be(T0.ToUnixTimeMilliseconds());
    }

    [Fact]
    public async Task RepeatedIdenticalRefusals_AreDeduplicatedButTheFirstIsAlwaysRecorded()
    {
        var options = new IdentityOptions
        {
            Audit = new IdentityAuditOptions { RejectionDeduplicationWindow = TimeSpan.FromMinutes(5) },
        };
        var factory = CreateFactory(options);

        for (var i = 0; i < 4; i++)
        {
            _ = await factory.ResolveInteractiveAsync(Token(), "corr-6");
        }

        // Deduplication, not sampling: the first of the burst is the record an operator acts on.
        _ = _audit.Authentications.Should().ContainSingle();

        // Past the window the next occurrence is recorded again, so a persistent problem stays
        // visible instead of being silenced forever by its own first occurrence.
        _time.Advance(TimeSpan.FromMinutes(6));
        _ = await factory.ResolveInteractiveAsync(Token(), "corr-6");
        _ = _audit.Authentications.Should().HaveCount(2);
    }

    [Fact]
    public async Task ADifferentRefusalReason_IsNotSuppressedByAnEarlierOne()
    {
        var factory = CreateFactory(new IdentityOptions
        {
            Audit = new IdentityAuditOptions { RejectionDeduplicationWindow = TimeSpan.FromMinutes(5) },
        });

        _ = await factory.ResolveInteractiveAsync(Token(), "corr-a");
        _ = await ProvisionAsync(TenantStatus.Suspended);
        _ = await factory.ResolveInteractiveAsync(Token(), "corr-b");

        // Two different reasons from the same directory inside one window. Keying the throttle on
        // the tenant alone would hide the second, which is the transition an operator most needs
        // to see.
        _ = _audit.Authentications.Select(r => r.Reason).Should().BeEquivalentTo(
            [PrincipalResolution.TenantNotProvisioned, PrincipalResolution.TenantSuspended]);
    }

    [Fact]
    public void TheDevelopmentPrincipal_CarriesTheLegacyTenantAndIsInteractive()
    {
        var principal = CreateFactory(new IdentityOptions { LegacyTenantId = "legacy" })
            .CreateDevelopmentPrincipal();

        _ = principal.TenantId.Should().Be("legacy");
        _ = principal.Source.Should().Be(PrincipalSource.Interactive);
        _ = principal.Actor.Kind.Should().Be(PrincipalKind.EndUser);
    }

    /// <summary>
    /// Builds a token whose identifier claim is carried under an arbitrary claim type, so a test
    /// can present <c>email</c> or the mapped <c>ClaimTypes.Email</c>/<c>ClaimTypes.Upn</c> URI
    /// with no <c>preferred_username</c> at all - the exact shape #349 is about.
    /// </summary>
    private static ClaimsPrincipal TokenWithIdentifierClaim(string claimType, string value)
    {
        List<Claim> claims =
        [
            new Claim("tid", EntraTenant),
            new Claim("oid", ObjectId),
            new Claim(claimType, value),
            new Claim("jti", "jti-349"),
        ];

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "TestBearer"));
    }

    /// <summary>
    /// Spec 8.2 and <see cref="ITenantStore.TryBindFirstAdminAsync"/> both say
    /// <c>preferred_username</c> is the ONLY claim the first-admin binding trusts. These are the
    /// claims that used to be trusted alongside it (#349).
    /// </summary>
    /// <remarks>
    /// <c>MapInboundClaims</c> defaults to true, so a raw <c>email</c> claim arrives as
    /// <see cref="ClaimTypes.Email"/> and a raw <c>upn</c> as <see cref="ClaimTypes.Upn"/>. Both
    /// forms are asserted, because a fix that narrows only the short names would still admit the
    /// mapped ones - and the mapped ones are what the pipeline actually delivers.
    /// </remarks>
    public static TheoryData<string> ClaimsThatMustNotBindTheFirstAdmin() =>
        [ClaimTypes.Email, ClaimTypes.Upn, "email", "upn"];

    [Theory]
    [MemberData(nameof(ClaimsThatMustNotBindTheFirstAdmin))]
    public async Task AClaimOtherThanPreferredUsername_DoesNotBindTheFirstAdmin(string claimType)
    {
        _ = await ProvisionAsync();

        // The value MATCHES the recorded firstAdminUpn exactly. That is the point: the binding must
        // be refused on the strength of WHICH claim carried it, not on the value being wrong.
        var resolution = await CreateFactory()
            .ResolveInteractiveAsync(TokenWithIdentifierClaim(claimType, AdminUpn), "corr-349");

        // The sign-in itself still succeeds - this is about the one-shot admin grant, not about
        // admitting the user.
        _ = resolution.Principal.Should().NotBeNull();
        _ = (await ReadBoundUserIdAsync()).Should().BeNull();
        _ = (await ReadBoundAtAsync()).Should().BeNull();
        _ = resolution.Principal!.Roles.Should().NotContain("admin");
    }

    [Fact]
    public async Task PreferredUsername_StillBindsTheFirstAdmin()
    {
        // The non-vacuity half of the theory above: with the narrowing in place the ONE claim the
        // spec names must still work, or the theory would pass just as well against a binding path
        // that had been deleted outright.
        _ = await ProvisionAsync();

        var resolution = await CreateFactory()
            .ResolveInteractiveAsync(TokenWithIdentifierClaim("preferred_username", AdminUpn), "corr-349b");

        _ = (await ReadBoundUserIdAsync()).Should().Be($"{EntraTenant}:{ObjectId}");
        _ = resolution.Principal!.Roles.Should().Contain("admin");
    }

    [Fact]
    public async Task ARejectedSignInStillAudits_TheWiderIdentifierClaim()
    {
        // The narrowing applies to BINDING, not to the audit trail. An operator diagnosing a
        // refusal needs a human-readable identifier, and that value authorizes nothing - so the
        // wide claim set stays wide exactly here, and nowhere else.
        var factory = CreateFactory(new IdentityOptions
        {
            Audit = new IdentityAuditOptions { IncludeUpn = true },
        });

        // No tenant provisioned, so this rejects with tenant_not_provisioned.
        _ = await factory.ResolveInteractiveAsync(
            TokenWithIdentifierClaim(ClaimTypes.Email, AdminUpn),
            "corr-349c");

        _ = _audit.Authentications.Should().ContainSingle().Subject.ClaimedUpn.Should().Be(AdminUpn);
    }

    private async Task<string?> ReadBoundUserIdAsync()
    {
        await using var connection = await _factory.GetConnectionAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT user_id FROM tenant_admins WHERE tenant_id = $t AND upn = $u;";
        _ = command.Parameters.AddWithValue("$t", InternalTenant);
        _ = command.Parameters.AddWithValue("$u", AdminUpn);
        return await command.ExecuteScalarAsync() as string;
    }

    private async Task<long?> ReadBoundAtAsync()
    {
        await using var connection = await _factory.GetConnectionAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT bound_at FROM tenant_admins WHERE tenant_id = $t AND upn = $u;";
        _ = command.Parameters.AddWithValue("$t", InternalTenant);
        _ = command.Parameters.AddWithValue("$u", AdminUpn);
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? null : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }
    [Fact]
    public async Task AnUnreachableTenantStore_IsRefusedAsUnavailableRatherThanAsABadToken()
    {
        // The token here is perfectly good; it is the tenant DIRECTORY that cannot be read. If that
        // surfaces as an authentication failure, the JwtBearer handler answers 401, the SPA reads
        // 401 as "not signed in" and starts sign-in again, Entra returns the same good token, and
        // the outage becomes an infinite redirect loop against the identity provider - the exact
        // failure this design refuses everywhere else. It has to be a 503: a statement that the
        // SERVER is unwell, which no amount of signing in again can fix.
        var factory = CreateFactory(new UnavailableTenantStore(), new IdentityOptions { Enforce = true });

        var resolution = await factory.ResolveInteractiveAsync(Token(), "trace-unavailable");

        Assert.True(resolution.IsRejected);
        Assert.Equal("identity_unavailable", resolution.Code);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, resolution.StatusCode);
    }

    [Fact]
    public async Task AnUnreachableTenantStore_IsAuditedSoTheOutageIsVisible()
    {
        var factory = CreateFactory(new UnavailableTenantStore(), new IdentityOptions { Enforce = true });

        _ = await factory.ResolveInteractiveAsync(Token(), "trace-unavailable");

        var record = _audit.Authentications.Should().ContainSingle().Subject;
        _ = record.Outcome.Should().Be(AuthenticationOutcome.Rejected);
        _ = record.Reason.Should().Be("identity_unavailable");
    }

}
