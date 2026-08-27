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
/// Every sandbox-backed conversation must appear in the registry's session→thread index, not just the
/// ones that carry <c>subAgentOptions</c>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="WorkspacePluginSelectionService"/> decides whether a plugin-selection migration may tear
/// a live sandbox session down by asking the registry which threads belong to that session and then
/// asking the run-activity probe whether any of them is mid-turn. A thread that was never registered
/// is invisible to that query, so the wait loop is never even entered and the migration proceeds to
/// destroy the session underneath a running turn — silently, with no error anywhere.
/// </para>
/// <para>
/// <b>Why this has to be a composition-root test.</b> The registration lives in <c>Program.cs</c>'s pool
/// agent factory, which is the only place an ordinary (non-subagent) workspace conversation passes
/// through. The bug is an omission in that wiring: both halves — the registry index and the idle wait —
/// are individually correct and individually covered, and a unit test that hands the service a
/// pre-populated index would stay green through the omission. So the real host is booted and the agent
/// is requested through the real pool, exactly as a browser conversation would.
/// </para>
/// <para>
/// <b>What is faked and what is not.</b> Only the two edges that cannot run in a test process are
/// substituted: the sandbox gateway (an in-memory <see cref="HttpMessageHandler"/>) and the run-activity
/// probe (an interface precisely so a run can be held "in progress" for the length of the wait). The
/// registry, the workspace store, the compatibility service and the migration orchestrator are all the
/// real production types. Critically, <c>GetThreads</c> is NEVER stubbed — the index this test reads is
/// the one <c>Program.cs</c> populated.
/// </para>
/// </remarks>
public sealed class WorkspaceThreadRegistrationCompositionTests
{
    private static readonly AgentProfile WorkspaceMode = SystemChatModes.GetById(
        SystemChatModes.WorkspaceAgentModeId
    )!;

    private const string Marketplace = "official";

    /// <summary>The one plugin that is legal under <see cref="Marketplace"/> in the stub catalog.</summary>
    private static readonly PluginRef SelectedPlugin = new(Marketplace, "code-review");

