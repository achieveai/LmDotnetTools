using System.Runtime.CompilerServices;
using AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Agents;
using AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Configuration;
using AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Models;
using AchieveAi.LmDotnetTools.LmCore.AgentRuntime;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using AchieveAi.LmDotnetTools.ProcessLauncher;
using FluentAssertions;
using Xunit;

namespace LmMultiTurn.Tests;

/// <summary>
/// A Claude-backed conversation keeps its memory in the CLI session, not in the prompt: the loop
/// sends only the new input on each turn. So when the host restarts, or the agent pool evicts and
/// rebuilds the loop, the rebuilt loop must pass <c>--resume &lt;sessionId&gt;</c> for the session
/// recorded in <see cref="ThreadMetadata.SessionMappings"/>; otherwise the CLI starts a fresh
/// session and the agent silently forgets the whole conversation.
///
/// Every test here observes the real argv at the process-launch seam: the loop builds a real
/// <see cref="ClaudeAgentSdkClient"/>, and a recording <see cref="IProcessLauncher"/> captures the
/// arguments and refuses to spawn. Recovery goes through <see cref="MultiTurnAgentBase.RunAsync"/>'s
/// own startup recovery, which is the path the agent pool uses (it never calls RecoverAsync).
/// </summary>
public class ClaudeAgentLoopSessionRestoreTests
{
    [Fact]
    public async Task RebuiltLoop_ResumesSessionRecordedByPreviousLoop()
    {
        // The whole restart, end to end: the first loop learns its session id from the SDK and
        // persists it; a second loop over the same store (the restarted host) must resume it.
        const string threadId = "restart-roundtrip";
        const string sessionId = "sess-before-restart";
        var store = new InMemoryConversationStore();

        await using (
            var first = new ClaudeAgentLoop(
                claudeOptions: new ClaudeAgentSdkOptions { Mode = ClaudeAgentSdkMode.OneShot, MaxTurnsPerRun = 5 },
                mcpServers: null,
                threadId: threadId,
                store: store,
                clientFactory: (_, _) => new ScriptedClient(sessionId)
            )
        )
        {
            await DriveOneScriptedRunAsync(first);
        }

        var persisted = await store.LoadMetadataAsync(threadId);
        persisted!.SessionMappings.Should().ContainKey($"claude-sdk:{sessionId}", "precondition: run one persisted it");

        var argv = await CaptureFirstLaunchArgvAsync(threadId, store);

        ResumeIdOf(argv).Should().Be(sessionId, "a rebuilt loop must continue the CLI session it already had");
        argv.Should().NotContain("--session-id");
    }

    [Fact]
    public async Task Recovery_PrefersTheMappingForLatestRunId()
    {
        // The stale mapping is listed LAST, so a "take the last entry" rule would pick it. Only the
        // LatestRunId match identifies the session the conversation was actually in.
        const string threadId = "restore-prefers-latest-run";
        var store = await StoreWithMappingsAsync(
            threadId,
            latestRunId: "run-2",
            ("claude-sdk:sess-current", "run-2"),
            ("claude-sdk:sess-stale", "run-1")
        );

        var argv = await CaptureFirstLaunchArgvAsync(threadId, store);

        ResumeIdOf(argv).Should().Be("sess-current");
    }

    [Fact]
    public async Task Recovery_WithoutLatestRunMatch_FallsBackToLastClaudeMapping()
    {
        // No mapping carries LatestRunId, so the last-written claude-sdk entry is the best record
        // left. A mapping owned by another provider is never mistaken for a Claude session.
        const string threadId = "restore-fallback";
        var store = await StoreWithMappingsAsync(
            threadId,
            latestRunId: "run-9",
            ("claude-sdk:sess-older", "run-1"),
            ("claude-sdk:sess-newer", "run-2"),
            ("other-provider:sess-foreign", "run-3")
        );

        var argv = await CaptureFirstLaunchArgvAsync(threadId, store);

        ResumeIdOf(argv).Should().Be("sess-newer");
    }

    [Fact]
    public async Task Recovery_ConstructorSeedWinsOverStoredMapping()
    {
        const string threadId = "restore-ctor-seed-wins";
        var store = await StoreWithMappingsAsync(threadId, latestRunId: "run-1", ("claude-sdk:sess-stored", "run-1"));

        var argv = await CaptureFirstLaunchArgvAsync(threadId, store, initialSessionId: "sess-from-caller");

        ResumeIdOf(argv).Should().Be("sess-from-caller", "an explicit caller seed is a deliberate choice");
    }

    [Fact]
    public async Task Recovery_WithoutClaudeMapping_StartsFreshSession()
    {
        // Metadata exists (so recovery runs its hooks) but records no Claude session.
        const string threadId = "restore-no-mapping";
        var store = await StoreWithMappingsAsync(threadId, latestRunId: "run-1", ("other-provider:sess-x", "run-1"));

        var argv = await CaptureFirstLaunchArgvAsync(threadId, store);

        argv.Should().NotContain("--resume");
        argv.Should().NotContain("--session-id");
    }

