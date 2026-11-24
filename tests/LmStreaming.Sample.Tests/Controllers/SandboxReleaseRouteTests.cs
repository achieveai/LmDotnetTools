using System.Collections.Immutable;
using AchieveAi.LmDotnetTools.LmCore.Identity;
using AchieveAi.LmDotnetTools.LmTestUtils;
using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
using LmStreaming.Sample.Identity;
using LmStreaming.Sample.Services;
using LmStreaming.Sample.Tests.Identity;
using LmStreaming.Sample.Tests.Services;
using LmStreaming.Sample.Tests.TestDoubles;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace LmStreaming.Sample.Tests.Controllers;

/// <summary>
/// The sandbox release route driven directly, for the states a composition host cannot hold still: a run in
/// progress, a turn that rebuilds the agent while the old one is being disposed, and a host without a sandbox
/// registry. The pool and the registry are the production types; the agent is <see cref="FakeMultiTurnAgent"/> and
/// the gateway is <see cref="OwnedSandboxGateway"/>.
/// </summary>
public sealed class SandboxReleaseRouteTests : LoggingTestBase
{
    private const string ThreadId = "thread-review-7";
    private static readonly AgentProfile WorkspaceMode = SystemChatModes.GetById(SystemChatModes.WorkspaceAgentModeId)!;

    public SandboxReleaseRouteTests(ITestOutputHelper output)
        : base(output) { }

    [Fact]
    public async Task The_release_route_refuses_while_a_run_is_in_progress_and_deletes_nothing()
    {
        var gateway = new OwnedSandboxGateway();
        await using var registry = gateway.CreateRegistry(LoggerFactory, "lm-release-route-test");
        await using var pool = ConversationsControllerTests.CreatePool();
        var store = await SeedAsync("PRs/r-7");
        var agent = (FakeMultiTurnAgent)pool.GetOrCreateAgent(ThreadId, WorkspaceMode);
        agent.StartRun("run-1");
        var session = await registry.GetOrCreateSessionAsync(OwnedRef());
        var controller = CreateController(store, pool, registry);

        var result = await controller.ReleaseSandbox(
            ThreadId,
            new ReleaseSandboxRequest { WorkingDirectoryRelPath = "PRs/r-9" },
            CancellationToken.None
        );
        Log(result, gateway);

        var conflict = result.Should().BeOfType<ConflictObjectResult>().Subject;
        JsonSerializer
            .Serialize(conflict.Value)
            .Should()
            .Contain("sandbox_release_while_running")
            .And.Contain("work in hand", "the pool reports the run as Busy");
        pool.GetOrCreateAgent(ThreadId, WorkspaceMode).Should().BeSameAs(agent);
        registry
            .TryGetExistingSession(SandboxSessionRegistry.DefaultWorkspaceId, false, ThreadId)
            .Should()
            .BeSameAs(session);
        gateway.DeleteAttempts.Should().Be(0);
        (await ConversationWorkingDirectory.ReadAsync(store, ThreadId)).Should().Be("PRs/r-7");
    }

    [Fact]
    public async Task A_turn_that_rebuilds_the_agent_while_the_old_one_is_disposed_keeps_its_sandbox_and_the_route_answers_409()
    {
        await using var race = await ReplacementRace.StartAsync(this);

        var result = await race.RunAsync(controller =>
            controller.ReleaseSandbox(
                ThreadId,
                new ReleaseSandboxRequest { WorkingDirectoryRelPath = "PRs/r-9" },
                CancellationToken.None
            )
        );

        race.AssertReplacementKeptItsSandbox(result, "sandbox_release_while_running");
        (await ConversationWorkingDirectory.ReadAsync(race.Store, ThreadId))
            .Should()
            .Be("PRs/r-7", "the replacement was built for this directory");
    }

