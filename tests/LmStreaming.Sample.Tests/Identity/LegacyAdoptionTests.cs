using AchieveAi.LmDotnetTools.LmCore.Identity;
using Microsoft.AspNetCore.Http;
using LmStreaming.Sample.Identity;
using LmStreaming.Sample.Tests.TestDoubles;
using Microsoft.Extensions.Options;

namespace LmStreaming.Sample.Tests.Identity;

/// <summary>
/// Pins the two halves of the legacy path of P1 spec 8.5: the startup repair that stamps unclaimed
/// conversations with the quarantine tenant, and the operator route that moves them out of it.
/// </summary>
/// <remarks>
/// Both are one-way doors over customer data. The repair decides what an un-migrated conversation
/// becomes, and adoption decides who it then belongs to - so what is asserted here is mostly what
/// they refuse to do: not stamp with a real tenant's id, not adopt into a directory the owner is
/// not in, and not write anything on a rehearsal.
/// </remarks>
public sealed class LegacyAdoptionTests
{
    private const string Quarantine = "legacy";
    private const string Acme = "tnt_acme";
    private const string AcmeDirectory = "11111111-1111-1111-1111-111111111111";

    private readonly RecordingTenantStore _tenants = new();
    private readonly RecordingAuditSink _audit = new();
    private readonly InMemoryConversationStore _store = new();

    private static IOptions<IdentityOptions> IdentityConfig(bool enforce = false) =>
        Options.Create(new IdentityOptions { Enforce = enforce, LegacyTenantId = Quarantine });