    [Fact]
    public async Task Recovery_WithAssignSessionId_KeepsHostAssignedSession()
    {
        // AssignSessionId is the host taking explicit control of session identity. Like the
        // constructor mutex with initialSessionId, the loop never emits both flags, and the
        // host's explicit choice outranks an implicit store restore.
        const string threadId = "restore-assign-wins";
        const string assigned = "00000000-0000-4000-8000-00000000beef";
        var store = await StoreWithMappingsAsync(threadId, latestRunId: "run-1", ("claude-sdk:sess-stored", "run-1"));

        var argv = await CaptureFirstLaunchArgvAsync(
            threadId,
            store,
            configure: o => o with { AssignSessionId = assigned }
        );

        argv.Should().NotContain("--resume");
        SessionIdFlagOf(argv).Should().Be(assigned);
    }

    [Fact]
    public async Task Recovery_WithSessionPersistenceDisabled_DoesNotResume()
    {
        // With persistence disabled the CLI never wrote the session to disk, so resuming it would
        // turn every turn into a hard "no conversation found" failure.
        const string threadId = "restore-persistence-off";
        var store = await StoreWithMappingsAsync(threadId, latestRunId: "run-1", ("claude-sdk:sess-stored", "run-1"));

        var argv = await CaptureFirstLaunchArgvAsync(
            threadId,
            store,
            configure: o => o with { DisableSessionPersistence = true }
        );

        argv.Should().NotContain("--resume");
    }

    [Fact]
    public async Task Recovery_WithStagedProfile_DoesNotResume()
    {
        // A profile with skills points CLAUDE_CONFIG_DIR, where the CLI keeps its sessions, at a
        // new temp directory per loop. The previous loop's session cannot be there, so resuming it
        // would fail every turn instead of starting fresh.
        const string threadId = "restore-staged-profile";
        var store = await StoreWithMappingsAsync(threadId, latestRunId: "run-1", ("claude-sdk:sess-stored", "run-1"));

        var argv = await CaptureFirstLaunchArgvAsync(
            threadId,
            store,
            configure: o => o with { Profile = new AgentRuntimeProfile { Skills = [AgentSkill.Inline("s", "body")] } }
        );

        argv.Should().NotContain("--resume");
    }

    [Fact]
    public async Task Recovery_UsesTheMetadataRecoveryAlreadyLoaded()
    {
        // Base recovery reads the metadata once. A second read that fails (a transient store
        // fault) must not turn a successful recovery into a fresh CLI session.
        const string threadId = "restore-second-read-fails";
        var store = await StoreWithMappingsAsync(threadId, latestRunId: "run-1", ("claude-sdk:sess-stored", "run-1"));

        var argv = await CaptureFirstLaunchArgvAsync(threadId, new FailAfterFirstMetadataReadStore(store));

        ResumeIdOf(argv).Should().Be("sess-stored");
    }

    private static string? ResumeIdOf(IReadOnlyList<string> argv) => ValueAfter(argv, "--resume");

    private static string? SessionIdFlagOf(IReadOnlyList<string> argv) => ValueAfter(argv, "--session-id");

    private static string? ValueAfter(IReadOnlyList<string> argv, string flag)
    {
        var index = argv.ToList().IndexOf(flag);
        return index >= 0 && index + 1 < argv.Count ? argv[index + 1] : null;
    }

    private static async Task<InMemoryConversationStore> StoreWithMappingsAsync(
        string threadId,
        string latestRunId,
        params (string Key, string RunId)[] mappings
    )
    {
        var store = new InMemoryConversationStore();
        await store.SaveMetadataAsync(
            threadId,
            new ThreadMetadata
            {
                ThreadId = threadId,
                LatestRunId = latestRunId,
                LastUpdated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                SessionMappings = mappings.ToDictionary(m => m.Key, m => m.RunId),
            }
        );
        return store;
    }

    /// <summary>
    /// Builds a loop over <paramref name="store"/> with the real SDK client, sends one input, and
    /// returns the argv the client handed to the process launcher. No process is started.
    /// </summary>
    private static async Task<IReadOnlyList<string>> CaptureFirstLaunchArgvAsync(
        string threadId,
        IConversationStore store,
        string? initialSessionId = null,
        Func<ClaudeAgentSdkOptions, ClaudeAgentSdkOptions>? configure = null
    )
    {
        var launcher = new RecordingLauncher();
        var options = new ClaudeAgentSdkOptions
        {
            Mode = ClaudeAgentSdkMode.OneShot,
            MaxTurnsPerRun = 5,
            CliPath = "/fake/cli.js",
            ProcessLauncher = launcher,
        };
        options = configure?.Invoke(options) ?? options;

        var loop = new ClaudeAgentLoop(
            claudeOptions: options,
            mcpServers: null,
            threadId: threadId,
            store: store,
            initialSessionId: initialSessionId
        );

        using var cts = new CancellationTokenSource();
        _ = loop.RunAsync(cts.Token);
        try
        {
            await loop.SendAsync([new TextMessage { Text = "after restart", Role = Role.User }], ct: cts.Token);
            return await launcher.Launched.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            await cts.CancelAsync();

            // The refused launch faults the run loop by design; stop and dispose surface that
            // fault. Anything other than the recorder's own refusal still fails the test.
            try
            {
                await loop.StopAsync();
            }
            catch (ProcessLauncherException) { }

            try
            {
                await loop.DisposeAsync();
            }
            catch (ProcessLauncherException) { }
        }
    }