    [Fact]
    public async Task A_turn_that_rebuilds_the_agent_while_delete_disposes_the_old_one_keeps_its_sandbox_and_transcript_and_delete_answers_409()
    {
        await using var race = await ReplacementRace.StartAsync(this);
        await race.Store.AppendMessagesAsync(
            ThreadId,
            [
                MessagePersistenceConverter.ToPersistedMessage(
                    new TextMessage { Role = Role.User, Text = "review r-7" },
                    ThreadId,
                    "run-1"
                ),
            ]
        );

        var result = await race.RunAsync(controller => controller.Delete(ThreadId, CancellationToken.None));

        race.AssertReplacementKeptItsSandbox(result, "conversation_delete_while_running");
        (await race.Store.LoadMetadataAsync(ThreadId))
            .Should()
            .NotBeNull("the conversation is not deleted under the replacement");
        (await race.Store.LoadMessagesAsync(ThreadId))
            .Should()
            .ContainSingle("the transcript survives the refused delete");
    }

    /// <summary>
    /// An editor grantee's turn on the owner's dedicated thread hands the thread over: the owner's idle agent is
    /// released and the grantee gets its own, on the same sandbox. Nothing is deleted or created on the gateway.
    /// </summary>
    [Fact]
    public async Task An_editor_grantees_turn_on_the_owners_dedicated_thread_reuses_its_sandbox_and_deletes_nothing()
    {
        const string Tenant = "tnt_a";
        const string Alice = "dir-a:alice";
        const string Bob = "dir-a:bob";
        var gateway = new OwnedSandboxGateway();
        await using var registry = gateway.CreateRegistry(LoggerFactory, "lm-release-route-test");
        await using var pool = ConversationsControllerTests.CreatePool();
        var store = await SeedAsync("PRs/r-7", Tenant, Alice);
        var grants = new InMemoryResourceGrantStore();
        await grants.GrantAsync(
            new ResourceGrant
            {
                TenantId = Tenant,
                Resource = ConversationAuthorizer.ConversationRef(ThreadId),
                SubjectId = Bob,
                Role = GrantRole.Editor,
                GrantedBy = Alice,
                GrantedAt = DateTimeOffset.UnixEpoch,
                ExpiresAt = null,
            },
            CancellationToken.None
        );
        var alices = pool.GetOrCreateAgent(
            ThreadId,
            WorkspaceMode,
            requestedProviderId: null,
            requestResponseDumpFileName: null,
            ownerUserId: Alice
        );
        var session = await registry.GetOrCreateSessionAsync(OwnedRef());
        var bob = new Principal
        {
            TenantId = Tenant,
            Actor = new PrincipalRef(PrincipalKind.EndUser, Bob),
            Roles = new HashSet<string>(StringComparer.Ordinal),
            Source = PrincipalSource.Interactive,
        };
        var controller = ConversationsControllerTests.CreateController(
            store,
            pool,
            ConversationsControllerTests.ModeStoreResolvingSystemModes(),
            sandboxSessionRegistry: registry,
            authorizer: TestAuthorizers.Enforcing(bob, grants, new RecordingAuditSink())
        );

        var result = await controller.SwitchMode(
            ThreadId,
            new SwitchModeRequest { ModeId = SystemChatModes.WorkflowAuthorModeId },
            CancellationToken.None
        );
        Log(result, gateway);

        result.Should().BeOfType<OkObjectResult>();
        pool.TryGetHandoffState(ThreadId, out var handoff).Should().BeTrue();
        handoff.OwnerUserId.Should().Be(Bob, "the owner's idle agent was released and the grantee got its own");
        pool.GetOrCreateAgent(ThreadId, WorkspaceMode).Should().NotBeSameAs(alices);
        (await registry.GetOrCreateSessionAsync(OwnedRef()))
            .Should()
            .BeSameAs(session, "the grantee's turn reuses the conversation's sandbox");
        gateway.Creates.Should().ContainSingle();
        gateway.DeleteAttempts.Should().Be(0, "a handoff deletes no sandbox");
    }

