using System.Net;
using System.Text;

namespace LmStreaming.Sample.Tests.Services;

public class SandboxSessionRegistryDestroyTests
{
    [Fact]
    public async Task DestroyWorkspaceSessionAsync_UnknownWorkspace_IsNoOp()
    {
        var deletes = 0;
        var handler = new CountingHandler(req =>
        {
            if (req.Method == HttpMethod.Delete)
                deletes++;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var gateway = new SandboxGatewayLifetime(
            new SandboxGatewayOptions { BaseUrl = "http://localhost:3000" },
            NullLogger<SandboxGatewayLifetime>.Instance,
            new HttpClient(new CountingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)))
        );
        var registry = new SandboxSessionRegistry(
            gateway,
            new SandboxGatewayOptions { BaseUrl = "http://localhost:3000" },
            NullLogger<SandboxSessionRegistry>.Instance,
            new HttpClient(handler),
            new AuthOptions(),
            new SessionSecretStore(
                Path.Combine(Path.GetTempPath(), "lmstreaming-test-secrets", Guid.NewGuid().ToString("N")),
                NullLogger<SessionSecretStore>.Instance
            )
        );

        await registry.DestroyWorkspaceSessionAsync("never-created");

        deletes.Should().Be(0);
    }

    /// <summary>
    /// The identity-scoped sibling of <c>DestroyWorkspaceSessionAsync</c>. It reports the LOGICAL
    /// retirement only — whether the gateway accepted the DELETE — because the gateway removes its
    /// session record before its best-effort container teardown and never says whether that succeeded, so
    /// nothing here (a re-read of the deleted id included) can distinguish a released mount from a leaked
    /// one.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.OK, SessionRetirementOutcome.Retired, null)]
    [InlineData(HttpStatusCode.NotFound, SessionRetirementOutcome.Retired, null)]
    [InlineData(HttpStatusCode.InternalServerError, SessionRetirementOutcome.Unconfirmed, "gateway_status_500")]
    public async Task TryRetireSessionAsync_ReportsWhetherTheGatewayAcceptedTheTeardown(
        HttpStatusCode deleteStatus,
        SessionRetirementOutcome expected,
        string? expectedReason
    )
    {
        var (registry, deleted) = CreateRegistry(deleteStatus);
        await using var lifetime = registry;
        var session = await registry.GetOrCreateSessionAsync("ws-release", default, new SandboxCredential("app", "k"));

        var result = await registry.TryRetireSessionAsync(session.SessionId);

        result.Outcome.Should().Be(expected);
        result.UnconfirmedReason.Should().Be(expectedReason);
        result.UnconfirmedReason.Should().NotContain(session.SessionId, "a reason code must not name the session");

        // One DELETE and no follow-up read: the host forgets the session either way, and a probe that
        // cannot tell the two apart would only add latency to the same answer.
        deleted().Should().ContainSingle().Which.Should().Be(session.SessionId);
        registry.TryGetSessionById(session.SessionId, out _).Should().BeFalse();
    }

    [Fact]
    public async Task TryRetireSessionAsync_UnknownSession_ReportsNothingToRetire()
    {
        var (registry, deleted) = CreateRegistry(HttpStatusCode.OK);
        await using var lifetime = registry;

        var result = await registry.TryRetireSessionAsync("sess-never-created");

        result.Outcome.Should().Be(SessionRetirementOutcome.NothingToRetire);
        deleted().Should().BeEmpty();
    }

    /// <summary>
    /// The cross-app isolation this replaced workspace-wide teardown to get. One workspace, two caller app
    /// ids: the cache is partitioned by (workspace id, app id), so tearing down "the workspace" would take
    /// a second caller's live session with it — a session nobody asked to release.
    /// </summary>
    [Fact]
    public async Task TryRetireSessionAsync_TouchesOnlyTheAskedForAppsSession()
    {
        var (registry, deleted) = CreateRegistry(HttpStatusCode.OK);
        await using var lifetime = registry;

        var mine = await registry.GetOrCreateSessionAsync("ws-shared", default, new SandboxCredential("app-1", "k1"));
        var theirs = await registry.GetOrCreateSessionAsync("ws-shared", default, new SandboxCredential("app-2", "k2"));
        mine.SessionId.Should().NotBe(theirs.SessionId, "the partition must actually have produced two sessions");
        registry.RegisterThread(theirs.SessionId, "thread-other-app");

        var result = await registry.TryRetireSessionAsync(mine.SessionId);

        result.Outcome.Should().Be(SessionRetirementOutcome.Retired);
        deleted().Should().ContainSingle().Which.Should().Be(mine.SessionId);
        registry.TryGetSessionById(theirs.SessionId, out _).Should().BeTrue("the other app's session is untouched");
        registry.GetBoundThreads(theirs.SessionId).Should().Equal("thread-other-app");

        // And it is still resolvable, so the other app keeps working rather than silently losing its mount.
        (await registry.GetOrCreateSessionAsync("ws-shared", default, new SandboxCredential("app-2", "k2")))
            .SessionId.Should()
            .Be(theirs.SessionId);
    }

