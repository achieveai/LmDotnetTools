using System.Runtime.CompilerServices;
using System.Text.Json;
using AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Agents;
using AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Configuration;
using AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Models;
using AchieveAi.LmDotnetTools.CodexSdkProvider.Agents;
using AchieveAi.LmDotnetTools.CodexSdkProvider.Configuration;
using AchieveAi.LmDotnetTools.CodexSdkProvider.Models;
using AchieveAi.LmDotnetTools.CopilotSdkProvider.Agents;
using AchieveAi.LmDotnetTools.CopilotSdkProvider.Configuration;
using AchieveAi.LmDotnetTools.CopilotSdkProvider.Models;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using FluentAssertions;
using Xunit;

namespace LmMultiTurn.Tests;

/// <summary>
/// Spec-review regression tests for WI #194 tasks 1-4 (commit e315b1fd): the concrete
/// <see cref="IMultiTurnAgent"/> loops that talk to a real provider bridge/CLI —
/// <see cref="ClaudeAgentLoop"/>, <see cref="CodexAgentLoop"/>, <see cref="CopilotAgentLoop"/> —
/// must actually execute turns on the run's OWN token (<c>MultiTurnAgentBase.CurrentRunToken</c>,
/// captured right after <c>StartRunAsync</c>), not the outer run-loop's lifetime token. Before this
/// fix, a matching <c>CancelCurrentRunAsync</c> call cancelled a per-run <c>CancellationTokenSource</c>
/// that nothing downstream ever observed — the underlying provider stream never saw a cancellation,
/// <c>CancelCurrentRunAsync</c>'s "Accepted" was a lie, and (for Codex/Copilot) the bridge client's
/// turn was never interrupted.
/// </summary>
public class AgentLoopRunCancellationTests
{
    [Fact]
    public async Task ClaudeAgentLoop_OneShot_CancelCurrentRunAsync_MatchingRun_CancelsInFlightTurn_AndLoopContinues()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fakeClient = new GatedClaudeClient(
            started,
            secondCallMessages: [new TextMessage { Text = "second run reply", Role = Role.Assistant }]);

        var store = new InMemoryConversationStore();
        var options = new ClaudeAgentSdkOptions { Mode = ClaudeAgentSdkMode.OneShot };

        await using var loop = new ClaudeAgentLoop(
            claudeOptions: options,
            mcpServers: null,
            threadId: "claude-cancel-thread",
            store: store,
            clientFactory: (_, _) => fakeClient,
            persistRunLedger: true);

        using var cts = new CancellationTokenSource();
        _ = loop.RunAsync(cts.Token);

        var input1 = new UserInput([new TextMessage { Text = "Hi", Role = Role.User }], InputId: "claude-cancel-in-1");

        var messages1 = new List<IMessage>();
        var run1Completion = Task.Run(async () =>
        {
            await foreach (var msg in loop.ExecuteRunAsync(input1, cts.Token))
            {
                messages1.Add(msg);
            }

            return messages1;
        });

        // Confirm the turn is genuinely mid-flight (the fake client's first call is hanging)
        // before cancelling, so this races real in-flight work. ClaudeAgentLoop only publishes
        // RunAssignmentMessage on dequeue detection (deferred to the flush safety-net at run
        // completion in OneShot mode), so read the run id via the public CurrentRunId property
        // instead of waiting for that message.
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var runId1 = loop.CurrentRunId;
        runId1.Should().NotBeNull("a run must be active once the fake client's first call has started");

        var outcome = await loop.CancelCurrentRunAsync(runId1!);
        outcome.Should().Be(RunCancellationResult.Accepted);

        var completedMessages1 = await run1Completion.WaitAsync(TimeSpan.FromSeconds(5));

        // Before the fix: ExecuteOneShotModeAsync's `stream.WithCancellation(ct)` was passed the
        // OUTER loop token (never cancelled by CancelCurrentRunAsync), so the fake client's
        // Task.Delay(Timeout.Infinite, runToken) would never observe cancellation and this line
        // would hang until the WaitAsync timeout fired the test failure.
        var completions1 = completedMessages1.OfType<RunCompletedMessage>().ToList();
        completions1.Should().HaveCount(1, "the run must reach exactly one terminal outcome");
        completions1[0].CompletedRunId.Should().Be(runId1);
        completions1[0].IsCancelled.Should().BeTrue(
            "the fake client's in-flight call must have observed the RUN's own token, not the outer loop token, being cancelled");
        completions1[0].IsError.Should().BeFalse();

        var ledgerEntry = await store.LoadRunLedgerAsync(runId1!);
        ledgerEntry.Should().NotBeNull();
        ledgerEntry!.Status.Should().Be(RunStatus.Cancelled);

