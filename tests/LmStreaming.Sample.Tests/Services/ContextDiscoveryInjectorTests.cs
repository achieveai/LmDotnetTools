using System.Collections.Concurrent;
using LmStreaming.Sample.Tests.TestDoubles;
using Microsoft.Extensions.Logging;

namespace LmStreaming.Sample.Tests.Services;

public class ContextDiscoveryInjectorTests
{
    private const string SessionId = "session-inject";

    [Fact]
    public async Task InjectAsync_MissingOwner_TwoRootsReceiveNothing()
    {
        using var harness = new Harness();
        var first = harness.Register("first-root");
        var second = harness.Register("idle-peer");
        (await harness.Injector.InjectAsync(Payload(null), CancellationToken.None)).Should().Be(0);
        first.SentMessages.Should().BeEmpty();
        second.SentMessages.Should().BeEmpty();
        first.RecordedContext.Should().BeEmpty();
        second.RecordedContext.Should().BeEmpty();
    }

    [Fact]
    public async Task OwnerContext_AppendsOnlyToOwner_WithoutInputAcceptance_AndDeduplicates()
    {
        using var harness = new Harness();
        var owner = harness.Register("owner");
        var peer = harness.Register("peer");
        (await harness.Injector.InjectAsync(Payload("owner"), CancellationToken.None)).Should().Be(1);
        (await harness.Injector.InjectAsync(Payload("owner"), CancellationToken.None)).Should().Be(0);
        owner.RecordedContext.Should().ContainSingle().Which.Text.Should().Contain("Project rules.");
        owner.SentMessages.Should().BeEmpty();
        peer.RecordedContext.Should().BeEmpty();
        peer.SentMessages.Should().BeEmpty();
        owner.CurrentRunId = null;
        harness.Pool.TryGetHandoffState("owner", out var state).Should().BeTrue();
        state.IsBusy.Should().BeFalse("passive context did not create an accepted input");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("agent-1")]
    [InlineData("unknown")]
    public async Task UnidentifiedOwner_DropsWithoutBroadcast(string? owner)
    {
        using var harness = new Harness();
        var first = harness.Register("first");
        var second = harness.Register("second");
        (await harness.Injector.InjectAsync(Payload(owner), CancellationToken.None)).Should().Be(0);
        first.RecordedContext.Should().BeEmpty();
        second.RecordedContext.Should().BeEmpty();
    }

    [Fact]
    public async Task IdleOwner_DropsWithoutQueuedInput()
    {
        using var harness = new Harness();
        var owner = harness.Register("owner");
        owner.CurrentRunId = null;
        (await harness.Injector.InjectAsync(Payload("owner"), CancellationToken.None)).Should().Be(0);
        owner.RecordedContext.Should().BeEmpty();
        owner.SentMessages.Should().BeEmpty();
    }

    [Fact]
    public async Task OwnerFailure_DoesNotRedirectToPeer()
    {
        using var harness = new Harness();
        harness.Register("owner").ThrowOnSend = true;
        var peer = harness.Register("peer");
        (await harness.Injector.InjectAsync(Payload("owner"), CancellationToken.None)).Should().Be(0);
        (await harness.Injector.InjectAsync(Payload("owner"), CancellationToken.None)).Should().Be(0);
        peer.RecordedContext.Should().BeEmpty();
    }

    [Fact]
    public async Task DistinctOwnersAndPaths_HaveIndependentDedup()
    {
        using var harness = new Harness();
        var first = harness.Register("first");
        var second = harness.Register("second");
        foreach (var owner in new[] { "first", "second" })
        {
            (await harness.Injector.InjectAsync(Payload(owner), CancellationToken.None)).Should().Be(1);
            (await harness.Injector.InjectAsync(Payload(owner, "AGENTS.md"), CancellationToken.None)).Should().Be(1);
        }
        first.RecordedContext.Should().HaveCount(2);
        second.RecordedContext.Should().HaveCount(2);
    }

