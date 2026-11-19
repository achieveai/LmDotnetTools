using System.Net;
using System.Text;
using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace LmStreaming.Sample.Tests.Services;

/// <summary>
/// Issue #153 M2: the session cache is partitioned by (workspace id, caller app id) — not just
/// workspace id — so two different callers requesting the SAME logical workspace each get their
/// OWN gateway session instead of colliding on one shared session (and one shared credential).
///
/// <para>
/// These tests pin: (1) two credentials for one workspace id produce two distinct create calls,
/// each carrying its own <c>X-Sbx-App-Id</c> header; (2) the same credential still single-flights
/// through the cache exactly like the pre-M2 (workspaceId-only) behavior; (3) a background recreate
/// after a gateway 404 reuses the credential the caller re-supplies — nothing is persisted
/// server-side, so the caller must resend it every turn (design's cross-actor resume matrix); and
/// (4) destroying "a workspace" (the only thing a daemon-style caller knows) tears down every app
/// id's session for it, not just the first one found, and leaves other workspaces untouched.
/// </para>
/// </summary>
public class SandboxSessionRegistryPartitionTests : LoggingTestBase
{
    private const string GatewayBaseUrl = "http://localhost:3000";
    private const string DefaultAppId = "default-app";
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    public SandboxSessionRegistryPartitionTests(ITestOutputHelper output)
        : base(output) { }

    [Fact]
    public async Task Different_credentials_for_the_same_workspace_create_distinct_sessions()
    {
        var (registry, calls) = CreateRegistry();
        var credentialA = new SandboxCredential("caller-a", "keyA");
        var credentialB = new SandboxCredential("caller-b", "keyB");

        var sessionA = await registry.GetOrCreateSessionAsync(
            SandboxSessionRegistry.DefaultWorkspaceId,
            default,
            credentialA
        );
        var sessionB = await registry.GetOrCreateSessionAsync(
            SandboxSessionRegistry.DefaultWorkspaceId,
            default,
            credentialB
        );

        sessionA
            .SessionId.Should()
            .NotBe(sessionB.SessionId, "different callers must never collide on one shared session");
        calls.PostCount.Should().Be(2, "each distinct credential must trigger its own create call");
        calls.PostAppIds.Should().Equal("caller-a", "caller-b");
    }

    [Fact]
    public async Task Same_credential_for_the_same_workspace_reuses_the_cached_session()
    {
        // Sanity check for the partition key itself: the SAME credential must still hit the
        // single-flight cache, exactly like the pre-M2 (workspaceId-only) behavior.
        var (registry, calls) = CreateRegistry();
        var credential = new SandboxCredential("caller-a", "keyA");

        var first = await registry.GetOrCreateSessionAsync(
            SandboxSessionRegistry.DefaultWorkspaceId,
            default,
            credential
        );
        var second = await registry.GetOrCreateSessionAsync(
            SandboxSessionRegistry.DefaultWorkspaceId,
            default,
            credential
        );

        second.SessionId.Should().Be(first.SessionId);
        calls.PostCount.Should().Be(1);
    }

    [Fact]
    public async Task Background_recreate_after_404_reuses_the_credential_resent_by_the_caller()
    {
        var (registry, calls) = CreateRegistry();
        var credential = new SandboxCredential("caller-x", "keyX");

        var first = await registry.GetOrCreateSessionAsync(
            SandboxSessionRegistry.DefaultWorkspaceId,
            default,
            credential
        );
        calls.PostAppIds.Should().ContainSingle().Which.Should().Be("caller-x");

        calls.Evict(first.SessionId); // the gateway forgets the idle session

        // The caller re-sends the SAME credential on the next turn (design: "re-sent every turn,
        // nothing stored server-side") — the recreate must carry it forward, not silently fall back
        // to the process-wide default.
        var live = await registry.GetOrCreateLiveSessionAsync(
            SandboxSessionRegistry.DefaultWorkspaceId,
            default,
            credential
        );

        live.SessionId.Should().NotBe(first.SessionId, "the dead session must be replaced, not reused");
        calls.PostCount.Should().Be(2, "exactly one extra create POST should happen for the recreate");
        calls.PostAppIds.Should().Equal("caller-x", "caller-x");
    }

