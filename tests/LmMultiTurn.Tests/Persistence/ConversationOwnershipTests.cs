using AchieveAi.LmDotnetTools.LmCore.Identity;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence.Sqlite;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace LmMultiTurn.Tests.Persistence;

/// <summary>
/// Pins the owner columns, the scoped listing of P1 spec 7.5, and the tenancy maintenance of 8.5
/// across ALL THREE conversation stores.
/// </summary>
/// <remarks>
/// <para>
/// Written from the point of view of an authenticated user of tenant A trying to read tenant B's
/// conversations. The happy path (an owner sees their own) is the least interesting claim here -
/// what these pin is the set of rows that must NOT come back.
/// </para>
/// <para>
/// Every claim runs against the SQL store, the file store and the in-memory store from one theory
/// body. Three implementations of one predicate is exactly the shape that drifts, and the sample
/// ships the FILE store (<c>Program.cs</c>) while the spec's predicate is written in SQL - so the
/// implementation the product actually runs is the one a SQL-only test would never touch.
/// </para>
/// </remarks>
public sealed class ConversationOwnershipTests : IAsyncLifetime
{
    private const string TenantA = "tnt_a";
    private const string TenantB = "tnt_b";
    private const string UserA = "dir-a:user-1";
    private const string UserA2 = "dir-a:user-2";
    private const string UserB = "dir-b:user-9";

    /// <summary>
    /// One more id than the bundled SQLite will bind as parameters to a single statement
    /// (<c>SQLITE_MAX_VARIABLE_NUMBER</c>, 32,766 since 3.32). Sized to the engine under test
    /// rather than to the 999 the issue quotes - see the ceiling tests for why that distinction is
    /// the whole point.
    /// </summary>
    private const int AboveBinderCeiling = 32_767;

    private string _root = null!;
    private readonly List<IAsyncDisposable> _disposables = [];

    public Task InitializeAsync()
    {
        _root = Path.Combine(Path.GetTempPath(), $"ownership_{Guid.NewGuid():N}");
        _ = Directory.CreateDirectory(_root);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        foreach (var disposable in _disposables)
        {
            await disposable.DisposeAsync();
        }

        SqliteConnection.ClearAllPools();
        await Task.Delay(50);

        // #477: the "file" store kind here is a FileConversationStore with the exclusive-create retry
        // loop, so detach-then-delete rather than recursive-delete in place — see DetachedStoreTeardown.
        DetachedStoreTeardown.Purge(_root);
    }

    /// <summary>The three store flavours, by name, so a failure says which one drifted.</summary>
    public static TheoryData<string> StoreKinds => ["sqlite", "file", "memory"];

    private IConversationStore CreateStore(string kind)
    {
        switch (kind)
        {
            case "sqlite":
                var sqlite = new SqliteConversationStore(
                    Path.Combine(_root, $"conv_{Guid.NewGuid():N}.db"));
                _disposables.Add(sqlite);
                return sqlite;
            case "file":
                var directory = Path.Combine(_root, $"file_{Guid.NewGuid():N}");
                _ = Directory.CreateDirectory(directory);
                return new FileConversationStore(directory);
            default:
                return new InMemoryConversationStore();
        }
    }

    private static async Task WriteAsync(
        IConversationStore store,
        string threadId,
        string? tenantId,
        string? ownerUserId = null,
        string? ownerAppId = null,
        Visibility? visibility = null,
        long lastUpdated = 1_000) =>
        await store.UpdateMetadataAsync(
            threadId,
            _ => new ThreadMetadata
            {
                ThreadId = threadId,
                LastUpdated = lastUpdated,
                TenantId = tenantId,
                OwnerUserId = ownerUserId,
                OwnerAppId = ownerAppId,
                Visibility = visibility,
            },
            CancellationToken.None);

    private static ConversationListScope Scope(
        string tenantId,
        string? userId = null,
        string? appId = null,
        bool isTenantAdmin = false,
        params string[] granted) =>
        new()
        {
            TenantId = tenantId,
            UserId = userId,
            AppId = appId,
            IsTenantAdmin = isTenantAdmin,
            GrantedThreadIds = new HashSet<string>(granted, StringComparer.Ordinal),
        };