    [Fact]
    public async Task RemovedOwner_DropsRatherThanRedirecting()
    {
        using var harness = new Harness();
        _ = harness.Register("owner");
        var peer = harness.Register("peer");
        await harness.Pool.RemoveAgentAsync("owner");
        (await harness.Injector.InjectAsync(Payload("owner"), CancellationToken.None)).Should().Be(0);
        peer.RecordedContext.Should().BeEmpty();
    }

    /// <summary>
    /// PC-TRI1-3: the skill row gives the reason the agent row gives for the same finder
    /// (ContextDiscoveryControllerTests.NotifyAsync_AgentFoundWithNoOwningConversation_LogsTheFindersOwnReason): an
    /// untiered root presents no agent id at all. A sub-agent's ordinal id keeps its own reason.
    /// </summary>
    [Theory]
    [InlineData(null, "the finder sent no agent id (a root that does not tier)")]
    [InlineData("", "the finder sent no agent id (a root that does not tier)")]
    [InlineData("agent-1", "the finder is not a routable agent")]
    public async Task AnnounceSkillsAsync_AFinderItCannotRoute_LogsTheFindersOwnReason(string? agentId, string reason)
    {
        var logger = new CapturingLogger<ContextDiscoveryInjector>();
        using var harness = new Harness(logger);

        (await harness.Injector.AnnounceSkillsAsync(SessionId, [agentId], CancellationToken.None)).Should().Be(0);

        logger
            .Entries.Where(e => e.Message.StartsWith("Capabilities skill notice skipped", StringComparison.Ordinal))
            .Should()
            .ContainSingle()
            .Which.Message.Should()
            .EndWith(" " + reason);
    }

    private static ContextDiscoveryPayload Payload(string? owner, string path = "CLAUDE.md") =>
        new()
        {
            SessionId = SessionId,
            AgentId = owner,
            Kind = "context_file",
            Path = path,
            Content = "Project rules.",
        };

    private sealed class Harness : IDisposable
    {
        private readonly ConcurrentDictionary<string, RecordingMultiTurnAgent> _agents = new();

        public Harness(ILogger<ContextDiscoveryInjector>? injectorLogger = null)
        {
            var options = new SandboxGatewayOptions { BaseUrl = "http://unused.invalid" };
            var gateway = new SandboxGatewayLifetime(
                options,
                NullLogger<SandboxGatewayLifetime>.Instance,
                new HttpClient(new RejectNetwork())
            );
            Registry = new SandboxSessionRegistry(
                gateway,
                options,
                NullLogger<SandboxSessionRegistry>.Instance,
                new HttpClient(new RejectNetwork()),
                new AuthOptions(),
                new SessionSecretStore(
                    System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ctx-tests-" + Guid.NewGuid().ToString("N")),
                    NullLogger<SessionSecretStore>.Instance
                )
            );
            Pool = new MultiTurnAgentPool(
                (thread, _, _) =>
                    new MultiTurnAgentPool.AgentCreationResult(
                        _agents.GetOrAdd(thread, id => new RecordingMultiTurnAgent(id))
                    ),
                NullLogger<MultiTurnAgentPool>.Instance
            );
            Pool.ThreadRemoved += Registry.UnregisterThreadFromAllSessions;
            Injector = new ContextDiscoveryInjector(
                Registry,
                Pool,
                new ContextDiscoveryFormatter(),
                new ContextDiscoveryOptions { RouteToOpeningSubAgent = true },
                new ContextDiscoveryDiagnostics(),
                injectorLogger ?? NullLogger<ContextDiscoveryInjector>.Instance
            );
        }

        public SandboxSessionRegistry Registry { get; }
        public MultiTurnAgentPool Pool { get; }
        public ContextDiscoveryInjector Injector { get; }

        public RecordingMultiTurnAgent Register(string thread)
        {
            _ = Pool.GetOrCreateAgent(thread, SystemChatModes.GetById(SystemChatModes.DefaultModeId)!);
            Registry.RegisterThread(SessionId, thread);
            _agents[thread].CurrentRunId = "active-run";
            return _agents[thread];
        }

        public void Dispose()
        {
            Pool.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Registry.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private sealed class RejectNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new InvalidOperationException("No network expected");
    }
}
