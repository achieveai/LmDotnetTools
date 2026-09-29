using System.Collections.Immutable;
using AchieveAi.LmDotnetTools.LmCore.Identity;
using LmStreaming.Sample.Identity;
using LmStreaming.Sample.Services;
using LmStreaming.Sample.Tests.Agents;
using LmStreaming.Sample.Tests.Identity;
using LmStreaming.Sample.Tests.TestDoubles;
using Microsoft.Extensions.Time.Testing;

namespace LmStreaming.Sample.Tests.Controllers;

/// <summary>
/// Pins the conversation fork routes: <c>POST fork</c>, <c>GET branches</c>, and the delete that
/// keeps an original its forks still read.
/// </summary>
/// <remarks>
/// The store is a real <see cref="InMemoryConversationStore"/>, so the fork's history is the one the
/// store actually stitches, not a mock's answer.
/// </remarks>
public sealed class ConversationsControllerForkTests
{
    private const string Source = "thread-source";

    private readonly InMemoryConversationStore _store = new();

    /// <summary>
    /// A fork is a new conversation that runs like its source - same provider, workspace, mode and
    /// prompt settings - but owns none of its state, and reads the shared history under its ids.
    /// </summary>
    [Fact]
    public async Task Fork_CreatesAPeerConversation_CarryingItsSettingsButNotItsState()
    {
        await SeedSourceAsync(providerId: "test");
        await using var pool = ConversationsControllerTests.CreatePool();
        var controller = CreateController(pool);

        var result = await controller.Fork(Source, new ForkConversationRequest { AfterRunId = "run-1" });

        var created = Assert.IsType<ObjectResult>(result);
        created.StatusCode.Should().Be(201);
        var fork = created.Value.Should().BeOfType<ForkConversationResponse>().Subject;
        fork.Title.Should().Be("Plan (fork)");
        fork.RootThreadId.Should().Be(Source);
        fork.ForkedFrom.Should()
            .BeEquivalentTo(
                new ForkOrigin
                {
                    ThreadId = Source,
                    MessageId = "a1",
                    Seq = 2,
                }
            );
        fork.PrefillText.Should().BeNull();

        var metadata = await _store.LoadMetadataAsync(fork.ThreadId);
        metadata!.Properties.Should().Contain(MultiTurnAgentPool.ProviderPropertyKey, "test");
        metadata.Properties.Should().Contain(MultiTurnAgentPool.WorkspacePropertyKey, "ws-1");
        metadata.Properties.Should().Contain(MultiTurnAgentPool.ModePropertyKey, "math-helper");
        metadata.Properties.Should().Contain("title", "Plan (fork)");
        metadata.Properties.Should().NotContainKey("usage.source-only", "usage describes the source's own runs");
        metadata.LatestRunId.Should().Be("run-1");

        (await _store.LoadMessagesAsync(fork.ThreadId)).Select(m => m.Id).Should().Equal("u1", "a1");
        (await _store.LoadMessagesAsync(Source)).Should().HaveCount(4, "forking changes nothing on the source");

        var list = ListOf(await controller.List());
        list.Single(c => c.ThreadId == fork.ThreadId)
            .Should()
            .Match<ConversationSummary>(c => c.ForkedFrom!.ThreadId == Source && c.RootThreadId == Source);
        list.Single(c => c.ThreadId == Source)
            .Should()
            .Match<ConversationSummary>(c => c.ForkedFrom == null && c.RootThreadId == null && !c.Deleted);
    }

    /// <summary>Edit-in-fork: the fork ends just before the edited question and hands its text back.</summary>
    [Fact]
    public async Task Fork_BeforeAUserMessage_ReturnsItsTextToPrefill()
    {
        await SeedSourceAsync(providerId: "test");
        await using var pool = ConversationsControllerTests.CreatePool();

        var result = await CreateController(pool)
            .Fork(Source, new ForkConversationRequest { BeforeMessageId = "u2", Title = "  Retry  " });

        var fork = Assert.IsType<ObjectResult>(result).Value.Should().BeOfType<ForkConversationResponse>().Subject;
        fork.PrefillText.Should().Be("second question");
        fork.Title.Should().Be("Retry");
        (await _store.LoadMessagesAsync(fork.ThreadId)).Select(m => m.Id).Should().Equal("u1", "a1");
    }