    /// <summary>
    /// The four owner columns survive a write and a read. Without this every claim below could pass
    /// vacuously by never storing anything for the filter to match.
    /// </summary>
    [Theory]
    [MemberData(nameof(StoreKinds))]
    public async Task OwnerColumns_RoundTrip(string kind)
    {
        var store = CreateStore(kind);
        await WriteAsync(store, "t1", TenantA, UserA, "app-1", Visibility.Shared);

        var loaded = await store.LoadMetadataAsync("t1", CancellationToken.None);

        _ = loaded.Should().NotBeNull();
        _ = loaded!.TenantId.Should().Be(TenantA);
        _ = loaded.OwnerUserId.Should().Be(UserA);
        _ = loaded.OwnerAppId.Should().Be("app-1");
        _ = loaded.Visibility.Should().Be(Visibility.Shared);
    }

    /// <summary>
    /// The outer boundary. A user of tenant B never sees a conversation of tenant A, whatever else
    /// is true about it - including a conversation whose owner id happens to be theirs.
    /// </summary>
    [Theory]
    [MemberData(nameof(StoreKinds))]
    public async Task Listing_ExcludesAnotherTenantsConversations(string kind)
    {
        var store = CreateStore(kind);
        await WriteAsync(store, "a-own", TenantA, UserA);

        // The SAME owner id stamped into the OTHER tenant, listed AS that owner. Only this shape
        // proves the tenant conjunct: a row owned by anyone else is already excluded by the owner
        // conjunct, so dropping the tenant one entirely would leave such a test green. This row is
        // excluded by tenancy alone.
        await WriteAsync(store, "b-same-owner", TenantB, UserA);

        var listed = await store.ListThreadsAsync(Scope(TenantA, UserA), 50, 0, CancellationToken.None);

        _ = listed.Select(t => t.ThreadId).Should().BeEquivalentTo(["a-own"]);
    }

    /// <summary>
    /// The background sub-agent scan opts into "this tenant OR untenanted"
    /// (<see cref="ConversationListScope.ForTenantIncludingUntenanted"/>), so a tenanted root's
    /// still-untenanted descendant is not dropped by the #388a narrowing. A CALLER-facing scope must not
    /// admit that same untenanted row - both halves are pinned here, in every store, because the SQLite
    /// predicate and the in-memory <c>Admits</c> spelling are written separately and can drift.
    /// </summary>
    [Theory]
    [MemberData(nameof(StoreKinds))]
    public async Task Listing_ScanScopeAdmitsUntenanted_WhileACallerScopeDoesNot(string kind)
    {
        var store = CreateStore(kind);
        await WriteAsync(store, "tenanted", TenantA, UserA);
        await WriteAsync(store, "untenanted", tenantId: null, ownerUserId: UserA);

        var scanned = await store.ListThreadsAsync(
            ConversationListScope.ForTenantIncludingUntenanted(TenantA), 50, 0, CancellationToken.None);
        var asCaller = await store.ListThreadsAsync(
            Scope(TenantA, UserA), 50, 0, CancellationToken.None);

        _ = scanned.Select(t => t.ThreadId).Should().BeEquivalentTo(["tenanted", "untenanted"]);
        _ = asCaller.Select(t => t.ThreadId).Should().BeEquivalentTo(["tenanted"]);
    }

    /// <summary>
    /// Spec 7.1 principle 4: a null owner matches nobody. The C# stores are where this can go wrong
    /// - <c>null == null</c> is true - and getting it wrong hands every unclaimed conversation to
    /// every app-only caller.
    /// </summary>
    [Theory]
    [MemberData(nameof(StoreKinds))]
    public async Task Listing_NullOwnerMatchesNobody(string kind)
    {
        var store = CreateStore(kind);
        await WriteAsync(store, "unowned", TenantA, ownerUserId: null, ownerAppId: null);

        var asUser = await store.ListThreadsAsync(Scope(TenantA, UserA), 50, 0, CancellationToken.None);
        var asApp = await store.ListThreadsAsync(
            Scope(TenantA, userId: null, appId: null), 50, 0, CancellationToken.None);

        _ = asUser.Should().BeEmpty();
        _ = asApp.Should().BeEmpty();
    }

    /// <summary>A user sees their own conversations and not a tenant-mate's.</summary>
    [Theory]
    [MemberData(nameof(StoreKinds))]
    public async Task Listing_OwnerSeesOwnAndNotAPeers(string kind)
    {
        var store = CreateStore(kind);
        await WriteAsync(store, "mine", TenantA, UserA);
        await WriteAsync(store, "theirs", TenantA, UserA2);

        var listed = await store.ListThreadsAsync(Scope(TenantA, UserA), 50, 0, CancellationToken.None);

        _ = listed.Select(t => t.ThreadId).Should().BeEquivalentTo(["mine"]);
    }