        // Loop liveness: a matching per-run cancellation must never propagate to the loop's own
        // top-level cancellation handling and tear the whole RunLoopAsync down.
        var input2 = new UserInput([new TextMessage { Text = "Again", Role = Role.User }], InputId: "claude-cancel-in-2");
        var messages2 = new List<IMessage>();
        await foreach (var msg in loop.ExecuteRunAsync(input2, cts.Token))
        {
            messages2.Add(msg);
        }

        messages2.OfType<RunCompletedMessage>().Should().ContainSingle(m => !m.IsCancelled && !m.IsError);

        await cts.CancelAsync();
    }

    [Fact]
    public async Task CodexAgentLoop_CancelCurrentRunAsync_MatchingRun_InterruptsBridgeClient_CancelsRun_AndLoopContinues()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fakeClient = new GatedFakeCodexClient(
            started,
            secondCallEvents:
            [
                CodexEvent("turn.completed", """{"type":"turn.completed","usage":{"input_tokens":1,"cached_input_tokens":0,"output_tokens":1}}"""),
            ]);

        var store = new InMemoryConversationStore();
        await using var loop = new CodexAgentLoop(
            new CodexSdkOptions(),
            new Dictionary<string, CodexMcpServerConfig>(),
            functionRegistry: null,
            enabledTools: null,
            threadId: "codex-cancel-thread",
            store: store,
            clientFactory: (_, _) => fakeClient,
            persistRunLedger: true);

        using var cts = new CancellationTokenSource();
        _ = loop.RunAsync(cts.Token);

        var input1 = new UserInput([new TextMessage { Text = "Hi", Role = Role.User }], InputId: "codex-cancel-in-1");
        var (runId1, run1Completion) = await StartRunAndCaptureAssignmentAsync(loop, input1, cts.Token);

        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var outcome = await loop.CancelCurrentRunAsync(runId1);
        outcome.Should().Be(RunCancellationResult.Accepted);

        var messages1 = await run1Completion.WaitAsync(TimeSpan.FromSeconds(5));

        var completions1 = messages1.OfType<RunCompletedMessage>().ToList();
        completions1.Should().HaveCount(1);
        completions1[0].CompletedRunId.Should().Be(runId1);
        completions1[0].IsCancelled.Should().BeTrue();
        completions1[0].IsError.Should().BeFalse();

        fakeClient.InterruptCallCount.Should().BeGreaterThanOrEqualTo(
            1,
            "ExecuteRunAsync's bridge-interrupt catch must have observed the run's own token being cancelled, not the outer loop token");

        var ledgerEntry = await store.LoadRunLedgerAsync(runId1);
        ledgerEntry.Should().NotBeNull();
        ledgerEntry!.Status.Should().Be(RunStatus.Cancelled);

        var input2 = new UserInput([new TextMessage { Text = "Again", Role = Role.User }], InputId: "codex-cancel-in-2");
        var messages2 = new List<IMessage>();
        await foreach (var msg in loop.ExecuteRunAsync(input2, cts.Token))
        {
            messages2.Add(msg);
        }

        messages2.OfType<RunCompletedMessage>().Should().ContainSingle(m => !m.IsCancelled && !m.IsError);

        await cts.CancelAsync();
    }

    [Fact]
    public async Task CopilotAgentLoop_CancelCurrentRunAsync_MatchingRun_InterruptsBridgeClient_CancelsRun_AndLoopContinues()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fakeClient = new GatedFakeCopilotClient(
            started,
            secondCallEvents: [CopilotPromptCompleted("""{"usage":{"inputTokens":1,"outputTokens":1,"cachedInputTokens":0}}""")]);

        var store = new InMemoryConversationStore();
        await using var loop = new CopilotAgentLoop(
            new CopilotSdkOptions(),
            threadId: "copilot-cancel-thread",
            store: store,
            clientFactory: (_, _) => fakeClient,
            persistRunLedger: true);

        using var cts = new CancellationTokenSource();
        _ = loop.RunAsync(cts.Token);

        var input1 = new UserInput([new TextMessage { Text = "Hi", Role = Role.User }], InputId: "copilot-cancel-in-1");
        var (runId1, run1Completion) = await StartRunAndCaptureAssignmentAsync(loop, input1, cts.Token);

        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var outcome = await loop.CancelCurrentRunAsync(runId1);
        outcome.Should().Be(RunCancellationResult.Accepted);

        var messages1 = await run1Completion.WaitAsync(TimeSpan.FromSeconds(5));

        var completions1 = messages1.OfType<RunCompletedMessage>().ToList();
        completions1.Should().HaveCount(1);
        completions1[0].CompletedRunId.Should().Be(runId1);
        completions1[0].IsCancelled.Should().BeTrue();
        completions1[0].IsError.Should().BeFalse();

        fakeClient.InterruptCallCount.Should().BeGreaterThanOrEqualTo(
            1,
            "ExecuteRunAsync's bridge-interrupt catch must have observed the run's own token being cancelled, not the outer loop token");

        var ledgerEntry = await store.LoadRunLedgerAsync(runId1);
        ledgerEntry.Should().NotBeNull();
        ledgerEntry!.Status.Should().Be(RunStatus.Cancelled);

        var input2 = new UserInput([new TextMessage { Text = "Again", Role = Role.User }], InputId: "copilot-cancel-in-2");
        var messages2 = new List<IMessage>();
        await foreach (var msg in loop.ExecuteRunAsync(input2, cts.Token))
        {
            messages2.Add(msg);
        }

        messages2.OfType<RunCompletedMessage>().Should().ContainSingle(m => !m.IsCancelled && !m.IsError);

        await cts.CancelAsync();
    }

    /// <summary>
    /// Runs <paramref name="input"/> through <see cref="IMultiTurnAgent.ExecuteRunAsync"/> on a
    /// background task, waits for its <see cref="RunAssignmentMessage"/> to arrive, and returns
    /// the assigned run id plus a task that completes with every message the run produced once
    /// <c>ExecuteRunAsync</c> itself completes.
    /// </summary>
    private static async Task<(string RunId, Task<List<IMessage>> Completion)> StartRunAndCaptureAssignmentAsync(
        IMultiTurnAgent agent,
        UserInput input,
        CancellationToken ct)
    {
        var assignmentTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var messages = new List<IMessage>();

        var completion = Task.Run(async () =>
        {
            await foreach (var msg in agent.ExecuteRunAsync(input, ct))
            {
                messages.Add(msg);
                if (msg is RunAssignmentMessage ram && !assignmentTcs.Task.IsCompleted)
                {
                    assignmentTcs.TrySetResult(ram.Assignment.RunId);
                }
            }

            return messages;
        });

        var runId = await assignmentTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        return (runId, completion);
    }

    private static CodexTurnEventEnvelope CodexEvent(string name, string json)
    {
        var element = JsonDocument.Parse(json).RootElement.Clone();
        return new CodexTurnEventEnvelope
        {
            Type = name,
            Event = element,
            RequestId = Guid.NewGuid().ToString("N"),
            ThreadId = null,
        };
    }

    private static CopilotTurnEventEnvelope CopilotPromptCompleted(string innerJson)
    {
        using var innerDoc = JsonDocument.Parse(innerJson);
        var inner = innerDoc.RootElement;

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "session/prompt/completed");
            foreach (var property in inner.EnumerateObject())
            {
                property.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        using var envelopeDoc = JsonDocument.Parse(stream.ToArray());
        var element = envelopeDoc.RootElement.Clone();

        return new CopilotTurnEventEnvelope
        {
            Type = "event",
            Event = element,
            RequestId = Guid.NewGuid().ToString("N"),
            SessionId = null,
        };
    }

    /// <summary>
    /// Minimal in-process Claude SDK mock (OneShot mode). The FIRST <c>SendMessagesAsync</c> call
    /// signals <see cref="_started"/> then hangs on the token passed to it — so a test can prove
    /// that the token flowing into the client is the RUN's own token (cancelled by a matching
    /// <c>CancelCurrentRunAsync</c>), not the outer loop's lifetime token (never cancelled here
    /// until the test explicitly does so at the end). The SECOND call replays a normal reply.
    /// </summary>
    private sealed class GatedClaudeClient : IClaudeAgentSdkClient
    {
        private readonly TaskCompletionSource _started;
        private readonly IReadOnlyList<IMessage> _secondCallMessages;
        private int _callCount;

        public GatedClaudeClient(TaskCompletionSource started, IReadOnlyList<IMessage> secondCallMessages)
        {
            _started = started;
            _secondCallMessages = secondCallMessages;
        }

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
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref _callCount);
            if (call == 1)
            {
                _started.TrySetResult();

                // Hangs until the token passed to THIS call is cancelled. If the caller passes
                // the outer loop token (the pre-fix bug), this never returns within the test's
                // WaitAsync bound and the test fails on timeout instead of observing IsCancelled.
                await Task.Delay(Timeout.Infinite, cancellationToken);
                yield break;
            }

            foreach (var msg in _secondCallMessages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return msg;
                await Task.Yield();
            }
        }

        public IAsyncEnumerable<IMessage> SubscribeToMessagesAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException("OneShot mode under test does not subscribe.");

        public Task SendAsync(IEnumerable<IMessage> messages, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<bool> SendExitCommandAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(true);

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

        public void Dispose()
        {
            IsRunning = false;
        }
    }

    /// <summary>
    /// Minimal in-process Codex bridge mock. The FIRST <c>RunStreamingAsync</c> call signals
    /// <see cref="_started"/> then hangs on the token passed to it, mirroring
    /// <see cref="GatedClaudeClient"/>. Tracks <see cref="InterruptCallCount"/> so a test can prove
    /// <c>CodexAgentLoop.ExecuteRunAsync</c>'s bridge-interrupt catch was actually reached.
    /// </summary>
    private sealed class GatedFakeCodexClient : ICodexSdkClient
    {
        private readonly TaskCompletionSource _started;
        private readonly IReadOnlyList<CodexTurnEventEnvelope> _secondCallEvents;
        private int _callCount;

        public GatedFakeCodexClient(TaskCompletionSource started, IReadOnlyList<CodexTurnEventEnvelope> secondCallEvents)
        {
            _started = started;
            _secondCallEvents = secondCallEvents;
        }

        public int InterruptCallCount { get; private set; }

        public bool IsRunning { get; private set; }

        public string? CurrentCodexThreadId { get; private set; }

        public string? CurrentTurnId { get; private set; }

        public string DependencyState => "ready";

        public void ConfigureDynamicToolExecutor(
            Func<CodexDynamicToolCallRequest, CancellationToken, Task<CodexDynamicToolCallResponse>>? executor)
        {
        }

        public Task StartOrResumeThreadAsync(CodexBridgeInitOptions options, CancellationToken ct = default)
        {
            IsRunning = true;
            CurrentCodexThreadId = options.ThreadId;
            return Task.CompletedTask;
        }

        public Task EnsureStartedAsync(CodexBridgeInitOptions options, CancellationToken ct = default)
            => StartOrResumeThreadAsync(options, ct);

        public async IAsyncEnumerable<CodexTurnEventEnvelope> RunStreamingAsync(
            string input,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            var call = Interlocked.Increment(ref _callCount);
            if (call == 1)
            {
                _started.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
                yield break;
            }

            foreach (var item in _secondCallEvents)
            {
                ct.ThrowIfCancellationRequested();
                yield return item;
                await Task.Yield();
            }
        }

        public Task InterruptTurnAsync(CancellationToken ct = default)
        {
            InterruptCallCount++;
            return Task.CompletedTask;
        }

        public Task ShutdownAsync(TimeSpan? timeout = null, CancellationToken ct = default)
        {
            IsRunning = false;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>
    /// Minimal in-process Copilot bridge mock, mirroring <see cref="GatedFakeCodexClient"/>.
    /// </summary>
    private sealed class GatedFakeCopilotClient : ICopilotSdkClient
    {
        private readonly TaskCompletionSource _started;
        private readonly IReadOnlyList<CopilotTurnEventEnvelope> _secondCallEvents;
        private int _callCount;

        public GatedFakeCopilotClient(TaskCompletionSource started, IReadOnlyList<CopilotTurnEventEnvelope> secondCallEvents)
        {
            _started = started;
            _secondCallEvents = secondCallEvents;
        }

        public int InterruptCallCount { get; private set; }

        public bool IsRunning { get; private set; }

        public string? CurrentCopilotSessionId { get; private set; }

        public string DependencyState => "ready";

        public void ConfigureDynamicToolExecutor(
            Func<CopilotDynamicToolCallRequest, CancellationToken, Task<CopilotDynamicToolCallResponse>>? executor)
        {
        }

        public Task StartOrResumeSessionAsync(CopilotBridgeInitOptions options, CancellationToken ct = default)
        {
            IsRunning = true;
            CurrentCopilotSessionId = "sess-cancel-test";
            return Task.CompletedTask;
        }

        public Task EnsureStartedAsync(CopilotBridgeInitOptions options, CancellationToken ct = default)
            => StartOrResumeSessionAsync(options, ct);

        public async IAsyncEnumerable<CopilotTurnEventEnvelope> RunStreamingAsync(
            string input,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            var call = Interlocked.Increment(ref _callCount);
            if (call == 1)
            {
                _started.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
                yield break;
            }

            foreach (var item in _secondCallEvents)
            {
                ct.ThrowIfCancellationRequested();
                yield return item;
                await Task.Yield();
            }
        }

        public Task InterruptTurnAsync(CancellationToken ct = default)
        {
            InterruptCallCount++;
            return Task.CompletedTask;
        }

        public Task ShutdownAsync(TimeSpan? timeout = null, CancellationToken ct = default)
        {
            IsRunning = false;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
