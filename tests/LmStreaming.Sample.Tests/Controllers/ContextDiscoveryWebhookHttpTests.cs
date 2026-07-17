using System.Net;
using System.Text;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using LmStreaming.Sample.Services.Discovery;
using LmStreaming.Sample.Tests.TestDoubles;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace LmStreaming.Sample.Tests.Controllers;

/// <summary>
/// Over-HTTP proof of the context-discovery webhook: drives a real <c>POST</c> through the live
/// ASP.NET pipeline (routing, snake_case <c>[FromBody]</c> binding, the <c>Authorization</c> header,
/// and the real <see cref="ContextDiscoveryController"/> + discovery services) into a pre-seeded
/// session/thread, and asserts the pooled agent received the formatted message. Uses an in-process
/// <see cref="TestServer"/> hosting the app's real controllers (loaded as an application part) rather
/// than booting all of <c>Program</c> — Program's startup does blocking I/O (MCP client creation,
/// sandbox/session spawn) that would make the test flaky in CI, while adding nothing to the webhook
/// path under test.
/// </summary>
public sealed class ContextDiscoveryWebhookHttpTests
{
    private const string Secret = "http-shared-secret-7c2b";
    private const string SessionId = "sess-http";
    private const string ThreadId = "thread-http";

    [Fact]
    public async Task PostBatchedContextFileWebhook_BindsAndReachesAgent()
    {
        // The REAL gateway wire contract (SandboxedOstoolsMcpServer
        // Docs/context-discovery.md §Webhook payload) is a BATCHED envelope with a
        // `discoveries` array — NOT a single flat item. This drives that exact shape end to
        // end and asserts the context_file reaches the live agent, plus that the diagnostics
        // endpoint reports the arrival over HTTP.
        await using var app = await TestApp.BuildAsync();

        const string json = """
            {
              "event": "context_discovery",
              "session_id": "sess-http",
              "app_id": "lmstreaming",
              "discoveries": [
                { "kind": "context_file", "path": "CLAUDE.md", "content": "# Project rules\nBe terse.", "truncated": false }
              ]
            }
            """;
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/discovery/context_discovery")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Authorization", Secret);

        var response = await app.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var message = app.Agent.SentMessages.Should().ContainSingle().Which
            .Should().BeOfType<NotifyMessage>().Subject;
        message.Role.Should().Be(Role.User);
        message.NotifyKind.Should().Be(NotifyKinds.ContextDiscovery);
        message.Label.Should().Be("CLAUDE.md");
        message.Text.Should().Contain("<context-discovery path=\"CLAUDE.md\">");
        message.Text.Should().Contain("Be terse.");