    /// <summary>
    /// The grant branch. A conversation someone shared with this user appears in their list; the
    /// same conversation does not appear for a user with no grant.
    /// </summary>
    [Theory]
    [MemberData(nameof(StoreKinds))]
    public async Task Listing_IncludesGrantedConversations(string kind)
    {
        var store = CreateStore(kind);
        await WriteAsync(store, "shared", TenantA, UserA2, visibility: Visibility.Shared);

        var withGrant = await store.ListThreadsAsync(
            Scope(TenantA, UserA, granted: "shared"), 50, 0, CancellationToken.None);
        var withoutGrant = await store.ListThreadsAsync(
            Scope(TenantA, UserA), 50, 0, CancellationToken.None);

        _ = withGrant.Select(t => t.ThreadId).Should().BeEquivalentTo(["shared"]);
        _ = withoutGrant.Should().BeEmpty();
    }

    /// <summary>
    /// The admin branch of 7.4 mirrored into the listing. Omitting it produces the worst outcome
    /// available: an EMPTY list for a tenant admin while the point read on the same rows allows.
    /// </summary>
    [Theory]
    [MemberData(nameof(StoreKinds))]
    public async Task Listing_TenantAdminSeesEveryConversationInTheirTenantAndNoOther(string kind)
    {
        var store = CreateStore(kind);
        await WriteAsync(store, "a1", TenantA, UserA);
        await WriteAsync(store, "a2", TenantA, UserA2);
        await WriteAsync(store, "b1", TenantB, UserB);

        var listed = await store.ListThreadsAsync(
            Scope(TenantA, UserA, isTenantAdmin: true), 50, 0, CancellationToken.None);

        _ = listed.Select(t => t.ThreadId).Should().BeEquivalentTo(["a1", "a2"]);
    }

    /// <summary>
    /// An app-only caller matches on app id and never on the grant branch (spec 7.4 step 3): a
    /// grant names a person, and a service credential is not one.
    /// </summary>
    [Theory]
    [MemberData(nameof(StoreKinds))]
    public async Task Listing_AppOnlyCallerMatchesAppIdAndIgnoresGrants(string kind)
    {
        var store = CreateStore(kind);
        await WriteAsync(store, "app-owned", TenantA, ownerUserId: null, ownerAppId: "app-1");
        await WriteAsync(store, "user-owned", TenantA, UserA2);

        var listed = await store.ListThreadsAsync(
            Scope(TenantA, userId: null, appId: "app-1", granted: "user-owned"),
            50,
            0,
            CancellationToken.None);

        _ = listed.Select(t => t.ThreadId).Should().BeEquivalentTo(["app-owned"]);
    }

    /// <summary>
    /// The filter runs BEFORE the page is cut, not after it. With ten rows of which only the oldest
    /// belongs to the caller, a filter applied to an already-trimmed page of five returns nothing.
    /// </summary>
    [Theory]
    [MemberData(nameof(StoreKinds))]
    public async Task Listing_IncludesATenantPublishedConversation_TheCallerNeitherOwnsNorHoldsAGrantOn(
        string kind)
    {
        // The listing predicate has to mirror EVERY allow branch of spec 7.4, and the
        // tenant-published branch had no counterpart here. The point read already allows this row
        // (ResourceAccessPolicy adds the TenantMember relationship for a published resource), so
        // without it the two disagree in the worst direction the type's own doc comment describes:
        // a 200 on the direct read, and silently missing from the list.
        var store = CreateStore(kind);

        await WriteAsync(store, "published", TenantA, UserA2, visibility: Visibility.TenantPublished);

        var listed = await store.ListThreadsAsync(Scope(TenantA, UserA), 50, 0, CancellationToken.None);

        _ = listed.Select(t => t.ThreadId).Should().BeEquivalentTo(["published"]);
    }

