using System.Collections.Immutable;
using System.Net;
using System.Text;
using AchieveAi.LmDotnetTools.LmTestUtils.Persistence;
using LmStreaming.Sample.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace LmStreaming.Sample.Tests.Services;

/// <summary>
/// A thread provisioned with read-only egress gets its own sandbox, apart from a normal thread on the same
/// workspace and app id, and keeps it across a mode switch.
/// </summary>
/// <remarks>
/// Composition-root on purpose: the flag is read and put on the workspace ref inside <c>Program.cs</c>'s pool
/// agent factory, and an omission there is silent (the thread simply shares the lane's full-write sandbox).
/// Only the sandbox gateway (an in-memory handler that records each create body) and the marketplace catalog
/// are faked; the registry, the pool, the factory and the stores are the production types.
/// </remarks>
public sealed class ReadOnlyEgressCompositionTests
{
    private static readonly AgentProfile WorkspaceMode = SystemChatModes.GetById(SystemChatModes.WorkspaceAgentModeId)!;
    private static readonly AgentProfile WorkflowAuthorMode = SystemChatModes.GetById(
        SystemChatModes.WorkflowAuthorModeId
    )!;

    [Fact]
    public async Task A_read_only_thread_and_a_normal_thread_on_one_workspace_get_separate_sandboxes_and_keep_them()
    {
        var root = Path.Combine(Path.GetTempPath(), "lmstreaming-read-only-egress", Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(root);
        var gateway = new RecordingSandboxGateway();
        // Disposed before the purge below (see WorkspaceThreadRegistrationCompositionTests for why a block).
        await using (var host = new ReadOnlyEgressWebAppFactory(root, gateway))
        {
            var workspace = await host
                .Services.GetRequiredService<IWorkspaceStore>()
                .CreateAsync(new WorkspaceCreate { Name = "Proj" });
            var store = host.Services.GetRequiredService<IConversationStore>();
            var judge = $"judge-{Guid.NewGuid():N}";
            var lane = $"lane-{Guid.NewGuid():N}";
            // Stored exactly as the provision route stores it.
            await store.UpdateMetadataAsync(
                judge,
                _ => new ThreadMetadata
                {
                    ThreadId = judge,
                    LastUpdated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    Properties = ImmutableDictionary<string, object>.Empty.Add(
                        ConversationReadOnlyEgress.PropertyKey,
                        true
                    ),
                }
            );

            var pool = host.Services.GetRequiredService<MultiTurnAgentPool>();
            foreach (var threadId in new[] { lane, judge })
            {
                _ = pool.GetOrCreateAgent(threadId, WorkspaceMode, "test", null, requestedWorkspaceId: workspace.Id);
            }

            var registry = host.Services.GetRequiredService<SandboxSessionRegistry>();
            gateway.Creates.Should().HaveCount(2, "the judge must not share the lane's sandbox");
            var (laneSession, laneBody) = gateway.Creates[0];
            var (judgeSession, judgeBody) = gateway.Creates[1];
            registry.GetThreads(laneSession).Should().Equal(lane);
            registry.GetThreads(judgeSession).Should().Equal(judge);
            registry.TryGetSessionById(judgeSession, out var judgeSandbox).Should().BeTrue();
            judgeSandbox!.ReadOnlyEgress.Should().BeTrue();
            registry.TryGetSessionById(laneSession, out var laneSandbox).Should().BeTrue();
            laneSandbox!.ReadOnlyEgress.Should().BeFalse();
            AdoRuleMethods(laneBody).Should().Equal("*");
            AdoRuleMethods(judgeBody).Should().Equal("GET", "HEAD");

            // A mode switch rebuilds the agent through the factory, which must land on the same read-only sandbox.
            _ = await pool.SwitchModeAsync(judge, WorkflowAuthorMode);

            gateway.Creates.Should().HaveCount(2, "the switch must reuse the judge's read-only sandbox");
            registry.GetThreads(judgeSession).Should().Equal(judge);
            registry.GetThreads(laneSession).Should().Equal(lane);
        }

        DetachedStoreTeardown.Purge(root);
    }

    private static IReadOnlyList<string> AdoRuleMethods(string createBody)
    {
        using var document = JsonDocument.Parse(createBody);
        return
        [
            .. document
                .RootElement.GetProperty("network")
                .GetProperty("rules")
                .EnumerateArray()
                .Single(rule => rule.GetProperty("id").GetString() == "ado-api")
                .GetProperty("methods")
                .EnumerateArray()
                .Select(method => method.GetString()!),
        ];
    }

    /// <summary>
    /// Boots the real <c>Program</c> host with the sandbox gateway swapped for an in-memory fake, the stores in
    /// this test's temp dir, and one configured allow rule to ADO (any method) whose narrowing is observable.
    /// </summary>
    private sealed class ReadOnlyEgressWebAppFactory : WebApplicationFactory<Program>
    {
        private readonly string _root;
        private readonly HttpMessageHandler _gateway;

        public ReadOnlyEgressWebAppFactory(string root, HttpMessageHandler gateway)
        {
            _root = root;
            _gateway = gateway;
            Environment.SetEnvironmentVariable("LM_PROVIDER_MODE", "test");
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            // Never dialled: the gateway and sandbox MCP transports are both faked below.
            builder.UseSetting("SandboxGateway:BaseUrl", "http://127.0.0.1:1");
            builder.UseSetting("SandboxGateway:AutoSpawn", "false");
            builder.UseSetting("SandboxGateway:AppId", "lm-read-only-egress-test");
            builder.UseSetting("SandboxGateway:WorkspaceBasePath", Path.Combine(_root, "workspace-base"));
            builder.UseSetting("SandboxGateway:Network:Rules:ado-api:Hosts", "dev.azure.com");
            builder.UseSetting("SandboxGateway:Network:Rules:ado-api:Ports", "443");
            builder.UseSetting("SandboxGateway:Network:Rules:ado-api:Methods", "*");

            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IConversationStore>();
                services.AddSingleton<IConversationStore>(
                    new FileConversationStore(Path.Combine(_root, "conversations"))
                );
                services.RemoveAll<IWorkspaceStore>();
                services.AddSingleton<IWorkspaceStore>(new FileWorkspaceStore(Path.Combine(_root, "workspaces")));
                services.RemoveAll<IMarketplaceCatalogClient>();
                services.AddSingleton<IMarketplaceCatalogClient>(new EmptyCatalogClient());
                // The sandbox MCP tools are not under test. Fail their connect at once: on some hosts a
                // closed port hangs until the connect times out instead of refusing.
                services.AddKeyedSingleton<HttpMessageHandler>(
                    Program.SandboxMcpTransportHandlerKey,
                    new UnreachableMcpHandler()
                );

                services.RemoveAll<SandboxGatewayLifetime>();
                services.AddSingleton(sp => new SandboxGatewayLifetime(
                    sp.GetRequiredService<SandboxGatewayOptions>(),
                    sp.GetRequiredService<ILogger<SandboxGatewayLifetime>>(),
                    new HttpClient(_gateway, disposeHandler: false)
                ));
                services.RemoveAll<SandboxSessionRegistry>();
                services.AddSingleton(sp => new SandboxSessionRegistry(
                    sp.GetRequiredService<SandboxGatewayLifetime>(),
                    sp.GetRequiredService<SandboxGatewayOptions>(),
                    sp.GetRequiredService<ILogger<SandboxSessionRegistry>>(),
                    new HttpClient(_gateway, disposeHandler: false),
                    sp.GetRequiredService<AuthOptions>(),
                    sp.GetRequiredService<SessionSecretStore>()
                ));
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                Environment.SetEnvironmentVariable("LM_PROVIDER_MODE", null);
            }
        }
    }

