using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Json;
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
/// A thread provisioned with a dedicated sandbox gets a sandbox of its own, created with its working directory as
/// the gateway working directory, and loses it when its agent goes or when the release route is called.
/// </summary>
/// <remarks>
/// Composition-root on purpose, like <see cref="ReadOnlyEgressCompositionTests"/>: the flag is read and put on the
/// workspace ref inside <c>Program.cs</c>'s pool agent factory, and an omission there is silent (the thread simply
/// shares the workspace's sandbox, writable everywhere). Only the sandbox gateway and the marketplace catalog are
/// faked; the registry, the pool, the factory, the controller and the stores are the production types.
/// </remarks>
public sealed class DedicatedSandboxCompositionTests
{
    private static readonly AgentProfile WorkspaceMode = SystemChatModes.GetById(SystemChatModes.WorkspaceAgentModeId)!;
    private static readonly AgentProfile WorkflowAuthorMode = SystemChatModes.GetById(
        SystemChatModes.WorkflowAuthorModeId
    )!;

    [Fact]
    public async Task Each_dedicated_thread_gets_its_own_jailed_sandbox_and_keeps_it_across_a_mode_switch()
    {
        await using var fixture = await Fixture.StartAsync();
        var a = await fixture.ProvisionAsync("slots/r-1/PRs/r-7");
        var b = await fixture.ProvisionAsync("slots/r-2/PRs/r-8");
        var shared = $"lane-{Guid.NewGuid():N}";

        foreach (var threadId in new[] { shared, a, b })
        {
            _ = fixture.Pool.GetOrCreateAgent(
                threadId,
                WorkspaceMode,
                "test",
                null,
                requestedWorkspaceId: fixture.WorkspaceId
            );
        }

        fixture.Gateway.Creates.Should().HaveCount(3, "no thread may share another's sandbox");
        var sessions = fixture.Gateway.Creates.Select(create => create.SessionId).ToList();
        fixture.Registry.GetThreads(sessions[0]).Should().Equal(shared);
        fixture.Registry.GetThreads(sessions[1]).Should().Equal(a);
        fixture.Registry.GetThreads(sessions[2]).Should().Equal(b);
        WorkingDir(fixture.Gateway.Creates[0].Body).Should().BeNull("the shared sandbox stays writable everywhere");
        WorkingDir(fixture.Gateway.Creates[1].Body).Should().Be("slots/r-1/PRs/r-7");
        WorkingDir(fixture.Gateway.Creates[2].Body).Should().Be("slots/r-2/PRs/r-8");
        fixture.Registry.TryGetSessionById(sessions[1], out var sandboxA).Should().BeTrue();
        sandboxA!.OwnerThreadId.Should().Be(a);

        _ = await fixture.Pool.SwitchModeAsync(a, WorkflowAuthorMode);

        fixture.Gateway.Creates.Should().HaveCount(3, "the switch must reuse the thread's own sandbox");
        fixture.Registry.GetThreads(sessions[1]).Should().Equal(a);
    }

    [Fact]
    public async Task Removing_a_dedicated_threads_agent_deletes_its_sandbox_but_not_the_shared_one()
    {
        await using var fixture = await Fixture.StartAsync();
        var a = await fixture.ProvisionAsync("PRs/r-7");
        var shared = $"lane-{Guid.NewGuid():N}";
        foreach (var threadId in new[] { shared, a })
        {
            _ = fixture.Pool.GetOrCreateAgent(
                threadId,
                WorkspaceMode,
                "test",
                null,
                requestedWorkspaceId: fixture.WorkspaceId
            );
        }

        await fixture.Pool.RemoveAgentAsync(a);
        await fixture.Pool.RemoveAgentAsync(shared);

        (await fixture.Gateway.NextDeleteAsync()).Should().Be(fixture.Gateway.Creates[1].SessionId);
        fixture.Gateway.Deletes.Should().Equal(fixture.Gateway.Creates[1].SessionId);
    }

