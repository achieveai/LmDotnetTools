using AchieveAi.LmDotnetTools.LmCore.Identity;
using Microsoft.AspNetCore.Http;
using LmStreaming.Sample.Identity;
using LmStreaming.Sample.Services;
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
    private const string Root = "thread-root";
    private const string Child = "subagent-child";
    private const string GrandChild = "subagent-grandchild";
    private const string Cousin = "subagent-cousin";
    private const string Unrelated = "thread-unrelated";
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

    private Task SeedThreadAsync(string threadId, string? tenantId, string? subAgentOf = null) =>
        _store.UpdateMetadataAsync(
            threadId,
            _ => new ThreadMetadata
            {
                ThreadId = threadId,
                LastUpdated = 1_000,
                TenantId = tenantId,
                Properties = subAgentOf is null
                    ? null
                    : SubAgentProvenance.Build(subAgentOf, snapshot: null),
            },
            CancellationToken.None);

    private async Task<string?> TenantOfAsync(string threadId) =>
        (await _store.LoadMetadataAsync(threadId, CancellationToken.None))?.TenantId;

    /// <summary>
    /// Seeds the tree every #405 test below argues about: a root, its sub-agent child, that child's
    /// own child, and an unrelated conversation that shares only the quarantine tenant.
    /// </summary>
    private async Task SeedQuarantinedTreeAsync()
    {
        SeedTenant();
        await SeedThreadAsync(Root, Quarantine);
        await SeedThreadAsync(Child, Quarantine, subAgentOf: Root);
        await SeedThreadAsync(GrandChild, Quarantine, subAgentOf: Child);
        await SeedThreadAsync(Unrelated, Quarantine);
    }

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

    /// <summary>
    /// A subset adoption that names a root moves that root's whole sub-agent subtree with it (#405).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The scan that builds a conversation's sub-agent roster scopes by the ROOT row's tenant and
    /// admits only that tenant or an untenanted row (<c>SubAgentScanScope</c>, #388a/#395). A root
    /// adopted into a real tenant while its children stay in quarantine therefore loses those
    /// children from its roster silently - and the incomplete roster is then cached. An operator
    /// naming ids has no way to know the tree extends past the one they named.
    /// </para>
    /// <para>
    /// The unrelated conversation is what stops this from passing by adopting everything: the
    /// expansion has to follow recorded parentage, not give up on the subset.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AdoptingARoot_TakesItsSubAgentDescendantsWithIt()
    {
        await SeedQuarantinedTreeAsync();

        var ok = Assert.IsType<OkObjectResult>(await CreateController().AdoptLegacyAsync(
            Acme,
            Request(resourceIds: [Root]),
            CancellationToken.None));

        _ = Assert.IsType<AdoptLegacyResponse>(ok.Value).AffectedCount.Should().Be(3);
        _ = (await TenantOfAsync(Root)).Should().Be(Acme);
        _ = (await TenantOfAsync(Child)).Should().Be(Acme, "a child left behind vanishes from the roster");
        _ = (await TenantOfAsync(GrandChild)).Should().Be(Acme, "the walk is transitive, not one hop");
        _ = (await TenantOfAsync(Unrelated)).Should().Be(
            Quarantine,
            "a subset adoption must still be a subset - following parentage is not adopting everything");
    }

    /// <summary>
    /// The consequence the tenant columns are only a proxy for: after adoption the root's roster
    /// still lists every descendant.
    /// </summary>
    /// <remarks>
    /// The column assertions above would be satisfied by any change that moved the rows; this is the
    /// one that says the change moved them somewhere the scan can still see. It also fails on the
    /// unfixed code for the reason the issue names rather than by coincidence - with the child left
    /// in quarantine, the scan scoped to the root's new tenant drops it.
    /// </remarks>
    [Fact]
    public async Task AfterAdoptingARoot_ItsRosterStillListsEveryDescendant()
    {
        await SeedQuarantinedTreeAsync();

        _ = await CreateController().AdoptLegacyAsync(
            Acme,
            Request(resourceIds: [Root]),
            CancellationToken.None);

        var roster = await new ConversationDescendantScanner(
                _store,
                NullLogger<ConversationDescendantScanner>.Instance)
            .ScanAsync(Root, CancellationToken.None);

        _ = roster.Select(r => r.ThreadId).Should().BeEquivalentTo([Child, GrandChild]);
    }

    /// <summary>
    /// The same rule read from the other end: naming a sub-agent takes its ancestors too, so a tree
    /// cannot be split upward either.
    /// </summary>
    /// <remarks>
    /// A downward-only expansion would leave the root in quarantine, and the roster scan rooted there
    /// would then drop the very child the operator adopted - the identical disclosure, reached by
    /// naming the other end of the same edge. One walk over the whole connected tree is what makes
    /// the direction of the operator's selection stop mattering.
    /// </remarks>
    [Fact]
    public async Task AdoptingASubAgent_TakesItsAncestorsWithIt()
    {
        await SeedQuarantinedTreeAsync();

        var ok = Assert.IsType<OkObjectResult>(await CreateController().AdoptLegacyAsync(
            Acme,
            Request(resourceIds: [Child]),
            CancellationToken.None));

        _ = Assert.IsType<AdoptLegacyResponse>(ok.Value).AffectedCount.Should().Be(3);
        _ = (await TenantOfAsync(Root)).Should().Be(Acme);
        _ = (await TenantOfAsync(GrandChild)).Should().Be(Acme);
        _ = (await TenantOfAsync(Unrelated)).Should().Be(Quarantine);
    }

    /// <summary>
    /// A rehearsal reports the expanded count, not the submitted one.
    /// </summary>
    /// <remarks>
    /// The rehearsal exists so an operator can see the blast radius before committing to it. If the
    /// expansion ran only on the real call, the rehearsal would under-report exactly the rows the
    /// operator did not ask for - the ones they most need to be told about.
    /// </remarks>
    [Fact]
    public async Task DryRunAdoption_ReportsTheWholeTreeItWouldMove()
    {
        await SeedQuarantinedTreeAsync();

        var ok = Assert.IsType<OkObjectResult>(await CreateController().AdoptLegacyAsync(
            Acme,
            Request(dryRun: true, resourceIds: [Root]),
            CancellationToken.None));

        var response = Assert.IsType<AdoptLegacyResponse>(ok.Value);
        _ = response.AffectedCount.Should().Be(3);
        _ = response.Sample.Should().BeEquivalentTo([Root, Child, GrandChild]);
        _ = (await TenantOfAsync(Child)).Should().Be(Quarantine, "a rehearsal still writes nothing");
    }

    /// <summary>
    /// The bound this mechanism does NOT reach: a descendant already living in a real tenant is left
    /// where it is.
    /// </summary>
    /// <remarks>
    /// Adoption moves rows OUT of quarantine; it is not a tenant-transfer tool, and pulling a row
    /// out of a real tenant on the strength of a parent link would let one operator move another
    /// tenant's conversation by adopting its parent. So this closes the way a split is CREATED from
    /// quarantine - it cannot repair a split that already exists across two real tenants. Written
    /// down as a test rather than only as prose, because the reader who wants to know whether their
    /// already-split tree gets fixed is the one who will not read the prose.
    /// </remarks>
    [Fact]
    public async Task ADescendantAlreadyInARealTenant_IsNotPulledOutOfIt()
    {
        SeedTenant();
        SeedTenant("tnt_other", "22222222-2222-2222-2222-222222222222");
        await SeedThreadAsync(Root, Quarantine);
        await SeedThreadAsync(Child, "tnt_other", subAgentOf: Root);

        var ok = Assert.IsType<OkObjectResult>(await CreateController().AdoptLegacyAsync(
            Acme,
            Request(resourceIds: [Root]),
            CancellationToken.None));

        _ = Assert.IsType<AdoptLegacyResponse>(ok.Value).AffectedCount.Should().Be(1);
        _ = (await TenantOfAsync(Root)).Should().Be(Acme);
        _ = (await TenantOfAsync(Child)).Should().Be("tnt_other");
    }

    /// <summary>
    /// The walk stops at a parent that is not in quarantine, so it does not reach that parent's
    /// other children and sweep them in.
    /// </summary>
    /// <remarks>
    /// The stopping rule earns its keep here rather than at the parent itself: adopting a row that
    /// is already in a real tenant is refused by the source-tenant filter regardless. What only the
    /// rule prevents is continuing THROUGH such a parent - which would move a sibling the operator
    /// never named, out of quarantine, into a tenant whose tree it is still not joined to, because
    /// the parent linking them stays where it is.
    /// </remarks>
    [Fact]
    public async Task ASiblingUnderAParentInARealTenant_IsNotSweptIn()
    {
        SeedTenant();
        SeedTenant("tnt_other", "22222222-2222-2222-2222-222222222222");
        await SeedThreadAsync(Root, "tnt_other");
        await SeedThreadAsync(Child, Quarantine, subAgentOf: Root);
        await SeedThreadAsync("subagent-sibling", Quarantine, subAgentOf: Root);

        var ok = Assert.IsType<OkObjectResult>(await CreateController().AdoptLegacyAsync(
            Acme,
            Request(resourceIds: [Child]),
            CancellationToken.None));

        _ = Assert.IsType<AdoptLegacyResponse>(ok.Value).AffectedCount.Should().Be(1);
        _ = (await TenantOfAsync(Child)).Should().Be(Acme);
        _ = (await TenantOfAsync("subagent-sibling")).Should().Be(Quarantine);
    }

    /// <summary>
    /// Naming a conversation that is NOT in quarantine adopts nothing — least of all its quarantined
    /// children.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The mirror of <see cref="ASiblingUnderAParentInARealTenant_IsNotSweptIn"/>, and the sharper
    /// case: there the real-tenant row was reached mid-walk, here it is the id the operator typed.
    /// A walk that seeds itself unconditionally starts at a row it may not touch and descends into
    /// children it then moves — severing them from the parent that stayed put. That is the #405
    /// defect exactly, manufactured by the #405 fix, which is why the seeds are filtered by the same
    /// in-quarantine rule the walk uses rather than trusted because the operator named them.
    /// </para>
    /// <para>
    /// Adopting nothing is the right answer and not a silently swallowed one: the row named is
    /// already in a real tenant, so there is nothing here to move, and the count says so.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task NamingAConversationOutsideQuarantine_AdoptsNothing_NotItsChildren()
    {
        SeedTenant();
        SeedTenant("tnt_other", "22222222-2222-2222-2222-222222222222");
        await SeedThreadAsync(Root, "tnt_other");
        await SeedThreadAsync(Child, Quarantine, subAgentOf: Root);
        await SeedThreadAsync(GrandChild, Quarantine, subAgentOf: Child);

        var ok = Assert.IsType<OkObjectResult>(await CreateController().AdoptLegacyAsync(
            Acme,
            Request(resourceIds: [Root]),
            CancellationToken.None));

        _ = Assert.IsType<AdoptLegacyResponse>(ok.Value).AffectedCount.Should().Be(0);
        _ = (await TenantOfAsync(Root)).Should().Be("tnt_other");
        _ = (await TenantOfAsync(Child)).Should().Be(
            Quarantine,
            "a child moved away from a parent that stayed put is the very split this walk exists to prevent");
        _ = (await TenantOfAsync(GrandChild)).Should().Be(Quarantine);
    }

    /// <summary>
    /// A null element in <c>resourceIds</c> is ignored, not thrown on.
    /// </summary>
    /// <remarks>
    /// <c>["thread-1", null]</c> is valid JSON and binds to a list with a null element. Both the
    /// parent lookup and the in-quarantine check are ordinal-comparer keyed, and both throw
    /// <see cref="ArgumentNullException"/> on a null key — so the walk answered an operator's typo
    /// with an unhandled <c>500</c>, on a route whose every other refusal is a stable code in an
    /// audit record.
    /// </remarks>
    [Fact]
    public async Task ANullIdInTheList_IsIgnored_NotThrownOn()
    {
        await SeedQuarantinedTreeAsync();

        var ok = Assert.IsType<OkObjectResult>(await CreateController().AdoptLegacyAsync(
            Acme,
            Request(resourceIds: [Root, null!]),
            CancellationToken.None));

        _ = Assert.IsType<AdoptLegacyResponse>(ok.Value).AffectedCount.Should().Be(3);
        _ = (await TenantOfAsync(Unrelated)).Should().Be(Quarantine);
    }

    /// <summary>
    /// When the quarantine tenant holds more rows than one bounded scan can read, a subset adoption
    /// is REFUSED rather than performed on a tree it could not finish walking.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The expansion is built from a single bounded scan - the same shape, and for the same reason,
    /// as <c>ConversationDescendantScanner</c>: offset paging over a store contractually ordered by
    /// last-updated skips rows whenever a scanned conversation is touched mid-walk. A truncated scan
    /// cannot see the parent links past the cap, so proceeding would split trees again, silently,
    /// and only on the deployments large enough that nobody is watching.
    /// </para>
    /// <para>
    /// Refusing costs the operator nothing they cannot recover: adopting the whole quarantine tenant
    /// (no id list) needs no expansion and is unaffected, which is what the second half asserts.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ASubsetAdoptionOverTheScanCap_IsRefusedRatherThanSplittingTrees()
    {
        SeedTenant();
        await SeedThreadAsync(Root, Quarantine);
        await SeedThreadAsync(Child, Quarantine, subAgentOf: Root);
        for (var i = 0; i < TenantsController.AdoptionScanMaxThreads; i++)
        {
            await SeedThreadAsync($"filler-{i}", Quarantine);
        }

        var refused = Assert.IsType<ObjectResult>(await CreateController().AdoptLegacyAsync(
            Acme,
            Request(resourceIds: [Root]),
            CancellationToken.None));

        _ = refused.StatusCode.Should().Be(503);
        _ = JsonSerializer.Serialize(refused.Value).Should().Contain("adoption_scan_truncated");
        _ = (await TenantOfAsync(Root)).Should().Be(Quarantine, "a refused adoption writes nothing");

        // Adopting the whole tenant needs no walk, so the cap does not block the operator's way out.
        var ok = Assert.IsType<OkObjectResult>(await CreateController().AdoptLegacyAsync(
            Acme,
            Request(),
            CancellationToken.None));

        _ = Assert.IsType<AdoptLegacyResponse>(ok.Value).AffectedCount
            .Should().Be(TenantsController.AdoptionScanMaxThreads + 2);
    }

    /// <summary>
    /// Adopting a grandchild pulls in a cousin reached only by climbing to the shared root and then
    /// descending back down a different branch.
    /// </summary>
    /// <remarks>
    /// The walk is bidirectional: from the seed it climbs to the parent, and that parent has to be
    /// re-enqueued so its OTHER children are then picked up on the way back down. A walk that climbed
    /// to an ancestor once and never continued outward from it would reach only the seed's own line
    /// of ancestors, silently leaving a sibling branch of the same quarantined tree behind - which is
    /// the same #405 split, just not caught by any test that only climbs or only descends.
    /// </remarks>
    [Fact]
    public async Task AdoptingAGrandchild_PullsInACousinReachedOnlyByClimbingThenDescending()
    {
        SeedTenant();
        await SeedThreadAsync(Root, Quarantine);
        await SeedThreadAsync(Child, Quarantine, subAgentOf: Root);
        await SeedThreadAsync(Cousin, Quarantine, subAgentOf: Root);
        await SeedThreadAsync(GrandChild, Quarantine, subAgentOf: Child);

        var ok = Assert.IsType<OkObjectResult>(await CreateController().AdoptLegacyAsync(
            Acme,
            Request(resourceIds: [GrandChild]),
            CancellationToken.None));

        _ = Assert.IsType<AdoptLegacyResponse>(ok.Value).AffectedCount.Should().Be(4);
        _ = (await TenantOfAsync(Cousin)).Should().Be(
            Acme,
            "the walk must re-enqueue the shared root to descend into a branch reached only by climbing");
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
