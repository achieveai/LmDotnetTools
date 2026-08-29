using System.Net;
using System.Text;
using AchieveAi.LmDotnetTools.LmLifecycle;
using AchieveAi.LmDotnetTools.LmLifecycle.Payloads;
using AchieveAi.LmDotnetTools.LmLifecycle.Serialization;
using AchieveAi.LmDotnetTools.LmMultiTurn.Lifecycle;

namespace LmStreaming.Sample.Tests.Services;

/// <summary>
/// The registry's <see cref="LifecycleEventTypes.SandboxCreated"/> contract: a subscriber is told
/// about a sandbox exactly when one exists, exactly once, and never about one that does not.
/// </summary>
/// <remarks>
/// <para>
/// The failure this pins down is a subscriber acting on a session that was never committed — a
/// dashboard showing a session the gateway rejected, or an audit trail claiming N sandboxes for a
/// workspace that only ever had one because every concurrent caller reported the same creation. The
/// registry single-flights creation behind a shared <see cref="System.Lazy{T}"/>, so "one caller,
/// one event" and "one creation, one event" are different claims, and only the second is true.
/// </para>
/// <para>
/// Recreation is the interesting case: the replaced session's id has to reach the new session's
/// event, and the caller that noticed the eviction is not necessarily the caller that wins the
/// creation race. These tests assert the linkage from the outside — on the payload — rather than on
/// how it gets there.
/// </para>
/// </remarks>
public class SandboxSessionRegistryLifecycleEventTests
{
    private const string GatewayBaseUrl = "http://localhost:3000";

    #region A committed creation is reported once

    [Fact]
    public async Task Committed_create_publishes_one_event_identifying_the_session()
    {
        await using var harness = CreateHarness();

        var session = await harness.Registry.GetOrCreateSessionAsync("ws-1");

        harness.Calls.PostCount.Should().Be(1);
        var envelope = harness.Events.Events.Should().ContainSingle().Which;
        envelope.EventType.Should().Be(LifecycleEventTypes.SandboxCreated);
        envelope
            .SourceStreamId.Should()
            .Be(
                LifecycleSourceStream.ForSandbox(session.SessionId),
                "a sandbox's events are ordered against the sandbox, not against whichever thread asked for it"
            );
        envelope.Correlation!.SandboxSessionId.Should().Be(session.SessionId);
        envelope.Correlation.WorkspaceId.Should().Be("ws-1");

        var payload = harness.Events.PayloadAt<SandboxCreatedPayload>(0);
        payload.SessionId.Should().Be(session.SessionId);
        payload.WorkspaceId.Should().Be("ws-1");
        payload.WasRecreated.Should().BeFalse();
        payload.ReplacedSessionId.Should().BeNull("a first session replaced nothing");
    }

    [Fact]
    public async Task Created_event_carries_the_gateways_status_and_confirmed_inventory()
    {
        await using var harness = CreateHarness(
            createExtras: """
            "status":"running","inventory":{"status":"confirmed","items":[
                {"kind":"plugin","id":"development","version":"1.4.0"},
                {"kind":"skill","id":"development:implement"}
            ]}
            """
        );

        _ = await harness.Registry.GetOrCreateSessionAsync("ws-1");

        var payload = harness.Events.PayloadAt<SandboxCreatedPayload>(0);
        payload.Status.Should().Be("running");
        payload.Inventory.Status.Should().Be(LifecycleInventoryStatuses.Confirmed);
        payload
            .Inventory.Items.Select(i => (i.Kind, i.Id, i.Version))
            .Should()
            .Equal(
                [("plugin", "development", "1.4.0"), ("skill", "development:implement", null)],
                "the event reports what the gateway confirmed it loaded, in the gateway's own order"
            );
    }

    [Fact]
    public async Task Created_event_reports_an_unconfirmable_inventory_as_unavailable_rather_than_empty()
    {
        // The shape today's gateway produces: no status, and no inventory block at all.
        await using var harness = CreateHarness();

        _ = await harness.Registry.GetOrCreateSessionAsync("ws-1");

        var payload = harness.Events.PayloadAt<SandboxCreatedPayload>(0);
        payload.Inventory.Status.Should().Be(LifecycleInventoryStatuses.Unavailable);
        payload.Inventory.Items.Should().BeEmpty();
        payload
            .Inventory.UnavailableReason.Should()
            .NotBeNullOrWhiteSpace("a silent gateway must not be reportable as a session that loaded nothing");
    }

    #endregion

    #region Nothing else is reported

    [Fact]
    public async Task Cache_hit_publishes_nothing()
    {
        await using var harness = CreateHarness();

        var first = await harness.Registry.GetOrCreateSessionAsync("ws-1");
        var second = await harness.Registry.GetOrCreateSessionAsync("ws-1");

        second.SessionId.Should().Be(first.SessionId);
        harness.Calls.PostCount.Should().Be(1);
        harness.Events.Events.Should().ContainSingle("handing back a session that already existed is not a creation");
    }

