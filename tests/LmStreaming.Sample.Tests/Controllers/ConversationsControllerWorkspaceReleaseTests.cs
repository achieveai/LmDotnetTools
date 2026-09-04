using System.Collections.Immutable;
using System.Net;
using System.Text;
using LmStreaming.Sample.Services;
using LmStreaming.Sample.Tests.Agents;
using LmStreaming.Sample.Tests.TestDoubles;

namespace LmStreaming.Sample.Tests.Controllers;

/// <summary>
/// Covers <c>POST /api/conversations/{threadId}/workspace-session/release</c>: handing a finished
/// review's pooled workspace back without destroying the review itself.
/// </summary>
/// <remarks>
/// <para>
/// The hole this closes: a retained conversation kept its agent, and its agent kept a writable mount on
/// a pooled slot that had already been handed back — so the next lease found another process still
/// working in the tree. Deleting the conversation would have released the mount and destroyed the
/// transcript with it, which is why this is a separate route rather than a reuse of <c>DELETE</c>.
/// </para>
/// <para>
/// The gateway session is shared by <c>(workspaceId, appId)</c>, so the destructive half is gated twice:
/// nothing is torn down while any run sharing the session is live, and nothing is torn down while any
/// other conversation is still bound to it.
/// </para>
/// </remarks>
public class ConversationsControllerWorkspaceReleaseTests
{
    private const string RootThread = "thread-review-root";
    private const string SiblingThread = "thread-review-sibling";
    private const string WorkspaceId = "ws-review-slot-0";

    [Fact]
    public async Task AnIdleConversation_HandsItsWorkspaceBack_AndKeepsEveryWordOfItsTranscript()
    {
        var (registry, calls) = CreateRegistry();
        await using var registryLifetime = registry;
        var store = new InMemoryConversationStore();
        await SeedConversationAsync(store, RootThread);
        var session = await BindAsync(registry, RootThread);

        await using var pool = CreatePool();
        var controller = CreateController(store, pool);

        var response = await ReleaseAsync(controller, registry, RootThread);

        response.SessionOutcome.Should().Be(ConversationsController.WorkspaceSessionOutcomes.Released);
        calls
            .DeletedSessionIds.Should()
            .ContainSingle("the last bound conversation released it")
            .Which.Should()
            .Be(session.SessionId);
        registry.TryGetSessionById(session.SessionId, out _).Should().BeFalse();

        // The whole point of not reusing DELETE: the review is still readable afterwards.
        (await store.LoadMessagesAsync(RootThread))
            .Should()
            .HaveCount(2);
        var metadata = await store.LoadMetadataAsync(RootThread);
        metadata!.Properties.Should().ContainKey("title").WhoseValue.Should().Be("Review of PR 42");
        metadata.OwnerUserId.Should().Be("dir-a:alice", "release must not unstamp ownership of the transcript");
        var messages = await controller.GetMessages(RootThread);
        Assert.IsType<OkObjectResult>(messages);
    }

    [Fact]
    public async Task TheResponse_NeverClaimsTheMountIsQuiescent_AndNeverNamesTheSession()
    {
        // The gateway deletes its session record BEFORE its best-effort container teardown and reports
        // nothing about the latter, so no answer this endpoint can give proves the mount is gone. A
        // caller that reads only this flag must still retire the slot.
        var (registry, _) = CreateRegistry();
        await using var registryLifetime = registry;
        var store = new InMemoryConversationStore();
        await SeedConversationAsync(store, RootThread);
        var session = await BindAsync(registry, RootThread);

        await using var pool = CreatePool();
        var response = await ReleaseAsync(CreateController(store, pool), registry, RootThread);

        response.MountQuiescenceConfirmed.Should().BeFalse();
        response
            .MountQuiescenceEvidence.Should()
            .Be(ReleaseWorkspaceSessionResponse.MountQuiescenceNotObservable, "silence must not read as a guarantee");
        JsonSerializer
            .Serialize(response)
            .Should()
            .NotContain(session.SessionId, "a session id is not the caller's to see");
    }