    /// <summary>
    /// Every refusal, with the status and code the client branches on. The last one is the reason a
    /// delete must not erase: a hidden original is gone for the user, so it cannot be forked again.
    /// </summary>
    [Fact]
    public async Task Fork_Refuses_CliProviders_AgentThreads_BadAnchors_ARunningTurn_AndUnknownOrDeletedSources()
    {
        await SeedSourceAsync(providerId: "claude");
        await using var pool = ConversationsControllerTests.CreatePool();
        var controller = CreateController(pool);

        Code(await controller.Fork(Source, new ForkConversationRequest { AfterRunId = "run-1" }))
            .Should()
            .Be((409, "cli_provider_unsupported"));

        await SetPropertyAsync(Source, MultiTurnAgentPool.ProviderPropertyKey, "test");
        Code(await controller.Fork("subagent-abc", new ForkConversationRequest { AfterRunId = "run-1" }))
            .Should()
            .Be((403, "agent_owned_thread"));
        Code(await controller.Fork(Source, new ForkConversationRequest { AfterMessageId = "nope" }))
            .Should()
            .Be((400, ForkRefusals.AnchorNotFound));
        Code(await controller.Fork(Source, new ForkConversationRequest { AfterRunId = "run-9" }))
            .Should()
            .Be((400, ForkRefusals.AnchorNotFound));
        Code(await controller.Fork(Source, new ForkConversationRequest()))
            .Should()
            .Be((400, ForkRefusals.InvalidAnchor));

        var agent = (FakeMultiTurnAgent)
            pool.GetOrCreateAgent(Source, SystemChatModes.GetById(SystemChatModes.DefaultModeId)!);
        agent.CurrentRunId = "run-2";
        Code(await controller.Fork(Source, new ForkConversationRequest { AfterRunId = "run-2" }))
            .Should()
            .Be((409, ForkRefusals.TurnInProgress), "the running turn still writes the rows the fork would share");
        Assert
            .IsType<ObjectResult>(await controller.Fork(Source, new ForkConversationRequest { AfterRunId = "run-1" }))
            .StatusCode.Should()
            .Be(201, "a fork before the running turn shares nothing it writes");
        await pool.RemoveAgentAsync(Source);

        Code(await controller.Fork("thread-missing", new ForkConversationRequest { AfterRunId = "run-1" }))
            .Should()
            .Be((404, "unknown_thread"));
        (await controller.Delete(Source)).Should().BeOfType<NoContentResult>();
        Code(await controller.Fork(Source, new ForkConversationRequest { AfterRunId = "run-1" }))
            .Should()
            .Be((404, "unknown_thread"));
    }

    /// <summary>
    /// Forking needs Write on the source: a fork keeps the source's messages alive past its deletion,
    /// so a viewer must not be able to pin a conversation it cannot change.
    /// </summary>
    [Fact]
    public async Task Fork_ByAViewer_IsRefused_AndCreatesNothing()
    {
        const string Tenant = "tnt_a";
        await SeedSourceAsync(providerId: "test", tenantId: Tenant, ownerUserId: "dir-a:alice");
        var grants = new InMemoryResourceGrantStore();
        await grants.GrantAsync(
            new ResourceGrant
            {
                TenantId = Tenant,
                Resource = ConversationAuthorizer.ConversationRef(Source),
                SubjectId = "dir-a:bob",
                Role = GrantRole.Viewer,
                GrantedBy = "dir-a:alice",
                GrantedAt = DateTimeOffset.UnixEpoch,
                ExpiresAt = null,
            },
            CancellationToken.None
        );
        var bob = new Principal
        {
            TenantId = Tenant,
            Actor = new PrincipalRef(PrincipalKind.EndUser, "dir-a:bob"),
            Roles = new HashSet<string>(StringComparer.Ordinal),
            Source = PrincipalSource.Interactive,
        };
        await using var pool = ConversationsControllerTests.CreatePool();
        var controller = CreateController(pool, TestAuthorizers.Enforcing(bob, grants, new RecordingAuditSink()));

        (await controller.GetMessages(Source)).Should().BeOfType<OkObjectResult>("a viewer may read it");
        var refused = await controller.Fork(Source, new ForkConversationRequest { AfterRunId = "run-1" });

        Assert.IsAssignableFrom<ObjectResult>(refused).StatusCode.Should().Be(403);
        (await _store.ListThreadsAsync(100, 0)).Select(t => t.ThreadId).Should().Equal(Source);
    }