    private sealed class EmptyCatalogClient : IMarketplaceCatalogClient
    {
        public Task<MarketplaceCatalog> GetCatalogAsync(
            IReadOnlyList<string>? marketplaces = null,
            CancellationToken ct = default
        ) => Task.FromResult(new MarketplaceCatalog([], []) { Capabilities = new MarketplaceCapabilities(true) });
    }

    private sealed class UnreachableMcpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) => throw new HttpRequestException("The sandbox MCP endpoint is not faked in this test.");
    }

    /// <summary>In-memory sandbox gateway that records each create's session id and request body.</summary>
    private sealed class RecordingSandboxGateway : HttpMessageHandler
    {
        private readonly object _gate = new();
        private readonly List<(string SessionId, string Body)> _creates = [];

        public IReadOnlyList<(string SessionId, string Body)> Creates
        {
            get
            {
                lock (_gate)
                {
                    return [.. _creates];
                }
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (request.Method != HttpMethod.Post || !path.EndsWith("/sandboxes", StringComparison.Ordinal))
            {
                // Health probes, liveness GETs and the boot-time context-file reads.
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            string sessionId;
            lock (_gate)
            {
                sessionId = $"sess-{_creates.Count + 1}";
                _creates.Add((sessionId, body));
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $$"""
                    { "session_id": "{{sessionId}}", "container_id": "c-{{sessionId}}",
                      "volumes": { "workspace": { "container_path": "/workspace", "read_only": false } } }
                    """,
                    Encoding.UTF8,
                    "application/json"
                ),
            };
        }
    }
}