    [Fact]
    public async Task DestroyWorkspaceSessionAsync_removes_every_app_ids_session_for_the_workspace()
    {
        var (registry, calls) = CreateRegistry();
        const string workspaceId = "ws-multi";
        var credentialA = new SandboxCredential("caller-a", "keyA");
        var credentialB = new SandboxCredential("caller-b", "keyB");

        var sessionA = await registry.GetOrCreateSessionAsync(workspaceId, default, credentialA);
        var sessionB = await registry.GetOrCreateSessionAsync(workspaceId, default, credentialB);
        sessionA.SessionId.Should().NotBe(sessionB.SessionId);

        await registry.DestroyWorkspaceSessionAsync(workspaceId);

        calls
            .DeletedSessionIds.Should()
            .BeEquivalentTo(
                [sessionA.SessionId, sessionB.SessionId],
                "every app id's session for the workspace must be torn down, not just the first one found"
            );
        registry.TryGetSessionById(sessionA.SessionId, out _).Should().BeFalse();
        registry.TryGetSessionById(sessionB.SessionId, out _).Should().BeFalse();
    }

    [Fact]
    public async Task DestroyWorkspaceSessionAsync_leaves_other_workspaces_untouched()
    {
        var (registry, calls) = CreateRegistry();
        var credential = new SandboxCredential("caller-a", "keyA");

        var kept = await registry.GetOrCreateSessionAsync("ws-keep", default, credential);
        var removed = await registry.GetOrCreateSessionAsync("ws-remove", default, credential);

        await registry.DestroyWorkspaceSessionAsync("ws-remove");

        calls.DeletedSessionIds.Should().Equal(removed.SessionId);
        registry.TryGetSessionById(kept.SessionId, out _).Should().BeTrue();
    }

    [Fact]
    public async Task Read_only_and_normal_refs_for_one_workspace_and_app_get_two_sessions_with_their_own_rules()
    {
        // The judge and the review lanes share the workspace AND the app id, so only the read-only part of
        // the key keeps the judge out of the lane's full-write sandbox (and the lane out of the judge's).
        var auth = new AuthOptions();
        auth.Ado.ClientId = "ado-client";
        var (registry, calls) = CreateRegistry(auth);

        var normal = await registry.GetOrCreateSessionAsync(new WorkspaceRef("ws-1"));
        var readOnly = await registry.GetOrCreateSessionAsync(new WorkspaceRef("ws-1", ReadOnlyEgress: true));

        readOnly.SessionId.Should().NotBe(normal.SessionId);
        normal.ReadOnlyEgress.Should().BeFalse();
        readOnly.ReadOnlyEgress.Should().BeTrue();
        calls.PostAppIds.Should().Equal(DefaultAppId, DefaultAppId);
        AdoMethods(calls.PostBodies[0]).Should().BeEmpty("the lane's ado rule must keep allowing every method");
        AdoMethods(calls.PostBodies[1]).Should().Equal("GET", "HEAD");
    }

