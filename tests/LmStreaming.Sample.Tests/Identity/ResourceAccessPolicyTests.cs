using AchieveAi.LmDotnetTools.LmCore.Identity;
using LmStreaming.Sample.Tests.TestDoubles;

namespace LmStreaming.Sample.Tests.Identity;

/// <summary>
/// Pins <see cref="ResourceAccessPolicy"/> against the evaluation order of P1 spec 7.4 and the
/// normative rights table of 7.4.1.
/// </summary>
/// <remarks>
/// The reason strings are contract, not diagnostics: 7.4.1 names them, the HTTP layer chooses
/// between <c>403</c> and <c>404</c> by reading them, and the audit trail stores them. Asserting on
/// <c>Allowed</c> alone would let a refusal for the wrong reason pass - which is how a
/// <c>cross_tenant</c> denial silently becomes a <c>403</c> that admits the resource exists.
/// </remarks>
public sealed class ResourceAccessPolicyTests
{
    private const string TenantA = "tnt_a";
    private const string TenantB = "tnt_b";
    private const string UserA = "dir-a:user-1";
    private const string UserA2 = "dir-a:user-2";

    private readonly InMemoryResourceGrantStore _grants = new();
    private readonly RecordingAuditSink _audit = new();

    private ResourceAccessPolicy CreatePolicy(bool enforce = true) =>
        new(_grants, _audit, new StaticEnforcementGate(enforce), TimeProvider.System);