    [Fact]
    public async Task AConversationWithARunInFlight_IsRefused_AndNothingIsTornDown()
    {
        var (registry, calls) = CreateRegistry();
        await using var registryLifetime = registry;
        var store = new InMemoryConversationStore();
        await SeedConversationAsync(store, RootThread);
        var session = await BindAsync(registry, RootThread);

        await using var pool = CreatePool();
        var agent = CreateAgent(pool, RootThread);
        agent.StartRun("run_1", "input-0");

        var result = await CreateController(store, pool).ReleaseWorkspaceSession(RootThread, registry);

        AssertBusy(result);
        calls.DeletedSessionIds.Should().BeEmpty();
        registry.TryGetSessionById(session.SessionId, out _).Should().BeTrue();
        pool.TryGetHandoffState(RootThread, out _).Should().BeTrue("the running agent must survive the refusal");

        // A refused release leaves no stamp, so the conversation is still writable.
        IsReleased(await store.LoadMetadataAsync(RootThread)).Should().BeFalse();
    }

    [Fact]
    public async Task ARunThatStartsBetweenTheDecisionAndTheRemoval_IsNotInterrupted()
    {
        // The TOCTOU the pool's compare-and-remove exists for, forced deterministically: the interleaving
        // hook fires while the release is writing its stamp — after the entry has been observed idle and
        // before it is removed. Reading the pool once and removing unconditionally would dispose an agent
        // that has since accepted a turn and tear its mount down under it.
        var (registry, calls) = CreateRegistry();
        await using var registryLifetime = registry;
        var store = new InMemoryConversationStore();
        await SeedConversationAsync(store, RootThread);
        var session = await BindAsync(registry, RootThread);

        await using var pool = CreatePool();
        var agent = CreateAgent(pool, RootThread);
        var clock = new InterleavingTimeProvider(() => ReportAccept(pool, RootThread, "input-late", agent));

        var result = await CreateController(store, pool, clock).ReleaseWorkspaceSession(RootThread, registry);

        clock.Fired.Should().BeTrue("the interleaving must actually have happened, or this test proves nothing");
        AssertBusy(result);
        calls.DeletedSessionIds.Should().BeEmpty("the late turn's mount must survive");
        registry.TryGetSessionById(session.SessionId, out _).Should().BeTrue();
        pool.TryGetHandoffState(RootThread, out _).Should().BeTrue("the entry holding the late turn must survive");
    }

    [Fact]
    public async Task AnotherConversationBoundToTheSameSession_KeepsThatSessionAlive()
    {
        var (registry, calls) = CreateRegistry();
        await using var registryLifetime = registry;
        var store = new InMemoryConversationStore();
        await SeedConversationAsync(store, RootThread);
        var session = await BindAsync(registry, RootThread);
        registry.PublishEstablishedBinding(SiblingThread, BindingFor(session.SessionId));

        await using var pool = CreatePool();
        var response = await ReleaseAsync(CreateController(store, pool), registry, RootThread);

        response.SessionOutcome.Should().Be(ConversationsController.WorkspaceSessionOutcomes.Retained);
        calls.DeletedSessionIds.Should().BeEmpty();
        registry.GetBoundThreads(session.SessionId).Should().Equal(SiblingThread);

        // Released is about THIS conversation and stays true: it can no longer be continued either way.
        response.Released.Should().BeTrue();
        IsReleased(await store.LoadMetadataAsync(RootThread)).Should().BeTrue();
    }

    /// <summary>
    /// Cross-app isolation at the endpoint. The session cache is partitioned by (workspace id, app id), so
    /// releasing "the workspace" would destroy a second caller's live session — one nobody asked to
    /// release, still bound, possibly mid-run.
    /// </summary>
    [Fact]
    public async Task ReleasingOneAppsConversation_LeavesAnotherAppsSessionOnTheSameWorkspaceAlone()
    {
        var (registry, calls) = CreateRegistry();
        await using var registryLifetime = registry;
        var store = new InMemoryConversationStore();
        await SeedConversationAsync(store, RootThread);

        var mine = await BindAsync(registry, RootThread);
        var theirs = await registry.GetOrCreateSessionAsync(WorkspaceId, default, OtherAppCredential);
        theirs.SessionId.Should().NotBe(mine.SessionId, "the partition must have produced two sessions");
        registry.RegisterThread(theirs.SessionId, SiblingThread);

        await using var pool = CreatePool();
        var response = await ReleaseAsync(CreateController(store, pool), registry, RootThread);

        response.SessionOutcome.Should().Be(ConversationsController.WorkspaceSessionOutcomes.Released);
        calls.DeletedSessionIds.Should().ContainSingle().Which.Should().Be(mine.SessionId);
        registry.TryGetSessionById(theirs.SessionId, out _).Should().BeTrue("the other app keeps its session");
        registry.GetBoundThreads(theirs.SessionId).Should().Equal(SiblingThread);
    }

