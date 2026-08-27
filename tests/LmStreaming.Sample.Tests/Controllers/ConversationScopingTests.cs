using AchieveAi.LmDotnetTools.LmCore.Identity;
using LmStreaming.Sample.Identity;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using LmStreaming.Sample.Services;
using LmStreaming.Sample.Tests.Agents;
using LmStreaming.Sample.Tests.Identity;
using LmStreaming.Sample.Tests.TestDoubles;

namespace LmStreaming.Sample.Tests.Controllers;

/// <summary>
/// Pins <see cref="ConversationsController"/> against an authenticated user of one tenant actively
/// trying to read, list, change, delete and re-share another tenant's conversations (P1 spec 7.4,
/// 7.5, 8.4).
/// </summary>
/// <remarks>
/// <para>
/// Every refusal here is asserted on the STATUS and, where the status is <c>404</c>, on the body
/// being the one an unknown thread produces. A <c>403</c> would be a correct-looking refusal that
/// still answers "yes, that id names something" - which, for ids an attacker can enumerate, is the
/// leak the <c>404</c> exists to close.
/// </para>
/// <para>
/// The controller is driven directly rather than over HTTP. What is under test is the decision, and
/// a <see cref="ConversationAuthorizer"/> constructed with a chosen principal is the only way to
/// play a SECOND user without an Entra tenant to sign them in from.
/// </para>
/// </remarks>
public sealed class ConversationScopingTests
{
    private const string TenantA = "tnt_a";
    private const string TenantB = "tnt_b";
    private const string Alice = "dir-a:alice";
    private const string Bob = "dir-a:bob";
    private const string Mallory = "dir-b:mallory";

    private readonly InMemoryConversationStore _store = new();
    private readonly InMemoryResourceGrantStore _grants = new();
    private readonly RecordingAuditSink _audit = new();

    private static Principal Signed(string tenantId, string userId, params string[] roles) =>
        new()
        {
            TenantId = tenantId,
            Actor = new PrincipalRef(PrincipalKind.EndUser, userId),
            Roles = new HashSet<string>(roles, StringComparer.Ordinal),
            Source = PrincipalSource.Interactive,
        };