    /// <summary>
    /// Deleting an original its forks still read hides it: the list says deleted, the fork still
    /// loads the shared messages; deleting the last fork then erases the original too.
    /// </summary>
    [Fact]
    public async Task Delete_OfAnOriginalWithAFork_HidesIt_UntilTheForkGoes()
    {
        await SeedSourceAsync(providerId: "test");
        await using var pool = ConversationsControllerTests.CreatePool();
        var controller = CreateController(pool);
        var fork = await ForkAsync(controller, "run-1");

        (await controller.Delete(Source)).Should().BeOfType<NoContentResult>();

        ListOf(await controller.List()).Single(c => c.ThreadId == Source).Deleted.Should().BeTrue();
        var messages = Assert.IsType<OkObjectResult>(await controller.GetMessages(fork));
        messages.Value.Should().BeAssignableTo<IEnumerable<PersistedMessage>>().Which.Should().HaveCount(2);

        (await controller.Delete(fork)).Should().BeOfType<NoContentResult>();
        (await _store.LoadMetadataAsync(Source)).Should().BeNull("its last fork is gone");
        (await _store.LoadMessagesAsync(Source)).Should().BeEmpty();
    }

    /// <summary>
    /// The switcher at the fork point offers the original first, then the fork, each titled, with the
    /// viewed one current; a conversation without forks has no points.
    /// </summary>
    [Fact]
    public async Task Branches_OfferEveryContinuationAtTheForkPoint_WithTitles()
    {
        await SeedSourceAsync(providerId: "test");
        await using var pool = ConversationsControllerTests.CreatePool();
        var controller = CreateController(pool);

        Points(await controller.GetBranches(Source)).Should().BeEmpty();

        var fork = await ForkAsync(controller, "run-1");

        var points = Points(await controller.GetBranches(fork));
        var point = points.Should().ContainSingle().Subject;
        point.AfterMessageId.Should().Be("a1");
        point.AfterSeq.Should().Be(2);
        point
            .Options.Select(o => (o.ThreadId, o.Title, o.Current))
            .Should()
            .Equal((Source, "Plan", false), (fork, "Plan (fork)", true));

        Points(await controller.GetBranches(Source))
            .Single()
            .Options.Select(o => (o.ThreadId, o.Current))
            .Should()
            .Equal((Source, true), (fork, false));
    }

    /// <summary>
    /// Forks at one point are offered in the order they were made, not the order the store lists them
    /// (most recently used first), so the switcher's positions do not shuffle as they are used.
    /// </summary>
    [Fact]
    public async Task Branches_OrderSiblingForksByCreation_AndAnUnknownThreadHasNone()
    {
        await SeedSourceAsync(providerId: "test");
        await using var pool = ConversationsControllerTests.CreatePool();
        var clock = new FakeTimeProvider(DateTimeOffset.FromUnixTimeMilliseconds(1_000));
        var controller = CreateController(pool, time: clock);

        var first = await ForkAsync(controller, "run-1");
        clock.Advance(TimeSpan.FromSeconds(1));
        var second = await ForkAsync(controller, "run-1");
        ListOf(await controller.List())
            .Select(c => c.ThreadId)
            .Should()
            .StartWith([second, first], "the list leads with the newest, which the switcher must not follow");

        Points(await controller.GetBranches(Source))
            .Single()
            .Options.Select(o => o.ThreadId)
            .Should()
            .Equal(Source, first, second);

        Code(await controller.GetBranches("thread-missing")).Should().Be((404, "unknown_thread"));
    }