    /// <summary>
    /// The acquisition barrier, registration-wins direction: a conversation that registers against the
    /// session before the retirement commits keeps it alive. This is the ordering the endpoint's old
    /// "count bound threads, then delete" could not guarantee, because the two steps were not atomic.
    /// </summary>
    [Fact]
    public async Task TryRetireSessionAsync_AThreadRegisteredFirst_KeepsTheSession()
    {
        var (registry, deleted) = CreateRegistry(HttpStatusCode.OK);
        await using var lifetime = registry;
        var session = await registry.GetOrCreateSessionAsync("ws-race", default, new SandboxCredential("app", "k"));

        registry.RegisterThread(session.SessionId, "thread-late-claimant");
        var result = await registry.TryRetireSessionAsync(session.SessionId, "thread-releasing");

        result.Outcome.Should().Be(SessionRetirementOutcome.StillBound);
        result.BoundThreads.Should().Be(1);
        deleted().Should().BeEmpty("a session another conversation holds must never be deleted");
        registry.TryGetSessionById(session.SessionId, out _).Should().BeTrue();
    }

    /// <summary>
    /// The retirement-first direction. There is no tombstone to consult, so nothing has to REFUSE the
    /// late claimant: acquisition resolves the slot afresh, finds it empty, and hands back a live
    /// replacement. What must never happen is the claimant binding to the id that was just deleted.
    /// </summary>
    [Fact]
    public async Task AcquireSessionForAgentAsync_AfterTheSlotWasRetired_ResolvesALiveReplacement()
    {
        var (registry, deleted) = CreateRegistry(HttpStatusCode.OK);
        await using var lifetime = registry;
        var credential = new SandboxCredential("app", "k");
        var retired = (await registry.GetOrCreateSessionAsync("ws-race", default, credential)).SessionId;

        (await registry.TryRetireSessionAsync(retired)).Outcome.Should().Be(SessionRetirementOutcome.Retired);

        var acquired = await registry.AcquireSessionForAgentAsync(
            new WorkspaceRef("ws-race"),
            "thread-late-claimant",
            credential
        );

        acquired.SessionId.Should().NotBe(retired, "the claimant must not be handed the deleted mount");
        registry.GetBoundThreads(retired).Should().BeEmpty("nothing may be routed at the retired id");
        registry.GetBoundThreads(acquired.SessionId).Should().Equal("thread-late-claimant");
        deleted().Should().ContainSingle().Which.Should().Be(retired);
    }