    /// <summary>
    /// The count-then-delete race, forced deterministically: a conversation registers against the session
    /// while the release is writing its stamp — after the release has decided to proceed and before the
    /// teardown commits. It must keep the session, because that conversation is about to use the mount.
    /// </summary>
    [Fact]
    public async Task AConversationThatRegistersBeforeTheTeardownCommits_KeepsTheSession()
    {
        var (registry, calls) = CreateRegistry();
        await using var registryLifetime = registry;
        var store = new InMemoryConversationStore();
        await SeedConversationAsync(store, RootThread);
        var session = await BindAsync(registry, RootThread);

        await using var pool = CreatePool();
        var clock = new InterleavingTimeProvider(() => registry.RegisterThread(session.SessionId, SiblingThread));

        var response = await ReleaseAsync(CreateController(store, pool, clock), registry, RootThread);

        clock.Fired.Should().BeTrue("the interleaving must actually have happened, or this test proves nothing");
        response.SessionOutcome.Should().Be(ConversationsController.WorkspaceSessionOutcomes.Retained);
        calls.DeletedSessionIds.Should().BeEmpty("the late claimant's mount must survive");
        registry.TryGetSessionById(session.SessionId, out _).Should().BeTrue();
        registry.GetBoundThreads(session.SessionId).Should().Equal(SiblingThread);
    }

    /// <summary>
    /// The losing side of the same barrier. A conversation whose acquisition starts after the teardown
    /// committed must not end up on the deleted session. There is no tombstone to consult and none is
    /// needed: the slot is empty, so the next acquisition mints a live replacement and binds to that.
    /// </summary>
    [Fact]
    public async Task AConversationThatAcquiresAfterTheTeardown_GetsALiveReplacementNotTheDeletedSession()
    {
        var (registry, calls) = CreateRegistry();
        await using var registryLifetime = registry;
        var store = new InMemoryConversationStore();
        await SeedConversationAsync(store, RootThread);
        var session = await BindAsync(registry, RootThread);

        await using var pool = CreatePool();
        var response = await ReleaseAsync(CreateController(store, pool), registry, RootThread);
        response.SessionOutcome.Should().Be(ConversationsController.WorkspaceSessionOutcomes.Released);

        var acquired = await registry.AcquireSessionForAgentAsync(
            new WorkspaceRef(WorkspaceId),
            SiblingThread,
            Credential
        );

        acquired.SessionId.Should().NotBe(session.SessionId, "the deleted mount must never be handed out again");
        registry.GetBoundThreads(session.SessionId).Should().BeEmpty();
        registry.GetBoundThreads(acquired.SessionId).Should().Equal(SiblingThread);
        calls.DeletedSessionIds.Should().ContainSingle("the replacement must not trigger another teardown");
    }

    [Fact]
    public async Task ABusySiblingOnTheSameSession_IsRefusedRatherThanSilentlyRetained()
    {
        var (registry, calls) = CreateRegistry();
        await using var registryLifetime = registry;
        var store = new InMemoryConversationStore();
        await SeedConversationAsync(store, RootThread);
        var session = await BindAsync(registry, RootThread);
        registry.PublishEstablishedBinding(SiblingThread, BindingFor(session.SessionId));

        await using var pool = CreatePool();
        CreateAgent(pool, SiblingThread).StartRun("run_sibling", "input-0");

        var result = await CreateController(store, pool).ReleaseWorkspaceSession(RootThread, registry);

        AssertBusy(result);
        calls.DeletedSessionIds.Should().BeEmpty();
        IsReleased(await store.LoadMetadataAsync(RootThread)).Should().BeFalse("a refusal must not retire the thread");
    }

