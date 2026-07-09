using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence.Sqlite;
using AchieveAi.LmDotnetTools.LmStreaming.Sample.Triggers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LmStreaming.Sample.Tests.Triggers;

/// <summary>
/// Issue #145 integration test: proves the sample host's durable notify-wait wiring
/// (<c>Program.cs</c> DI registration of <see cref="INotifyWaitStore"/> + the <c>TriggerOptions</c>
/// <c>with</c>-attach in the pool's agent factory) actually persists notify-watcher arming records
/// and reconciles them across a simulated restart against REAL on-disk SQLite — the one gap no
/// existing in-memory test covers.
/// </summary>
/// <remarks>
/// <para>
/// A "restart" is simulated as: (1) seed the durable notify-wait store + thread metadata on disk
/// directly (write side), then (2) boot the real <c>Program</c> host over that same on-disk
/// state and open the thread. Booting the host resolves the DI-registered store and drives the pool
/// agent factory, whose <c>RunAsync</c> auto-recovery calls
/// <c>TriggerRuntime.RestoreNotifyWaitsAsync</c> — the wiring under test. Seeding directly (rather
/// than driving the mock model to arm a wait) keeps the test focused on the new host wiring; the
/// model-driven arm path is already covered at the loop level.
/// </para>
/// <para>
/// Thread metadata MUST be seeded: <c>MultiTurnAgentBase.RecoverAsync</c> only invokes
/// <c>OnThreadRecoveredAsync</c> (→ notify restore) when the thread has metadata; a metadata-less
/// thread short-circuits and restore never runs.
/// </para>
/// <para>
/// Tests are serialized (see <c>AssemblyInfo.cs</c> <c>DisableTestParallelization</c>) because the
/// host reads the process-global <c>LM_PROVIDER_MODE</c>.
/// </para>
/// </remarks>
public sealed class NotifyWaitDurableRestoreTests
{
    private const string ScheduleWaitId = "call_schedule_1";
    private const string ProcessWaitId = "call_process_1";
    private const string ProcessBarrierWaitId = "call_process_barrier";

    /// <summary>
    /// Boots the real <c>Program</c> over an isolated notify-wait db + conversation store.
    /// Uses the default in-memory <c>TestServer</c> (we only need <see cref="WebApplicationFactory{TEntryPoint}.Services"/>,
    /// not HTTP), so — unlike <c>BrowserWebAppFactory</c> — no Kestrel swap / cast workaround is needed.
    /// </summary>
    private sealed class NotifyRestoreWebAppFactory : WebApplicationFactory<Program>
    {
        private readonly string _notifyDbPath;
        private readonly string _conversationsPath;