    /// <summary>
    /// The claim-first direction, forced rather than raced. The claimant is parked INSIDE its
    /// acquisition — after the claim is taken, before the session is registered — which is precisely the
    /// window a bounded "recently retired" ledger had to outlive. It cannot: the window is a remote call
    /// of unbounded duration, and the parked handler ignores cancellation, so no timeout ends it either.
    /// The intervening retirement volume is deliberately far past any such cap to make the point that the
    /// new rule has no capacity concept at all — retirement defers because a claim on the slot is
    /// OUTSTANDING, not because the retirement is recent enough to still be remembered.
    /// </summary>
    [Fact]
    public async Task TryRetireSessionAsync_WhileAnAcquisitionIsParkedMidResolution_DefersInsteadOfDeleting()
    {
        // Comfortably past the 256-entry cap the deleted retired-id ledger used, so a test that passes
        // here could not have passed against it.
        const int RetirementVolume = 300;

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string? parkOn = null;

        var (registry, deleted) = CreateRegistry(
            HttpStatusCode.OK,
            req =>
            {
                if (
                    req.Method != HttpMethod.Get
                    || parkOn is not { } id
                    || !req.RequestUri!.AbsolutePath.EndsWith(id, StringComparison.Ordinal)
                )
                {
                    return;
                }

                // Park exactly once, and park SYNCHRONOUSLY: the handler never observes the probe's
                // cancellation token, so the liveness probe's own timeout cannot cut the window short
                // and turn this into a timing test.
                parkOn = null;
                entered.SetResult();
                release.Task.GetAwaiter().GetResult();
            }
        );
        await using var lifetime = registry;
        var credential = new SandboxCredential("app", "k");
        var session = await registry.GetOrCreateSessionAsync("ws-race", default, credential);

        // Created up front so the parked window contains only the retirements themselves.
        var volume = new List<string>(RetirementVolume);
        for (var i = 0; i < RetirementVolume; i++)
        {
            volume.Add((await registry.GetOrCreateSessionAsync($"ws-volume-{i}", default, credential)).SessionId);
        }

        parkOn = session.SessionId;
        var claimant = Task.Run(() =>
            registry.AcquireSessionForAgentAsync(new WorkspaceRef("ws-race"), "thread-claimant", credential)
        );
        await entered.Task;

        foreach (var other in volume)
        {
            (await registry.TryRetireSessionAsync(other)).Outcome.Should().Be(SessionRetirementOutcome.Retired);
        }

        var deletedBeforeRelease = deleted().Count;
        claimant.IsCompleted.Should().BeFalse("the interleaving is only real while the claimant is still parked");
        var result = await registry.TryRetireSessionAsync(session.SessionId, "thread-releasing");

        result.Outcome.Should().Be(SessionRetirementOutcome.ClaimInFlight);
        deleted().Should().HaveCount(deletedBeforeRelease, "a claimed slot must not be torn down");
        deleted().Should().NotContain(session.SessionId);

        release.SetResult();
        var acquired = await claimant;

        acquired.SessionId.Should().Be(session.SessionId, "the claim kept alive the very session it resolved");
        registry.TryGetSessionById(session.SessionId, out _).Should().BeTrue();
        registry.GetBoundThreads(session.SessionId).Should().Contain("thread-claimant");
    }

    /// <summary>
    /// A replacement created after the retirement decision must never be deleted in the retired session's
    /// place — the removal compares entry identity, not the (workspace, app) key.
    /// </summary>
    [Fact]
    public async Task TryRetireSessionAsync_ASecondCallAfterAReplacement_LeavesTheReplacementAlone()
    {
        var (registry, deleted) = CreateRegistry(HttpStatusCode.OK);
        await using var lifetime = registry;
        var credential = new SandboxCredential("app", "k");
        var first = await registry.GetOrCreateSessionAsync("ws-race", default, credential);
        await registry.TryRetireSessionAsync(first.SessionId);

        var replacement = await registry.GetOrCreateSessionAsync("ws-race", default, credential);

        // A retry of the ORIGINAL release, arriving after the slot was refilled.
        var repeat = await registry.TryRetireSessionAsync(first.SessionId);

        repeat.Outcome.Should().Be(SessionRetirementOutcome.NothingToRetire);
        deleted().Should().ContainSingle().Which.Should().Be(first.SessionId);
        registry.TryGetSessionById(replacement.SessionId, out _).Should().BeTrue();
    }

    private static (SandboxSessionRegistry Registry, Func<IReadOnlyList<string>> Deleted) CreateRegistry(
        HttpStatusCode deleteStatus,
        Action<HttpRequestMessage>? beforeRespond = null
    )
    {
        var deleted = new List<string>();
        var created = 0;
        var options = new SandboxGatewayOptions { BaseUrl = "http://localhost:3000" };

        var handler = new CountingHandler(req =>
        {
            beforeRespond?.Invoke(req);

            if (req.Method == HttpMethod.Delete)
            {
                var path = req.RequestUri!.AbsolutePath;
                lock (deleted)
                {
                    deleted.Add(path[(path.LastIndexOf('/') + 1)..]);
                }

                return deleteStatus == HttpStatusCode.OK
                    ? new HttpResponseMessage(HttpStatusCode.OK)
                    : new HttpResponseMessage(deleteStatus)
                    {
                        Content = new StringContent(
                            $$"""{ "code": {{(int)deleteStatus}}, "error": "no" }""",
                            Encoding.UTF8,
                            "application/json"
                        ),
                    };
            }

            if (req.Method == HttpMethod.Post)
            {
                created++;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        $$"""
                        { "session_id": "sess-{{created}}", "container_id": "c-{{created}}",
                          "volumes": { "workspace": { "container_path": "/workspace", "read_only": false } } }
                        """,
                        Encoding.UTF8,
                        "application/json"
                    ),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var gateway = new SandboxGatewayLifetime(
            options,
            NullLogger<SandboxGatewayLifetime>.Instance,
            new HttpClient(new CountingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)))
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

        return (
            registry,
            () =>
            {
                lock (deleted)
                {
                    return [.. deleted];
                }
            }
        );
    }

    private sealed class CountingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request));
    }
}