        var diagJson = await app.Client.GetStringAsync("/api/diagnostics/context-discovery");
        using var doc = JsonDocument.Parse(diagJson);
        var root = doc.RootElement;
        root.GetProperty("discoveryEnabled").GetBoolean().Should().BeTrue();
        root.GetProperty("webhookUrl").GetString().Should().EndWith("/api/discovery/context_discovery");
        var session = root.GetProperty("sessions").EnumerateArray()
            .Should().ContainSingle(s => s.GetProperty("sessionId").GetString() == SessionId).Subject;
        session.GetProperty("receivedCount").GetInt64().Should().Be(1);
        session.GetProperty("lastPath").GetString().Should().Be("CLAUDE.md");
    }

    [Fact]
    public async Task PostContextFileWebhook_WithWrongSecret_Returns401_AndDoesNotInject()
    {
        await using var app = await TestApp.BuildAsync();

        // Batched envelope (the real contract) with a wrong secret → auth gate rejects it before
        // the body is even processed, so nothing is injected.
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/discovery/context_discovery")
        {
            Content = new StringContent(
                /*lang=json,strict*/ "{\"event\":\"context_discovery\",\"session_id\":\"sess-http\",\"discoveries\":[{\"kind\":\"context_file\",\"path\":\"CLAUDE.md\",\"content\":\"x\"}]}",
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Authorization", "wrong-secret");

        var response = await app.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        app.Agent.SentMessages.Should().BeEmpty();
    }

    // Deterministic end-to-end proof of the #198 ROUTED path over real HTTP: a live gateway webhook
    // (agent_id: "ctx-probe") flows POST /api/discovery/context_discovery → ContextDiscoveryController →
    // ContextDiscoveryInjector → pool.TryGet → the REAL MultiTurnAgentLoop (which IS an
    // ISubAgentContextSink) → SubAgentManager.TryDeliverToRunningAsync → the live "ctx-probe" sub-agent,
    // and is Delivered (Routed == 1) WITHOUT touching the primary conversation.
    //
    // The sub-agent is spawned DETERMINISTICALLY here rather than via a background instruction chain: the
    // pooled primary loop owns a SubAgentManager whose "probe" template is backed by a mock provider that
    // blocks on its first turn, so the sub-agent — spawned under the caller-name "ctx-probe", resolvable
    // via TryResolveAgentId's id-or-name path — stays Running with no 30s timer and no async race. The
    // equivalent manual/browser instruction-chain flow (a backgrounded sub-agent parked on a long Wait) is
    // documented in samples/LmStreaming.Sample/PromptExamples.md → "Backgrounded sub-agent parked on a long
    // wait (webhook-routing fixture)".
    [Fact]
    public async Task PostRoutedContextFileWebhook_ToLiveSubAgent_DeliversWithoutTouchingPrimary()
    {
        const string ProbeAgentId = "ctx-probe";

        static HttpResponseMessage Unused(HttpRequestMessage _) => new(HttpStatusCode.OK);
        var authOptions = new AuthOptions();
        var gateway = new SandboxGatewayLifetime(
            new SandboxGatewayOptions { BaseUrl = "http://localhost:3000" },
            NullLogger<SandboxGatewayLifetime>.Instance,
            new HttpClient(new StubHandler(Unused)));
        var registry = new SandboxSessionRegistry(
            gateway,
            new SandboxGatewayOptions { BaseUrl = "http://localhost:3000" },
            NullLogger<SandboxSessionRegistry>.Instance,
            new HttpClient(new StubHandler(Unused)),
            authOptions,
            new AuthSharedSecret(authOptions));

        // The sub-agent's provider BLOCKS on its first turn, so the spawned "ctx-probe" sub-agent stays
        // deterministically Running (no timer) for the whole routed delivery.
        var subAgentBlock = new TaskCompletionSource<bool>();
        var subAgentProvider = new Mock<IStreamingAgent>();
        subAgentProvider
            .Setup(a => a.GenerateReplyStreamingAsync(
                It.IsAny<IEnumerable<IMessage>>(),
                It.IsAny<GenerateReplyOptions>(),
                It.IsAny<CancellationToken>()))
            .Returns<IEnumerable<IMessage>, GenerateReplyOptions, CancellationToken>(
                async (_, _, ct) =>
                {
                    await subAgentBlock.Task.WaitAsync(ct);
                    return EmptyMessageStream();
                });

        var subAgentOptions = new SubAgentOptions
        {
            Templates = new Dictionary<string, SubAgentTemplate>
            {
                ["probe"] = new SubAgentTemplate
                {
                    SystemPrompt = "You are the ctx-probe sub-agent.",
                    AgentFactory = () => subAgentProvider.Object,
                },
            },
        };

        // The primary loop's own provider is never invoked (we never send it a user turn), so a bare mock
        // suffices. Its InMemoryConversationStore is what lets us prove the routed context never lands here.
        var primaryProvider = new Mock<IStreamingAgent>();
        var primaryStore = new InMemoryConversationStore();
        MultiTurnAgentLoop? primaryLoop = null;
        var pool = new MultiTurnAgentPool(
            (threadId, _, _) =>
            {
                primaryLoop = new MultiTurnAgentLoop(
                    primaryProvider.Object,
                    new FunctionRegistry(),
                    threadId,
                    store: primaryStore,
                    logger: NullLogger<MultiTurnAgentLoop>.Instance,
                    subAgentOptions: subAgentOptions);
                return new MultiTurnAgentPool.AgentCreationResult(primaryLoop);
            },
            NullLogger<MultiTurnAgentPool>.Instance);
        pool.ThreadRemoved += threadId => registry.UnregisterThreadFromAllSessions(threadId);

        var mode = SystemChatModes.GetById(SystemChatModes.DefaultModeId)!;
        _ = pool.GetOrCreateAgent(ThreadId, mode);
        registry.RegisterThread(SessionId, ThreadId);

        primaryLoop.Should().NotBeNull();

        // Spawn the live sub-agent under the caller-name "ctx-probe"; the blocking provider holds it
        // Running so the webhook below routes into it deterministically (id-or-name resolution).
        _ = await primaryLoop!.SubAgentManager!.SpawnAsync(
            "probe", "probe task", name: ProbeAgentId, runInBackground: true);

        var sharedSecret = new AuthSharedSecret(new AuthOptions
        {
            Webhook = new WebhookOptions { GatewaySharedSecret = Secret },
        });
        var diagnostics = new ContextDiscoveryDiagnostics();

        using var host = await new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder.UseTestServer();
                webBuilder.ConfigureServices(services =>
                {
                    services.AddControllers().AddApplicationPart(typeof(ContextDiscoveryController).Assembly);
                    services.AddSingleton(registry);
                    services.AddSingleton(pool);
                    services.AddSingleton(sharedSecret);
                    services.AddSingleton<ContextDiscoveryFormatter>();
                    // Flag ON so the routed path is exercised end to end.
                    services.AddSingleton(new ContextDiscoveryOptions { RouteToOpeningSubAgent = true });
                    services.AddSingleton(diagnostics);
                    services.AddSingleton<ContextDiscoveryInjector>();
                    services.AddSingleton<WorkspaceSubAgentLoader>();
                });
                webBuilder.Configure(appBuilder =>
                {
                    appBuilder.UseRouting();
                    appBuilder.UseEndpoints(endpoints => endpoints.MapControllers());
                });
            })
            .StartAsync();

        using var client = host.GetTestClient();

        // The gateway stamps agent_id: "ctx-probe" on the discovery; the injector routes it into
        // ctx-probe's own conversation rather than fanning it out to the primary.
        var json = $$"""
            {
              "event": "context_discovery",
              "session_id": "{{SessionId}}",
              "discoveries": [
                { "kind": "context_file", "path": "sub/CLAUDE.md", "content": "# Sub rules", "agent_id": "{{ProbeAgentId}}" }
              ]
            }
            """;
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/discovery/context_discovery")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Authorization", Secret);

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        // With ctx-probe live, the discovery is ROUTED to the sub-agent (Delivered) — never fanned out.
        diagnostics.RoutingSnapshot().Routed.Should().Be(1);
        diagnostics.RoutingSnapshot().Fallback.Should().Be(0);

        // The primary loop's conversation store never received the routed context turn.
        var primaryMessages = await primaryStore.LoadMessagesAsync(ThreadId);
        primaryMessages.Should().NotContain(
            m => m.MessageJson.Contains("Sub rules", StringComparison.Ordinal),
            "a routed delivery reaches the sub-agent's own conversation, never the primary");

        // Cleanup: disposal cancels the still-blocked sub-agent. We deliberately do NOT release the block
        // before disposing — releasing would let the sub-agent complete and relay a NotifyMessage to the
        // un-stubbed primary provider, spawning a spurious primary run during teardown. The trailing
        // TrySetResult is a belt-and-suspenders unblock in case disposal ever awaits the send.
        await registry.DisposeAsync();
        await pool.DisposeAsync();
        _ = subAgentBlock.TrySetResult(true);
    }

    /// <summary>
    /// A minimal empty assistant stream the blocking sub-agent provider mock returns once released.
    /// The sub-agent never actually enumerates it (it is cancelled at disposal while still blocked);
    /// it only exists to satisfy the <c>Task&lt;IAsyncEnumerable&lt;IMessage&gt;&gt;</c> return shape.
    /// </summary>
    private static async IAsyncEnumerable<IMessage> EmptyMessageStream()
    {
        await Task.CompletedTask;
        yield break;
    }

    private sealed class TestApp : IAsyncDisposable
    {
        private readonly IHost _host;

        private TestApp(IHost host, HttpClient client, RecordingMultiTurnAgent agent)
        {
            _host = host;
            Client = client;
            Agent = agent;
        }

        public HttpClient Client { get; }
        public RecordingMultiTurnAgent Agent { get; }

        public static async Task<TestApp> BuildAsync()
        {
            var registry = CreateRegistry();

            RecordingMultiTurnAgent? created = null;
            var pool = new MultiTurnAgentPool(
                (threadId, _, _) =>
                {
                    created = new RecordingMultiTurnAgent(threadId);
                    return new MultiTurnAgentPool.AgentCreationResult(created);
                },
                NullLogger<MultiTurnAgentPool>.Instance);
            pool.ThreadRemoved += threadId => registry.UnregisterThreadFromAllSessions(threadId);

            // Seed a live thread so the injection has somewhere to land — mirrors Program.cs:774.
            var mode = SystemChatModes.GetById(SystemChatModes.DefaultModeId)!;
            _ = pool.GetOrCreateAgent(ThreadId, mode);
            registry.RegisterThread(SessionId, ThreadId);
            var agent = created ?? throw new InvalidOperationException("recording agent was not created");

            var sharedSecret = new AuthSharedSecret(new AuthOptions
            {
                Webhook = new WebhookOptions { GatewaySharedSecret = Secret },
            });

            var host = await new HostBuilder()
                .ConfigureWebHost(webBuilder =>
                {
                    webBuilder.UseTestServer();
                    webBuilder.ConfigureServices(services =>
                    {
                        services.AddControllers()
                            .AddApplicationPart(typeof(ContextDiscoveryController).Assembly);

                        services.AddSingleton(registry);
                        services.AddSingleton(pool);
                        services.AddSingleton(sharedSecret);
                        services.AddSingleton<ContextDiscoveryFormatter>();
                        services.AddSingleton(new ContextDiscoveryOptions());
                        services.AddSingleton<ContextDiscoveryInjector>();
                        services.AddSingleton<ContextDiscoveryDiagnostics>();
                        services.AddSingleton<WorkspaceSubAgentLoader>();
                    });
                    webBuilder.Configure(appBuilder =>
                    {
                        appBuilder.UseRouting();
                        appBuilder.UseEndpoints(endpoints => endpoints.MapControllers());
                    });
                })
                .StartAsync();

            return new TestApp(host, host.GetTestClient(), agent);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _host.StopAsync();
            _host.Dispose();
        }

        private static SandboxSessionRegistry CreateRegistry()
        {
            static HttpResponseMessage Unused(HttpRequestMessage _) => new(HttpStatusCode.OK);

            var authOptions = new AuthOptions();
            var gateway = new SandboxGatewayLifetime(
                new SandboxGatewayOptions { BaseUrl = "http://localhost:3000" },
                NullLogger<SandboxGatewayLifetime>.Instance,
                new HttpClient(new StubHandler(Unused)));

            return new SandboxSessionRegistry(
                gateway,
                new SandboxGatewayOptions { BaseUrl = "http://localhost:3000" },
                NullLogger<SandboxSessionRegistry>.Instance,
                new HttpClient(new StubHandler(Unused)),
                authOptions,
                new AuthSharedSecret(authOptions));
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
