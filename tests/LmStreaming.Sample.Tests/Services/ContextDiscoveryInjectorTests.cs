using System.Collections.Concurrent;
using System.Net;
using LmStreaming.Sample.Tests.TestDoubles;

namespace LmStreaming.Sample.Tests.Services;

/// <summary>
/// Pins the end-to-end behaviour of <see cref="ContextDiscoveryInjector.InjectAsync"/>:
/// dedup, multi-thread fan-out, per-thread error isolation, and the F3 mode-switch invariant
/// (a recreated agent on the same threadId still receives subsequent injections).
/// </summary>
public class ContextDiscoveryInjectorTests
{
    private const string SessionId = "session-inject";
    private const string Path = "CLAUDE.md";
    private const string Kind = "context_file";
    private const string Content = "Project rules.";

    [Fact]
    public async Task InjectAsync_HappyPath_SendsToSingleThread()
    {
        using var harness = new Harness();
        var agent = harness.RegisterThread(SessionId, "thread-1");

        var sent = await harness.Injector.InjectAsync(
            BuildPayload(sessionId: SessionId, content: Content),
            CancellationToken.None);

        sent.Should().Be(1);
        agent.SentMessages.Should().ContainSingle();
        var message = agent.SentMessages[0].Should().BeOfType<NotifyMessage>().Subject;
        message.Role.Should().Be(Role.User);
        message.NotifyKind.Should().Be(NotifyKinds.ContextDiscovery);
        message.Label.Should().Be(Path);
        // The formatted file body stays reachable to the LLM inside the notification envelope.
        message.Text.Should().Contain("<context-discovery path=\"CLAUDE.md\">");
        message.Text.Should().Contain(Content);
    }

    [Fact]
    public async Task InjectAsync_MultipleThreads_FansOutToAll()
    {
        using var harness = new Harness();
        var a = harness.RegisterThread(SessionId, "thread-1");
        var b = harness.RegisterThread(SessionId, "thread-2");
        var c = harness.RegisterThread(SessionId, "thread-3");

        var sent = await harness.Injector.InjectAsync(
            BuildPayload(sessionId: SessionId, content: Content),
            CancellationToken.None);

        sent.Should().Be(3);
        a.SentMessages.Should().HaveCount(1);
        b.SentMessages.Should().HaveCount(1);
        c.SentMessages.Should().HaveCount(1);
    }

    [Fact]
    public async Task InjectAsync_DuplicateDelivery_DropsSecond()
    {
        using var harness = new Harness();
        var agent = harness.RegisterThread(SessionId, "thread-1");

        var first = await harness.Injector.InjectAsync(
            BuildPayload(sessionId: SessionId, content: Content),
            CancellationToken.None);
        var second = await harness.Injector.InjectAsync(
            BuildPayload(sessionId: SessionId, content: Content),
            CancellationToken.None);

        first.Should().Be(1);
        second.Should().Be(0);
        agent.SentMessages.Should().HaveCount(1);
    }

    [Fact]
    public async Task InjectAsync_DifferentPaths_SameSession_BothInject()
    {
        using var harness = new Harness();
        var agent = harness.RegisterThread(SessionId, "thread-1");

        _ = await harness.Injector.InjectAsync(
            BuildPayload(sessionId: SessionId, path: "CLAUDE.md", content: Content),
            CancellationToken.None);
        _ = await harness.Injector.InjectAsync(
            BuildPayload(sessionId: SessionId, path: "AGENTS.md", content: Content),
            CancellationToken.None);

        agent.SentMessages.Should().HaveCount(2);
    }

    [Fact]
    public async Task InjectAsync_NoSessionId_DropsAndReturnsZero()
    {
        using var harness = new Harness();
        _ = harness.RegisterThread(SessionId, "thread-1");

        var sent = await harness.Injector.InjectAsync(
            BuildPayload(sessionId: null, content: Content),
            CancellationToken.None);

        sent.Should().Be(0);
    }

    [Fact]
    public async Task InjectAsync_EmptyContent_DropsAndReturnsZero()
    {
        using var harness = new Harness();
        var agent = harness.RegisterThread(SessionId, "thread-1");

        var sent = await harness.Injector.InjectAsync(
            BuildPayload(sessionId: SessionId, content: string.Empty),
            CancellationToken.None);

        sent.Should().Be(0);
        agent.SentMessages.Should().BeEmpty();
    }

    [Fact]
    public async Task InjectAsync_NoLiveThreads_DropsAndStillMarksDedup()
    {
        using var harness = new Harness();
        // No RegisterThread call — registry has no thread routed for this session yet.

        var sent = await harness.Injector.InjectAsync(
            BuildPayload(sessionId: SessionId, content: Content),
            CancellationToken.None);

        sent.Should().Be(0);
        // Dedup MUST be marked even when no thread received the injection — by design, so a later
        // redelivery doesn't surprise a freshly-spun-up thread with stale content. The fallback path
        // marks under the session-level sentinel target.
        harness.Registry.TryMarkDiscoverySeen(
            SessionId, SandboxSessionRegistry.SessionDiscoveryTarget, Kind, Path).Should().BeFalse();
    }

    [Fact]
    public async Task InjectAsync_TruncatedFlag_PropagatesToEnvelope()
    {
        using var harness = new Harness();
        var agent = harness.RegisterThread(SessionId, "thread-1");

        _ = await harness.Injector.InjectAsync(
            BuildPayload(sessionId: SessionId, content: Content, truncated: true),
            CancellationToken.None);

        var message = agent.SentMessages.Should().ContainSingle().Which.Should().BeOfType<NotifyMessage>().Subject;
        message.NotifyKind.Should().Be(NotifyKinds.ContextDiscovery);
        message.Text.Should().Contain("truncated=\"true\"");
    }