    /// <summary>
    /// Resolves any real system mode id, so a route that has to resolve a mode before it reaches the
    /// agent pool gets there instead of answering 500. Every refusal in this file is decided before
    /// mode resolution, so this changes nothing for them.
    /// </summary>
    private static IChatModeStore ModeStoreResolvingSystemModes()
    {
        var modeStore = new Mock<IChatModeStore>();
        _ = modeStore
            .Setup(m => m.GetModeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string modeId, CancellationToken _) => SystemChatModes.GetById(modeId));
        return modeStore.Object;
    }

    /// <summary>The mode a conversation gets when nothing pinned one - what these threads run under.</summary>
    private static AgentProfile DefaultMode() => SystemChatModes.GetById(SystemChatModes.DefaultModeId)!;

    private static MultiTurnAgentPool CreatePool() =>
        new(
            (threadId, _, _) => new MultiTurnAgentPool.AgentCreationResult(new FakeMultiTurnAgent(threadId)),
            NullLogger<MultiTurnAgentPool>.Instance);

    /// <summary>
    /// An app-only caller: a service credential with an app id and no user behind it. The shape the
    /// S2S provisioning path actually mints, and the one <c>ResourceAccessPolicy</c> resolves down
    /// its <c>OwnerAppId</c> branch rather than its owner/grant/role branches.
    /// </summary>
    private static Principal AppOnly(string appId, string tenantId = TenantA) =>
        new()
        {
            TenantId = tenantId,
            Actor = new PrincipalRef(PrincipalKind.App, appId),
            AppId = appId,
            Source = PrincipalSource.AppOnly,
        };

    /// <summary>
    /// A workspace store that resolves the one id these tests provision against. The default
    /// <see cref="Mock.Of{T}()"/> answers null, which <c>Provision</c> correctly refuses - so a test
    /// that needs to reach the create path has to hand it a workspace that exists.
    /// </summary>
    private static IWorkspaceStore WorkspaceStoreResolving(string workspaceId)
    {
        var workspaces = new Mock<IWorkspaceStore>();
        _ = workspaces
            .Setup(w => w.GetAsync(workspaceId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Workspace
            {
                Id = workspaceId,
                Name = workspaceId,
                DirectoryRelPath = workspaceId,
            });
        return workspaces.Object;
    }

    private ConversationsController CreateController(
        Principal? principal,
        MultiTurnAgentPool pool,
        bool enforce = true,
        IResourceGrantStore? grantsOverride = null,
        IWorkspaceStore? workspaces = null) =>
        new(
            _store,
            pool,
            ModeStoreResolvingSystemModes(),
            workspaces ?? Mock.Of<IWorkspaceStore>(),
            new FakeProviderRegistry(defaultProviderId: "test", available: ["test"]).ToReal(),
            new ConversationStatusResolver(_store, _store),
            TimeProvider.System,
            new WorkflowRunRegistry(),
            enforce
                ? TestAuthorizers.Enforcing(principal, grantsOverride ?? _grants, _audit)
                : TestAuthorizers.Disabled(),
            NullLogger<ConversationsController>.Instance,
            NullLogger<AgentHierarchyService>.Instance,
            new SubAgentScanCoverageCache(),
            new ConversationDescendantScanner(_store, NullLogger<ConversationDescendantScanner>.Instance));

    private async Task SeedAsync(
        string threadId,
        string tenantId,
        string? ownerUserId,
        Visibility? visibility = null,
        string? title = null) =>
        await _store.UpdateMetadataAsync(
            threadId,
            existing => new ThreadMetadata
            {
                ThreadId = threadId,
                LastUpdated = 1_000,
                Properties = title is null
                    ? existing?.Properties
                    : (existing?.Properties ?? System.Collections.Immutable.ImmutableDictionary<string, object>.Empty)
                        .SetItem("title", title),
                TenantId = tenantId,
                OwnerUserId = ownerUserId,
                Visibility = visibility,
            },
            CancellationToken.None);

    /// <summary>
    /// A cross-tenant read is refused as UNKNOWN, with the same body an id that was never minted
    /// produces. The two responses are compared against each other rather than against a literal,
    /// so a future change to the not-found body cannot make them diverge without this failing.
    /// </summary>
    [Fact]
    public async Task CrossTenantRead_IsIndistinguishableFromAThreadThatDoesNotExist()
    {
        await using var pool = CreatePool();
        await SeedAsync("alice-thread", TenantA, Alice);

        var controller = CreateController(Signed(TenantB, Mallory), pool);

        var crossTenant = await controller.GetMessages("alice-thread", viewer: null, CancellationToken.None);
        var neverExisted = await controller.GetMessages("no-such-thread", viewer: null, CancellationToken.None);

        var refused = Assert.IsType<NotFoundObjectResult>(crossTenant);
        var missing = Assert.IsType<NotFoundObjectResult>(neverExisted);

        _ = refused.StatusCode.Should().Be(404);
        _ = System.Text.Json.JsonSerializer.Serialize(refused.Value)
            .Should().Be(
                System.Text.Json.JsonSerializer.Serialize(missing.Value)
                    .Replace("no-such-thread", "alice-thread", StringComparison.Ordinal));
    }

    /// <summary>
    /// The most damaging call this controller answers by id alone. A cross-tenant delete is refused
    /// AND the conversation is still there afterwards - the assertion on the row is what separates
    /// "refused" from "refused after doing it".
    /// </summary>
    [Fact]
    public async Task CrossTenantDelete_IsRefusedAndChangesNothing()
    {
        await using var pool = CreatePool();
        await SeedAsync("alice-thread", TenantA, Alice);

        var controller = CreateController(Signed(TenantB, Mallory), pool);

        var result = await controller.Delete("alice-thread", CancellationToken.None);

        _ = Assert.IsType<NotFoundObjectResult>(result).StatusCode.Should().Be(404);
        _ = (await _store.LoadMetadataAsync("alice-thread", CancellationToken.None))
            .Should().NotBeNull();
    }

    /// <summary>A cross-tenant rename is refused and the title is unchanged.</summary>
    [Fact]
    public async Task CrossTenantRename_IsRefusedAndChangesNothing()
    {
        await using var pool = CreatePool();
        await SeedAsync("alice-thread", TenantA, Alice, title: "Alice's plan");

        var controller = CreateController(Signed(TenantB, Mallory), pool);

        var result = await controller.UpdateMetadata(
            "alice-thread",
            new ConversationMetadataUpdate { Title = "owned" },
            CancellationToken.None);

        _ = Assert.IsType<NotFoundObjectResult>(result).StatusCode.Should().Be(404);

        var metadata = await _store.LoadMetadataAsync("alice-thread", CancellationToken.None);
        _ = metadata!.Properties!["title"].Should().Be("Alice's plan");
    }

    /// <summary>
    /// A tenant-MATE is refused too, and refused the same way. Same tenant is not the boundary; the
    /// owner is.
    /// </summary>
    [Fact]
    public async Task TenantMateWithoutAGrant_IsRefusedAsUnknown()
    {
        await using var pool = CreatePool();
        await SeedAsync("alice-thread", TenantA, Alice);

        var controller = CreateController(Signed(TenantA, Bob), pool);

        var result = await controller.GetMessages("alice-thread", viewer: null, CancellationToken.None);

        _ = Assert.IsType<NotFoundObjectResult>(result).StatusCode.Should().Be(404);
    }

    // -------- The refusal must cost the same lookup WORK, not just the same bytes --------

    /// <summary>
    /// <c>SendMessage</c> answered a null-metadata thread with <c>unknown_thread</c> BEFORE reaching the
    /// authorizer, so a thread that was never minted cost zero grant look-ups while a forbidden
    /// cross-tenant thread cost one (the authorizer's equalising lookup). That difference in store
    /// round-trips is a #389 work-shape existence oracle even though the two 404 bodies are byte-identical.
    /// This pins the write path: both cases must cost the same number of look-ups.
    /// </summary>
    [Fact]
    public async Task SendMessage_CrossTenant_AndAMissingThread_CostTheSameGrantLookups()
    {
        await using var pool = CreatePool();
        await SeedAsync("alice-thread", TenantA, Alice);

        var forbidden = await CountSendLookupsAsync(Signed(TenantB, Mallory), "alice-thread", pool);
        var missing = await CountSendLookupsAsync(Signed(TenantB, Mallory), "no-such-thread", pool);

        _ = forbidden.Should().BeGreaterThan(
            0, "a cross-tenant write runs the authorizer's equalising grant lookup");
        _ = missing.Should().Be(
            forbidden,
            "a thread that was never minted must cost the same grant-lookup work, or the round-trip count is an existence oracle");
    }

    /// <summary>
    /// The same work-shape oracle on the read path: <c>GetStatus</c> short-circuited a null-metadata thread
    /// to <c>unknown_thread</c> before the authorizer, so a missing thread cost zero grant look-ups and a
    /// forbidden one cost one.
    /// </summary>
    [Fact]
    public async Task GetStatus_CrossTenant_AndAMissingThread_CostTheSameGrantLookups()
    {
        await using var pool = CreatePool();
        await SeedAsync("alice-thread", TenantA, Alice);

        var forbidden = await CountStatusLookupsAsync(Signed(TenantB, Mallory), "alice-thread", pool);
        var missing = await CountStatusLookupsAsync(Signed(TenantB, Mallory), "no-such-thread", pool);

        _ = forbidden.Should().BeGreaterThan(
            0, "a cross-tenant status read runs the authorizer's equalising grant lookup");
        _ = missing.Should().Be(
            forbidden,
            "a thread that was never minted must cost the same grant-lookup work, or the round-trip count is an existence oracle");
    }

    /// <summary>Sends to <paramref name="threadId"/> as <paramref name="principal"/> over a fresh counting
    /// grant store, and returns how many grant look-ups the request made.</summary>
    private async Task<int> CountSendLookupsAsync(Principal principal, string threadId, MultiTurnAgentPool pool)
    {
        var grants = new CountingResourceGrantStore(_grants);
        var controller = CreateController(principal, pool, grantsOverride: grants);
        _ = await controller.SendMessage(threadId, new SendMessageRequest { Text = "hi" }, CancellationToken.None);
        return grants.FindGrantCallCount;
    }

    /// <summary>Reads status for <paramref name="threadId"/> as <paramref name="principal"/> over a fresh
    /// counting grant store, and returns how many grant look-ups the request made.</summary>
    private async Task<int> CountStatusLookupsAsync(Principal principal, string threadId, MultiTurnAgentPool pool)
    {
        var grants = new CountingResourceGrantStore(_grants);
        var controller = CreateController(principal, pool, grantsOverride: grants);
        _ = await controller.GetStatus(threadId, runId: "run-1", inputId: null, CancellationToken.None);
        return grants.FindGrantCallCount;
    }

    /// <summary>
    /// A tenant ADMIN of another tenant is refused, and refused as unknown.
    /// </summary>
    /// <remarks>
    /// This is the case that separates the tenant boundary from the owner check. For an ordinary
    /// caller the two refuse independently, so removing the tenant conjunct changes no answer here;
    /// for an admin the admin branch would allow, and the tenant boundary is the ONLY thing between
    /// an admin of any tenant and every conversation in the deployment.
    /// </remarks>
    [Fact]
    public async Task CrossTenantAdmin_IsRefusedAsUnknown()
    {
        await using var pool = CreatePool();
        await SeedAsync("alice-thread", TenantA, Alice);

        var controller = CreateController(Signed(TenantB, Mallory, "admin"), pool);

        var result = await controller.GetMessages("alice-thread", viewer: null, CancellationToken.None);

        _ = Assert.IsType<NotFoundObjectResult>(result).StatusCode.Should().Be(404);
    }

    /// <summary>
    /// Liveness is a fact about the conversation, so the run-state poll is scoped like a read. Left
    /// open it is an existence-and-activity oracle over enumerable ids.
    /// </summary>
    [Fact]
    public async Task RunStatePoll_IsScoped()
    {
        await using var pool = CreatePool();
        await SeedAsync("alice-thread", TenantA, Alice);

        var controller = CreateController(Signed(TenantB, Mallory), pool);

        var result = await controller.GetRunState("alice-thread", CancellationToken.None);

        _ = Assert.IsType<NotFoundObjectResult>(result).StatusCode.Should().Be(404);
    }

    /// <summary>The listing returns the caller's conversations and nobody else's.</summary>
    [Fact]
    public async Task Listing_ReturnsOnlyTheCallersConversations()
    {
        await using var pool = CreatePool();
        await SeedAsync("alice-thread", TenantA, Alice);
        await SeedAsync("bob-thread", TenantA, Bob);
        await SeedAsync("mallory-thread", TenantB, Mallory);

        var controller = CreateController(Signed(TenantA, Alice), pool);

        var ok = Assert.IsType<OkObjectResult>(await controller.List(50, 0, CancellationToken.None));
        var summaries = ((IEnumerable<ConversationSummary>)ok.Value!).ToArray();

        _ = summaries.Select(s => s.ThreadId).Should().BeEquivalentTo(["alice-thread"]);
    }

    /// <summary>
    /// An UNAUTHENTICATED caller under enforcement lists nothing.
    /// </summary>
    /// <remarks>
    /// The list path builds a filter rather than asking per row, so "no principal" has to become a
    /// filter that matches nothing. The tempting spelling - no principal, therefore no filter -
    /// reads as "unscoped" one layer down and hands every conversation in the deployment to a
    /// caller who presented nothing. Empty is the only safe answer, and a refusal would be a
    /// second-best one: this asserts the empty listing, not the status.
    /// </remarks>
    [Fact]
    public async Task UnauthenticatedListing_ReturnsNothing()
    {
        await using var pool = CreatePool();
        await SeedAsync("alice-thread", TenantA, Alice);
        await SeedAsync("mallory-thread", TenantB, Mallory);

        var controller = CreateController(principal: null, pool);

        var result = await controller.List(50, 0, CancellationToken.None);

        if (result is OkObjectResult ok)
        {
            _ = ((IEnumerable<ConversationSummary>)ok.Value!).Should().BeEmpty();
        }
        else
        {
            _ = Assert.IsAssignableFrom<IStatusCodeActionResult>(result)
                .StatusCode.Should().Be(401);
        }
    }

    /// <summary>
    /// Provisioning stamps the row with the caller's tenant and user (spec 8.3; closes #162). Every
    /// scoping claim above is meaningless if the create path leaves the columns null.
    /// </summary>
    [Fact]
    public async Task Provision_StampsTenantAndOwner()
    {
        await using var pool = CreatePool();

        var workspaceStore = new Mock<IWorkspaceStore>();
        _ = workspaceStore
            .Setup(w => w.GetAsync("ws", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Workspace { Id = "ws", Name = "ws", DirectoryRelPath = "ws" });

        var modeStore = new Mock<IChatModeStore>();
        _ = modeStore
            .Setup(m => m.GetModeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SystemChatModes.GetById(SystemChatModes.DefaultModeId));

        var controller = new ConversationsController(
            _store,
            pool,
            modeStore.Object,
            workspaceStore.Object,
            new FakeProviderRegistry(defaultProviderId: "test", available: ["test"]).ToReal(),
            new ConversationStatusResolver(_store, _store),
            TimeProvider.System,
            new WorkflowRunRegistry(),
            TestAuthorizers.Enforcing(Signed(TenantA, Alice), _grants, _audit),
            NullLogger<ConversationsController>.Instance,
            NullLogger<AgentHierarchyService>.Instance,
            new SubAgentScanCoverageCache(),
            new ConversationDescendantScanner(_store, NullLogger<ConversationDescendantScanner>.Instance));

        var ok = Assert.IsType<OkObjectResult>(await controller.Provision(
            new ProvisionConversationRequest
            {
                WorkspaceId = "ws",
                ProviderId = "test",
                ModeId = SystemChatModes.DefaultModeId,
            },
            CancellationToken.None));

        var threadId = ((ProvisionConversationResponse)ok.Value!).ThreadId;
        var metadata = await _store.LoadMetadataAsync(threadId, CancellationToken.None);

        _ = metadata!.TenantId.Should().Be(TenantA);
        _ = metadata.OwnerUserId.Should().Be(Alice);
        _ = metadata.Visibility.Should().Be(Visibility.Private);
    }

    /// <summary>
    /// Provisions a conversation as <paramref name="principal"/> and returns the minted thread id.
    /// </summary>
    private async Task<string> ProvisionAsAsync(Principal principal, MultiTurnAgentPool pool)
    {
        var ok = Assert.IsType<OkObjectResult>(
            await CreateController(principal, pool, workspaces: WorkspaceStoreResolving("ws")).Provision(
                new ProvisionConversationRequest
                {
                    WorkspaceId = "ws",
                    ProviderId = "test",
                    ModeId = SystemChatModes.DefaultModeId,
                },
                CancellationToken.None));

        return ((ProvisionConversationResponse)ok.Value!).ThreadId;
    }

    /// <summary>
    /// The window #162 named: a conversation an S2S app provisions is owned from that moment, not
    /// from whenever it first sends. A second app that reaches the thread id before the first
    /// message is refused, and refused as unknown.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The load-bearing arrangement is what is ABSENT. Every other cross-app test in this repo seeds
    /// the pool directly, so the refusal they observe comes from the in-memory caller freeze (#153) -
    /// a binding that only exists once an agent has been created and that a process restart erases.
    /// Here nothing has ever been pooled for this thread, which is asserted rather than assumed, so
    /// the only thing that can refuse is the <c>OwnerAppId</c> stamped on the row at provision time.
    /// A test that let an agent exist first would pass with the stamp removed entirely.
    /// </para>
    /// <para>
    /// <c>404</c>, not the <c>409</c> the issue proposed. A thread id is the credential here: minted
    /// unguessable and handed only to the provisioning caller. Answering <c>409</c> would confirm the
    /// id names a real conversation to whoever guessed it, which is the disclosure the rest of this
    /// file's refusals are shaped to avoid - so the body is compared against the one a never-minted
    /// id produces rather than merely checked for a status.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AConversationProvisionedByOneApp_RefusesAnotherAppsFirstMessage_WithNothingEverPooled()
    {
        await using var pool = CreatePool();

        var threadId = await ProvisionAsAsync(AppOnly("app-a"), pool);

        // The mechanism, read off the row: an app-only caller leaves no user behind, so OwnerAppId is
        // the entire durable binding.
        var stamped = await _store.LoadMetadataAsync(threadId, CancellationToken.None);
        _ = stamped!.OwnerAppId.Should().Be("app-a");
        _ = stamped.OwnerUserId.Should().BeNull();

        // Non-vacuity: no agent entry was ever pooled for this thread, so the in-memory freeze has
        // nothing to freeze and cannot be what refuses below.
        _ = pool.TryGetHandoffState(threadId, out _).Should().BeFalse();

        var intruder = CreateController(AppOnly("app-b"), pool);
        var refused = await intruder.SendMessage(
            threadId,
            new SendMessageRequest { Text = "not yours" },
            CancellationToken.None);
        var neverMinted = await intruder.SendMessage(
            "thread-never-minted",
            new SendMessageRequest { Text = "not yours" },
            CancellationToken.None);

        var denied = Assert.IsType<NotFoundObjectResult>(refused);
        var missing = Assert.IsType<NotFoundObjectResult>(neverMinted);

        _ = denied.StatusCode.Should().Be(404);
        _ = System.Text.Json.JsonSerializer.Serialize(denied.Value)
            .Should().Be(
                System.Text.Json.JsonSerializer.Serialize(missing.Value)
                    .Replace("thread-never-minted", threadId, StringComparison.Ordinal),
                "a refused caller must not be able to tell a real conversation from an imaginary one");

        // Refused BEFORE the pool, not after: a refusal that still minted an agent would leave the
        // conversation frozen to the intruder for the owner's own first message. No agent entry is
        // pooled for this thread at all.
        _ = pool.TryGetHandoffState(threadId, out _).Should().BeFalse();
    }

    /// <summary>
    /// The other half of the claim above: the refusal is about which app is asking, not about
    /// provisioning being a state a conversation cannot be sent to out of.
    /// </summary>
    /// <remarks>
    /// Without this, deleting the whole <c>OwnerAppId</c> comparison and refusing every first message
    /// would satisfy the test above.
    /// </remarks>
    [Fact]
    public async Task TheAppThatProvisionedIt_CanSendItsOwnFirstMessage()
    {
        await using var pool = CreatePool();

        var threadId = await ProvisionAsAsync(AppOnly("app-a"), pool);

        var owner = CreateController(AppOnly("app-a"), pool);
        var accepted = await owner.SendMessage(
            threadId,
            new SendMessageRequest { Text = "mine" },
            CancellationToken.None);

        _ = Assert.IsType<AcceptedResult>(accepted).StatusCode.Should().Be(202);
    }

    /// <summary>
    /// A rename by the owner leaves the conversation readable by the owner.
    /// </summary>
    /// <remarks>
    /// The update path REPLACES the stored row from a projection rather than patching it, so the
    /// owner columns have to be carried forward by name. Leaving them off unstamps the row on every
    /// rename, and an unstamped row is one nobody can read - the owner locks themselves out by
    /// editing the title. The second read is the assertion that matters; the column check alone
    /// would not say whether the consequence is reachable.
    /// </remarks>
    [Fact]
    public async Task RenameByTheOwner_DoesNotUnstampTheConversation()
    {
        await using var pool = CreatePool();
        await SeedAsync("alice-thread", TenantA, Alice, title: "before");

        var owner = CreateController(Signed(TenantA, Alice), pool);
        _ = await owner.UpdateMetadata(
            "alice-thread",
            new ConversationMetadataUpdate { Title = "after" },
            CancellationToken.None);

        var stored = await _store.LoadMetadataAsync("alice-thread", CancellationToken.None);
        _ = stored!.TenantId.Should().Be(TenantA);
        _ = stored.OwnerUserId.Should().Be(Alice);

        _ = Assert.IsType<OkObjectResult>(
            await CreateController(Signed(TenantA, Alice), pool)
                .GetMessages("alice-thread", viewer: null, CancellationToken.None));
    }

    /// <summary>
    /// The sharing round trip: the owner shares, the grantee can read, the conversation becomes
    /// <see cref="Visibility.Shared"/>, and someone with no grant still cannot read it.
    /// </summary>
    [Fact]
    public async Task Sharing_LetsTheNamedPersonReadAndNobodyElse()
    {
        await using var pool = CreatePool();
        await SeedAsync("alice-thread", TenantA, Alice);

        var owner = CreateController(Signed(TenantA, Alice), pool);
        _ = Assert.IsType<OkObjectResult>(await owner.AddShare(
            "alice-thread",
            new ConversationShareRequest { SubjectId = Bob, Role = "viewer" },
            CancellationToken.None));

        var stored = await _store.LoadMetadataAsync("alice-thread", CancellationToken.None);
        _ = stored!.Visibility.Should().Be(Visibility.Shared);

        var grantee = CreateController(Signed(TenantA, Bob), pool);
        _ = Assert.IsType<OkObjectResult>(
            await grantee.GetMessages("alice-thread", viewer: null, CancellationToken.None));

        var stranger = CreateController(Signed(TenantA, "dir-a:carol"), pool);
        _ = Assert.IsType<NotFoundObjectResult>(
            await stranger.GetMessages("alice-thread", viewer: null, CancellationToken.None));
    }

    /// <summary>
    /// A viewer grant does not confer write. This is the case where a <c>403</c> is correct rather
    /// than a <c>404</c>: the grantee already knows the conversation exists, so hiding it would be
    /// theatre, and the reason code is what tells them their grant is the wrong one.
    /// </summary>
    [Fact]
    public async Task ViewerGrantee_IsRefusedAWriteWithAReason()
    {
        await using var pool = CreatePool();
        await SeedAsync("alice-thread", TenantA, Alice);

        var owner = CreateController(Signed(TenantA, Alice), pool);
        _ = await owner.AddShare(
            "alice-thread",
            new ConversationShareRequest { SubjectId = Bob, Role = "viewer" },
            CancellationToken.None);

        var grantee = CreateController(Signed(TenantA, Bob), pool);
        var refused = await grantee.UpdateMetadata(
            "alice-thread",
            new ConversationMetadataUpdate { Title = "renamed" },
            CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(refused);
        _ = objectResult.StatusCode.Should().Be(403);
        _ = System.Text.Json.JsonSerializer.Serialize(objectResult.Value)
            .Should().Contain("grant_does_not_confer_action", Exactly.Once());
    }

    /// <summary>
    /// The policy allowed the write; the agent pool refused it (#376). One agent is cached per thread
    /// and its owning user is frozen on the entry, so an editor grantee writing to a conversation whose
    /// agent is currently the owner's used to be answered <c>409 principal_conflict</c> - a refusal
    /// produced by a cache, not by a decision.
    /// </summary>
    /// <remarks>
    /// The second assertion is the one that matters, and it is deliberately stronger than "not 409":
    /// the turn must run on an agent of the GRANTEE's, not on the owner's. What that buys is the
    /// agent, NOT isolation - this remark used to claim the grantee does not inherit the owner's
    /// sandbox, and that was false. Releasing clears the pool entry only; the recreate resolves the
    /// same workspace id and the session registry keys <c>(workspaceId, appId)</c> off the SAME
    /// configured default app id for both interactive callers, so both land on one live session. The
    /// open product decision is tracked in #417. A fix that let the grantee through by relaxing the
    /// guard would satisfy the status assertion while leaving the owner's agent - and her in-flight
    /// state - underneath both of them.
    /// </remarks>
    [Fact]
    public async Task EditorGrantee_MayTakeATurn_WhileTheAgentIsStillBoundToTheOwner()
    {
        await using var pool = CreatePool();
        await SeedAsync("alice-thread", TenantA, Alice);

        var owner = CreateController(Signed(TenantA, Alice), pool);
        _ = await owner.AddShare(
            "alice-thread",
            new ConversationShareRequest { SubjectId = Bob, Role = "editor" },
            CancellationToken.None);

        // The owner has a live agent - the state her own turn leaves behind, and the state that made
        // this conflict intermittent: it clears whenever the entry is evicted.
        _ = pool.GetOrCreateAgent("alice-thread", DefaultMode(), null, null, ownerUserId: Alice);
        _ = pool.GetAgentOwnerUserId("alice-thread").Should().Be(Alice);

        var grantee = CreateController(Signed(TenantA, Bob), pool);
        var accepted = await grantee.SendMessage(
            "alice-thread",
            new SendMessageRequest { Text = "my turn" },
            CancellationToken.None);

        _ = Assert.IsType<AcceptedResult>(accepted).StatusCode.Should().Be(202);
        _ = pool.GetAgentOwnerUserId("alice-thread").Should().Be(
            Bob,
            "the grantee's turn must run on an agent of their own, not on the owner's");

        // A release happens on a HANDOFF, not on every turn. Without this, a fix that released
        // whenever the entry is owned at all would pass everything above while throwing away - and
        // reprovisioning - the caller's own agent on each message they send.
        _ = pool.TryGet("alice-thread", out var afterHandoff);
        _ = await grantee.SendMessage(
            "alice-thread",
            new SendMessageRequest { Text = "and another" },
            CancellationToken.None);
        _ = pool.TryGet("alice-thread", out var afterSecondTurn);
        _ = afterSecondTurn.Should().BeSameAs(
            afterHandoff,
            "a caller writing to an agent that is already theirs must keep it");
    }

    /// <summary>
    /// The other half of #376: releasing the owner's agent for an AUTHORIZED caller must not become a
    /// way for an unauthorized one to evict it. A tenant mate with no grant is refused as unknown -
    /// byte-identical to a thread that was never minted - and the owner's agent is still there.
    /// </summary>
    /// <remarks>
    /// Without the last assertion this test passes with the release moved above the authorization
    /// check, which would turn a 404 into a remote eviction any tenant member could trigger by id
    /// alone: the caller learns nothing from the response, but the owner loses their live agent and
    /// its sandbox. The refusal has to cost the owner nothing.
    /// </remarks>
    [Fact]
    public async Task TenantMateWithoutAGrant_IsRefusedAWrite_AndTheOwnersLiveAgentSurvivesIt()
    {
        await using var pool = CreatePool();
        await SeedAsync("alice-thread", TenantA, Alice);

        _ = pool.GetOrCreateAgent("alice-thread", DefaultMode(), null, null, ownerUserId: Alice);

        var stranger = CreateController(Signed(TenantA, Bob), pool);
        var refused = await stranger.SendMessage(
            "alice-thread",
            new SendMessageRequest { Text = "let me in" },
            CancellationToken.None);
        var neverExisted = await stranger.SendMessage(
            "no-such-thread",
            new SendMessageRequest { Text = "let me in" },
            CancellationToken.None);

        var hidden = Assert.IsType<NotFoundObjectResult>(refused);
        var missing = Assert.IsType<NotFoundObjectResult>(neverExisted);
        _ = hidden.StatusCode.Should().Be(404);
        _ = System.Text.Json.JsonSerializer.Serialize(hidden.Value)
            .Should().Be(
                System.Text.Json.JsonSerializer.Serialize(missing.Value)
                    .Replace("no-such-thread", "alice-thread", StringComparison.Ordinal));

        _ = pool.GetAgentOwnerUserId("alice-thread").Should().Be(
            Alice,
            "a refused caller must not be able to evict the owner's live agent");
    }

    /// <summary>
    /// The app-id freeze (#153) must survive the grantee release (#376). The release removes the whole
    /// pooled entry - including the <c>CallerCredential</c> the thread was frozen to - so the recreate
    /// that follows finds no entry, skips the app-id compare entirely, and re-freezes the conversation
    /// to whatever app the NEW caller presents. The freeze is not "deliberately not released": it is
    /// released, silently, by the same removal.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The #153 cross-actor matrix stayed green because an app-only caller carries no
    /// <c>EffectiveUserId</c> and returns from the release before it can remove anything. The hole
    /// needs a caller who has BOTH a user id (so the release runs) and a different app id from the one
    /// the thread is frozen to - which is exactly an editor grantee signing in through the UI to a
    /// conversation an S2S app minted.
    /// </para>
    /// <para>
    /// The second assertion is the one that cannot be satisfied by accident: refusing with the right
    /// status while still having torn the entry down would leave the conversation unfrozen for the
    /// next caller, so the freeze has to be READ before the removal, not after it.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task EditorGranteeOfAnotherApp_IsRefused_AndTheThreadStaysFrozenToTheAppThatMintedIt()
    {
        await using var pool = CreatePool();
        await SeedAsync("alice-thread", TenantA, Alice);

        var owner = CreateController(Signed(TenantA, Alice), pool);
        _ = await owner.AddShare(
            "alice-thread",
            new ConversationShareRequest { SubjectId = Bob, Role = "editor" },
            CancellationToken.None);

        // Alice's live agent was minted by an S2S app, so the thread is frozen to that app id.
        var daemon = new SandboxCredential("review-daemon", "0123456789abcdef0123456789abcdef");
        _ = pool.GetOrCreateAgent(
            "alice-thread",
            DefaultMode(),
            null,
            null,
            callerCredential: daemon,
            ownerUserId: Alice);
        _ = pool.GetAgentCallerAppId("alice-thread").Should().Be("review-daemon");

        // Bob is an authorized editor, but he arrives through the UI: no sandbox credential, so a null
        // app id against a frozen "review-daemon". #153 says that is a refusal.
        var grantee = CreateController(Signed(TenantA, Bob), pool);
        var refused = await grantee.SendMessage(
            "alice-thread",
            new SendMessageRequest { Text = "my turn" },
            CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(refused);
        _ = conflict.StatusCode.Should().Be(409);
        _ = System.Text.Json.JsonSerializer.Serialize(conflict.Value)
            .Should().Contain("\"code\":\"caller_credential_conflict\"");

        _ = pool.GetAgentCallerAppId("alice-thread").Should().Be(
            "review-daemon",
            "a refused handoff must leave the thread frozen to the app that minted it, not unfrozen");
        _ = pool.GetAgentOwnerUserId("alice-thread").Should().Be(
            Alice,
            "the refusal costs the owner nothing - her agent is still hers");

        // The refusal must not double as a directory lookup. Bob is an authorized editor of this
        // conversation; he is NOT authorized to learn which service minted it. The WebSocket sibling
        // of this refusal suppresses the same id - it did NOT originally, and was corrected alongside
        // this, so the two transports cannot be played against each other.
        _ = System.Text.Json.JsonSerializer.Serialize(conflict.Value)
            .Should().NotContain(
                "review-daemon",
                "a 409 must not name the app identity the thread is frozen to");
    }

    /// <summary>
    /// The <c>409 principal_conflict</c> body must not name EITHER end user. The exception message
    /// interpolates both stable ids so the LOG line is diagnosable; relaying that message to the
    /// caller as <c>detail</c> hands one user the other's stable id over HTTP.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The WebSocket sibling of this refusal
    /// (<c>ChatWebSocketManager.SendPrincipalConflictErrorAsync</c>) already suppresses exactly this,
    /// with the reason written down: the connection has not been authorized to learn who else uses
    /// the conversation. REST answering the same condition with the ids spelled out made the
    /// suppression decorative - an attacker just uses the other transport.
    /// </para>
    /// <para>
    /// Reaching the conflict at all takes a run in progress. The #376 release hands an authorized
    /// grantee an agent of their own, so the pool's principal guard is only reachable on the
    /// best-effort branch where a run is streaming and the release declines to evict it. That is why
    /// this pool's agents report a live run.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task PrincipalConflict_NamesNeitherPartysUserId()
    {
        const string Owner = "dir-a:euii-owner-9f2c";
        const string Grantee = "dir-a:euii-grantee-71ab";

        // A live run is what makes the guard reachable: the release below declines to evict a
        // streaming turn, so the recreate meets an entry still frozen to the owner.
        await using var pool = new MultiTurnAgentPool(
            (threadId, _, _) => new MultiTurnAgentPool.AgentCreationResult(
                new FakeMultiTurnAgent(threadId) { CurrentRunId = "run-in-flight" }),
            NullLogger<MultiTurnAgentPool>.Instance);

        await SeedAsync("shared-thread", TenantA, Owner);

        var owner = CreateController(Signed(TenantA, Owner), pool);
        _ = await owner.AddShare(
            "shared-thread",
            new ConversationShareRequest { SubjectId = Grantee, Role = "editor" },
            CancellationToken.None);

        _ = pool.GetOrCreateAgent("shared-thread", DefaultMode(), null, null, ownerUserId: Owner);
        _ = pool.IsRunInProgress("shared-thread").Should().BeTrue(
            "the guard is only reachable while the owner's turn is streaming");

        var grantee = CreateController(Signed(TenantA, Grantee), pool);
        var refused = await grantee.SendMessage(
            "shared-thread",
            new SendMessageRequest { Text = "my turn" },
            CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(refused);
        _ = conflict.StatusCode.Should().Be(409);

        var payload = System.Text.Json.JsonSerializer.Serialize(conflict.Value);
        _ = payload.Should().Contain("\"code\":\"principal_conflict\"");
        _ = payload.Should().NotContain(
            Owner,
            "the refused caller must not learn the owner's stable user id");
        _ = payload.Should().NotContain(
            Grantee,
            "a refusal has no reason to echo the caller's own stable id back into a body");
    }

    /// <summary>
    /// A grantee may not re-share, even an editor. Sharing is the owner's right; a grant that could
    /// be re-granted would make revocation meaningless.
    /// </summary>
    [Fact]
    public async Task EditorGrantee_MayNotReshare()
    {
        await using var pool = CreatePool();
        await SeedAsync("alice-thread", TenantA, Alice);

        var owner = CreateController(Signed(TenantA, Alice), pool);
        _ = await owner.AddShare(
            "alice-thread",
            new ConversationShareRequest { SubjectId = Bob, Role = "editor" },
            CancellationToken.None);

        var grantee = CreateController(Signed(TenantA, Bob), pool);
        var refused = await grantee.AddShare(
            "alice-thread",
            new ConversationShareRequest { SubjectId = "dir-a:carol", Role = "viewer" },
            CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(refused);
        _ = objectResult.StatusCode.Should().Be(403);
        _ = System.Text.Json.JsonSerializer.Serialize(objectResult.Value)
            .Should().Contain("grantee_may_not_reshare", Exactly.Once());
        _ = _grants.All.Should().ContainSingle();
    }

    /// <summary>
    /// Revoking the last grant returns the conversation to <see cref="Visibility.Private"/>.
    /// Visibility is stored rather than derived, so nothing else would ever make that transition
    /// and the conversation would read as shared with nobody forever.
    /// </summary>
    [Fact]
    public async Task RevokingTheLastGrant_ReturnsTheConversationToPrivate()
    {
        await using var pool = CreatePool();
        await SeedAsync("alice-thread", TenantA, Alice);

        var owner = CreateController(Signed(TenantA, Alice), pool);
        _ = await owner.AddShare(
            "alice-thread",
            new ConversationShareRequest { SubjectId = Bob, Role = "viewer" },
            CancellationToken.None);
        _ = await owner.AddShare(
            "alice-thread",
            new ConversationShareRequest { SubjectId = "dir-a:carol", Role = "viewer" },
            CancellationToken.None);

        _ = await owner.RemoveShare("alice-thread", Bob, CancellationToken.None);

        var stillShared = await _store.LoadMetadataAsync("alice-thread", CancellationToken.None);
        _ = stillShared!.Visibility.Should().Be(Visibility.Shared);

        _ = await owner.RemoveShare("alice-thread", "dir-a:carol", CancellationToken.None);

        var nowPrivate = await _store.LoadMetadataAsync("alice-thread", CancellationToken.None);
        _ = nowPrivate!.Visibility.Should().Be(Visibility.Private);
    }

    /// <summary>
    /// #375's last acceptance criterion: the client must be able to REFLECT the conversation's
    /// visibility, which the sharing routes flip as the first grant is added and the last is
    /// revoked. The roster is not a substitute for it - visibility is stored rather than derived
    /// (see <see cref="RevokingTheLastGrant_ReturnsTheConversationToPrivate"/>), and a
    /// tenant-published conversation carries no grants at all.
    /// </summary>
    /// <remarks>
    /// Asserted on the SERIALIZED payload, not on the DTO property: a field that never leaves the
    /// process is not exposed, and reading it off the object would pass either way.
    /// </remarks>
    [Fact]
    public async Task List_ExposesTheStoredVisibility_AndFollowsTheShareFlip()
    {
        await using var pool = CreatePool();
        await SeedAsync("alice-thread", TenantA, Alice);

        var owner = CreateController(Signed(TenantA, Alice), pool);

        _ = (await ListedVisibilityAsync(owner)).Should().Be("private");

        _ = Assert.IsType<OkObjectResult>(await owner.AddShare(
            "alice-thread",
            new ConversationShareRequest { SubjectId = Bob, Role = "viewer" },
            CancellationToken.None));

        _ = (await ListedVisibilityAsync(owner)).Should().Be("shared");

        _ = await owner.RemoveShare("alice-thread", Bob, CancellationToken.None);

        _ = (await ListedVisibilityAsync(owner)).Should().Be("private");
    }

    /// <summary>MVC's default naming policy, so the payload read here is the one a client receives.</summary>
    private static readonly JsonSerializerOptions WireOptions =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>
    /// The <c>visibility</c> the conversation listing puts on the wire for the single conversation
    /// these tests seed. Null when the payload carries no such field at all.
    /// </summary>
    private static async Task<string?> ListedVisibilityAsync(ConversationsController controller)
    {
        var ok = Assert.IsType<OkObjectResult>(await controller.List());
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value, WireOptions));
        var summary = document.RootElement.EnumerateArray().Single();
        return summary.TryGetProperty("visibility", out var visibility)
            ? visibility.GetString()
            : null;
    }

    /// <summary>
    /// #482's first acceptance criterion. The listing says whether THIS viewer may change who the
    /// conversation is shared with, so the share control can stop offering a mutation the server is
    /// going to refuse. <c>visibility</c> cannot answer it: the owner and the grantee of one shared
    /// conversation both read <c>"shared"</c>.
    /// </summary>
    /// <remarks>
    /// Both halves are asserted from ONE seeded conversation listed twice, so a
    /// <c>canShare</c> wired to something that happens to differ between the two rows (a title, an
    /// id) cannot pass: the row is byte-identical and only the viewer changes.
    /// </remarks>
    [Fact]
    public async Task List_SaysTheOwnerMayShare_AndAGranteeMayNot()
    {
        await using var pool = CreatePool();
        await SeedAsync("alice-thread", TenantA, Alice);

        var owner = CreateController(Signed(TenantA, Alice), pool);
        _ = Assert.IsType<OkObjectResult>(await owner.AddShare(
            "alice-thread",
            new ConversationShareRequest { SubjectId = Bob, Role = "viewer" },
            CancellationToken.None));

        _ = (await ListedCanShareAsync(owner, "alice-thread")).Should().BeTrue();

        var grantee = CreateController(Signed(TenantA, Bob), pool);
        _ = (await ListedCanShareAsync(grantee, "alice-thread")).Should().BeFalse();
    }

    /// <summary>
    /// A tenant admin may READ every conversation in the tenant and may not re-share any of them
    /// (<c>admin_may_not_reshare</c>). The flag is asserted against the refusal the share ROUTE
    /// produces for the same principal, so the listing cannot answer one thing while the route
    /// answers another - which is the whole failure mode of computing a permission twice.
    /// </summary>
    [Fact]
    public async Task List_SaysATenantAdminMayNotShare_MatchingWhatTheShareRouteRefuses()
    {
        await using var pool = CreatePool();
        await SeedAsync("alice-thread", TenantA, Alice);

        var admin = CreateController(Signed(TenantA, Bob, ResourceAccessPolicy.AdminRole), pool);

        _ = (await ListedCanShareAsync(admin, "alice-thread")).Should().BeFalse();

        var refused = await admin.AddShare(
            "alice-thread",
            new ConversationShareRequest { SubjectId = "dir-a:carol", Role = "viewer" },
            CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(refused);
        _ = objectResult.StatusCode.Should().Be(403);
        _ = JsonSerializer.Serialize(objectResult.Value)
            .Should().Contain("admin_may_not_reshare", Exactly.Once());
    }

    /// <summary>
    /// The owner of a TENANT-PUBLISHED conversation may not share it
    /// (<c>publication_supersedes_sharing</c>), so <c>canShare</c> is false for the very principal
    /// that owns the row.
    /// </summary>
    /// <remarks>
    /// This is the case that fails an owner-shaped shortcut. A controller that computed the flag as
    /// "the row's <c>OwnerUserId</c> is me" - the obvious re-derivation, and the one this issue
    /// exists to prevent - passes every other test in this file and fails only here.
    /// </remarks>
    [Fact]
    public async Task List_SaysTheOwnerOfAPublishedConversationMayNotShare()
    {
        await using var pool = CreatePool();
        await SeedAsync("alice-thread", TenantA, Alice, Visibility.TenantPublished);

        var owner = CreateController(Signed(TenantA, Alice), pool);

        _ = (await ListedCanShareAsync(owner, "alice-thread")).Should().BeFalse();
    }

    /// <summary>
    /// With <c>Identity:Enforce</c> off the authorizer allows every action, so the listing must say
    /// so too. A flag hard-wired to false, or one that quietly needs a principal, would hide the
    /// share control throughout the pre-enforcement window that
    /// <c>docs/deployment/AUTH_ENFORCE.md</c> exists to make survivable.
    /// </summary>
    [Fact]
    public async Task List_WithEnforcementOff_SaysTheViewerMayShare()
    {
        await using var pool = CreatePool();
        await SeedAsync("alice-thread", TenantA, Alice);

        var unenforced = CreateController(principal: null, pool, enforce: false);

        _ = (await ListedCanShareAsync(unenforced, "alice-thread")).Should().BeTrue();
    }

    /// <summary>
    /// The <c>canShare</c> the listing puts on the wire for one conversation. Null when the payload
    /// carries no such field, which is what makes these tests fail before the field exists rather
    /// than reading a C# <c>default</c> as a deliberate <c>false</c>.
    /// </summary>
    /// <remarks>
    /// The row itself is located by <c>threadId</c> and asserted to be present: a listing that
    /// simply dropped the conversation would otherwise answer "no permission" and read as a pass.
    /// </remarks>
    private static async Task<bool?> ListedCanShareAsync(
        ConversationsController controller,
        string threadId)
    {
        var ok = Assert.IsType<OkObjectResult>(await controller.List());
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value, WireOptions));
        var summary = document.RootElement
            .EnumerateArray()
            .Single(e => e.GetProperty("threadId").GetString() == threadId);
        return summary.TryGetProperty("canShare", out var canShare)
            ? canShare.GetBoolean()
            : null;
    }

    /// <summary>
    /// A stranger cannot revoke, and the grant survives the attempt. Asserting only on the status
    /// would pass against an implementation that revoked and then reported a refusal.
    /// </summary>
    [Fact]
    public async Task Stranger_CannotRevokeAGrant()
    {
        await using var pool = CreatePool();
        await SeedAsync("alice-thread", TenantA, Alice);

        var owner = CreateController(Signed(TenantA, Alice), pool);
        _ = await owner.AddShare(
            "alice-thread",
            new ConversationShareRequest { SubjectId = Bob, Role = "viewer" },
            CancellationToken.None);

        var mallory = CreateController(Signed(TenantB, Mallory), pool);
        var refused = await mallory.RemoveShare("alice-thread", Bob, CancellationToken.None);

        _ = Assert.IsType<NotFoundObjectResult>(refused).StatusCode.Should().Be(404);
        _ = _grants.All.Should().ContainSingle();
    }

    /// <summary>An unrecognised role is refused, never defaulted to the weaker one.</summary>
    [Fact]
    public async Task UnknownRole_IsRefused()
    {
        await using var pool = CreatePool();
        await SeedAsync("alice-thread", TenantA, Alice);

        var owner = CreateController(Signed(TenantA, Alice), pool);
        var refused = await owner.AddShare(
            "alice-thread",
            new ConversationShareRequest { SubjectId = Bob, Role = "owner" },
            CancellationToken.None);

        _ = Assert.IsType<BadRequestObjectResult>(refused).StatusCode.Should().Be(400);
        _ = _grants.All.Should().BeEmpty();
    }

    /// <summary>
    /// A row the startup repair has not reached belongs to no tenant, so it is refused to everyone -
    /// including a tenant admin. Reading it as "unowned, therefore anyone's" is the failure this
    /// closes.
    /// </summary>
    [Fact]
    public async Task UnstampedRow_IsRefusedToEveryone()
    {
        await using var pool = CreatePool();
        await SeedAsync("legacy-thread", tenantId: null!, ownerUserId: null);

        var admin = CreateController(Signed(TenantA, Alice, "admin"), pool);

        var result = await admin.GetMessages("legacy-thread", viewer: null, CancellationToken.None);

        _ = Assert.IsType<NotFoundObjectResult>(result).StatusCode.Should().Be(404);
    }

    /// <summary>
    /// With enforcement off nothing above applies: the pre-rollout path is unchanged, which is what
    /// keeps every test predating #302 green and what makes the flip - not the deploy - the moment
    /// behaviour changes.
    /// </summary>
    [Fact]
    public async Task EnforcementOff_LeavesEveryRouteOpen()
    {
        await using var pool = CreatePool();
        await SeedAsync("alice-thread", TenantA, Alice);

        var controller = CreateController(principal: null, pool, enforce: false);

        var result = await controller.GetMessages("alice-thread", viewer: null, CancellationToken.None);

        _ = Assert.IsType<OkObjectResult>(result);
    }
}