    [Fact]
    public async Task The_release_route_deletes_the_sandbox_moves_the_directory_and_the_next_turn_gets_a_fresh_one()
    {
        await using var fixture = await Fixture.StartAsync();
        var a = await fixture.ProvisionAsync("slots/r-1/PRs/r-7");
        _ = fixture.Pool.GetOrCreateAgent(a, WorkspaceMode, "test", null, requestedWorkspaceId: fixture.WorkspaceId);
        var first = fixture.Gateway.Creates.Single().SessionId;

        using var response = await fixture.Client.PostAsJsonAsync(
            $"/api/conversations/{a}/sandbox/release",
            new ReleaseSandboxRequest { WorkingDirectoryRelPath = "slots/r-3/PRs/r-7" }
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<ReleaseSandboxResponse>())!
            .WorkingDirectoryRelPath.Should()
            .Be("slots/r-3/PRs/r-7");
        fixture.Gateway.Deletes.Should().Equal(first);
        fixture.Pool.ActiveAgentCount.Should().Be(0, "the agent held the deleted sandbox's session id");

        _ = fixture.Pool.GetOrCreateAgent(a, WorkspaceMode, "test", null, requestedWorkspaceId: fixture.WorkspaceId);

        fixture.Gateway.Creates.Should().HaveCount(2);
        WorkingDir(fixture.Gateway.Creates[1].Body).Should().Be("slots/r-3/PRs/r-7");
    }

    [Fact]
    public async Task The_release_route_refuses_a_thread_without_a_dedicated_sandbox()
    {
        await using var fixture = await Fixture.StartAsync();
        var shared = $"lane-{Guid.NewGuid():N}";
        _ = fixture.Pool.GetOrCreateAgent(
            shared,
            WorkspaceMode,
            "test",
            null,
            requestedWorkspaceId: fixture.WorkspaceId
        );

        using var response = await fixture.Client.PostAsJsonAsync(
            $"/api/conversations/{shared}/sandbox/release",
            new ReleaseSandboxRequest()
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("dedicated_sandbox_required");
        fixture.Gateway.Deletes.Should().BeEmpty("the workspace's shared sandbox serves other threads");
    }

    [Fact]
    public async Task The_release_route_with_a_directory_gives_a_shared_sandbox_thread_its_own_and_leaves_the_shared_one()
    {
        // A review conversation from before dedicated sandboxes runs in the workspace's shared sandbox. Continuing it
        // must not keep it there, writable everywhere, nor delete the sandbox other threads still use.
        await using var fixture = await Fixture.StartAsync();
        // Without a directory: a shared-sandbox directory is resolved by a command this fake gateway does not
        // serve, and the conversion does not read the old directory anyway.
        var legacy = await fixture.ProvisionAsync(null, dedicated: false);
        _ = fixture.Pool.GetOrCreateAgent(
            legacy,
            WorkspaceMode,
            "test",
            null,
            requestedWorkspaceId: fixture.WorkspaceId
        );
        var shared = fixture.Gateway.Creates.Single();
        WorkingDir(shared.Body).Should().BeNull();

        using var response = await fixture.Client.PostAsJsonAsync(
            $"/api/conversations/{legacy}/sandbox/release",
            new ReleaseSandboxRequest { WorkingDirectoryRelPath = "slots/r-3/PRs/r-7" }
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        fixture.Gateway.Deletes.Should().BeEmpty("the shared sandbox serves other threads");
        fixture.Pool.ActiveAgentCount.Should().Be(0, "the agent held the shared sandbox's session id");
        _ = fixture.Pool.GetOrCreateAgent(
            legacy,
            WorkspaceMode,
            "test",
            null,
            requestedWorkspaceId: fixture.WorkspaceId
        );
        fixture.Gateway.Creates.Should().HaveCount(2);
        WorkingDir(fixture.Gateway.Creates[1].Body).Should().Be("slots/r-3/PRs/r-7");
        fixture.Registry.GetThreads(fixture.Gateway.Creates[1].SessionId).Should().Equal(legacy);
    }

    private static string? WorkingDir(string createBody)
    {
        using var document = JsonDocument.Parse(createBody);
        return document.RootElement.TryGetProperty("working_dir", out var dir) ? dir.GetString() : null;
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _root;
        private readonly DedicatedSandboxWebAppFactory _host;

        private Fixture(string root, RecordingSandboxGateway gateway, DedicatedSandboxWebAppFactory host)
        {
            _root = root;
            Gateway = gateway;
            _host = host;
            Client = host.CreateClient();
        }

        public RecordingSandboxGateway Gateway { get; }
        public HttpClient Client { get; }
        public string WorkspaceId { get; private set; } = string.Empty;
        public MultiTurnAgentPool Pool => _host.Services.GetRequiredService<MultiTurnAgentPool>();
        public SandboxSessionRegistry Registry => _host.Services.GetRequiredService<SandboxSessionRegistry>();

        public static async Task<Fixture> StartAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "lmstreaming-dedicated-sandbox", Guid.NewGuid().ToString("N"));
            _ = Directory.CreateDirectory(root);
            var gateway = new RecordingSandboxGateway();
            var fixture = new Fixture(root, gateway, new DedicatedSandboxWebAppFactory(root, gateway));
            fixture.WorkspaceId = (
                await fixture
                    ._host.Services.GetRequiredService<IWorkspaceStore>()
                    .CreateAsync(new WorkspaceCreate { Name = "Reviews" })
            ).Id;
            return fixture;
        }

        /// <summary>Stores a thread exactly as the provision route stores a dedicated one.</summary>
        public async Task<string> ProvisionAsync(string? workingDirectory, bool dedicated = true)
        {
            var threadId = $"review-{Guid.NewGuid():N}";
            var properties = ImmutableDictionary<string, object>.Empty.Add(
                MultiTurnAgentPool.WorkspacePropertyKey,
                WorkspaceId
            );
            if (workingDirectory is not null)
            {
                properties = properties.Add(ConversationWorkingDirectory.PropertyKey, workingDirectory);
            }
            await _host
                .Services.GetRequiredService<IConversationStore>()
                .UpdateMetadataAsync(
                    threadId,
                    _ => new ThreadMetadata
                    {
                        ThreadId = threadId,
                        LastUpdated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        Properties = dedicated
                            ? properties.Add(ConversationDedicatedSandbox.PropertyKey, true)
                            : properties,
                    }
                );
            return threadId;
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _host.DisposeAsync();
            DetachedStoreTeardown.Purge(_root);
        }
    }