    [Fact]
    public async Task InjectAsync_OneThreadThrows_OthersStillReceive()
    {
        using var harness = new Harness();
        var good1 = harness.RegisterThread(SessionId, "thread-good-1");
        var bad = harness.RegisterThread(SessionId, "thread-bad", throwOnSend: true);
        var good2 = harness.RegisterThread(SessionId, "thread-good-2");

        var sent = await harness.Injector.InjectAsync(
            BuildPayload(sessionId: SessionId, content: Content),
            CancellationToken.None);

        sent.Should().Be(2);
        good1.SentMessages.Should().HaveCount(1);
        good2.SentMessages.Should().HaveCount(1);
        bad.SentMessages.Should().BeEmpty();
    }

    [Fact]
    public async Task InjectAsync_AfterModeSwitch_StillReachesRecreatedAgent()
    {
        // F3 regression: a mode switch tears down the agent entry and creates a new one keyed by
        // the same threadId. The pool intentionally does NOT raise ThreadRemoved for that path,
        // so the registry's session→thread map must continue routing the injection to the new
        // agent rather than dropping the message.
        using var harness = new Harness();
        _ = harness.RegisterThread(SessionId, "thread-mode-swap");

        var newMode = SystemChatModes.All[0];
        var recreated = await harness.Pool.RecreateAgentWithModeAsync("thread-mode-swap", newMode);

        var sent = await harness.Injector.InjectAsync(
            BuildPayload(sessionId: SessionId, content: Content),
            CancellationToken.None);

        sent.Should().Be(1);
        ((RecordingMultiTurnAgent)recreated).SentMessages.Should().ContainSingle();
    }

    [Fact]
    public async Task InjectAsync_AfterThreadRemoved_DropsForRemovedThread()
    {
        using var harness = new Harness();
        var a = harness.RegisterThread(SessionId, "thread-stays");
        _ = harness.RegisterThread(SessionId, "thread-goes");

        // RemoveAgentAsync raises ThreadRemoved → registry.UnregisterThreadFromAllSessions wired
        // by the harness. Subsequent injection should only reach the surviving thread.
        await harness.Pool.RemoveAgentAsync("thread-goes");

        var sent = await harness.Injector.InjectAsync(
            BuildPayload(sessionId: SessionId, content: Content),
            CancellationToken.None);

        sent.Should().Be(1);
        a.SentMessages.Should().HaveCount(1);
    }

    private static ContextDiscoveryPayload BuildPayload(
        string? sessionId,
        string content,
        string path = Path,
        bool truncated = false)
    {
        return new ContextDiscoveryPayload
        {
            SessionId = sessionId,
            Kind = Kind,
            Path = path,
            Content = content,
            Truncated = truncated,
        };
    }

    private sealed class Harness : IDisposable
    {
        private readonly ConcurrentDictionary<string, RecordingMultiTurnAgent> _agents = new();

        public Harness()
        {
            Registry = CreateRegistry();
            Pool = new MultiTurnAgentPool(
                (threadId, _, _) =>
                {
                    var agent = _agents.GetOrAdd(threadId, id => new RecordingMultiTurnAgent(id));
                    return new MultiTurnAgentPool.AgentCreationResult(agent);
                },
                NullLogger<MultiTurnAgentPool>.Instance);

            // Mirror Program.cs's wiring: pool-side teardown invalidates the registry's routing.
            Pool.ThreadRemoved += threadId => Registry.UnregisterThreadFromAllSessions(threadId);

            Injector = new ContextDiscoveryInjector(
                Registry,
                Pool,
                new ContextDiscoveryFormatter(),
                new ContextDiscoveryOptions(),
                new ContextDiscoveryDiagnostics(),
                NullLogger<ContextDiscoveryInjector>.Instance);
        }

        public SandboxSessionRegistry Registry { get; }
        public MultiTurnAgentPool Pool { get; }
        public ContextDiscoveryInjector Injector { get; }

        public RecordingMultiTurnAgent RegisterThread(string sessionId, string threadId, bool throwOnSend = false)
        {
            var mode = SystemChatModes.GetById(SystemChatModes.DefaultModeId)!;
            _ = Pool.GetOrCreateAgent(threadId, mode);
            var agent = _agents[threadId];
            agent.ThrowOnSend = throwOnSend;
            Registry.RegisterThread(sessionId, threadId);
            return agent;
        }

        public void Dispose()
        {
            Pool.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Registry.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        private static SandboxSessionRegistry CreateRegistry()
        {
            static HttpResponseMessage Unused(HttpRequestMessage _) =>
                new(HttpStatusCode.OK);

            var gateway = new SandboxGatewayLifetime(
                new SandboxGatewayOptions { BaseUrl = "http://localhost:3000" },
                NullLogger<SandboxGatewayLifetime>.Instance,
                new HttpClient(new StubHandler(Unused)));

            return new SandboxSessionRegistry(
                gateway,
                new SandboxGatewayOptions { BaseUrl = "http://localhost:3000" },
                NullLogger<SandboxSessionRegistry>.Instance,
                new HttpClient(new StubHandler(Unused)),
                new AuthOptions(),
                new SessionSecretStore(
                    System.IO.Path.Combine(
                        System.IO.Path.GetTempPath(),
                        "lmstreaming-test-secrets",
                        Guid.NewGuid().ToString("N")),
                    NullLogger<SessionSecretStore>.Instance));
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(respond(request));
        }
    }
}