    [Fact]
    public async Task The_release_route_on_a_host_without_a_sandbox_registry_answers_400_and_changes_nothing()
    {
        await using var pool = ConversationsControllerTests.CreatePool();
        var store = await SeedAsync("PRs/r-7");
        var before = await store.LoadMetadataAsync(ThreadId);
        var agent = pool.GetOrCreateAgent(ThreadId, WorkspaceMode);
        var controller = CreateController(store, pool, registry: null);

        var result = await controller.ReleaseSandbox(
            ThreadId,
            new ReleaseSandboxRequest { WorkingDirectoryRelPath = "PRs/r-9" },
            CancellationToken.None
        );
        Logger.LogDebug("Release without a registry answered {Result}", Describe(result));

        var refusal = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        var body = JsonSerializer.Serialize(refusal.Value);
        body.Should().Contain("dedicated_sandbox_required").And.Contain("no sandbox registry");
        (await store.LoadMetadataAsync(ThreadId)).Should().BeEquivalentTo(before);
        pool.GetOrCreateAgent(ThreadId, WorkspaceMode).Should().BeSameAs(agent);
    }

    [Fact]
    public async Task The_release_route_refuses_an_idle_conversation_parked_on_a_question_and_deletes_nothing()
    {
        // No run is in progress, so the plain idle release would dispose the agent and orphan the question. The
        // route refuses whatever the idle sweep would keep loaded.
        var gateway = new OwnedSandboxGateway();
        await using var registry = gateway.CreateRegistry(LoggerFactory, "lm-release-route-test");
        await using var pool = await ConversationsControllerTests.CreatePoolWithParkedAskUserQuestionAsync(
            ThreadId,
            "tc_colour"
        );
        await Wait.UntilAsync(
            () => !pool.IsRunInProgress(ThreadId) && pool.GetOutstandingInputCountForTest(ThreadId) == 0,
            "the run parked and its input was retired"
        );
        (await pool.HasPendingAskUserQuestionAsync(ThreadId)).Should().BeTrue();
        pool.TryGetHandoffState(ThreadId, out var before).Should().BeTrue();
        var store = await SeedAsync("PRs/r-7");
        var session = await registry.GetOrCreateSessionAsync(OwnedRef());
        var controller = CreateController(store, pool, registry);

        var result = await controller.ReleaseSandbox(
            ThreadId,
            new ReleaseSandboxRequest { WorkingDirectoryRelPath = "PRs/r-9" },
            CancellationToken.None
        );
        Log(result, gateway);

        var conflict = result.Should().BeOfType<ConflictObjectResult>().Subject;
        JsonSerializer
            .Serialize(conflict.Value)
            .Should()
            .Contain("sandbox_release_while_running")
            .And.Contain("work only this host keeps", "a parked call is not a run, so the reason differs from Busy");
        pool.TryGetHandoffState(ThreadId, out var after).Should().BeTrue("the agent stays loaded");
        after.EntryToken.Should().BeSameAs(before.EntryToken);
        (await pool.HasPendingAskUserQuestionAsync(ThreadId)).Should().BeTrue("the question is not orphaned");
        registry
            .TryGetExistingSession(SandboxSessionRegistry.DefaultWorkspaceId, false, ThreadId)
            .Should()
            .BeSameAs(session);
        gateway.DeleteAttempts.Should().Be(0);
        (await ConversationWorkingDirectory.ReadAsync(store, ThreadId)).Should().Be("PRs/r-7");
    }

    [Fact]
    public async Task A_move_the_store_refuses_after_the_delete_answers_503_and_a_retry_stores_it()
    {
        var gateway = new OwnedSandboxGateway();
        await using var registry = gateway.CreateRegistry(LoggerFactory, "lm-release-route-test");
        await using var pool = ConversationsControllerTests.CreatePool();
        var store = new MetadataFaultStore(await SeedAsync("PRs/r-7"));
        var session = await registry.GetOrCreateSessionAsync(OwnedRef());
        var controller = CreateController(store, pool, registry);
        store.FailUpdates = true;

        var refused = await controller.ReleaseSandbox(
            ThreadId,
            new ReleaseSandboxRequest { WorkingDirectoryRelPath = "PRs/r-9" },
            CancellationToken.None
        );
        Log(refused, gateway);

        var failure = refused.Should().BeOfType<ObjectResult>().Subject;
        failure.StatusCode.Should().Be(503);
        JsonSerializer
            .Serialize(failure.Value)
            .Should()
            .Contain("sandbox_release_failed")
            .And.Contain("was not stored");
        gateway.Deletes.Should().Equal(session.SessionId);
        (await ConversationWorkingDirectory.ReadAsync(store, ThreadId)).Should().Be("PRs/r-7", "nothing moved");

        store.FailUpdates = false;
        var retried = await controller.ReleaseSandbox(
            ThreadId,
            new ReleaseSandboxRequest { WorkingDirectoryRelPath = "PRs/r-9" },
            CancellationToken.None
        );
        Log(retried, gateway);

        retried.Should().BeOfType<OkObjectResult>();
        (await ConversationWorkingDirectory.ReadAsync(store, ThreadId)).Should().Be("PRs/r-9");
        gateway.DeleteAttempts.Should().Be(1, "the retry has no sandbox left to delete");
    }