    private static async Task DriveOneScriptedRunAsync(ClaudeAgentLoop loop)
    {
        using var cts = new CancellationTokenSource();
        _ = loop.RunAsync(cts.Token);

        var input = new UserInput([new TextMessage { Text = "hi", Role = Role.User }], InputId: "first-input");
        await foreach (var _ in loop.ExecuteRunAsync(input, cts.Token).WithCancellation(cts.Token))
        {
            // Drain: the run's completion is what persists the session mapping.
        }

        await cts.CancelAsync();
        await loop.StopAsync();
    }

    /// <summary>Records the first launch's argv and never spawns a process.</summary>
    private sealed class RecordingLauncher : IProcessLauncher
    {
        public TaskCompletionSource<IReadOnlyList<string>> Launched { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IProcessHandle> LaunchAsync(
            ProcessLaunchRequest request,
            CancellationToken cancellationToken = default
        )
        {
            _ = Launched.TrySetResult(request.Arguments);
            throw new ProcessLauncherException("recording launcher: never spawns");
        }
    }

    /// <summary>Serves the first metadata read, then fails every later one.</summary>
    private sealed class FailAfterFirstMetadataReadStore(IConversationStore inner) : IConversationStore
    {
        private int _metadataReads;

        public Task<ThreadMetadata?> LoadMetadataAsync(string threadId, CancellationToken ct = default) =>
            Interlocked.Increment(ref _metadataReads) == 1
                ? inner.LoadMetadataAsync(threadId, ct)
                : throw new InvalidOperationException("injected: second metadata read failed");

        public Task AppendMessagesAsync(
            string threadId,
            IReadOnlyList<PersistedMessage> messages,
            CancellationToken ct = default
        ) => inner.AppendMessagesAsync(threadId, messages, ct);

        public Task<IReadOnlyList<PersistedMessage>> LoadMessagesAsync(
            string threadId,
            CancellationToken ct = default
        ) => inner.LoadMessagesAsync(threadId, ct);

        public Task ReplaceMessageAsync(
            string threadId,
            PersistedMessage replacement,
            CancellationToken ct = default
        ) => inner.ReplaceMessageAsync(threadId, replacement, ct);

        public Task SaveMetadataAsync(string threadId, ThreadMetadata metadata, CancellationToken ct = default) =>
            inner.SaveMetadataAsync(threadId, metadata, ct);

        public Task UpdateMetadataAsync(
            string threadId,
            Func<ThreadMetadata?, ThreadMetadata> update,
            CancellationToken ct = default
        ) => inner.UpdateMetadataAsync(threadId, update, ct);

        public Task DeleteThreadAsync(string threadId, CancellationToken ct = default) =>
            inner.DeleteThreadAsync(threadId, ct);

        public Task<IReadOnlyList<ThreadMetadata>> ListThreadsAsync(
            int limit = 50,
            int offset = 0,
            ConversationListOptions? options = null,
            CancellationToken ct = default
        ) => inner.ListThreadsAsync(limit, offset, options, ct);
    }

    /// <summary>
    /// Stands in for the CLI in the first (pre-restart) loop: reports <paramref name="sessionId"/>
    /// the way OneShot does, through <see cref="IClaudeAgentSdkClient.CurrentSession"/>.
    /// </summary>
    private sealed class ScriptedClient(string sessionId) : IClaudeAgentSdkClient
    {
        public bool IsRunning { get; private set; }

        public SessionInfo? CurrentSession { get; private set; }

        public ClaudeAgentSdkRequest? LastRequest { get; private set; }

        public Task StartAsync(ClaudeAgentSdkRequest request, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            IsRunning = true;
            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<IMessage> SendMessagesAsync(
            IEnumerable<IMessage> messages,
            [EnumeratorCancellation] CancellationToken cancellationToken = default
        )
        {
            CurrentSession = new SessionInfo
            {
                SessionId = sessionId,
                CreatedAt = DateTime.UtcNow,
                ProjectRoot = "test",
            };
            await Task.Yield();
            yield return new TextMessage { Text = "hello", Role = Role.Assistant };
            yield return new ResultEventMessage { IsError = false };
            IsRunning = false;
        }

        public async IAsyncEnumerable<IMessage> SubscribeToMessagesAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default
        )
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task SendAsync(IEnumerable<IMessage> messages, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<bool> SendExitCommandAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task ShutdownAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            IsRunning = false;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            IsRunning = false;
            return ValueTask.CompletedTask;
        }

        public void Dispose() => IsRunning = false;
    }
}