    [Theory]
    [MemberData(nameof(StoreKinds))]
    public async Task Listing_StillExcludesAPrivateConversation_OwnedByAnotherMemberOfTheSameTenant(
        string kind)
    {
        // Non-vacuity for the test above. A branch that admitted every same-tenant row - rather
        // than only the published ones - would satisfy it while handing every member's private
        // conversations to every other member.
        var store = CreateStore(kind);

        await WriteAsync(store, "private-peer", TenantA, UserA2, visibility: Visibility.Private);
        await WriteAsync(store, "unset-peer", TenantA, UserA2);

        var listed = await store.ListThreadsAsync(Scope(TenantA, UserA), 50, 0, CancellationToken.None);

        _ = listed.Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(StoreKinds))]
    public async Task Listing_DoesNotPublishAcrossTenants(string kind)
    {
        // Publication is scoped to the tenant that published it. The tenant filter runs first, and
        // a branch placed ahead of it would turn "published to my organisation" into "published to
        // everyone".
        var store = CreateStore(kind);

        await WriteAsync(store, "other-published", TenantB, "user-b", visibility: Visibility.TenantPublished);

        var listed = await store.ListThreadsAsync(Scope(TenantA, UserA), 50, 0, CancellationToken.None);

        _ = listed.Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(StoreKinds))]
    public async Task Listing_FiltersBeforePaging(string kind)
    {
        var store = CreateStore(kind);

        for (var i = 0; i < 9; i++)
        {
            await WriteAsync(store, $"peer-{i}", TenantA, UserA2, lastUpdated: 2_000 + i);
        }

        await WriteAsync(store, "mine", TenantA, UserA, lastUpdated: 1_000);

        var listed = await store.ListThreadsAsync(Scope(TenantA, UserA), 5, 0, CancellationToken.None);

        _ = listed.Select(t => t.ThreadId).Should().BeEquivalentTo(["mine"]);
    }

    /// <summary>
    /// The startup repair of 8.5.4 claims every unstamped row for the quarantine tenant, and claims
    /// only those - a row already in a real tenant is not moved.
    /// </summary>
    [Theory]
    [MemberData(nameof(StoreKinds))]
    public async Task StampUnownedThreads_ClaimsOnlyUnstampedRows(string kind)
    {
        var store = CreateStore(kind);
        var ownership = (IConversationOwnershipStore)store;

        await WriteAsync(store, "legacy-1", tenantId: null);
        await WriteAsync(store, "legacy-2", tenantId: null);
        await WriteAsync(store, "already", TenantA, UserA);

        var stamped = await ownership.StampUnownedThreadsAsync("tnt_quarantine", CancellationToken.None);

        _ = stamped.Should().Be(2);
        _ = (await store.LoadMetadataAsync("legacy-1", CancellationToken.None))!.TenantId
            .Should().Be("tnt_quarantine");
        _ = (await store.LoadMetadataAsync("already", CancellationToken.None))!.TenantId
            .Should().Be(TenantA);
    }

    /// <summary>
    /// Adoption selects on the SOURCE tenant, which is what makes a repeated call idempotent rather
    /// than destructive: the second run finds nothing, and the row adopted by the first is not
    /// re-stamped into a second tenant.
    /// </summary>
    [Theory]
    [MemberData(nameof(StoreKinds))]
    public async Task AdoptThreads_IsIdempotentAndAssignsOwner(string kind)
    {
        var store = CreateStore(kind);
        var ownership = (IConversationOwnershipStore)store;

        await WriteAsync(store, "q1", "tnt_quarantine");
        await WriteAsync(store, "q2", "tnt_quarantine");

        var first = await ownership.AdoptThreadsAsync(
            "tnt_quarantine", TenantA, UserA, null, CancellationToken.None);
        var second = await ownership.AdoptThreadsAsync(
            "tnt_quarantine", TenantB, UserB, null, CancellationToken.None);

        _ = first.Should().Be(2);
        _ = second.Should().Be(0);

        var adopted = await store.LoadMetadataAsync("q1", CancellationToken.None);
        _ = adopted!.TenantId.Should().Be(TenantA);
        _ = adopted.OwnerUserId.Should().Be(UserA);
    }

    /// <summary>
    /// An explicitly EMPTY id list adopts nothing. Collapsing it into "no restriction" would turn a
    /// call that named no conversations into a full-tenant adoption - the difference between a
    /// no-op and moving every quarantined conversation in the deployment.
    /// </summary>
    [Theory]
    [MemberData(nameof(StoreKinds))]
    public async Task AdoptThreads_EmptySelectionAdoptsNothing(string kind)
    {
        var store = CreateStore(kind);
        var ownership = (IConversationOwnershipStore)store;

        await WriteAsync(store, "q1", "tnt_quarantine");

        var affected = await ownership.AdoptThreadsAsync(
            "tnt_quarantine", TenantA, UserA, [], CancellationToken.None);

        _ = affected.Should().Be(0);
        _ = (await store.LoadMetadataAsync("q1", CancellationToken.None))!.TenantId
            .Should().Be("tnt_quarantine");
    }

    /// <summary>
    /// The rehearsal's row set is the applied call's row set. A dry run that reported a different
    /// count from the one the apply would move is a rehearsal of nothing.
    /// </summary>
    [Theory]
    [MemberData(nameof(StoreKinds))]
    public async Task ListThreadIdsByTenant_MatchesWhatAdoptionWouldMove(string kind)
    {
        var store = CreateStore(kind);
        var ownership = (IConversationOwnershipStore)store;

        await WriteAsync(store, "q1", "tnt_quarantine");
        await WriteAsync(store, "q2", "tnt_quarantine");
        await WriteAsync(store, "real", TenantA, UserA);

        var rehearsed = await ownership.ListThreadIdsByTenantAsync(
            "tnt_quarantine", null, CancellationToken.None);
        var applied = await ownership.AdoptThreadsAsync(
            "tnt_quarantine", TenantA, null, null, CancellationToken.None);

        _ = rehearsed.Should().BeEquivalentTo(["q1", "q2"]);
        _ = applied.Should().Be(rehearsed.Count);
    }

    /// <summary>
    /// Neither id list in this store has a parameter ceiling any more (#388). Both used to bind one
    /// parameter per id, so a caller naming more ids than the binder allows did not get a wrong
    /// answer - it threw, and took the whole listing or the whole adoption down with it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The issue names the historical 999-variable limit, and the first draft of these tests used
    /// 1,200 ids for that reason. That draft was worthless: restoring the per-id binding left it
    /// green, because the SQLite bundled with Microsoft.Data.Sqlite raises
    /// <c>SQLITE_MAX_VARIABLE_NUMBER</c> to 32,766. 999 is not a ceiling this build can hit, so a
    /// test sized for it exercises nothing. <see cref="AboveBinderCeiling"/> is sized to the limit
    /// this engine actually enforces, which is the only size at which the claim is falsifiable.
    /// </para>
    /// <para>
    /// SQLite-only, deliberately. The ceiling is a property of the parameter binder; the file and
    /// in-memory stores compare ids in managed code and have no such limit, so running these
    /// against them would spend the time and prove nothing the theories above do not.
    /// </para>
    /// <para>
    /// The id sets are large but nearly all of them name rows that do not exist, because what is
    /// under test is the number of ids BOUND, not the number of rows matched. Seeding tens of
    /// thousands of rows would make the test slow without making it stronger.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Listing_AcceptsAGrantSetAboveTheBinderParameterCeiling()
    {
        var store = CreateStore("sqlite");

        await WriteAsync(store, "granted-peer", TenantA, UserA2, visibility: Visibility.Private);
        await WriteAsync(store, "ungranted-peer", TenantA, UserA2, visibility: Visibility.Private);

        var granted = new List<string> { "granted-peer" };
        for (var i = 0; i < AboveBinderCeiling; i++)
        {
            granted.Add(FormattableString.Invariant($"absent-{i}"));
        }

        var listed = await store.ListThreadsAsync(
            Scope(TenantA, UserA, granted: [.. granted]), 50, 0, CancellationToken.None);

        // Both halves matter. The first says the oversized call completed at all; the second says
        // it still filtered - a clause that collapsed to "match everything" under the new shape
        // would satisfy the first assertion while handing over a peer's private conversation.
        _ = listed.Select(t => t.ThreadId).Should().BeEquivalentTo(["granted-peer"]);
    }

    [Fact]
    public async Task Adoption_AcceptsAnIdListAboveTheBinderParameterCeiling()
    {
        var store = CreateStore("sqlite");
        var ownership = (IConversationOwnershipStore)store;

        await WriteAsync(store, "q1", "tnt_quarantine");
        await WriteAsync(store, "q2", "tnt_quarantine");

        var selection = new List<string> { "q1" };
        for (var i = 0; i < AboveBinderCeiling; i++)
        {
            selection.Add(FormattableString.Invariant($"absent-{i}"));
        }

        var rehearsed = await ownership.ListThreadIdsByTenantAsync(
            "tnt_quarantine", selection, CancellationToken.None);
        var applied = await ownership.AdoptThreadsAsync(
            "tnt_quarantine", TenantA, UserA, selection, CancellationToken.None);

        // q2 is the non-vacuity partner: it is in the source tenant and NOT in the selection, so a
        // restriction that silently stopped restricting would adopt it too.
        _ = rehearsed.Should().BeEquivalentTo(["q1"]);
        _ = applied.Should().Be(1);
        _ = (await store.LoadMetadataAsync("q2", CancellationToken.None))!.TenantId
            .Should().Be("tnt_quarantine");
    }
}
