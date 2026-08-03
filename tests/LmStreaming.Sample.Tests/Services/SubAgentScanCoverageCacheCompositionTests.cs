using LmStreaming.Sample.Configuration;
using LmStreaming.Sample.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LmStreaming.Sample.Tests.Services;

/// <summary>
/// PR #245 review (LOW): proves the host's <c>AgentCollaboration:MaxPersistedHierarchyEntries</c>
/// configuration knob actually reaches the <see cref="SubAgentScanCoverageCache"/> singleton
/// <c>Program.cs</c> constructs from it (<c>sp.GetRequiredService&lt;AgentCollaborationHostOptions&gt;()
/// .MaxPersistedHierarchyEntries</c>), rather than the cache silently falling back to
/// <see cref="SubAgentScanCoverageCache.DefaultCapacity"/>. <see cref="SubAgentScanCoverageCacheTests"/>
/// already proves the cache's own eviction bookkeeping is correct in isolation; a unit test built
/// directly against the class can never catch a composition-root wiring regression (e.g. the factory
/// accidentally reading <see cref="SubAgentScanCoverageCache.DefaultCapacity"/> instead of the bound
/// option, or binding the wrong section). This test boots the real <c>Program</c> host — the same
/// pattern <c>NotifyWaitDurableRestoreTests</c> uses to prove config-to-DI wiring — with a
/// deliberately tiny configured capacity and proves the RESOLVED singleton evicts at exactly that
/// size.
/// </summary>
public sealed class SubAgentScanCoverageCacheCompositionTests
{
    private const int ConfiguredCapacity = 2;

    /// <summary>
    /// Boots the real <c>Program</c> with a test-supplied <c>MaxPersistedHierarchyEntries</c>. Only
    /// <see cref="WebApplicationFactory{TEntryPoint}.Services"/> is needed (no HTTP), matching
    /// <c>NotifyWaitDurableRestoreTests</c>'s <c>NotifyRestoreWebAppFactory</c>.
    /// </summary>
    private sealed class CollaborationCacheWebAppFactory : WebApplicationFactory<Program>
    {
        private readonly string _conversationsPath;

        public CollaborationCacheWebAppFactory(string conversationsPath)
        {
            _conversationsPath = conversationsPath;

            // 'test' mode keeps startup provider discovery side-effect-free (no real API key/network
            // needed to boot the host) — same rationale as NotifyRestoreWebAppFactory.
            Environment.SetEnvironmentVariable("LM_PROVIDER_MODE", "test");
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // Avoids the Vite dev-server auto-spawn (matches every other in-process host test here).
            builder.UseEnvironment("Production");

            // The one setting under test. ConfiguredCapacity is far below
            // SubAgentScanCoverageCache.DefaultCapacity, so if Program.cs's singleton factory ever
            // regressed to ignoring this option (falling back to the default), the eviction assertion
            // below would fail to trigger and the test would catch it.
            builder.UseSetting(
                $"{AgentCollaborationHostOptions.SectionName}:{nameof(AgentCollaborationHostOptions.MaxPersistedHierarchyEntries)}",
                ConfiguredCapacity.ToString());

            builder.ConfigureTestServices(services =>
            {
                // Isolates conversation storage to this test's temp dir. Irrelevant to the assertions
                // below, but keeps the run from touching the shared bin-output conversations folder.
                services.RemoveAll<IConversationStore>();
                services.AddSingleton<IConversationStore>(new FileConversationStore(_conversationsPath));
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

    [Fact]
    public async Task Host_BindsConfiguredMaxPersistedHierarchyEntries_IntoTheRegisteredScanCoverageCacheSingleton()
    {
        var root = Path.Combine(
            Path.GetTempPath(), "lmstreaming-collab-cache-composition-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var host = new CollaborationCacheWebAppFactory(Path.Combine(root, "conversations"));

            var options = host.Services.GetRequiredService<AgentCollaborationHostOptions>();
            options.MaxPersistedHierarchyEntries.Should().Be(
                ConfiguredCapacity,
                "the test-supplied AgentCollaboration:MaxPersistedHierarchyEntries setting must bind onto the host options");

            var cache = host.Services.GetRequiredService<SubAgentScanCoverageCache>();

            // Record one more distinct thread than the configured capacity: if the DI factory had
            // silently used SubAgentScanCoverageCache.DefaultCapacity (much larger than 2) instead of
            // the bound option, every one of these would still be retrievable and the assertion below
            // would fail — that failure is exactly what would expose a composition-root regression.
            const int distinctThreads = ConfiguredCapacity + 1;
            var owners = new object[distinctThreads];
            for (var i = 0; i < distinctThreads; i++)
            {
                owners[i] = new object();
                cache.RecordRecovered(
                    $"thread-{i}",
                    owners[i],
                    [new SubAgentSummary
                    {
                        AgentId = $"child-{i}",
                        Template = "worker",
                        Task = "task",
                        Status = "completed",
                        ThreadId = $"subagent-child-{i}",
                    }],
                    cache.CaptureWriteEpoch());
            }

            var survivorCount = Enumerable.Range(0, distinctThreads)
                .Count(i => cache.TryGetRecovered($"thread-{i}", owners[i], out _));

            survivorCount.Should().Be(
                ConfiguredCapacity,
                "the singleton resolved from DI must evict down to the CONFIGURED capacity "
                    + $"({ConfiguredCapacity}), not {SubAgentScanCoverageCache.DefaultCapacity} (the "
                    + "library default) — proving Program.cs actually threads "
                    + "AgentCollaboration:MaxPersistedHierarchyEntries into this singleton");
        }
        finally
        {
            TryDeleteDir(root);
        }
    }

    private static void TryDeleteDir(string root)
    {
        try
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup; a leftover temp dir must not fail the test.
        }
    }
}
