using System.Collections.Concurrent;
using System.Net;
using LmStreaming.Sample.Services.Discovery;
using LmStreaming.Sample.Tests.TestDoubles;
using Microsoft.AspNetCore.Http;

namespace LmStreaming.Sample.Tests.Controllers;

/// <summary>
/// End-to-end (in-process) proof that a context_file discovery actually reaches a live agent:
/// drives the REAL <see cref="ContextDiscoveryController"/> → <see cref="ContextDiscoveryInjector"/>
/// → real <see cref="SandboxSessionRegistry"/> + <see cref="MultiTurnAgentPool"/> chain and asserts
/// the pooled agent received the formatted <c>&lt;context-discovery&gt;</c> message. The existing
/// suites split this — controller tests prove dispatch (dedup side effect), injector tests prove the
/// message shape — but nothing ran the whole chain together. If discovery is broken, this goes RED.
/// </summary>
public sealed class ContextDiscoveryEndToEndTests
{
    private const string Secret = "e2e-shared-secret-9f3a";
    private const string SessionId = "sess-e2e";
    private const string ThreadId = "thread-e2e";
    private const string GatewayBaseUrl = "http://localhost:3000";

    [Fact]
    public async Task ContextFileWebhook_FlowsThroughControllerAndInjector_ReachesLiveAgent()
    {
        using var harness = new Harness();
        var agent = harness.RegisterLiveThread(SessionId, ThreadId);

        var controller = harness.CreateController(authorizationHeader: Secret);
        var payload = new ContextDiscoveryPayload
        {
            SessionId = SessionId,
            Kind = "context_file",
            Path = "CLAUDE.md",
            Content = "# Project rules\nAlways be concise.",
        };

        var result = await controller.NotifyAsync(payload, CancellationToken.None);

        result.Should().BeOfType<OkResult>();
        var message = agent.SentMessages.Should().ContainSingle().Which
            .Should().BeOfType<TextMessage>().Subject;
        message.Role.Should().Be(Role.User);
        message.Text.Should().Contain("<context-discovery path=\"CLAUDE.md\">");
        message.Text.Should().Contain("Always be concise.");
        message.Metadata.Should().NotBeNull();
        message.Metadata!.Should().ContainKey(ContextDiscoveryInjector.MetadataKey);
    }

    [Fact]
    public async Task ContextFileWebhook_WrongSecret_DoesNotReachAgent()
    {
        // The auth gate is part of the chain: a bad secret must 401 and never inject.
        using var harness = new Harness();
        var agent = harness.RegisterLiveThread(SessionId, ThreadId);

        var controller = harness.CreateController(authorizationHeader: "not-the-secret");
        var result = await controller.NotifyAsync(
            new ContextDiscoveryPayload
            {
                SessionId = SessionId,
                Kind = "context_file",
                Path = "CLAUDE.md",
                Content = "secret-gated",
            },
            CancellationToken.None);

        result.Should().BeOfType<UnauthorizedResult>();
        agent.SentMessages.Should().BeEmpty();
    }

    [Fact]
    public void GatewayPayload_SnakeCaseJson_BindsToContextDiscoveryPayload()
    {
        // The gateway's wire contract is snake_case. The [JsonPropertyName] bindings on the DTO are
        // what make a real POST body map correctly; this pins them deterministically (the over-HTTP
        // test exercises the same path through the live ASP.NET pipeline).
        const string json = """
            {
              "session_id": "sess-1",
              "kind": "context_file",
              "name": "CLAUDE.md",
              "description": "root context",
              "path": "CLAUDE.md",
              "content": "hello world",
              "truncated": true
            }
            """;

        var payload = JsonSerializer.Deserialize<ContextDiscoveryPayload>(json);

        payload.Should().NotBeNull();
        payload!.SessionId.Should().Be("sess-1");
        payload.Kind.Should().Be("context_file");
        payload.Name.Should().Be("CLAUDE.md");
        payload.Description.Should().Be("root context");
        payload.Path.Should().Be("CLAUDE.md");
        payload.Content.Should().Be("hello world");
        payload.Truncated.Should().BeTrue();
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
            Pool.ThreadRemoved += threadId => Registry.UnregisterThreadFromAllSessions(threadId);

            Injector = new ContextDiscoveryInjector(
                Registry,
                Pool,
                new ContextDiscoveryFormatter(),
                NullLogger<ContextDiscoveryInjector>.Instance);
            Loader = new WorkspaceSubAgentLoader(Registry, NullLogger<WorkspaceSubAgentLoader>.Instance);
            SharedSecret = new AuthSharedSecret(new AuthOptions
            {
                Webhook = new WebhookOptions { GatewaySharedSecret = Secret },
            });
            Diagnostics = new ContextDiscoveryDiagnostics();
        }

        public SandboxSessionRegistry Registry { get; }
        public MultiTurnAgentPool Pool { get; }
        public ContextDiscoveryInjector Injector { get; }
        public WorkspaceSubAgentLoader Loader { get; }
        public AuthSharedSecret SharedSecret { get; }
        public ContextDiscoveryDiagnostics Diagnostics { get; }

        public RecordingMultiTurnAgent RegisterLiveThread(string sessionId, string threadId)
        {
            var mode = SystemChatModes.GetById(SystemChatModes.DefaultModeId)!;
            _ = Pool.GetOrCreateAgent(threadId, mode);
            Registry.RegisterThread(sessionId, threadId);
            return _agents[threadId];
        }

        public ContextDiscoveryController CreateController(string? authorizationHeader)
        {
            var controller = new ContextDiscoveryController(
                SharedSecret,
                Registry,
                Loader,
                Injector,
                Diagnostics,
                NullLogger<ContextDiscoveryController>.Instance);

            var httpContext = new DefaultHttpContext();
            if (authorizationHeader is not null)
            {
                httpContext.Request.Headers.Authorization = authorizationHeader;
            }

            controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
            return controller;
        }

        public void Dispose()
        {
            Pool.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Registry.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        private static SandboxSessionRegistry CreateRegistry()
        {
            static HttpResponseMessage Unused(HttpRequestMessage _) => new(HttpStatusCode.OK);

            var gateway = new SandboxGatewayLifetime(
                new SandboxGatewayOptions { BaseUrl = GatewayBaseUrl },
                NullLogger<SandboxGatewayLifetime>.Instance,
                new HttpClient(new StubHandler(Unused)));

            return new SandboxSessionRegistry(
                gateway,
                new SandboxGatewayOptions { BaseUrl = GatewayBaseUrl },
                NullLogger<SandboxSessionRegistry>.Instance,
                new HttpClient(new StubHandler(Unused)),
                new AuthOptions(),
                new AuthSharedSecret(new AuthOptions()));
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
