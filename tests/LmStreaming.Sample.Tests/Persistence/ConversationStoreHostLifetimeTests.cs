using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmMultiTurn.Collaboration;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence.Sqlite;
using AchieveAi.LmDotnetTools.LmTestUtils;
using LmStreaming.Sample.Services;
using LmStreaming.Sample.Tests.TestDoubles;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace LmStreaming.Sample.Tests.Persistence;

/// <summary>Exercises the sample's production SQLite registration across the real host boundary.</summary>
public sealed class ConversationStoreHostLifetimeTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Dispose_DrainsFinalUsageTerminalAndBindingWrites_BeforeClosingSqlite(
        bool synchronous,
        bool agentDisposeFails
    )
    {
        const string threadId = "shutdown-thread";
        const string runId = "shutdown-run";
        using var directory = new DatabaseDirectory();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var written = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var factory = new ShutdownHost(
            directory.DatabasePath,
            store => new FakeMultiTurnAgent(threadId)
            {
                ThrowOnDispose = agentDisposeFails,
                RunBehavior = async ct =>
                {
                    using var registration = ct.Register(() => stopping.TrySetResult());
                    entered.TrySetResult();
                    // This writer has passed its cancellation boundary. Only the pool's drain waits for it.
                    await release.Task;
                    try
                    {
                        await WriteFinalStateAsync(store, threadId, runId);
                        written.TrySetResult();
                    }
                    catch (Exception ex)
                    {
                        written.TrySetException(ex);
                        throw;
                    }
                },
            }
        );
        var store = factory.Services.GetRequiredService<IConversationStore>();
        File.Exists(directory.DatabasePath).Should().BeTrue("the production registration must use the isolated path");
        await store.UpdateMetadataAsync(threadId, _ => new ThreadMetadata { ThreadId = threadId, LastUpdated = 1 });
        await ((IRunLifecycleStore)store).RecordRunStartedAsync(
            new RunLifecycleState
            {
                ThreadId = threadId,
                RunId = runId,
                StartedAt = DateTimeOffset.UtcNow,
            }
        );
        var pool = factory.Services.GetRequiredService<MultiTurnAgentPool>();
        _ = pool.GetOrCreateAgent(threadId, SystemChatModes.GetById(SystemChatModes.DefaultModeId)!);

        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await factory.Host.StopAsync();
            var dispose = Task.Run(async () =>
            {
                if (synchronous)
                    factory.Host.Dispose();
                else
                    await factory.DisposeAsync();
            });
            await stopping.Task.WaitAsync(TimeSpan.FromSeconds(30));
            // RunAsync can also initiate disposal after StopAsync. A second Dispose call may return
            // immediately, so its task's timing is not proof. The held writer must still have storage.
            await store.UpdateMetadataAsync(threadId, existing => existing!);
            release.TrySetResult();
            await written.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await dispose.WaitAsync(TimeSpan.FromSeconds(30));

            // Repeated host disposal must neither repeat the writes nor leave the original store usable.
            await factory.DisposeAsync();
            await Wait.UntilAsync(
                async () =>
                {
                    try
                    {
                        _ = await store.LoadMetadataAsync(threadId);
                        return false;
                    }
                    catch (ObjectDisposedException)
                    {
                        return true;
                    }
                },
                "the host's SQLite owner has closed after the final writer",
                TimeSpan.FromSeconds(30)
            );

            await using var reopened = new SqliteConversationStore(directory.DatabasePath);
            var usage = await reopened.LoadUsageAsync(threadId);
            usage.Should().NotBeNull();
            usage!.Completeness.Should().Be(UsageCompleteness.Complete);
            usage.Records.Should().ContainSingle().Which.InputTokens.Should().Be(17);
            var terminal = await reopened.LoadRunLifecycleAsync(runId);
            terminal.Should().NotBeNull();
            terminal!.Phase.Should().Be(RunLifecyclePhase.Terminal);
            terminal.Outcome.Should().Be("interrupted");
            var binding = await ConversationAgentBindingProjection.LoadAsync(reopened, threadId);
            binding.Should().NotBeNull();
            binding!.Session.Should().Be(7);
            binding.RootAgentId.Should().Be(threadId);
        }
        finally
        {
            release.TrySetResult();
        }
    }

    private static async Task WriteFinalStateAsync(IConversationStore store, string threadId, string runId)
    {
        var record = new UsageRecord
        {
            LogicalCallId = "shutdown-call",
            ProviderAttemptId = "shutdown-attempt",
            RootConversationId = threadId,
            RequestedModel = "test-model",
            Revision = 1,
            InputTokens = 17,
        };
        await ((IConversationUsageStore)store).AppendUsageAsync(
            ConversationUsageAggregate.Fold(threadId, [record], 1, UsageCompleteness.Complete),
            [record]
        );
        (await ((IRunLifecycleStore)store).TryMarkRunTerminalAsync(runId, "interrupted", 1, DateTimeOffset.UtcNow))
            .Should()
            .BeTrue();
        await ConversationAgentBindingProjection.SaveAsync(
            store,
            new AgentIdentityBindingSet
            {
                CollaborationId = threadId,
                RootAgentId = threadId,
                CapturedAtUtc = DateTimeOffset.UtcNow,
                Session = 7,
            }
        );
    }

    private sealed class ShutdownHost : WebApplicationFactory<Program>
    {
        private readonly string? _previousProviderMode = Environment.GetEnvironmentVariable("LM_PROVIDER_MODE");
        private readonly string? _previousEnvFile = Environment.GetEnvironmentVariable("LMSTREAMING_ENV_FILE");
        private readonly Func<IConversationStore, FakeMultiTurnAgent>? _createAgent;

        public ShutdownHost(string databasePath, Func<IConversationStore, FakeMultiTurnAgent>? createAgent = null)
        {
            DatabasePath = databasePath;
            _createAgent = createAgent;
            var emptyEnvPath = Path.Combine(Path.GetDirectoryName(DatabasePath)!, ".env");
            Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
            File.WriteAllText(emptyEnvPath, string.Empty);
            Environment.SetEnvironmentVariable("LMSTREAMING_ENV_FILE", emptyEnvPath);
            Environment.SetEnvironmentVariable("LM_PROVIDER_MODE", "test");
        }

        public string DatabasePath { get; }
        public IHost Host { get; private set; } = null!;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            // UseSetting reaches the minimal host BEFORE Program captures these paths. A later
            // ConfigureAppConfiguration callback alone does not redirect those factory closures.
            builder.UseSetting("LmStreaming:ConversationDbPath", DatabasePath);
            builder.UseSetting(
                "LmStreaming:NotifyWaitDbPath",
                Path.Combine(Path.GetDirectoryName(DatabasePath)!, "notify-waits.db")
            );
            builder.UseSetting(
                "Identity:DatabasePath",
                Path.Combine(Path.GetDirectoryName(DatabasePath)!, "identity.db")
            );
            builder.UseSetting(
                "Auth:TokenStoreDir",
                Path.Combine(Path.GetDirectoryName(DatabasePath)!, "oauth-tokens")
            );
            builder.ConfigureAppConfiguration(
                (_, configuration) =>
                {
                    // Never inherit operator appsettings, token paths or gateway settings in this fixture.
                    configuration.Sources.Clear();
                    configuration.AddInMemoryCollection(
                        new Dictionary<string, string?>
                        {
                            ["LmStreaming:ConversationDbPath"] = DatabasePath,
                            ["LmStreaming:NotifyWaitDbPath"] = Path.Combine(
                                Path.GetDirectoryName(DatabasePath)!,
                                "notify-waits.db"
                            ),
                            ["Identity:DatabasePath"] = Path.Combine(
                                Path.GetDirectoryName(DatabasePath)!,
                                "identity.db"
                            ),
                            ["Auth:TokenStoreDir"] = Path.Combine(Path.GetDirectoryName(DatabasePath)!, "oauth-tokens"),
                        }
                    );
                }
            );
            builder.ConfigureTestServices(services =>
            {
                // Keep the real web host and checkpoint service, but no gateway, token hydrator,
                // mock-provider listener or other unrelated startup job. A future accidental provider
                // resolution fails locally rather than probing the developer's CLI credentials.
                foreach (
                    var descriptor in services
                        .Where(descriptor =>
                            descriptor.ServiceType == typeof(IHostedService)
                            && descriptor.ImplementationType?.FullName
                                != "Microsoft.AspNetCore.Hosting.GenericWebHostService"
                        )
                        .ToArray()
                )
                    services.Remove(descriptor);
                services.AddHostedService(sp =>
                    ActivatorUtilities.CreateInstance<SqliteWalCheckpointService>(sp, DatabasePath)
                );
                services.RemoveAll<ProviderRegistry>();
                services.AddSingleton<ProviderRegistry>(_ =>
                    throw new InvalidOperationException("Provider discovery is outside this shutdown fixture.")
                );
                services.AddDataProtection().UseEphemeralDataProtectionProvider();

                // Keep the production store registrations. Only the agents need a deterministic fake.
                services.RemoveAll<MultiTurnAgentPool>();
                services.AddSingleton(sp => new MultiTurnAgentPool(
                    context => new MultiTurnAgentPool.AgentCreationResult(
                        _createAgent?.Invoke(sp.GetRequiredService<IConversationStore>())
                            ?? new FakeMultiTurnAgent(context.ThreadId)
                    ),
                    providerRegistry: null,
                    conversationStore: sp.GetRequiredService<IConversationStore>(),
                    NullLogger<MultiTurnAgentPool>.Instance
                ));
            });
        }

        protected override IHost CreateHost(IHostBuilder builder)
        {
            Host = base.CreateHost(builder);
            return Host;
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                Environment.SetEnvironmentVariable("LM_PROVIDER_MODE", _previousProviderMode);
                Environment.SetEnvironmentVariable("LMSTREAMING_ENV_FILE", _previousEnvFile);
            }
        }
    }

    private sealed class DatabaseDirectory : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), $"host-store-lifetime-{Guid.NewGuid():N}");
        public string DatabasePath => Path.Combine(_path, "conversations.db");

        public void Dispose()
        {
            SqliteConnectionFactory.ClearPool(DatabasePath);
            SqliteConnectionFactory.ClearPool(Path.Combine(_path, "notify-waits.db"));
            SqliteConnectionFactory.ClearPool(Path.Combine(_path, "identity.db"));
            if (Directory.Exists(_path))
                Directory.Delete(_path, recursive: true);
        }
    }
}