    /// <summary>The provider catalog tells the client which providers cannot fork.</summary>
    [Fact]
    public void ProviderCatalog_MarksTheCliBackedProviders()
    {
        var catalog = new ProviderRegistry(new FakeFileSystemProbe()).ListAll().ToDictionary(p => p.Id);

        foreach (var cli in new[] { "claude", "codex", "copilot" })
        {
            catalog[cli].CliBacked.Should().BeTrue(cli);
        }

        catalog["test"].CliBacked.Should().BeFalse();
        catalog["openai"].CliBacked.Should().BeFalse();
    }

    private ConversationsController CreateController(
        MultiTurnAgentPool pool,
        ConversationAuthorizer? authorizer = null,
        TimeProvider? time = null
    ) =>
        new(
            _store,
            pool,
            Mock.Of<IChatModeStore>(),
            Mock.Of<IWorkspaceStore>(),
            new FakeProviderRegistry(defaultProviderId: "test", available: ["test"]).ToReal(),
            new ConversationStatusResolver(_store, _store),
            time ?? TimeProvider.System,
            new WorkflowRunRegistry(),
            authorizer ?? TestAuthorizers.Disabled(),
            NullLogger<ConversationsController>.Instance,
            NullLogger<AgentHierarchyService>.Instance,
            new SubAgentScanCoverageCache(),
            new ConversationDescendantScanner(_store, NullLogger<ConversationDescendantScanner>.Instance)
        );

    /// <summary>Two runs: <c>u1 a1</c> (run-1) then <c>u2 a2</c> (run-2).</summary>
    private async Task SeedSourceAsync(string providerId, string? tenantId = null, string? ownerUserId = null)
    {
        await _store.SaveMetadataAsync(
            Source,
            new ThreadMetadata
            {
                ThreadId = Source,
                LastUpdated = 1,
                TenantId = tenantId,
                OwnerUserId = ownerUserId,
                Properties = ImmutableDictionary<string, object>
                    .Empty.Add("title", "Plan")
                    .Add(MultiTurnAgentPool.ProviderPropertyKey, providerId)
                    .Add(MultiTurnAgentPool.WorkspacePropertyKey, "ws-1")
                    .Add(MultiTurnAgentPool.ModePropertyKey, "math-helper")
                    .Add("usage.source-only", "42"),
            }
        );

        foreach (
            var (id, runId, role, text) in new[]
            {
                ("u1", "run-1", Role.User, "first question"),
                ("a1", "run-1", Role.Assistant, "first answer"),
                ("u2", "run-2", Role.User, "second question"),
                ("a2", "run-2", Role.Assistant, "second answer"),
            }
        )
        {
            var row = MessagePersistenceConverter.ToPersistedMessage(
                new TextMessage
                {
                    Text = text,
                    Role = role,
                    RunId = runId,
                },
                Source,
                runId
            ) with
            {
                Id = id,
            };
            await _store.AppendMessagesAsync(Source, [row]);
        }
    }

    private Task SetPropertyAsync(string threadId, string key, string value) =>
        _store.UpdateMetadataAsync(
            threadId,
            existing => existing! with { Properties = existing.Properties!.SetItem(key, value) },
            CancellationToken.None
        );

    private static async Task<string> ForkAsync(ConversationsController controller, string afterRunId)
    {
        var result = await controller.Fork(Source, new ForkConversationRequest { AfterRunId = afterRunId });
        return ((ForkConversationResponse)Assert.IsType<ObjectResult>(result).Value!).ThreadId;
    }

    private static (int Status, string? Code) Code(IActionResult result)
    {
        var refused = Assert.IsAssignableFrom<ObjectResult>(result);
        using var body = JsonDocument.Parse(JsonSerializer.Serialize(refused.Value));
        return (
            refused.StatusCode ?? 0,
            body.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null
        );
    }

    private static List<ConversationSummary> ListOf(IActionResult result) =>
        [.. (IEnumerable<ConversationSummary>)Assert.IsType<OkObjectResult>(result).Value!];

    private static IReadOnlyList<ConversationBranchPoint> Points(IActionResult result) =>
        ((ConversationBranchesResponse)Assert.IsType<OkObjectResult>(result).Value!).Points;
}