    [Fact]
    public async Task AReleasedConversation_RefusesTheNextSend_WithoutBuildingAnAgentOrASession()
    {
        var (registry, calls) = CreateRegistry();
        await using var registryLifetime = registry;
        var store = new InMemoryConversationStore();
        await SeedConversationAsync(store, RootThread);
        var bound = await BindAsync(registry, RootThread);
        var createsBeforeSend = calls.PostCount;

        await using var pool = CreatePool();
        var controller = CreateController(store, pool);
        var first = await ReleaseAsync(controller, registry, RootThread);

        var send = await controller.SendMessage(RootThread, new SendMessageRequest { Text = "one more thing" });

        var conflict = Assert.IsType<ConflictObjectResult>(send);
        CodeOf(conflict).Should().Be(ConversationsController.WorkspaceSessionReleasedCode);
        pool.ActiveAgentCount.Should().Be(0, "a released conversation must not get a fresh agent");
        calls.PostCount.Should().Be(createsBeforeSend, "nor a fresh gateway session");

        // Read routes stay open: the refusal closes the write, not the artifact.
        Assert.IsType<OkObjectResult>(await controller.GetMessages(RootThread));
    }

    [Fact]
    public async Task AReleasedConversation_RefusesAModeSwitch_WithoutBuildingAnAgentOrASession()
    {
        var (registry, calls) = CreateRegistry();
        await using var registryLifetime = registry;
        var store = new InMemoryConversationStore();
        await SeedConversationAsync(store, RootThread);
        _ = await BindAsync(registry, RootThread);
        var createsBeforeSwitch = calls.PostCount;

        await using var pool = CreatePool();
        var controller = CreateController(store, pool);
        _ = await ReleaseAsync(controller, registry, RootThread);

        var result = await controller.SwitchMode(RootThread, new SwitchModeRequest { ModeId = "math-helper" });

        var conflict = Assert.IsType<ConflictObjectResult>(result);
        CodeOf(conflict).Should().Be(ConversationsController.WorkspaceSessionReleasedCode);
        pool.ActiveAgentCount.Should().Be(0, "a mode switch must not reopen the released conversation");
        calls.PostCount.Should().Be(createsBeforeSwitch, "nor acquire a fresh gateway session");
    }

    [Fact]
    public async Task AReleasedConversation_RefusesAProviderSwitch_WithoutBuildingAnAgentOrASession()
    {
        var (registry, calls) = CreateRegistry();
        await using var registryLifetime = registry;
        var store = new InMemoryConversationStore();
        await SeedConversationAsync(store, RootThread);
        _ = await BindAsync(registry, RootThread);
        var createsBeforeSwitch = calls.PostCount;

        await using var pool = CreatePool();
        var controller = CreateController(store, pool);
        _ = await ReleaseAsync(controller, registry, RootThread);

        var result = await controller.SwitchProvider(RootThread, new SwitchProviderRequest { ProviderId = "test" });

        var conflict = Assert.IsType<ConflictObjectResult>(result);
        CodeOf(conflict).Should().Be(ConversationsController.WorkspaceSessionReleasedCode);
        pool.ActiveAgentCount.Should().Be(0, "a provider switch must not reopen the released conversation");
        calls.PostCount.Should().Be(createsBeforeSwitch, "nor acquire a fresh gateway session");
    }

    [Fact]
    public async Task ReleasingTwice_IsANoOp_AndKeepsTheOriginalReleaseInstant()
    {
        var (registry, calls) = CreateRegistry();
        await using var registryLifetime = registry;
        var store = new InMemoryConversationStore();
        await SeedConversationAsync(store, RootThread);
        var bound = await BindAsync(registry, RootThread);

        await using var pool = CreatePool();
        var controller = CreateController(store, pool);

        var first = await ReleaseAsync(controller, registry, RootThread);
        var stampedAt = ReleasedAt(await store.LoadMetadataAsync(RootThread));

        var second = await ReleaseAsync(controller, registry, RootThread);

        second.SessionOutcome.Should().Be(ConversationsController.WorkspaceSessionOutcomes.NothingToRelease);
        calls.DeletedSessionIds.Should().HaveCount(1, "the retry must not issue a second teardown");
        ReleasedAt(await store.LoadMetadataAsync(RootThread))
            .Should()
            .Be(stampedAt, "the stamp answers WHEN it was handed back; a retry must not rewrite that");
    }

