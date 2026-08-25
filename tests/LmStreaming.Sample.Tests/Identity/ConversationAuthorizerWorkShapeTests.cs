using AchieveAi.LmDotnetTools.LmCore.Identity;
using LmStreaming.Sample.Identity;
using LmStreaming.Sample.Tests.TestDoubles;

namespace LmStreaming.Sample.Tests.Identity;

/// <summary>
/// Pins that every existence-hiding refusal <see cref="ConversationAuthorizer.AuthorizeAsync"/> can
/// produce does the same SHAPE of work (#389).
/// </summary>
/// <remarks>
/// <para>
/// The three refusals - id names no row, row belongs to another tenant, row is in my tenant and
/// grants me nothing - already return byte-identical bodies. What they did not share was the grant
/// lookup: only the last one made it, so the difference between "no such conversation" and "a
/// conversation I may not read" survived as one extra round trip, and an authenticated member could
/// read it as an existence oracle for ids inside their own tenant.
/// </para>
/// <para>
/// Asserted by COUNTING the lookups, not by timing them. A single round trip is well under the
/// scheduling noise of the machine this runs on, so a wall-clock assertion would be flaky and would
/// then be widened until it passed for both shapes - a test that cannot fail, guarding the claim
/// that matters most in this file.
/// </para>
/// </remarks>
public sealed class ConversationAuthorizerWorkShapeTests
{
    private const string TenantA = "tnt_a";
    private const string TenantB = "tnt_b";
    private const string UserA = "dir-a:user-1";
    private const string UserA2 = "dir-a:user-2";
    private const string ThreadId = "thread-probed";

    private static Principal User(string tenantId = TenantA, string userId = UserA) =>
        new()
        {
            TenantId = tenantId,
            Actor = new PrincipalRef(PrincipalKind.EndUser, userId),
            Roles = new HashSet<string>(StringComparer.Ordinal),
            Source = PrincipalSource.Interactive,
        };

    private static Principal App(string tenantId = TenantA, string appId = "app-1") =>
        new()
        {
            TenantId = tenantId,
            Actor = new PrincipalRef(PrincipalKind.App, appId),
            AppId = appId,
            Source = PrincipalSource.AppOnly,
        };

    private static ThreadMetadata Row(string? tenantId, string? ownerUserId = UserA2) =>
        new()
        {
            ThreadId = ThreadId,
            LastUpdated = 0,
            TenantId = tenantId,
            OwnerUserId = ownerUserId,
            Visibility = Visibility.Private,
        };

    private static async Task<(int Lookups, ConversationAccessResult Result)> ProbeAsync(
        Principal principal,
        ThreadMetadata? metadata)
    {
        var grants = new CountingResourceGrantStore(new InMemoryResourceGrantStore());
        var authorizer = TestAuthorizers.Enforcing(principal, grants);

        var result = await authorizer.AuthorizeAsync(
            ThreadId, metadata, AccessAction.Read, CancellationToken.None);

        return (grants.FindGrantCallCount, result);
    }

    /// <summary>
    /// The three refusals an interactive member can provoke by probing an id cost one grant lookup
    /// each.
    /// </summary>
    [Fact]
    public async Task EveryExistenceHidingRefusal_CostsTheSameNumberOfGrantLookups()
    {
        var absent = await ProbeAsync(User(), metadata: null);
        var unstamped = await ProbeAsync(User(), Row(tenantId: null));
        var otherTenant = await ProbeAsync(User(), Row(TenantB));
        var sameTenantNoRelationship = await ProbeAsync(User(), Row(TenantA));

        // Non-vacuity first, and it is the assertion that gives the rest their meaning: if these
        // four did not all refuse - and refuse in the way that hides existence - then equal counts
        // would say nothing, because the outcome itself would already be the oracle.
        foreach (var (_, result) in new[] { absent, unstamped, otherTenant, sameTenantNoRelationship })
        {
            _ = result.Allowed.Should().BeFalse();
            _ = result.HidesExistence.Should().BeTrue();
        }

        _ = sameTenantNoRelationship.Lookups.Should().Be(
            1, "the policy consults the grant registry for any same-tenant non-owner");
        _ = absent.Lookups.Should().Be(sameTenantNoRelationship.Lookups);
        _ = unstamped.Lookups.Should().Be(sameTenantNoRelationship.Lookups);

        // The cross-tenant case is called out separately because the obvious narrow fix - pad only
        // the absent-row path - leaves exactly this one at zero, converting an intra-tenant oracle
        // into a cross-tenant one.
        _ = otherTenant.Lookups.Should().Be(
            sameTenantNoRelationship.Lookups,
            "a caller must not be able to tell 'this id exists in some other tenant' from 'this id "
                + "does not exist' either");
    }

