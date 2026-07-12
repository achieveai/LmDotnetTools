using System.Net;
using System.Text;
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