    /// <summary>
    /// A dedicated thread whose idle agent a route takes out of the pool while a send rebuilds it: the old agent's
    /// dispose is held until the replacement is pooled, from the sandbox still cached.
    /// </summary>
    private sealed class ReplacementRace : IAsyncDisposable
    {
        private readonly SandboxReleaseRouteTests _test;
        private readonly OwnedSandboxGateway _gateway;
        private readonly SandboxSessionRegistry _registry;
        private readonly MultiTurnAgentPool _pool;
        private readonly SandboxSession _session;
        private IMultiTurnAgent? _replacement;

        private ReplacementRace(
            SandboxReleaseRouteTests test,
            OwnedSandboxGateway gateway,
            SandboxSessionRegistry registry,
            MultiTurnAgentPool pool,
            InMemoryConversationStore store,
            SandboxSession session
        )
        {
            _test = test;
            _gateway = gateway;
            _registry = registry;
            _pool = pool;
            Store = store;
            _session = session;
        }

        public InMemoryConversationStore Store { get; }

        public static async Task<ReplacementRace> StartAsync(SandboxReleaseRouteTests test)
        {
            var gateway = new OwnedSandboxGateway();
            var registry = gateway.CreateRegistry(test.LoggerFactory, "lm-release-route-test");
            var pool = ConversationsControllerTests.CreatePool();
            var store = await test.SeedAsync("PRs/r-7");
            var session = await registry.GetOrCreateSessionAsync(OwnedRef());
            return new ReplacementRace(test, gateway, registry, pool, store, session);
        }

        public async Task<IActionResult> RunAsync(Func<ConversationsController, Task<IActionResult>> route)
        {
            var disposing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var old = (FakeMultiTurnAgent)_pool.GetOrCreateAgent(ThreadId, WorkspaceMode);
            old.DisposeGate = disposing.Task;

            var pending = route(CreateController(Store, _pool, _registry));
            await Wait.UntilAsync(() => _pool.ActiveAgentCount == 0, "the route took the old agent out of the pool");
            // A send arrives during the old agent's dispose: the replacement is built, from the sandbox still cached.
            _replacement = _pool.GetOrCreateAgent(ThreadId, WorkspaceMode);
            _replacement.Should().NotBeSameAs(old);
            (await _registry.GetOrCreateSessionAsync(OwnedRef())).Should().BeSameAs(_session);
            disposing.SetResult();
            var result = await pending;
            _test.Log(result, _gateway);
            return result;
        }

        /// <summary>The route answered 409 for the replacement, which keeps its agent and its sandbox.</summary>
        public void AssertReplacementKeptItsSandbox(IActionResult result, string code)
        {
            var conflict = result.Should().BeOfType<ConflictObjectResult>().Subject;
            JsonSerializer
                .Serialize(conflict.Value)
                .Should()
                .Contain(code)
                .And.Contain(
                    "a new turn took the conversation",
                    "the old agent was idle; the replacement is what refuses"
                );
            _pool.GetOrCreateAgent(ThreadId, WorkspaceMode).Should().BeSameAs(_replacement);
            _gateway.DeleteAttempts.Should().Be(0, "the replacement's sandbox must not be deleted under it");
            _registry
                .TryGetExistingSession(SandboxSessionRegistry.DefaultWorkspaceId, false, ThreadId)
                .Should()
                .BeSameAs(_session);
        }