    // ---- helpers -------------------------------------------------------------------------------

    private static async Task<ReleaseWorkspaceSessionResponse> ReleaseAsync(
        ConversationsController controller,
        SandboxSessionRegistry registry,
        string threadId
    )
    {
        var result = await controller.ReleaseWorkspaceSession(threadId, registry);
        var ok = Assert.IsType<OkObjectResult>(result);
        return Assert.IsType<ReleaseWorkspaceSessionResponse>(ok.Value);
    }

    private static void AssertBusy(IActionResult result) =>
        CodeOf(Assert.IsType<ConflictObjectResult>(result))
            .Should()
            .Be(ConversationsController.WorkspaceSessionBusyCode);

    /// <summary>Reads the anonymous error body's <c>code</c> without pinning the rest of its shape.</summary>
    private static string? CodeOf(ObjectResult result) =>
        JsonSerializer.SerializeToNode(result.Value)?["code"]?.GetValue<string>();

    private static bool IsReleased(ThreadMetadata? metadata) =>
        metadata?.Properties?.ContainsKey(ConversationsController.WorkspaceSessionReleasedAtPropertyKey) == true;

    private static object? ReleasedAt(ThreadMetadata? metadata) =>
        metadata?.Properties?[ConversationsController.WorkspaceSessionReleasedAtPropertyKey];

    private static async Task SeedConversationAsync(InMemoryConversationStore store, string threadId)
    {
        await store.SaveMetadataAsync(
            threadId,
            new ThreadMetadata
            {
                ThreadId = threadId,
                LastUpdated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                OwnerUserId = "dir-a:alice",
                Properties = ImmutableDictionary<string, object>
                    .Empty.SetItem("title", "Review of PR 42")
                    .SetItem(MultiTurnAgentPool.WorkspacePropertyKey, WorkspaceId),
            }
        );

        await store.AppendMessagesAsync(
            threadId,
            [Persisted(threadId, "pm-1", "user"), Persisted(threadId, "pm-2", "assistant")]
        );
    }

    private static PersistedMessage Persisted(string threadId, string id, string role) =>
        new()
        {
            Id = id,
            ThreadId = threadId,
            RunId = "run_1",
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            MessageType = "text",
            Role = role,
            MessageJson = """{"text":"reviewed"}""",
        };

    /// <summary>Creates the workspace's gateway session and binds <paramref name="threadId"/> to it.</summary>
    private static async Task<SandboxSession> BindAsync(SandboxSessionRegistry registry, string threadId)
    {
        var session = await registry.GetOrCreateSessionAsync(WorkspaceId, default, Credential);
        registry.PublishEstablishedBinding(threadId, BindingFor(session.SessionId));
        return session;
    }

    private static SandboxEstablishedBinding BindingFor(string sessionId) =>
        new(new WorkspaceRef(WorkspaceId), Credential, Credential, sessionId);

    private static SandboxCredential Credential { get; } = new("review-daemon", "key-1");

    /// <summary>A second caller app id on the SAME workspace — its own cache partition, its own session.</summary>
    private static SandboxCredential OtherAppCredential { get; } = new("other-app", "key-2");

    private static FakeMultiTurnAgent CreateAgent(MultiTurnAgentPool pool, string threadId) =>
        (FakeMultiTurnAgent)
            pool.GetOrCreateAgent(
                threadId,
                SystemChatModes.GetById(SystemChatModes.DefaultModeId)!,
                requestedProviderId: null,
                requestResponseDumpFileName: null,
                requestedWorkspaceId: null,
                callerCredential: null,
                ownerUserId: "dir-a:alice"
            );

    private static void ReportAccept(MultiTurnAgentPool pool, string threadId, string inputId, IMultiTurnAgent agent) =>
        ((IInputAcceptanceObserver)pool).OnInputAccepted(threadId, inputId, agent);