    /// <summary>Boots the real <c>Program</c> host with the sandbox gateway swapped for an in-memory fake.</summary>
    private sealed class DedicatedSandboxWebAppFactory : WebApplicationFactory<Program>
    {
        private readonly string _root;
        private readonly HttpMessageHandler _gateway;

        public DedicatedSandboxWebAppFactory(string root, HttpMessageHandler gateway)
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
            builder.UseSetting("SandboxGateway:AppId", "lm-dedicated-sandbox-test");
            builder.UseSetting("SandboxGateway:WorkspaceBasePath", Path.Combine(_root, "workspace-base"));

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
                // The sandbox MCP tools are not under test. Fail their connect at once.
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

    /// <summary>
    /// In-memory sandbox gateway that records each create's session id and request body and each delete, and
    /// acknowledges a requested working directory as the real gateway does.
    /// </summary>
    private sealed class RecordingSandboxGateway : HttpMessageHandler
    {
        private readonly object _gate = new();
        private readonly List<(string SessionId, string Body)> _creates = [];
        private readonly List<string> _deletes = [];
        private readonly SemaphoreSlim _deleted = new(0);

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

        public IReadOnlyList<string> Deletes
        {
            get
            {
                lock (_gate)
                {
                    return [.. _deletes];
                }
            }
        }

        /// <summary>Waits for the next delete (a removed agent releases its sandbox in the background).</summary>
        public async Task<string> NextDeleteAsync()
        {
            (await _deleted.WaitAsync(TimeSpan.FromSeconds(10))).Should().BeTrue("a delete was expected");
            lock (_gate)
            {
                return _deletes[^1];
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (request.Method == HttpMethod.Delete && path.Contains("/sandboxes/", StringComparison.Ordinal))
            {
                lock (_gate)
                {
                    _deletes.Add(path[(path.LastIndexOf('/') + 1)..]);
                }
                _ = _deleted.Release();
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            if (request.Method != HttpMethod.Post || !path.EndsWith("/sandboxes", StringComparison.Ordinal))
            {
                // Health probes, liveness GETs, operations and the boot-time context-file reads.
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            string sessionId;
            lock (_gate)
            {
                sessionId = $"sess-{_creates.Count + 1}";
                _creates.Add((sessionId, body));
            }

            var workingDir = WorkingDir(body) is { } dir ? $", \"working_dir\": \"{dir}\"" : string.Empty;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $$"""
                    { "session_id": "{{sessionId}}", "container_id": "c-{{sessionId}}",
                      "volumes": { "workspace": { "container_path": "/workspace", "read_only": false{{workingDir}} } } }
                    """,
                    Encoding.UTF8,
                    "application/json"
                ),
            };
        }
    }
}