    [Fact]
    public async Task The_same_read_only_ref_twice_reuses_one_session()
    {
        var (registry, calls) = CreateRegistry();
        var readOnlyRef = new WorkspaceRef("ws-1", ReadOnlyEgress: true);

        var first = await registry.GetOrCreateSessionAsync(readOnlyRef);
        var second = await registry.GetOrCreateSessionAsync(readOnlyRef);

        second.SessionId.Should().Be(first.SessionId);
        calls.PostCount.Should().Be(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Destroy_and_release_of_a_workspace_stop_its_read_only_session_too(bool release)
    {
        var (registry, calls) = CreateRegistry();
        var normal = await registry.GetOrCreateSessionAsync(new WorkspaceRef("ws-1"));
        var readOnly = await registry.GetOrCreateSessionAsync(new WorkspaceRef("ws-1", ReadOnlyEgress: true));

        if (release)
        {
            await registry.ReleaseWorkspaceSessionsAsync("ws-1", DefaultAppId);
        }
        else
        {
            await registry.DestroyWorkspaceSessionAsync("ws-1");
        }

        calls.DeletedSessionIds.Should().BeEquivalentTo([normal.SessionId, readOnly.SessionId]);
        registry.TryGetSessionById(readOnly.SessionId, out _).Should().BeFalse();
    }

    [Fact]
    public async Task Each_owner_gets_its_own_sandbox_apart_from_the_shared_one_and_jailed_to_its_directory()
    {
        // Every review conversation sits on the same workspace and app id, so only the owner part of the key keeps
        // one review out of another's sandbox, and out of the shared one that can write the whole workspace.
        var (registry, calls) = CreateRegistry();
        var aRef = new WorkspaceRef("ws-1", WorkingDir: "slots/r-1/PRs/r-7", OwnerThreadId: "thread-a");

        var shared = await registry.GetOrCreateSessionAsync(new WorkspaceRef("ws-1"));
        var a = await registry.GetOrCreateSessionAsync(aRef);
        var b = await registry.GetOrCreateSessionAsync(
            new WorkspaceRef("ws-1", WorkingDir: "slots/r-2/PRs/r-8", OwnerThreadId: "thread-b")
        );

        new[] { shared.SessionId, a.SessionId, b.SessionId }.Should().OnlyHaveUniqueItems();
        shared.OwnerThreadId.Should().BeNull();
        a.OwnerThreadId.Should().Be("thread-a");
        a.WorkspaceWorkingDir.Should().Be("slots/r-1/PRs/r-7");
        b.WorkspaceWorkingDir.Should().Be("slots/r-2/PRs/r-8");
        WorkingDirSent(calls.PostBodies[0]).Should().BeNull();
        WorkingDirSent(calls.PostBodies[1]).Should().Be("slots/r-1/PRs/r-7");
        (await registry.GetOrCreateSessionAsync(aRef))
            .SessionId.Should()
            .Be(a.SessionId, "the owner's next turn reuses its own sandbox");
        calls.PostCount.Should().Be(3);
    }

    [Fact]
    public async Task Releasing_an_owner_deletes_only_its_sandbox_and_its_next_turn_gets_a_fresh_one()
    {
        var (registry, calls) = CreateRegistry();
        var aRef = new WorkspaceRef("ws-1", WorkingDir: "PRs/r-7", OwnerThreadId: "thread-a");
        var shared = await registry.GetOrCreateSessionAsync(new WorkspaceRef("ws-1"));
        var a = await registry.GetOrCreateSessionAsync(aRef);
        var b = await registry.GetOrCreateSessionAsync(
            new WorkspaceRef("ws-1", WorkingDir: "PRs/r-8", OwnerThreadId: "thread-b")
        );

        await registry.ReleaseOwnedSessionsAsync("thread-a");
        await registry.ReleaseOwnedSessionsAsync("thread-a"); // nothing left: a no-op, not a second DELETE

        calls.DeletedSessionIds.Should().Equal(a.SessionId);
        registry.TryGetSessionById(a.SessionId, out _).Should().BeFalse();
        registry.TryGetSessionById(shared.SessionId, out _).Should().BeTrue();
        registry.TryGetSessionById(b.SessionId, out _).Should().BeTrue();
        (await registry.GetOrCreateSessionAsync(aRef)).SessionId.Should().NotBe(a.SessionId);
    }

    [Fact]
    public async Task A_refused_release_throws_keeps_the_sandbox_tracked_and_never_hands_it_out_again()
    {
        var (registry, calls) = CreateRegistry();
        var aRef = OwnedRef("thread-a", "PRs/r-7");
        var a = await registry.GetOrCreateSessionAsync(aRef);
        Logger.LogInformation(
            "Owner {Owner} in {Directory} has sandbox {SessionId}",
            "thread-a",
            "PRs/r-7",
            a.SessionId
        );

        calls.FailDeletes = true;
        var refused = () => registry.ReleaseOwnedSessionsAsync("thread-a");
        await refused.Should().ThrowAsync<AchieveAi.LmDotnetTools.Sandbox.SandboxException>();

        registry.TryGetSessionById(a.SessionId, out _).Should().BeTrue("a refused delete leaves it alive and tracked");
        var next = await registry.GetOrCreateSessionAsync(aRef);
        next.SessionId.Should().NotBe(a.SessionId, "a sandbox being released is never handed out again");
        calls.FailDeletes = false;
        await registry.ReleaseOwnedSessionsAsync("thread-a");
        LogCalls(calls);
        calls.DeletedSessionIds.Should().BeEquivalentTo([a.SessionId, next.SessionId]);
    }

    [Fact]
    public async Task A_caller_that_stops_waiting_does_not_lose_the_sandbox_being_deleted()
    {
        var (registry, calls) = CreateRegistry();
        var a = await registry.GetOrCreateSessionAsync(OwnedRef("thread-a", "PRs/r-7"));
        var gate = calls.HoldDeletes();
        using var cts = new CancellationTokenSource();

        var release = registry.ReleaseOwnedSessionsAsync("thread-a", cts.Token);
        await calls.WaitForDeleteStartedAsync();
        await cts.CancelAsync();
        await release.Invoking(task => task).Should().ThrowAsync<OperationCanceledException>();

        // The gateway then refuses the DELETE the caller stopped waiting for.
        calls.FailDeletes = true;
        gate.SetResult();
        await registry
            .ReleaseOwnedSessionsAsync("thread-a")
            .Invoking(task => task)
            .Should()
            .ThrowAsync<AchieveAi.LmDotnetTools.Sandbox.SandboxException>(
                "the next release waits for that one, then retries the same sandbox"
            );
        calls.FailDeletes = false;
        await registry.ReleaseOwnedSessionsAsync("thread-a");
        LogCalls(calls);

        calls.DeletedSessionIds.Should().Equal(a.SessionId);
        calls.DeleteAttempts.Should().Be(3);
        registry.TryGetSessionById(a.SessionId, out _).Should().BeFalse();
    }

    [Fact]
    public async Task The_owner_resolved_while_its_old_sandbox_is_being_deleted_gets_a_fresh_one_the_delete_leaves_alone()
    {
        var (registry, calls) = CreateRegistry();
        var aRef = OwnedRef("thread-a", "PRs/r-7");
        var old = await registry.GetOrCreateSessionAsync(aRef);
        var gate = calls.HoldDeletes();

        var release = registry.ReleaseOwnedSessionsAsync("thread-a");
        await calls.WaitForDeleteStartedAsync();
        var fresh = await registry.GetOrCreateSessionAsync(aRef).WaitAsync(Patience);

        release.IsCompleted.Should().BeFalse("the old sandbox's DELETE is still held");
        fresh.SessionId.Should().NotBe(old.SessionId, "the sandbox being deleted must not be handed out");
        gate.SetResult();
        await release.WaitAsync(Patience);
        LogCalls(calls);

        calls.DeletedSessionIds.Should().Equal(old.SessionId);
        (await registry.GetOrCreateSessionAsync(aRef)).SessionId.Should().Be(fresh.SessionId);
        registry.TryGetSessionById(fresh.SessionId, out _).Should().BeTrue();
    }

    [Fact]
    public async Task A_refused_release_racing_a_recreate_keeps_both_sandboxes_tracked_until_shutdown()
    {
        var (registry, calls) = CreateRegistry();
        var aRef = OwnedRef("thread-a", "PRs/r-7");
        var old = await registry.GetOrCreateSessionAsync(aRef);
        var gate = calls.HoldDeletes();

        var release = registry.ReleaseOwnedSessionsAsync("thread-a");
        await calls.WaitForDeleteStartedAsync();
        var fresh = await registry.GetOrCreateSessionAsync(aRef).WaitAsync(Patience);
        calls.FailDeletes = true;
        gate.SetResult();
        await release.Invoking(task => task).Should().ThrowAsync<AchieveAi.LmDotnetTools.Sandbox.SandboxException>();

        calls.FailDeletes = false;
        await registry.DisposeAsync();
        LogCalls(calls);

        calls.DeletedSessionIds.Should().BeEquivalentTo([old.SessionId, fresh.SessionId]);
    }

    [Fact]
    public async Task A_second_release_waits_for_the_running_one_and_reports_the_gateways_answer()
    {
        // An agent removal starts a release nobody awaits; the release route then asks again. It must answer with
        // what the gateway said about that DELETE, not with "nothing left to release" while the DELETE is in flight.
        var (registry, calls) = CreateRegistry();
        var a = await registry.GetOrCreateSessionAsync(OwnedRef("thread-a", "PRs/r-7"));
        var gate = calls.HoldDeletes();

        var first = registry.ReleaseOwnedSessionsAsync("thread-a");
        await calls.WaitForDeleteStartedAsync();
        var second = registry.ReleaseOwnedSessionsAsync("thread-a");
        await Task.Delay(50);

        second.IsCompleted.Should().BeFalse("the first release's DELETE is still held");
        calls.FailDeletes = true;
        gate.SetResult();
        await first.Invoking(task => task).Should().ThrowAsync<AchieveAi.LmDotnetTools.Sandbox.SandboxException>();
        await second.Invoking(task => task).Should().ThrowAsync<AchieveAi.LmDotnetTools.Sandbox.SandboxException>();
        LogCalls(calls);

        calls.DeleteAttempts.Should().Be(2, "the second release retries the sandbox the first could not delete");
        calls.MaxDeletesInFlight.Should().Be(1, "releases of one owner never DELETE concurrently");
        registry.TryGetSessionById(a.SessionId, out _).Should().BeTrue();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_release_with_a_commit_holds_the_owners_next_sandbox_until_it_is_done(bool gatewayDeletes)
    {
        var (registry, calls) = CreateRegistry();
        var aRef = OwnedRef("thread-a", "PRs/r-7");
        var old = await registry.GetOrCreateSessionAsync(aRef);
        var gate = calls.HoldDeletes();
        int? postsWhenCommitted = null;

        var release = registry.ReleaseOwnedSessionsAsync(
            "thread-a",
            () =>
            {
                postsWhenCommitted = calls.PostCount;
                return Task.CompletedTask;
            }
        );
        await calls.WaitForDeleteStartedAsync();
        var next = registry.GetOrCreateSessionAsync(aRef);
        await Task.Delay(50);

        next.IsCompleted.Should().BeFalse("the owner's next sandbox waits for the release that commits");
        calls.FailDeletes = !gatewayDeletes;
        gate.SetResult();
        if (gatewayDeletes)
        {
            await release.WaitAsync(Patience);
        }
        else
        {
            await release
                .Invoking(task => task.WaitAsync(Patience))
                .Should()
                .ThrowAsync<AchieveAi.LmDotnetTools.Sandbox.SandboxException>();
        }

        var fresh = await next.WaitAsync(Patience);
        LogCalls(calls);

        fresh.SessionId.Should().NotBe(old.SessionId);
        if (gatewayDeletes)
        {
            postsWhenCommitted.Should().Be(1, "the commit is stored before the next sandbox is created");
        }
        else
        {
            postsWhenCommitted.Should().BeNull("nothing is committed when the gateway refuses the delete");
        }
    }

    private static WorkspaceRef OwnedRef(string owner, string directory) =>
        new("ws-1", WorkingDir: directory, OwnerThreadId: owner);

    private void LogCalls(CallLog calls) =>
        Logger.LogInformation(
            "Gateway saw creates {Creates}, delete attempts {DeleteAttempts} (at most {MaxInFlight} at once), "
                + "confirmed deletes {Deletes}",
            calls.PostCount,
            calls.DeleteAttempts,
            calls.MaxDeletesInFlight,
            calls.DeletedSessionIds
        );

    [Fact]
    public async Task A_dedicated_sandbox_the_gateway_evicted_comes_back_with_its_owner_and_working_dir()
    {
        // The reload hook rebuilds the ref from the workspace catalog, which knows neither the owner nor the
        // conversation's directory. Unpinned, the recreate would land in the shared sandbox, writable everywhere.
        var (registry, calls) = CreateRegistry(
            reloadWorkspaceRef: (workspaceId, _) => Task.FromResult<WorkspaceRef?>(new WorkspaceRef(workspaceId))
        );
        var aRef = new WorkspaceRef("ws-1", WorkingDir: "PRs/r-7", OwnerThreadId: "thread-a");
        var first = await registry.GetOrCreateSessionAsync(aRef);

        calls.Evict(first.SessionId);
        var live = await registry.GetOrCreateLiveSessionAsync(aRef);

        live.SessionId.Should().NotBe(first.SessionId);
        live.OwnerThreadId.Should().Be("thread-a");
        live.WorkspaceWorkingDir.Should().Be("PRs/r-7");
        WorkingDirSent(calls.PostBodies[1]).Should().Be("PRs/r-7");
        registry.TryGetExistingSession("ws-1").Should().BeNull("the recreate must not take the shared slot");
    }

    private static string? WorkingDirSent(string createBody)
    {
        using var document = JsonDocument.Parse(createBody);
        return document.RootElement.TryGetProperty("working_dir", out var dir) ? dir.GetString() : null;
    }

    private static IReadOnlyList<string> AdoMethods(string createBody)
    {
        using var document = JsonDocument.Parse(createBody);
        return
        [
            .. document
                .RootElement.GetProperty("network")
                .GetProperty("rules")
                .EnumerateArray()
                .Single(rule => rule.GetProperty("id").GetString() == "ado")
                .GetProperty("methods")
                .EnumerateArray()
                .Select(method => method.GetString()!),
        ];
    }

    private (SandboxSessionRegistry Registry, CallLog Calls) CreateRegistry(
        AuthOptions? auth = null,
        Func<string, CancellationToken, Task<WorkspaceRef?>>? reloadWorkspaceRef = null
    )
    {
        var calls = new CallLog();

        var registryHandler = new DeleteGatingHandler(
            calls,
            req =>
            {
                var path = req.RequestUri!.AbsolutePath;

                // Create: POST /api/v1/sandboxes — records the app id header that came with this
                // particular create call so tests can assert per-credential partitioning.
                if (req.Method == HttpMethod.Post && path.EndsWith("/sandboxes", StringComparison.Ordinal))
                {
                    var appId = req.Headers.GetValues("X-Sbx-App-Id").Single();
                    var ordinal = calls.RecordPost(appId, req.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                    var sessionId = $"sess-{ordinal}";
                    calls.MarkCreated(sessionId);
                    // Echoes a requested working directory, as the gateway does: the SDK refuses a create whose
                    // reply does not acknowledge it.
                    using var request = JsonDocument.Parse(calls.PostBodies[^1]);
                    var workingDir = request.RootElement.TryGetProperty("working_dir", out var dir)
                        ? $", \"working_dir\": \"{dir.GetString()}\""
                        : string.Empty;
                    var responseBody = $$"""
                        { "session_id": "{{sessionId}}", "container_id": "c-{{ordinal}}",
                          "volumes": { "workspace": { "container_path": "/workspace", "read_only": false{{workingDir}} } } }
                        """;
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
                    };
                }

                // Liveness: GET /api/v1/sandboxes/{id}
                if (req.Method == HttpMethod.Get && path.Contains("/sandboxes/", StringComparison.Ordinal))
                {
                    var id = path[(path.LastIndexOf('/') + 1)..];
                    return calls.IsAlive(id)
                        ? new HttpResponseMessage(HttpStatusCode.OK)
                        : new HttpResponseMessage(HttpStatusCode.NotFound)
                        {
                            Content = new StringContent(
                                $$"""{ "code": 404, "error": "Session not found: {{id}}" }""",
                                Encoding.UTF8,
                                "application/json"
                            ),
                        };
                }

                // Destroy: DELETE /api/v1/sandboxes/{id}
                if (req.Method == HttpMethod.Delete && path.Contains("/sandboxes/", StringComparison.Ordinal))
                {
                    var id = path[(path.LastIndexOf('/') + 1)..];
                    if (calls.FailDeletes)
                    {
                        return new HttpResponseMessage(HttpStatusCode.InternalServerError);
                    }
                    calls.RecordDelete(id);
                    return new HttpResponseMessage(HttpStatusCode.OK);
                }

                return new HttpResponseMessage(HttpStatusCode.OK);
            }
        );

        var options = new SandboxGatewayOptions
        {
            BaseUrl = GatewayBaseUrl,
            AppId = DefaultAppId,
            Marketplaces = null,
        };

        // The gateway lifetime client only serves the /health adopt probe; 200 ⇒ adopt an
        // "existing" gateway and proceed straight to create/liveness/destroy calls on the
        // registry's own client (the one `calls` observes).
        var gateway = new SandboxGatewayLifetime(
            options,
            NullLogger<SandboxGatewayLifetime>.Instance,
            new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)))
        );

        var registry = new SandboxSessionRegistry(
            gateway,
            options,
            LoggerFactory.CreateLogger<SandboxSessionRegistry>(),
            new HttpClient(registryHandler),
            auth ?? new AuthOptions(),
            new SessionSecretStore(
                Path.Combine(Path.GetTempPath(), "lmstreaming-test-secrets", Guid.NewGuid().ToString("N")),
                NullLogger<SessionSecretStore>.Instance
            ),
            reloadWorkspaceRef: reloadWorkspaceRef
        );

        return (registry, calls);
    }