    private static MultiTurnAgentPool CreatePool() =>
        new(
            (threadId, _, _) =>
                new MultiTurnAgentPool.AgentCreationResult(
                    new FakeMultiTurnAgent(threadId) { KeepSubscriptionOpen = true }
                ),
            NullLogger<MultiTurnAgentPool>.Instance
        );

    private static ConversationsController CreateController(
        IConversationStore store,
        MultiTurnAgentPool pool,
        TimeProvider? timeProvider = null
    ) =>
        new(
            store,
            pool,
            ConversationsControllerTests.ModeStoreResolvingSystemModes(),
            Mock.Of<IWorkspaceStore>(),
            new FakeProviderRegistry(defaultProviderId: "test", available: ["test"]).ToReal(),
            new ConversationStatusResolver(new InMemoryConversationStore(), new InMemoryConversationStore()),
            timeProvider ?? TimeProvider.System,
            new WorkflowRunRegistry(),
            TestAuthorizers.Disabled(),
            NullLogger<ConversationsController>.Instance,
            NullLogger<AgentHierarchyService>.Instance,
            new SubAgentScanCoverageCache(),
            new ConversationDescendantScanner(store, NullLogger<ConversationDescendantScanner>.Instance)
        );

    /// <summary>
    /// A clock that runs <paramref name="onFirstRead"/> the first time the controller asks for the time.
    /// The release reads it only while writing its released stamp, which is exactly the gap between
    /// observing the pooled entry and removing it — so this places a concurrent event in that gap
    /// deterministically, with no threads and no sleeps.
    /// </summary>
    private sealed class InterleavingTimeProvider(Action onFirstRead) : TimeProvider
    {
        public bool Fired { get; private set; }

        public override DateTimeOffset GetUtcNow()
        {
            if (!Fired)
            {
                Fired = true;
                onFirstRead();
            }

            return DateTimeOffset.UnixEpoch;
        }
    }

    private static (SandboxSessionRegistry Registry, CallLog Calls) CreateRegistry()
    {
        var calls = new CallLog();
        var options = new SandboxGatewayOptions
        {
            BaseUrl = "http://localhost:3000",
            AppId = "default-app",
            Marketplaces = null,
        };

        var handler = new StubHandler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;

            if (req.Method == HttpMethod.Post && path.EndsWith("/sandboxes", StringComparison.Ordinal))
            {
                var ordinal = calls.RecordPost();
                return Json(
                    $$"""
                    { "session_id": "sess-{{ordinal}}", "container_id": "c-{{ordinal}}",
                      "volumes": { "workspace": { "container_path": "/workspace", "read_only": false } } }
                    """
                );
            }

            if (req.Method == HttpMethod.Delete && path.Contains("/sandboxes/", StringComparison.Ordinal))
            {
                calls.RecordDelete(path[(path.LastIndexOf('/') + 1)..]);
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var gateway = new SandboxGatewayLifetime(
            options,
            NullLogger<SandboxGatewayLifetime>.Instance,
            new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)))
        );

        var registry = new SandboxSessionRegistry(
            gateway,
            options,
            NullLogger<SandboxSessionRegistry>.Instance,
            new HttpClient(handler),
            new AuthOptions(),
            new SessionSecretStore(
                Path.Combine(Path.GetTempPath(), "lmstreaming-test-secrets", Guid.NewGuid().ToString("N")),
                NullLogger<SessionSecretStore>.Instance
            )
        );

        return (registry, calls);
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class CallLog
    {
        private readonly object _gate = new();
        private readonly List<string> _deleted = [];
        private int _posts;

        public int PostCount
        {
            get
            {
                lock (_gate)
                {
                    return _posts;
                }
            }
        }

        public IReadOnlyList<string> DeletedSessionIds
        {
            get
            {
                lock (_gate)
                {
                    return [.. _deleted];
                }
            }
        }

        public int RecordPost()
        {
            lock (_gate)
            {
                return ++_posts;
            }
        }

        public void RecordDelete(string sessionId)
        {
            lock (_gate)
            {
                _deleted.Add(sessionId);
            }
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request));
    }
}
