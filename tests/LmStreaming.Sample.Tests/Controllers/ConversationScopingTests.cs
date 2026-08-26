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

    private ConversationsController CreateController(
        Principal? principal,
        MultiTurnAgentPool pool,
        bool enforce = true,
        IResourceGrantStore? grantsOverride = null) =>
        new(
            _store,
            pool,
            ModeStoreResolvingSystemModes(),
            Mock.Of<IWorkspaceStore>(),
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
    /// the turn must run on an agent of the GRANTEE's, not on the owner's. Sharing a conversation
    /// grants the conversation, whose history is durable and rehydrates; it does not grant the
    /// owner's sandbox, which is provisioned for one caller and holds whatever that caller put in it.
    /// A fix that let the grantee through by relaxing the guard would satisfy the status assertion
    /// and hand them the owner's sandbox.
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