    // Thread-safe: mirrors SandboxSessionRegistryRecreateOnGateway404Tests' CallLog.
    private sealed class CallLog
    {
        private readonly object _gate = new();
        private readonly List<string> _postAppIds = [];
        private readonly List<string> _postBodies = [];
        private readonly List<string> _deletedSessionIds = [];
        private readonly HashSet<string> _created = new(StringComparer.Ordinal);
        private readonly HashSet<string> _evicted = new(StringComparer.Ordinal);

        /// <summary>When set, every DELETE answers 500 and deletes nothing.</summary>
        public volatile bool FailDeletes;

        private readonly SemaphoreSlim _deletesStarted = new(0);
        private TaskCompletionSource? _deleteGate;
        private int _deleteAttempts;
        private int _deletesInFlight;
        private int _maxDeletesInFlight;

        /// <summary>Every DELETE that reached the gateway, answered or still held.</summary>
        public int DeleteAttempts => Volatile.Read(ref _deleteAttempts);

        /// <summary>The most DELETEs the gateway was answering at one time.</summary>
        public int MaxDeletesInFlight => Volatile.Read(ref _maxDeletesInFlight);

        /// <summary>Holds every DELETE from now on until the returned source is completed.</summary>
        public TaskCompletionSource HoldDeletes()
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref _deleteGate, gate);
            return gate;
        }

        public async Task WaitForDeleteStartedAsync() =>
            (await _deletesStarted.WaitAsync(Patience)).Should().BeTrue("a DELETE was expected");

        /// <summary>Counts a DELETE as begun and waits at the gate, if one is set.</summary>
        public async Task BeginDeleteAsync()
        {
            _ = Interlocked.Increment(ref _deleteAttempts);
            var inFlight = Interlocked.Increment(ref _deletesInFlight);
            int seen;
            while (inFlight > (seen = Volatile.Read(ref _maxDeletesInFlight)))
            {
                _ = Interlocked.CompareExchange(ref _maxDeletesInFlight, inFlight, seen);
            }

            _ = _deletesStarted.Release();
            if (Volatile.Read(ref _deleteGate) is { } gate)
            {
                await gate.Task;
            }
        }

        public void EndDelete() => _ = Interlocked.Decrement(ref _deletesInFlight);

        public int PostCount
        {
            get
            {
                lock (_gate)
                {
                    return _postAppIds.Count;
                }
            }
        }

        public IReadOnlyList<string> PostAppIds
        {
            get
            {
                lock (_gate)
                {
                    return [.. _postAppIds];
                }
            }
        }

        public IReadOnlyList<string> DeletedSessionIds
        {
            get
            {
                lock (_gate)
                {
                    return [.. _deletedSessionIds];
                }
            }
        }

        public IReadOnlyList<string> PostBodies
        {
            get
            {
                lock (_gate)
                {
                    return [.. _postBodies];
                }
            }
        }

        /// <summary>Records a create POST's app id header and body, and returns its 1-based ordinal.</summary>
        public int RecordPost(string appId, string body)
        {
            lock (_gate)
            {
                _postAppIds.Add(appId);
                _postBodies.Add(body);
                return _postAppIds.Count;
            }
        }

        public void RecordDelete(string sessionId)
        {
            lock (_gate)
            {
                _deletedSessionIds.Add(sessionId);
            }
        }

        public void MarkCreated(string sessionId)
        {
            lock (_gate)
            {
                _ = _created.Add(sessionId);
            }
        }

        /// <summary>Simulates the gateway forgetting an idle session.</summary>
        public void Evict(string sessionId)
        {
            lock (_gate)
            {
                _ = _evicted.Add(sessionId);
            }
        }

        public bool IsAlive(string sessionId)
        {
            lock (_gate)
            {
                return _created.Contains(sessionId) && !_evicted.Contains(sessionId);
            }
        }
    }

    /// <summary>Answers like <see cref="StubHandler"/>, but lets a test hold DELETEs and count them.</summary>
    private sealed class DeleteGatingHandler(CallLog calls, Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            if (request.Method != HttpMethod.Delete)
            {
                return respond(request);
            }

            try
            {
                await calls.BeginDeleteAsync();
                return respond(request);
            }
            finally
            {
                calls.EndDelete();
            }
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) => Task.FromResult(respond(request));
    }
}