    [Fact]
    public async Task Concurrent_first_callers_publish_one_event_between_them()
    {
        await using var harness = CreateHarness();

        var sessions = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ => harness.Registry.GetOrCreateSessionAsync("ws-1"))
        );

        sessions.Select(s => s.SessionId).Distinct().Should().ContainSingle();
        harness.Calls.PostCount.Should().Be(1, "the create is single-flighted");
        harness
            .Events.Events.Should()
            .ContainSingle("the event belongs to the creation, not to each caller that awaited it");
    }

    [Fact]
    public async Task Failed_create_publishes_nothing()
    {
        var events = new RecordingPublisher();
        var options = new SandboxGatewayOptions { BaseUrl = GatewayBaseUrl, Marketplaces = null };

        await using var registry = new SandboxSessionRegistry(
            AdoptedGateway(options),
            options,
            NullLogger<SandboxSessionRegistry>.Instance,
            new HttpClient(new StubHandler(_ => throw new HttpRequestException("connection refused"))),
            new AuthOptions(),
            NewSecretStore(),
            predefinedKeys: null,
            lifecycle: LifecycleWith(events)
        );

        var act = async () => await registry.GetOrCreateSessionAsync("ws-1");
        await act.Should().ThrowAsync<SandboxSessionUnavailableException>();

        events.Events.Should().BeEmpty("no session exists, so there is nothing to have been created");
    }

    [Fact]
    public async Task Rolled_back_create_publishes_nothing_even_though_the_gateway_session_existed()
    {
        // The window the rollback contract covers: the gateway create SUCCEEDED, then persisting the
        // session secret failed and the registry tore the session back down. A subscriber that heard
        // about it would be holding a session id the registry has already destroyed.
        var events = new RecordingPublisher();
        var calls = new CallLog();
        var options = new SandboxGatewayOptions { BaseUrl = GatewayBaseUrl, Marketplaces = null };

        // Replace the secret store's base directory with a FILE so its SaveAsync — which runs after
        // the create succeeds — fails deterministically.
        var secretDir = NewSecretDirectory();
        var secretStore = new SessionSecretStore(secretDir, NullLogger<SessionSecretStore>.Instance);
        Directory.Delete(secretDir, recursive: true);
        await File.WriteAllTextAsync(secretDir, "not-a-directory");

        await using var registry = new SandboxSessionRegistry(
            AdoptedGateway(options),
            options,
            NullLogger<SandboxSessionRegistry>.Instance,
            new HttpClient(GatewayHandler(calls, createExtras: null)),
            new AuthOptions(),
            secretStore,
            predefinedKeys: null,
            lifecycle: LifecycleWith(events)
        );

        var act = async () => await registry.GetOrCreateSessionAsync("ws-1");
        await act.Should().ThrowAsync<Exception>();

        calls.PostCount.Should().Be(1, "the gateway session really was created before the rollback");
        events.Events.Should().BeEmpty("the event follows the commit, and this attempt never reached one");
    }

    #endregion

    #region Recreation is reported as a replacement

    [Fact]
    public async Task Recreate_publishes_a_second_event_linked_to_the_session_it_replaced()
    {
        await using var harness = CreateHarness();

        var first = await harness.Registry.GetOrCreateSessionAsync("ws-1");
        harness.Calls.Evict(first.SessionId); // the gateway forgets the idle session

        var live = await harness.Registry.GetOrCreateLiveSessionAsync("ws-1");

        live.SessionId.Should().NotBe(first.SessionId);
        harness.Events.Events.Should().HaveCount(2);

        var replacement = harness.Events.PayloadAt<SandboxCreatedPayload>(1);
        replacement.SessionId.Should().Be(live.SessionId, "the event names the session that now exists");
        replacement.WasRecreated.Should().BeTrue();
        replacement
            .ReplacedSessionId.Should()
            .Be(
                first.SessionId,
                "without the dead id a subscriber cannot tell a replacement from a second workspace session"
            );
        harness
            .Events.Events[1]
            .SourceStreamId.Should()
            .Be(
                LifecycleSourceStream.ForSandbox(live.SessionId),
                "the replacement opens its own stream — it is a different sandbox"
            );
    }

    [Fact]
    public async Task Concurrent_recreates_publish_one_replacement_event_between_them()
    {
        await using var harness = CreateHarness();

        var first = await harness.Registry.GetOrCreateSessionAsync("ws-1");
        harness.Calls.Evict(first.SessionId);

        var live = await Task.WhenAll(
            harness.Registry.GetOrCreateLiveSessionAsync("ws-1"),
            harness.Registry.GetOrCreateLiveSessionAsync("ws-1")
        );

        live[0].SessionId.Should().Be(live[1].SessionId);
        harness.Events.Events.Should().HaveCount(2, "one creation and one replacement, however many callers raced");
        harness.Events.PayloadAt<SandboxCreatedPayload>(1).ReplacedSessionId.Should().Be(first.SessionId);
    }

    [Fact]
    public async Task A_deliberate_destroy_is_not_reported_as_a_replacement()
    {
        // Destroying a workspace's session and later asking for one again is a first session, not a
        // recreation: nothing failed, and there is no predecessor a subscriber should correlate the
        // new session with.
        await using var harness = CreateHarness();

        _ = await harness.Registry.GetOrCreateSessionAsync("ws-1");
        await harness.Registry.DestroyWorkspaceSessionAsync("ws-1");
        _ = await harness.Registry.GetOrCreateSessionAsync("ws-1");

        harness.Events.Events.Should().HaveCount(2);
        var second = harness.Events.PayloadAt<SandboxCreatedPayload>(1);
        second.WasRecreated.Should().BeFalse();
        second.ReplacedSessionId.Should().BeNull();
    }

    [Fact]
    public async Task A_second_workspaces_session_is_not_reported_as_replacing_the_first()
    {
        await using var harness = CreateHarness();

        var one = await harness.Registry.GetOrCreateSessionAsync("ws-1");
        var two = await harness.Registry.GetOrCreateSessionAsync("ws-2");

        var payloads = harness.Events.Payloads<SandboxCreatedPayload>(LifecycleEventTypes.SandboxCreated);
        payloads
            .Select(p => (p.WorkspaceId, p.SessionId))
            .Should()
            .Equal([("ws-1", one.SessionId), ("ws-2", two.SessionId)]);
        payloads
            .Should()
            .OnlyContain(
                p => !p.WasRecreated && p.ReplacedSessionId == null,
                "replacement is scoped to a workspace slot, not to the registry"
            );
    }

    [Fact]
    public async Task A_recreate_in_one_workspace_does_not_taint_another_workspaces_next_session()
    {
        await using var harness = CreateHarness();

        var one = await harness.Registry.GetOrCreateSessionAsync("ws-1");
        harness.Calls.Evict(one.SessionId);
        _ = await harness.Registry.GetOrCreateLiveSessionAsync("ws-1");

        var two = await harness.Registry.GetOrCreateSessionAsync("ws-2");

        var payloads = harness.Events.Payloads<SandboxCreatedPayload>(LifecycleEventTypes.SandboxCreated);
        payloads.Should().HaveCount(3);
        var last = payloads[2];
        last.SessionId.Should().Be(two.SessionId);
        last.WasRecreated.Should().BeFalse();
        last.ReplacedSessionId.Should().BeNull("ws-2 never had a session to replace");
    }

    #endregion

    #region Observation never costs the caller its session

    [Fact]
    public async Task A_failing_subscriber_does_not_fail_the_create()
    {
        var options = new SandboxGatewayOptions { BaseUrl = GatewayBaseUrl, Marketplaces = null };

        await using var registry = new SandboxSessionRegistry(
            AdoptedGateway(options),
            options,
            NullLogger<SandboxSessionRegistry>.Instance,
            new HttpClient(GatewayHandler(new CallLog(), createExtras: null)),
            new AuthOptions(),
            NewSecretStore(),
            predefinedKeys: null,
            lifecycle: LifecycleWith(new ThrowingPublisher())
        );

        var session = await registry.GetOrCreateSessionAsync("ws-1");

        session
            .SessionId.Should()
            .NotBeNullOrEmpty("a subscriber that cannot be reached is a dropped event, not a failed session");
        registry
            .TryGetSessionById(session.SessionId, out _)
            .Should()
            .BeTrue("the healthy session must stay cached — a publish failure is not a create failure");
    }

    [Fact]
    public async Task A_registry_without_lifecycle_still_creates_sessions()
    {
        // The default every existing construction site gets: no bundle, no publisher, no behavior change.
        var calls = new CallLog();
        var options = new SandboxGatewayOptions { BaseUrl = GatewayBaseUrl, Marketplaces = null };

        await using var registry = new SandboxSessionRegistry(
            AdoptedGateway(options),
            options,
            NullLogger<SandboxSessionRegistry>.Instance,
            new HttpClient(GatewayHandler(calls, createExtras: null)),
            new AuthOptions(),
            NewSecretStore()
        );

        var session = await registry.GetOrCreateSessionAsync("ws-1");

        session.SessionId.Should().Be("sess-1");
        calls.PostCount.Should().Be(1);
    }

    #endregion

    #region Harness

    private static MultiTurnLifecycleServices LifecycleWith(ILifecyclePublisher publisher) =>
        new() { Publisher = publisher };

    private static string NewSecretDirectory() =>
        Path.Combine(Path.GetTempPath(), "lmstreaming-test-secrets", Guid.NewGuid().ToString("N"));

    private static SessionSecretStore NewSecretStore() =>
        new(NewSecretDirectory(), NullLogger<SessionSecretStore>.Instance);

    /// <summary>
    /// A gateway lifetime whose /health probe answers 200, so the registry adopts an "existing"
    /// gateway and goes straight to create/liveness calls on its own client.
    /// </summary>
    private static SandboxGatewayLifetime AdoptedGateway(SandboxGatewayOptions options) =>
        new(
            options,
            NullLogger<SandboxGatewayLifetime>.Instance,
            new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)))
        );

    private static Harness CreateHarness(string? createExtras = null)
    {
        var calls = new CallLog();
        var events = new RecordingPublisher();
        var options = new SandboxGatewayOptions { BaseUrl = GatewayBaseUrl, Marketplaces = null };

        var registry = new SandboxSessionRegistry(
            AdoptedGateway(options),
            options,
            NullLogger<SandboxSessionRegistry>.Instance,
            new HttpClient(GatewayHandler(calls, createExtras)),
            new AuthOptions(),
            NewSecretStore(),
            predefinedKeys: null,
            lifecycle: LifecycleWith(events)
        );

        return new Harness(registry, calls, events);
    }

    /// <summary>
    /// A stub gateway where a freshly-created session is alive until a test <see cref="CallLog.Evict"/>s
    /// it, so the recreate path is driven the same way the 404 regression suite drives it.
    /// </summary>
    private static StubHandler GatewayHandler(CallLog calls, string? createExtras) =>
        new(req =>
        {
            var path = req.RequestUri!.AbsolutePath;

            if (req.Method == HttpMethod.Post && path.EndsWith("/sandboxes", StringComparison.Ordinal))
            {
                var ordinal = calls.RecordPost();
                var sessionId = $"sess-{ordinal}";
                calls.MarkCreated(sessionId);
                var extras = createExtras is null ? string.Empty : "," + createExtras;
                var responseBody = $$"""
                    { "session_id": "{{sessionId}}", "container_id": "c-{{ordinal}}",
                      "volumes": { "workspace": { "container_path": "/workspace", "read_only": false } }{{extras}} }
                    """;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
                };
            }

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

            return new HttpResponseMessage(HttpStatusCode.OK);
        });

    private sealed record Harness(SandboxSessionRegistry Registry, CallLog Calls, RecordingPublisher Events)
        : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Registry.DisposeAsync();
    }

    /// <summary>Keeps every envelope, so a test asserts on what was published rather than on the calls made.</summary>
    private sealed class RecordingPublisher : ILifecyclePublisher
    {
        private readonly object _gate = new();
        private readonly List<LifecycleEventEnvelope> _events = [];

        public IReadOnlyList<LifecycleEventEnvelope> Events
        {
            get
            {
                lock (_gate)
                {
                    return [.. _events];
                }
            }
        }

        public ValueTask PublishAsync(LifecycleEventEnvelope envelope, CancellationToken ct = default)
        {
            lock (_gate)
            {
                _events.Add(envelope);
            }

            return ValueTask.CompletedTask;
        }

        public TPayload PayloadAt<TPayload>(int index)
            where TPayload : class
        {
            LifecycleSerializer.TryReadPayload<TPayload>(Events[index], out var payload).Should().BeTrue();
            return payload!;
        }

        public IReadOnlyList<TPayload> Payloads<TPayload>(string eventType)
            where TPayload : class =>
            [
                .. Events
                    .Where(e => e.EventType == eventType)
                    .Select(e =>
                    {
                        LifecycleSerializer.TryReadPayload<TPayload>(e, out var payload).Should().BeTrue();
                        return payload!;
                    }),
            ];
    }

    private sealed class ThrowingPublisher : ILifecyclePublisher
    {
        public ValueTask PublishAsync(LifecycleEventEnvelope envelope, CancellationToken ct = default) =>
            throw new InvalidOperationException("subscriber transport is down");
    }

    // Thread-safe: the concurrency tests drive parallel creates and liveness GETs through the stub.
    private sealed class CallLog
    {
        private readonly object _gate = new();
        private readonly HashSet<string> _created = new(StringComparer.Ordinal);
        private readonly HashSet<string> _evicted = new(StringComparer.Ordinal);
        private int _postCount;

        public int PostCount
        {
            get
            {
                lock (_gate)
                {
                    return _postCount;
                }
            }
        }

        /// <summary>Records a create POST and returns its 1-based ordinal.</summary>
        public int RecordPost()
        {
            lock (_gate)
            {
                return ++_postCount;
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

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) => Task.FromResult(respond(request));
    }

    #endregion
}