    [Fact]
    public async Task OrdinaryWorkspaceConversation_IsIndexedByItsSession_SoAnActiveRunBlocksTheMigration()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "lmstreaming-thread-registration-composition",
            Guid.NewGuid().ToString("N")
        );
        _ = Directory.CreateDirectory(root);
        var gateway = new FakeSandboxGateway();
        var probe = new ThreadAwareActivityProbe();
        // An explicit `await using` BLOCK, not a method-scoped `await using var`: the host must be
        // disposed before the purge below, and method scope would dispose it at the method's closing
        // brace, i.e. AFTER. This test drives pool.GetOrCreateAgent, so the host is a live
        // conversation-store writer holding FileShare.None handles under `root`. The block is the shape that needs no
        // assumption about whether this factory subclass is idempotent on a second disposal.
        await using (var host = new WorkspaceCompositionWebAppFactory(root, gateway, probe))
        {
            // A real workspace with a real marketplace — the default workspace cannot be updated, and
            // the migration validates the selection against the (stubbed) catalog before it does
            // anything else, so the selection below has to be genuinely legal here.
            var store = host.Services.GetRequiredService<IWorkspaceStore>();
            var workspace = await store.CreateAsync(new WorkspaceCreate { Name = "Proj", Marketplaces = [Marketplace] });

            // An ORDINARY workspace conversation: no subAgentOptions, no S2S credential — the plain
            // interactive path a browser takes. This is the call whose side effects are under test.
            var pool = host.Services.GetRequiredService<MultiTurnAgentPool>();
            var threadId = $"ws-thread-{Guid.NewGuid():N}";
            var agent = pool.GetOrCreateAgent(
                threadId,
                WorkspaceMode,
                requestedProviderId: "test",
                requestResponseDumpFileName: null,
                requestedWorkspaceId: workspace.Id
            );
            agent.Should().NotBeNull("the workspace branch of the pool factory must build an agent");

            var registry = host.Services.GetRequiredService<SandboxSessionRegistry>();
            var sessionId = gateway
                .CreatedSessionIds.Should()
                .ContainSingle("the conversation must have provisioned exactly one sandbox session")
                .Subject;

            // The claim under test, read straight off the production index.
            registry
                .GetThreads(sessionId)
                .Should()
                .Contain(
                    threadId,
                    "an ordinary workspace conversation must be indexed against its session; without it "
                        + "the session looks thread-less and therefore permanently idle"
                );

            // The run goes hot AFTER the agent exists, exactly as it does when a turn starts.
            probe.BusyThreadId = threadId;

            var migration = host.Services.GetRequiredService<IWorkspacePluginSelectionService>();
            var act = () =>
                migration.ApplyPluginSelectionUpdateAsync(
                    workspace.Id,
                    new WorkspaceUpdate
                    {
                        Marketplaces = [Marketplace],
                        PluginSelection = new Optional<IReadOnlyList<PluginRef>?>([SelectedPlugin]),
                        PluginsRevision = 0,
                    }
                );

            _ = await act.Should()
                .ThrowExactlyAsync<SandboxSessionRestartTimeoutException>(
                    "the busy thread is reachable from the session, so the migration must give up rather "
                        + "than replace a session mid-turn"
                );

            // Non-vacuity guard. If the thread were missing from the index the wait loop would never
            // reach the probe at all, and every assertion below would still hold for the wrong reason:
            // no candidate, no delete and no persistence are equally true of a migration that timed out
            // and of one that never started. This is the assertion that separates them.
            probe.ObservedThreads.Should()
                .Contain(
                    threadId,
                    "the idle wait must have consulted the probe about THIS thread — that is the only "
                        + "evidence the registration actually reached WaitForIdleAsync"
                );

            // One create total is the conversation's own session: a migration that timed out must not
            // have built a replacement candidate...
            gateway.CreateAttempts.Should().Be(1, "a timed-out migration must create no candidate at all");
            // ...must not have retired anything...
            gateway.DeletedSessionIds.Should().BeEmpty("nothing may be torn down when the wait times out");
            registry
                .TryGetSessionById(sessionId, out _)
                .Should()
                .BeTrue("the conversation's session must still be serving");

            // ...and must not have consumed the revision or persisted the selection.
            var stored = await store.GetAsync(workspace.Id);
            stored!.PluginSelection.Should().BeNull("a timed-out migration must not persist the selection");
            stored.PluginsRevision.Should().Be(0, "a timed-out migration must not consume the revision");
        }

        // #477: detach-then-delete rather than recursive-delete in place - see DetachedStoreTeardown.
        // Deliberately NOT in a finally: Purge throws when it cannot detach, and a throw from a finally
        // REPLACES the assertion failure that is unwinding through it. A leaked temp directory is a far
        // cheaper outcome than losing the reason the test failed.
        DetachedStoreTeardown.Purge(root);
    }

    /// <summary>
    /// Boots the real <c>Program</c> host with the sandbox gateway swapped for an in-memory fake and
    /// every on-disk store redirected into this test's temp dir.
    /// </summary>
    private sealed class WorkspaceCompositionWebAppFactory : WebApplicationFactory<Program>
    {
        private readonly string _root;
        private readonly HttpMessageHandler _gateway;
        private readonly IAgentRunActivityProbe _probe;

        public WorkspaceCompositionWebAppFactory(string root, HttpMessageHandler gateway, IAgentRunActivityProbe probe)
        {
            _root = root;
            _gateway = gateway;
            _probe = probe;

            // 'test' mode keeps startup provider discovery side-effect-free (no real API key or network
            // needed to boot) — same rationale as the other in-process host tests here.
            Environment.SetEnvironmentVariable("LM_PROVIDER_MODE", "test");
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // Avoids the Vite dev-server auto-spawn (matches every other in-process host test here).
            builder.UseEnvironment("Production");

            // The sandbox MCP transport builds its own HttpClient inside HttpClientTransport and dials
            // BaseUrl directly, so the fake handler below cannot intercept it. A closed loopback port
            // makes that connect fail fast and the agent degrade to "no sandbox tools" — harmless here,
            // because the attach happens after the registration under test and is explicitly non-fatal.
            builder.UseSetting("SandboxGateway:BaseUrl", "http://127.0.0.1:1");
            builder.UseSetting("SandboxGateway:AutoSpawn", "false");
            builder.UseSetting("SandboxGateway:AppId", "lm-thread-registration-test");
            builder.UseSetting("SandboxGateway:WorkspaceBasePath", Path.Combine(_root, "workspace-base"));

            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IConversationStore>();
                services.AddSingleton<IConversationStore>(
                    new FileConversationStore(Path.Combine(_root, "conversations"))
                );

                // Program.cs registers the workspace store as a live INSTANCE rooted at the app's own
                // catalog directory, so this has to be replaced rather than reconfigured — otherwise the
                // workspace this test creates would land in the developer's real catalog.
                services.RemoveAll<IWorkspaceStore>();
                services.AddSingleton<IWorkspaceStore>(new FileWorkspaceStore(Path.Combine(_root, "workspaces")));

                // WorkspaceCatalogCompatibilityService is a plain ctor-injected singleton, so replacing
                // the client it depends on is enough to fake the whole catalog.
                services.RemoveAll<IMarketplaceCatalogClient>();
                services.AddSingleton<IMarketplaceCatalogClient>(new StubCatalogClient());

                // RemoveAll + AddSingleton is last-wins; the AddHostedService wrapper still resolves the
                // replacement lifetime singleton.
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

                // The real probe is the (sealed) agent pool, which cannot be made to report a run in
                // progress without actually streaming one.
                services.RemoveAll<IAgentRunActivityProbe>();
                services.AddSingleton(_probe);

                // The production type, wired to the production dependencies — only the two wait knobs
                // differ, so the timeout path costs milliseconds instead of the 30 s default.
                services.RemoveAll<IWorkspacePluginSelectionService>();
                services.AddSingleton<IWorkspacePluginSelectionService>(sp => new WorkspacePluginSelectionService(
                    sp.GetRequiredService<IWorkspaceStore>(),
                    sp.GetRequiredService<WorkspaceCatalogCompatibilityService>(),
                    sp.GetRequiredService<SandboxSessionRegistry>(),
                    sp.GetRequiredService<IAgentRunActivityProbe>(),
                    sp.GetRequiredService<SandboxGatewayOptions>(),
                    idleWaitTimeout: TimeSpan.FromMilliseconds(400),
                    idlePollInterval: TimeSpan.FromMilliseconds(20)
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

    /// <summary>
    /// Reports a run in progress for one nominated thread, and records every thread it was asked about
    /// so the test can prove the wait loop actually reached it.
    /// </summary>
    private sealed class ThreadAwareActivityProbe : IAgentRunActivityProbe
    {
        private readonly object _gate = new();
        private readonly HashSet<string> _observed = [];

        /// <summary>The thread that reports busy; null until the test starts the "run".</summary>
        public string? BusyThreadId { get; set; }

        public IReadOnlyCollection<string> ObservedThreads
        {
            get
            {
                lock (_gate)
                {
                    return [.. _observed];
                }
            }
        }

        public bool IsRunInProgress(string threadId)
        {
            lock (_gate)
            {
                _ = _observed.Add(threadId);
            }

            return threadId == BusyThreadId;
        }
    }

    /// <summary>One marketplace with one plugin, and plugin filtering supported.</summary>
    private sealed class StubCatalogClient : IMarketplaceCatalogClient
    {
        public Task<MarketplaceCatalog> GetCatalogAsync(
            IReadOnlyList<string>? marketplaces = null,
            CancellationToken ct = default
        ) =>
            Task.FromResult(
                new MarketplaceCatalog(
                    [Marketplace],
                    [new CatalogMarketplace(Marketplace, null, [new CatalogPlugin("code-review", null, "", [], [])])]
                )
                {
                    Capabilities = new MarketplaceCapabilities(true),
                }
            );
    }

    /// <summary>
    /// Minimal in-memory sandbox gateway. Records the session ids handed out and the ones deleted, not
    /// just counts: "no candidate was created" and "the live session was never torn down" are claims
    /// about the outside world that no return value can express.
    /// </summary>
    private sealed class FakeSandboxGateway : HttpMessageHandler
    {
        private readonly object _gate = new();
        private readonly List<string> _createdSessionIds = [];
        private readonly List<string> _deletedSessionIds = [];
        private int _creates;

        public IReadOnlyList<string> CreatedSessionIds
        {
            get
            {
                lock (_gate)
                {
                    return [.. _createdSessionIds];
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

        public int CreateAttempts => Volatile.Read(ref _creates);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;

            if (request.Method == HttpMethod.Delete && path.Contains("/sandboxes/", StringComparison.Ordinal))
            {
                lock (_gate)
                {
                    _deletedSessionIds.Add(path[(path.LastIndexOf('/') + 1)..]);
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            }

            if (request.Method != HttpMethod.Post || !path.EndsWith("/sandboxes", StringComparison.Ordinal))
            {
                // Health probes, liveness GETs and the boot-time context-file reads.
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            }

            var ordinal = Interlocked.Increment(ref _creates);
            var sessionId = $"sess-{ordinal}";
            lock (_gate)
            {
                _createdSessionIds.Add(sessionId);
            }

            var responseBody = $$"""
                { "session_id": "{{sessionId}}", "container_id": "c-{{ordinal}}",
                  "volumes": { "workspace": { "container_path": "/workspace", "read_only": false } } }
                """;
            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
                }
            );
        }
    }
}