    /// <summary>
    /// An app-only principal consults grants on NO path, so equalisation for it means zero lookups
    /// everywhere - not one.
    /// </summary>
    /// <remarks>
    /// The mirror image of the test above, and the reason the padding is conditional rather than
    /// unconditional. Spec 7.4 step 3 says a principal naming no end user never reaches the grant
    /// branch; padding its refusals to one lookup would make the app-only refusals differ from each
    /// other in the same way this issue is about.
    /// </remarks>
    [Fact]
    public async Task ForAnAppOnlyPrincipal_NoRefusalPathConsultsTheGrantRegistry()
    {
        var absent = await ProbeAsync(App(), metadata: null);
        var otherTenant = await ProbeAsync(App(), Row(TenantB));
        var sameTenantNoRelationship = await ProbeAsync(App(), Row(TenantA));

        foreach (var (_, result) in new[] { absent, otherTenant, sameTenantNoRelationship })
        {
            _ = result.Allowed.Should().BeFalse();
            _ = result.HidesExistence.Should().BeTrue();
        }

        _ = absent.Lookups.Should().Be(0);
        _ = otherTenant.Lookups.Should().Be(0);
        _ = sameTenantNoRelationship.Lookups.Should().Be(0);
    }

    /// <summary>
    /// The padding lookup decides nothing. A grant that exists still allows, and the reason code the
    /// route maps to a status is still the policy's.
    /// </summary>
    /// <remarks>
    /// Without this, a "fix" that swallowed the policy's answer and refused everything uniformly
    /// would satisfy every count assertion above perfectly.
    /// </remarks>
    [Fact]
    public async Task TheEqualisingLookup_DoesNotChangeAnyDecision()
    {
        var grants = new InMemoryResourceGrantStore();
        await grants.GrantAsync(
            new ResourceGrant
            {
                TenantId = TenantA,
                Resource = ConversationAuthorizer.ConversationRef(ThreadId),
                SubjectId = UserA,
                Role = GrantRole.Viewer,
                GrantedBy = UserA2,
                GrantedAt = DateTimeOffset.UnixEpoch,
                ExpiresAt = null,
            },
            CancellationToken.None);

        var authorizer = TestAuthorizers.Enforcing(User(), new CountingResourceGrantStore(grants));

        var granted = await authorizer.AuthorizeAsync(
            ThreadId, Row(TenantA), AccessAction.Read, CancellationToken.None);
        var owned = await authorizer.AuthorizeAsync(
            ThreadId, Row(TenantA, ownerUserId: UserA), AccessAction.Read, CancellationToken.None);
        var crossTenant = await authorizer.AuthorizeAsync(
            ThreadId, Row(TenantB), AccessAction.Read, CancellationToken.None);

        _ = granted.Allowed.Should().BeTrue("a viewer grant still reads");
        _ = owned.Allowed.Should().BeTrue();
        _ = crossTenant.Allowed.Should().BeFalse();
        _ = crossTenant.Reason.Should().Be(
            "cross_tenant", "the reason code is the policy's, and it is contract");
    }
}