    /// <summary>
    /// Builds the real controller. The operator-secret guard is an action filter and so is absent
    /// here by construction; it is pinned by <see cref="TenantsControllerTests"/> behind real
    /// routing, and repeating it would prove the filter refuses rather than that the route carries
    /// it. What these tests need instead is a <see cref="HttpContext"/>, because every path through
    /// the route writes an audit record that reads the caller's address from one.
    /// </summary>
    private TenantsController CreateController() =>
        new(
            _tenants,
            _audit,
            TimeProvider.System,
            _store,
            IdentityConfig(),
            NullLogger<TenantsController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

    private ConversationOwnershipRepairHostedService CreateRepair(bool enforce = false) =>
        new(
            _tenants,
            _store,
            IdentityConfig(enforce),
            TimeProvider.System,
            NullLogger<ConversationOwnershipRepairHostedService>.Instance);

    private void SeedTenant(
        string tenantId = Acme,
        string? entraTenantId = AcmeDirectory,
        TenantStatus status = TenantStatus.Active) =>
        _tenants.Provisioned.Add(new TenantRecord
        {
            TenantId = tenantId,
            EntraTenantId = entraTenantId,
            DisplayName = tenantId,
            Status = status,
            CreatedAt = DateTimeOffset.UnixEpoch,
            CreatedBy = "operator",
        });

    private Task SeedThreadAsync(string threadId, string? tenantId) =>
        _store.UpdateMetadataAsync(
            threadId,
            _ => new ThreadMetadata
            {
                ThreadId = threadId,
                LastUpdated = 1_000,
                TenantId = tenantId,
            },
            CancellationToken.None);

    private static AdoptLegacyRequest Request(
        string? ownerUserId = null,
        bool dryRun = false,
        IList<string>? resourceIds = null,
        string resourceType = AdoptLegacyResourceTypes.Thread) =>
        new()
        {
            OwnerUserId = ownerUserId,
            DryRun = dryRun,
            ResourceIds = resourceIds,
            ResourceType = resourceType,
        };

    /// <summary>
    /// A rehearsal reports what it would move and moves nothing. Asserting on the rows rather than
    /// only on the response flag is the point: a rehearsal that reported correctly AFTER writing
    /// would look identical from the response alone.
    /// </summary>
    [Fact]
    public async Task DryRunAdoption_ReportsTheCountAndMovesNothing()
    {
        SeedTenant();
        await SeedThreadAsync("legacy-1", Quarantine);
        await SeedThreadAsync("legacy-2", Quarantine);

        var ok = Assert.IsType<OkObjectResult>(
            await CreateController().AdoptLegacyAsync(Acme, Request(dryRun: true), CancellationToken.None));
        var response = Assert.IsType<AdoptLegacyResponse>(ok.Value);

        _ = response.AffectedCount.Should().Be(2);
        _ = response.DryRun.Should().BeTrue();
        _ = response.Sample.Should().BeEquivalentTo(["legacy-1", "legacy-2"]);

        var untouched = await _store.LoadMetadataAsync("legacy-1", CancellationToken.None);
        _ = untouched!.TenantId.Should().Be(Quarantine);
    }

    /// <summary>
    /// A rehearsal still audits. The record is the only evidence that an operator secret holder
    /// enumerated a tenant's quarantined conversations, and "it changed nothing" is not a reason to
    /// leave that unrecorded.
    /// </summary>
    [Fact]
    public async Task DryRunAdoption_IsAudited()
    {
        SeedTenant();
        await SeedThreadAsync("legacy-1", Quarantine);

        _ = await CreateController().AdoptLegacyAsync(Acme, Request(dryRun: true), CancellationToken.None);

        var record = _audit.Administrations.Should().ContainSingle().Subject;
        _ = record.Outcome.Should().Be(AdministrationOutcome.Rehearsed);
        _ = record.DryRun.Should().BeTrue();
        _ = record.AffectedCount.Should().Be(1);
        _ = record.TargetTenantId.Should().Be(Acme);
    }

    /// <summary>An applied adoption moves the rows and assigns the owner.</summary>
    [Fact]
    public async Task Adoption_MovesTheRowsAndAssignsTheOwner()
    {
        SeedTenant();
        await SeedThreadAsync("legacy-1", Quarantine);

        var ok = Assert.IsType<OkObjectResult>(await CreateController().AdoptLegacyAsync(
            Acme,
            Request(ownerUserId: AcmeDirectory + ":ada"),
            CancellationToken.None));

        _ = Assert.IsType<AdoptLegacyResponse>(ok.Value).AffectedCount.Should().Be(1);

        var adopted = await _store.LoadMetadataAsync("legacy-1", CancellationToken.None);
        _ = adopted!.TenantId.Should().Be(Acme);
        _ = adopted.OwnerUserId.Should().Be(AcmeDirectory + ":ada");
    }

    /// <summary>
    /// Running adoption twice adopts once. It selects on the SOURCE tenant, so the second pass
    /// finds nothing - which is what makes a retry after a timeout safe rather than a second sweep
    /// that would drag in whatever was quarantined since.
    /// </summary>
    [Fact]
    public async Task Adoption_IsIdempotent()
    {
        SeedTenant();
        await SeedThreadAsync("legacy-1", Quarantine);
        var controller = CreateController();

        _ = await controller.AdoptLegacyAsync(Acme, Request(), CancellationToken.None);
        var second = Assert.IsType<OkObjectResult>(
            await controller.AdoptLegacyAsync(Acme, Request(), CancellationToken.None));

        _ = Assert.IsType<AdoptLegacyResponse>(second.Value).AffectedCount.Should().Be(0);
    }

    /// <summary>
    /// An owner from another Entra directory is refused BEFORE anything is written. Adopting into a
    /// tenant with an owner id that 7.4 step 2 then denies would re-quarantine the data under a
    /// name that looks adopted.
    /// </summary>
    [Fact]
    public async Task ForeignDirectoryOwner_IsRefusedAndNothingMoves()
    {
        SeedTenant();
        await SeedThreadAsync("legacy-1", Quarantine);

        var refused = Assert.IsType<ObjectResult>(await CreateController().AdoptLegacyAsync(
            Acme,
            Request(ownerUserId: "22222222-2222-2222-2222-222222222222:mallory"),
            CancellationToken.None));

        _ = refused.StatusCode.Should().Be(400);
        _ = JsonSerializer.Serialize(refused.Value).Should().Contain("owner_tenant_mismatch");

        var untouched = await _store.LoadMetadataAsync("legacy-1", CancellationToken.None);
        _ = untouched!.TenantId.Should().Be(Quarantine);
        _ = _audit.Administrations.Should().ContainSingle()
            .Which.Outcome.Should().Be(AdministrationOutcome.Rejected);
    }

    /// <summary>
    /// A suspended tenant answers exactly as an absent one does. Distinguishing them would make the
    /// route a directory of which tenant ids exist but are switched off.
    /// </summary>
    [Fact]
    public async Task SuspendedTenant_IsAnsweredAsNotFound()
    {
        SeedTenant(status: TenantStatus.Suspended);
        await SeedThreadAsync("legacy-1", Quarantine);

        var refused = Assert.IsType<ObjectResult>(
            await CreateController().AdoptLegacyAsync(Acme, Request(), CancellationToken.None));
        var absent = Assert.IsType<ObjectResult>(
            await CreateController().AdoptLegacyAsync("tnt_nope", Request(), CancellationToken.None));

        _ = refused.StatusCode.Should().Be(404);
        _ = absent.StatusCode.Should().Be(404);
        _ = JsonSerializer.Serialize(refused.Value)
            .Should().Be(JsonSerializer.Serialize(absent.Value));
    }

    /// <summary>Adopting into the quarantine tenant is refused rather than reported as a no-op success.</summary>
    [Fact]
    public async Task AdoptingIntoTheQuarantineTenant_IsRefused()
    {
        SeedTenant(tenantId: Quarantine, entraTenantId: null);
        await SeedThreadAsync("legacy-1", Quarantine);

        var refused = Assert.IsType<ObjectResult>(
            await CreateController().AdoptLegacyAsync(Quarantine, Request(), CancellationToken.None));

        _ = refused.StatusCode.Should().Be(400);
        _ = JsonSerializer.Serialize(refused.Value).Should().Contain("target_is_quarantine_tenant");
    }

    /// <summary>
    /// An explicitly EMPTY id list adopts nothing. Normalising it to "omitted" would turn a request
    /// that named no conversations into one that swept every quarantined conversation there is.
    /// </summary>
    [Fact]
    public async Task EmptyResourceIdList_AdoptsNothing()
    {
        SeedTenant();
        await SeedThreadAsync("legacy-1", Quarantine);

        var ok = Assert.IsType<OkObjectResult>(await CreateController().AdoptLegacyAsync(
            Acme,
            Request(resourceIds: []),
            CancellationToken.None));

        _ = Assert.IsType<AdoptLegacyResponse>(ok.Value).AffectedCount.Should().Be(0);
        _ = (await _store.LoadMetadataAsync("legacy-1", CancellationToken.None))!.TenantId
            .Should().Be(Quarantine);
    }

    /// <summary>A resource type the route does not implement is refused, never read as a thread.</summary>
    [Fact]
    public async Task UnsupportedResourceType_IsRefused()
    {
        SeedTenant();

        var refused = Assert.IsType<ObjectResult>(await CreateController().AdoptLegacyAsync(
            Acme,
            Request(resourceType: "workspace"),
            CancellationToken.None));

        _ = refused.StatusCode.Should().Be(400);
        _ = JsonSerializer.Serialize(refused.Value).Should().Contain("unsupported_resource_type");
    }

    /// <summary>The startup repair stamps unclaimed conversations and leaves claimed ones alone.</summary>
    [Fact]
    public async Task StartupRepair_StampsOnlyUnclaimedConversations()
    {
        await SeedThreadAsync("unclaimed", tenantId: null);
        await SeedThreadAsync("claimed", Acme);

        await CreateRepair().StartAsync(CancellationToken.None);

        _ = (await _store.LoadMetadataAsync("unclaimed", CancellationToken.None))!.TenantId
            .Should().Be(Quarantine);
        _ = (await _store.LoadMetadataAsync("claimed", CancellationToken.None))!.TenantId
            .Should().Be(Acme);
    }

    /// <summary>
    /// A <c>LegacyTenantId</c> that names a REAL tenant fails the startup and writes nothing.
    /// Stamping unclaimed conversations with a customer's tenant id would hand that customer's
    /// admins read access to them the moment enforcement went on, and the failure mode is a
    /// configuration typo - so the boot has to stop before the update, not after it.
    /// </summary>
    [Fact]
    public async Task StartupRepair_RefusesALegacyTenantIdThatNamesARealTenant()
    {
        _tenants.QuarantineAvailable = false;
        await SeedThreadAsync("unclaimed", tenantId: null);

        var act = async () => await CreateRepair().StartAsync(CancellationToken.None);

        _ = (await act.Should().ThrowAsync<LegacyTenantIdCollisionException>())
            .Which.TenantId.Should().Be(Quarantine);
        _ = (await _store.LoadMetadataAsync("unclaimed", CancellationToken.None))!.TenantId
            .Should().BeNull();
    }

    /// <summary>
    /// A store whose rows cannot be stamped refuses the boot while enforcing. Under enforcement an
    /// unstamped row is invisible to everybody, so continuing would serve a silently empty product.
    /// </summary>
    [Fact]
    public async Task StartupRepair_RefusesToBootOnAnUnstampableStoreWhileEnforcing()
    {
        var repair = new ConversationOwnershipRepairHostedService(
            _tenants,
            new TouchingConversationStore(_store, "unused"),
            IdentityConfig(enforce: true),
            TimeProvider.System,
            NullLogger<ConversationOwnershipRepairHostedService>.Instance);

        var act = async () => await repair.StartAsync(CancellationToken.None);

        _ = await act.Should().ThrowAsync<InvalidOperationException>();
    }

    /// <summary>
    /// The same store boots fine with enforcement off. A repair that refused unconditionally would
    /// take down the pre-rollout deployment, which is the configuration everyone is on today.
    /// </summary>
    [Fact]
    public async Task StartupRepair_ToleratesAnUnstampableStoreWhileNotEnforcing()
    {
        var repair = new ConversationOwnershipRepairHostedService(
            _tenants,
            new TouchingConversationStore(_store, "unused"),
            IdentityConfig(enforce: false),
            TimeProvider.System,
            NullLogger<ConversationOwnershipRepairHostedService>.Instance);

        var act = async () => await repair.StartAsync(CancellationToken.None);

        _ = await act.Should().NotThrowAsync();
    }

    /// <summary>A blank legacy tenant id is refused: it is the id every unclaimed row is stamped with.</summary>
    [Fact]
    public async Task StartupRepair_RefusesABlankLegacyTenantId()
    {
        var repair = new ConversationOwnershipRepairHostedService(
            _tenants,
            _store,
            Options.Create(new IdentityOptions { LegacyTenantId = "  " }),
            TimeProvider.System,
            NullLogger<ConversationOwnershipRepairHostedService>.Instance);

        var act = async () => await repair.StartAsync(CancellationToken.None);

        _ = await act.Should().ThrowAsync<InvalidOperationException>();
    }
}