        public async ValueTask DisposeAsync()
        {
            await _pool.DisposeAsync();
            await _registry.DisposeAsync();
        }
    }

    private static WorkspaceRef OwnedRef() =>
        new(SandboxSessionRegistry.DefaultWorkspaceId, WorkingDir: "PRs/r-7", OwnerThreadId: ThreadId);

    private async Task<InMemoryConversationStore> SeedAsync(
        string directory,
        string? tenantId = null,
        string? ownerUserId = null
    )
    {
        var store = new InMemoryConversationStore();
        await store.UpdateMetadataAsync(
            ThreadId,
            _ => new ThreadMetadata
            {
                ThreadId = ThreadId,
                TenantId = tenantId,
                OwnerUserId = ownerUserId,
                LastUpdated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Properties = ImmutableDictionary<string, object>
                    .Empty.Add(MultiTurnAgentPool.ModePropertyKey, SystemChatModes.WorkspaceAgentModeId)
                    .Add(ConversationWorkingDirectory.PropertyKey, directory)
                    .Add(ConversationDedicatedSandbox.PropertyKey, true),
            }
        );
        Logger.LogDebug("Seeded dedicated thread {ThreadId} in {Directory}", ThreadId, directory);
        return store;
    }

    private static ConversationsController CreateController(
        IConversationStore store,
        MultiTurnAgentPool pool,
        SandboxSessionRegistry? registry
    ) =>
        ConversationsControllerTests.CreateController(
            store,
            pool,
            ConversationsControllerTests.ModeStoreResolvingSystemModes(),
            sandboxSessionRegistry: registry
        );

    private void Log(IActionResult result, OwnedSandboxGateway gateway) =>
        Logger.LogDebug(
            "Release answered {Result}; gateway saw {Creates} create(s), {DeleteAttempts} delete attempt(s)",
            Describe(result),
            gateway.Creates.Count,
            gateway.DeleteAttempts
        );

    private static string Describe(IActionResult result) =>
        result is ObjectResult objectResult
            ? $"{objectResult.StatusCode} {JsonSerializer.Serialize(objectResult.Value)}"
            : result.GetType().Name;

    /// <summary>Forwards to <paramref name="inner"/>; its metadata updates fail while <see cref="FailUpdates"/> is set.</summary>
    private sealed class MetadataFaultStore(IConversationStore inner) : IConversationStore
    {
        public volatile bool FailUpdates;

        public Task UpdateMetadataAsync(
            string threadId,
            Func<ThreadMetadata?, ThreadMetadata> update,
            CancellationToken ct = default
        ) =>
            FailUpdates
                ? throw new IOException("simulated metadata write failure")
                : inner.UpdateMetadataAsync(threadId, update, ct);

        public Task AppendMessagesAsync(
            string threadId,
            IReadOnlyList<PersistedMessage> messages,
            CancellationToken ct = default
        ) => inner.AppendMessagesAsync(threadId, messages, ct);

        public Task<IReadOnlyList<PersistedMessage>> LoadMessagesAsync(
            string threadId,
            CancellationToken ct = default
        ) => inner.LoadMessagesAsync(threadId, ct);

        public Task ReplaceMessageAsync(
            string threadId,
            PersistedMessage replacement,
            CancellationToken ct = default
        ) => inner.ReplaceMessageAsync(threadId, replacement, ct);

        public Task SaveMetadataAsync(string threadId, ThreadMetadata metadata, CancellationToken ct = default) =>
            inner.SaveMetadataAsync(threadId, metadata, ct);

        public Task<ThreadMetadata?> LoadMetadataAsync(string threadId, CancellationToken ct = default) =>
            inner.LoadMetadataAsync(threadId, ct);

        public Task DeleteThreadAsync(string threadId, CancellationToken ct = default) =>
            inner.DeleteThreadAsync(threadId, ct);

        public Task<IReadOnlyList<ThreadMetadata>> ListThreadsAsync(
            int limit = 50,
            int offset = 0,
            ConversationListOptions? options = null,
            CancellationToken ct = default
        ) => inner.ListThreadsAsync(limit, offset, options, ct);
    }
}