    private static Principal User(
        string tenantId = TenantA,
        string userId = UserA,
        params string[] roles) =>
        new()
        {
            TenantId = tenantId,
            Actor = new PrincipalRef(PrincipalKind.EndUser, userId),
            Roles = new HashSet<string>(roles, StringComparer.Ordinal),
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

    private static ResourceDescriptor Conversation(
        string tenantId = TenantA,
        string? ownerUserId = UserA,
        string? ownerAppId = null,
        Visibility visibility = Visibility.Private,
        string id = "thread-1") =>
        new()
        {
            Ref = new ResourceRef(ResourceTypes.Conversation, id),
            TenantId = tenantId,
            OwnerUserId = ownerUserId,
            OwnerAppId = ownerAppId,
            Visibility = visibility,
        };

    /// <summary>
    /// Step 2. A tenant admin of A is refused a resource of B, and refused with
    /// <c>cross_tenant</c> - the boundary is outside the role, not inside it.
    /// </summary>
    [Theory]
    [InlineData(AccessAction.Read)]
    [InlineData(AccessAction.Write)]
    [InlineData(AccessAction.Delete)]
    [InlineData(AccessAction.Share)]
    public async Task CrossTenant_IsRefusedForEveryAction_EvenForAnAdmin(AccessAction action)
    {
        var decision = await CreatePolicy().EvaluateAsync(
            User(TenantA, UserA, "admin"),
            Conversation(TenantB, ownerUserId: UserA),
            action);

        _ = decision.Allowed.Should().BeFalse();
        _ = decision.Reason.Should().Be("cross_tenant");
    }

    /// <summary>
    /// Spec 7.1 principle 4 in C#. An unclaimed resource has a null owner, an app-only principal has
    /// a null <c>EffectiveUserId</c>, and <c>null == null</c> is true - so without the explicit
    /// non-null guard every unclaimed resource in the tenant belongs to every service credential.
    /// </summary>
    [Fact]
    public async Task AppOnlyPrincipal_DoesNotOwnAnUnclaimedResource()
    {
        var decision = await CreatePolicy().EvaluateAsync(
            App(),
            Conversation(ownerUserId: null, ownerAppId: null),
            AccessAction.Read);

        _ = decision.Allowed.Should().BeFalse();
        _ = decision.Reason.Should().Be("app_only_no_owner");
    }

    /// <summary>The same rule for a user: a null owner is not "everybody's".</summary>
    [Fact]
    public async Task User_DoesNotOwnAnUnclaimedResource()
    {
        var decision = await CreatePolicy().EvaluateAsync(
            User(),
            Conversation(ownerUserId: null),
            AccessAction.Read);

        _ = decision.Allowed.Should().BeFalse();
        _ = decision.Reason.Should().Be("no_relationship");
    }

    /// <summary>An app-only caller owns what its own app id owns.</summary>
    [Fact]
    public async Task AppOnlyPrincipal_OwnsItsOwnResource()
    {
        var decision = await CreatePolicy().EvaluateAsync(
            App(),
            Conversation(ownerUserId: null, ownerAppId: "app-1"),
            AccessAction.Write);

        _ = decision.Allowed.Should().BeTrue();
        _ = decision.Reason.Should().Be("app_owner");
    }

    /// <summary>A tenant-mate with no grant has no relationship at all - not a weaker one.</summary>
    [Fact]
    public async Task TenantMate_WithoutAGrant_HasNoRelationship()
    {
        var decision = await CreatePolicy().EvaluateAsync(
            User(TenantA, UserA2),
            Conversation(ownerUserId: UserA),
            AccessAction.Read);

        _ = decision.Allowed.Should().BeFalse();
        _ = decision.Reason.Should().Be("no_relationship");
    }

    /// <summary>A viewer grant confers read and not write (7.4.1).</summary>
    [Fact]
    public async Task ViewerGrant_ReadsButDoesNotWrite()
    {
        await GrantAsync(GrantRole.Viewer);
        var policy = CreatePolicy();

        var read = await policy.EvaluateAsync(User(TenantA, UserA2), Conversation(), AccessAction.Read);
        var write = await policy.EvaluateAsync(User(TenantA, UserA2), Conversation(), AccessAction.Write);

        _ = read.Allowed.Should().BeTrue();
        _ = read.Reason.Should().Be("grant");
        _ = write.Allowed.Should().BeFalse();
        _ = write.Reason.Should().Be("grant_does_not_confer_action");
    }

    /// <summary>An editor grant confers write, and still confers neither delete nor re-share.</summary>
    [Fact]
    public async Task EditorGrant_WritesButNeitherDeletesNorReshares()
    {
        await GrantAsync(GrantRole.Editor);
        var policy = CreatePolicy();

        var write = await policy.EvaluateAsync(User(TenantA, UserA2), Conversation(), AccessAction.Write);
        var delete = await policy.EvaluateAsync(User(TenantA, UserA2), Conversation(), AccessAction.Delete);
        var share = await policy.EvaluateAsync(User(TenantA, UserA2), Conversation(), AccessAction.Share);

        _ = write.Allowed.Should().BeTrue();
        _ = delete.Allowed.Should().BeFalse();
        _ = delete.Reason.Should().Be("grant_confers_no_delete");
        _ = share.Allowed.Should().BeFalse();
        _ = share.Reason.Should().Be("grantee_may_not_reshare");
    }

    /// <summary>An expired grant confers nothing. It is a grant that is no longer one.</summary>
    [Fact]
    public async Task ExpiredGrant_ConfersNothing()
    {
        await _grants.GrantAsync(new ResourceGrant
        {
            TenantId = TenantA,
            Resource = new ResourceRef(ResourceTypes.Conversation, "thread-1"),
            SubjectId = UserA2,
            Role = GrantRole.Editor,
            GrantedBy = UserA,
            GrantedAt = DateTimeOffset.UnixEpoch,
            ExpiresAt = DateTimeOffset.UnixEpoch.AddMinutes(1),
        });

        var decision = await CreatePolicy().EvaluateAsync(
            User(TenantA, UserA2),
            Conversation(),
            AccessAction.Read);

        _ = decision.Allowed.Should().BeFalse();
        _ = decision.Reason.Should().Be("no_relationship");
    }

    /// <summary>
    /// A tenant admin reads but does not write or delete an unpublished resource (7.4.1). "Admin"
    /// is not a superuser: an admin who could edit a member's private conversation could rewrite
    /// what that member said.
    /// </summary>
    [Fact]
    public async Task TenantAdmin_ReadsButDoesNotWriteOrDeleteAPrivateResource()
    {
        var policy = CreatePolicy();
        var admin = User(TenantA, UserA2, "admin");

        var read = await policy.EvaluateAsync(admin, Conversation(), AccessAction.Read);
        var write = await policy.EvaluateAsync(admin, Conversation(), AccessAction.Write);
        var delete = await policy.EvaluateAsync(admin, Conversation(), AccessAction.Delete);

        _ = read.Allowed.Should().BeTrue();
        _ = read.Reason.Should().Be("tenant_admin");
        _ = write.Allowed.Should().BeFalse();
        _ = write.Reason.Should().Be("admin_no_write");
        _ = delete.Allowed.Should().BeFalse();
        _ = delete.Reason.Should().Be("admin_no_delete");
    }

    /// <summary>The owner keeps every action while the resource is private.</summary>
    [Theory]
    [InlineData(AccessAction.Read)]
    [InlineData(AccessAction.Write)]
    [InlineData(AccessAction.Delete)]
    [InlineData(AccessAction.Share)]
    public async Task Owner_KeepsEveryActionWhilePrivate(AccessAction action)
    {
        var decision = await CreatePolicy().EvaluateAsync(User(), Conversation(), action);

        _ = decision.Allowed.Should().BeTrue();
        _ = decision.Reason.Should().Be("owner");
    }

    /// <summary>
    /// Step -1 runs BEFORE step 0. A type/action pair the table does not define throws even with
    /// enforcement off - otherwise the check would be dead in the configuration every pre-rollout
    /// test runs under, which is the only configuration that exists before the flip.
    /// </summary>
    [Fact]
    public async Task UndefinedActionForType_ThrowsEvenWithEnforcementOff()
    {
        var act = async () => await CreatePolicy(enforce: false).EvaluateAsync(
            User(),
            Conversation(),
            AccessAction.Publish);

        _ = await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    /// <summary>With enforcement off the policy short-circuits before it looks at anything.</summary>
    [Fact]
    public async Task EnforcementOff_AllowsWithoutConsultingOwnership()
    {
        var decision = await CreatePolicy(enforce: false).EvaluateAsync(
            User(TenantA),
            Conversation(TenantB, ownerUserId: "somebody-else"),
            AccessAction.Delete);

        _ = decision.Allowed.Should().BeTrue();
        _ = decision.Reason.Should().Be("enforcement_disabled");
    }

    /// <summary>
    /// Every decision is audited, allows included. A deny-only trail cannot answer "was this ever
    /// attempted successfully?", which is the question an incident starts from.
    /// </summary>
    [Fact]
    public async Task EveryDecision_IsAudited_AllowsIncluded()
    {
        var policy = CreatePolicy();

        _ = await policy.EvaluateAsync(User(), Conversation(), AccessAction.Read);
        _ = await policy.EvaluateAsync(User(TenantA, UserA2), Conversation(), AccessAction.Read);

        _ = _audit.Authorizations.Should().HaveCount(2);
        _ = _audit.Authorizations[0].Outcome.Should().Be(AuthorizationOutcome.Allow);
        _ = _audit.Authorizations[0].EventClass.Should().Be(AuditEventClass.Routine);
        _ = _audit.Authorizations[1].Outcome.Should().Be(AuthorizationOutcome.Deny);
        _ = _audit.Authorizations[1].EventClass.Should().Be(AuditEventClass.Security);
        _ = _audit.Authorizations[1].Reason.Should().Be("no_relationship");
    }

    private Task GrantAsync(GrantRole role) =>
        _grants.GrantAsync(new ResourceGrant
        {
            TenantId = TenantA,
            Resource = new ResourceRef(ResourceTypes.Conversation, "thread-1"),
            SubjectId = UserA2,
            Role = role,
            GrantedBy = UserA,
            GrantedAt = DateTimeOffset.UnixEpoch,
        });
}