        public NotifyRestoreWebAppFactory(string notifyDbPath, string conversationsPath)
        {
            _notifyDbPath = notifyDbPath;
            _conversationsPath = conversationsPath;

            // Program.cs reads LM_PROVIDER_MODE at the top level, before any host-builder callback.
            // 'test' selects the scripted agent factory — the agent never needs a script here because
            // RunAsync runs recovery (and thus notify restore) before it processes any input.
            Environment.SetEnvironmentVariable("LM_PROVIDER_MODE", "test");
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // Production avoids the Vite dev-server auto-spawn (matches BrowserWebAppFactory / DaemonWebAppFactory).
            builder.UseEnvironment("Production");

            // Point the #145 notify-wait store at this test's isolated db (mirrors
            // CodeReviewDaemon.Sample's CodeReviewDaemon:DatabasePath test-override via UseSetting).
            builder.UseSetting("LmStreaming:NotifyWaitDbPath", _notifyDbPath);

            builder.ConfigureTestServices(services =>
            {
                // Isolate the conversation store to this test's temp dir so the seeded metadata (which
                // gates recovery) is what the host recovers. Same override seam BrowserWebAppFactory uses.
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
    public async Task ProcessNotifyWait_UnregisteredKindAfterRestart_DeliversTriggerLostAndClearsRow()
    {
        var (root, conversationsPath, notifyDbPath) = NewTempPaths();
        var threadId = "notify-restore-process-" + Guid.NewGuid().ToString("N");
        try
        {
            var now = NowMs();
            // A gateway-less host builds SampleTriggerRegistrations with sandboxEnabled:false, so the
            // 'process' kind is never registered. Restore therefore hits the unregistered-kind branch,
            // delivers trigger_lost_on_restart, and clears the row once the terminal envelope is accepted.
            await SeedAsync(conversationsPath, notifyDbPath, threadId,
            [
                new NotifyWaitRecord(
                    WaitId: ProcessWaitId,
                    ThreadId: threadId,
                    Kind: ProcessTriggerSource.KindName,
                    Args: "{\"handle\":\"proc-abc\"}",
                    Label: null,
                    MaxFires: null,
                    FiresSoFar: 0,
                    TimeoutAtUnixMs: now + 3_600_000,
                    ArmedAtUnixMs: now,
                    Status: "active"),
            ]);

            await using var host = new NotifyRestoreWebAppFactory(notifyDbPath, conversationsPath);
            var notifyStore = host.Services.GetRequiredService<INotifyWaitStore>();
            var pool = host.Services.GetRequiredService<MultiTurnAgentPool>();
            var mode = SystemChatModes.GetById(SystemChatModes.DefaultModeId)!;

            // Opening the thread runs the real agent-factory closure (the #145 wiring) and its
            // RunAsync auto-recovery → RestoreNotifyWaitsAsync.
            _ = pool.GetOrCreateAgent(threadId, mode, requestedProviderId: "test", requestResponseDumpFileName: null);

            var active = await PollActiveUntilAsync(notifyStore, threadId, rows => rows.Count == 0);

            active.Should().BeEmpty(
                "the process kind is not registered in a gateway-less host, so restore delivers " +
                "trigger_lost_on_restart and clears the durable row");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TryDeleteDir(root);
        }
    }

    [Fact]
    public async Task ScheduleNotifyWait_RestorableKind_SurvivesRestartAndReArms()
    {
        var (root, conversationsPath, notifyDbPath) = NewTempPaths();
        var threadId = "notify-restore-schedule-" + Guid.NewGuid().ToString("N");
        try
        {
            var now = NowMs();
            await SeedAsync(conversationsPath, notifyDbPath, threadId,
            [
                // Restorable: the schedule kind is always registered and re-arms deterministically from
                // (interval, armedAt). A long interval guarantees it never fires during the test, so the
                // row stays active after a successful re-arm.
                new NotifyWaitRecord(
                    WaitId: ScheduleWaitId,
                    ThreadId: threadId,
                    Kind: ScheduleTriggerSource.KindName,
                    Args: "{\"intervalSeconds\":86400}",
                    Label: null,
                    MaxFires: null,
                    FiresSoFar: 0,
                    TimeoutAtUnixMs: now + 3_600_000,
                    ArmedAtUnixMs: now,
                    Status: "active"),

                // Non-restorable barrier: its deletion is the positive signal that restore ran this pass.
                // Both rows are processed in one synchronous RestoreNotifyWaitsAsync loop, so once the
                // barrier is gone the schedule row has also been processed — making the schedule-survival
                // assertion race-free (rather than indistinguishable from "restore hasn't run yet").
                new NotifyWaitRecord(
                    WaitId: ProcessBarrierWaitId,
                    ThreadId: threadId,
                    Kind: ProcessTriggerSource.KindName,
                    Args: "{\"handle\":\"proc-xyz\"}",
                    Label: null,
                    MaxFires: null,
                    FiresSoFar: 0,
                    TimeoutAtUnixMs: now + 3_600_000,
                    ArmedAtUnixMs: now,
                    Status: "active"),
            ]);

            await using var host = new NotifyRestoreWebAppFactory(notifyDbPath, conversationsPath);
            var notifyStore = host.Services.GetRequiredService<INotifyWaitStore>();
            var pool = host.Services.GetRequiredService<MultiTurnAgentPool>();
            var mode = SystemChatModes.GetById(SystemChatModes.DefaultModeId)!;

            _ = pool.GetOrCreateAgent(threadId, mode, requestedProviderId: "test", requestResponseDumpFileName: null);

            // Converge to exactly {schedule}: the process barrier is gone (restore ran) AND the schedule
            // row is still active (it re-armed rather than being lost).
            var active = await PollActiveUntilAsync(
                notifyStore,
                threadId,
                rows => rows.Count == 1 && rows[0].WaitId == ScheduleWaitId);

            active.Select(r => r.WaitId).Should().BeEquivalentTo(
                [ScheduleWaitId],
                "the restorable schedule kind re-arms and survives the restart while the non-restorable " +
                "process barrier is cleared");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TryDeleteDir(root);
        }
    }

    // --- helpers -----------------------------------------------------------------------------

    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static (string Root, string ConversationsPath, string NotifyDbPath) NewTempPaths()
    {
        var root = Path.Combine(Path.GetTempPath(), "lm-notify-restore-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return (root, Path.Combine(root, "conversations"), Path.Combine(root, "notify-waits.db"));
    }

    /// <summary>
    /// Seeds the on-disk conversation metadata (required for recovery to fire) and the durable notify
    /// rows directly, then releases the SQLite connection pool's file handle so the host opens the db
    /// cleanly (needed on Windows, where the shared-cache pool otherwise pins the file).
    /// </summary>
    private static async Task SeedAsync(
        string conversationsPath,
        string notifyDbPath,
        string threadId,
        IReadOnlyList<NotifyWaitRecord> rows)
    {
        var conversationStore = new FileConversationStore(conversationsPath);
        await conversationStore.SaveMetadataAsync(
            threadId,
            new ThreadMetadata { ThreadId = threadId, LastUpdated = NowMs(), LatestRunId = "run_seed" });

        var factory = new SqliteConnectionFactory(notifyDbPath);
        try
        {
            var store = new SqliteNotifyWaitStore(factory);
            foreach (var row in rows)
            {
                await store.SaveAsync(row);
            }
        }
        finally
        {
            await factory.DisposeAsync();
        }

        SqliteConnection.ClearAllPools();
    }

    /// <summary>
    /// Polls <see cref="INotifyWaitStore.LoadActiveAsync"/> until <paramref name="predicate"/> holds
    /// or the bounded budget (~10s) elapses; returns the last-observed active set for assertion.
    /// </summary>
    private static async Task<IReadOnlyList<NotifyWaitRecord>> PollActiveUntilAsync(
        INotifyWaitStore store,
        string threadId,
        Func<IReadOnlyList<NotifyWaitRecord>, bool> predicate)
    {
        IReadOnlyList<NotifyWaitRecord> active = [];
        for (var i = 0; i < 200; i++)
        {
            active = await store.LoadActiveAsync(threadId);
            if (predicate(active))
            {
                break;
            }

            await Task.Delay(50);
        }

        return active;
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
