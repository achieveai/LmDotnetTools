using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
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
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace LmMultiTurn.Tests;

/// <summary>
/// #877, #878, #879: the Claude, Codex and Copilot loops must not lose input they have taken from
/// their queue when a stop lands before that input reaches history. The store holds the first
/// <c>Queued</c> run-ledger write, which comes after the drain and before the history append, so
/// input A is provably out of the channel and not in history when the stop arrives. After Run, A
/// must appear once, ahead of input B that was sent before the stop.
/// </summary>
public class AgentLoopRestartRetainsInputTests : LoggingTestBase
{
    public AgentLoopRestartRetainsInputTests(ITestOutputHelper output)
        : base(output) { }

    [Fact]
    public async Task Claude_InputTakenBeforeRunStarts_SurvivesStopAndPrecedesLaterInput()
    {
        var store = NewStoreGatingFirstQueuedWrite();
        await using var loop = new ClaudeAgentLoop(
            new ClaudeAgentSdkOptions { Mode = ClaudeAgentSdkMode.OneShot, MaxTurnsPerRun = 5 },
            mcpServers: null,
            threadId: "claude-restart-input",
            clientFactory: (_, _) => new OneShotClaudeClient(),
            store: store,
            logger: LoggerFactory.CreateLogger<ClaudeAgentLoop>(),
            persistRunLedger: true
        );

        await StopWhileTakenInputIsUnappendedThenRestartAsync(loop, store);
    }

    [Fact]
    public async Task Codex_InputTakenBeforeRunStarts_SurvivesStopAndPrecedesLaterInput()
    {
        var store = NewStoreGatingFirstQueuedWrite();
        await using var loop = new CodexAgentLoop(
            new CodexSdkOptions(),
            new Dictionary<string, CodexMcpServerConfig>(),
            functionRegistry: null,
            enabledTools: null,
            threadId: "codex-restart-input",
            clientFactory: (_, _) => new ScriptedCodexClient(),
            store: store,
            logger: LoggerFactory.CreateLogger<CodexAgentLoop>(),
            persistRunLedger: true
        );

        await StopWhileTakenInputIsUnappendedThenRestartAsync(loop, store);
    }

    [Fact]
    public async Task Copilot_InputTakenBeforeRunStarts_SurvivesStopAndPrecedesLaterInput()
    {
        var store = NewStoreGatingFirstQueuedWrite();
        await using var loop = new CopilotAgentLoop(
            new CopilotSdkOptions(),
            threadId: "copilot-restart-input",
            clientFactory: (_, _) => new ScriptedCopilotClient(),
            store: store,
            logger: LoggerFactory.CreateLogger<CopilotAgentLoop>(),
            persistRunLedger: true
        );

        await StopWhileTakenInputIsUnappendedThenRestartAsync(loop, store);
    }

    [Fact]
    public async Task Claude_InputQueuedLocallyDuringARun_SurvivesStopBeforeItsMergedRunStarts()
    {
        // Input that arrives during an Interactive run is queued inside the loop, then merged into the next
        // run. Stop at that merged run's first ledger write: A has left the local queue and is not in history.
        string? receiptA = null;
        var store = new AssignmentGateStore(entry =>
            entry.Status == RunStatus.Queued && Volatile.Read(ref receiptA) is { } id && entry.InputIds.Contains(id)
        );
        var client = new InteractiveClaudeClient();
        var queuedLocally = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var loop = new ClaudeAgentLoop(
            new ClaudeAgentSdkOptions { Mode = ClaudeAgentSdkMode.Interactive, MaxTurnsPerRun = 5 },
            mcpServers: null,
            threadId: "claude-restart-merged-input",
            clientFactory: (_, _) => client,
            store: store,
            logger: new QueuedLocallyLogger(queuedLocally, LoggerFactory.CreateLogger<ClaudeAgentLoop>()),
            persistRunLedger: true
        );

        var running = loop.RunAsync(cts.Token);
        await loop.WaitUntilReadyAsync(cts.Token);
        _ = await loop.SendAsync([new TextMessage { Text = "start", Role = Role.User }], inputId: "input-start");
        await client.FirstTurnSent.Task.WaitAsync(cts.Token);
        var first = await loop.SendAsync([MessageA()], inputId: "input-A");
        Volatile.Write(ref receiptA, first.ReceiptId);
        await queuedLocally.Task.WaitAsync(cts.Token);
        client.FinishFirstTurn();
        (await store.AssignmentEntered.Task.WaitAsync(cts.Token)).InputIds.Should().Equal(first.ReceiptId);
        UserTexts(loop).Should().NotContain("message-A");
        var second = await loop.SendAsync([MessageB()], inputId: "input-B");
        await loop.StopAsync(TimeSpan.FromSeconds(5));
        await StoppedAsync(running);

        await RestartUntilMessageBPersistedAsync(loop, store, cts.Token);

        await AssertConsumedOnceInOrderAsync(loop, store, first.ReceiptId, second.ReceiptId, cts.Token);
    }

    private static AssignmentGateStore NewStoreGatingFirstQueuedWrite() =>
        new(entry => entry.Status == RunStatus.Queued);

    private static async Task StopWhileTakenInputIsUnappendedThenRestartAsync(
        MultiTurnAgentBase loop,
        AssignmentGateStore store
    )
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var running = loop.RunAsync(cts.Token);
        await loop.WaitUntilReadyAsync(cts.Token);
        var first = await loop.SendAsync([MessageA()], inputId: "input-A");
        (await store.AssignmentEntered.Task.WaitAsync(cts.Token)).InputIds.Should().Equal(first.ReceiptId);
        UserTexts(loop).Should().NotContain("message-A");
        var second = await loop.SendAsync([MessageB()], inputId: "input-B");
        await loop.StopAsync(TimeSpan.FromSeconds(5));
        await StoppedAsync(running);

        await RestartUntilMessageBPersistedAsync(loop, store, cts.Token);

        await AssertConsumedOnceInOrderAsync(loop, store, first.ReceiptId, second.ReceiptId, cts.Token);
    }

    private static async Task RestartUntilMessageBPersistedAsync(
        MultiTurnAgentBase loop,
        AssignmentGateStore store,
        CancellationToken ct
    )
    {
        var running = loop.RunAsync(ct);
        await loop.WaitUntilReadyAsync(ct);
        await store.MessageBPersisted.Task.WaitAsync(ct);
        await loop.StopAsync(TimeSpan.FromSeconds(5));
        await StoppedAsync(running);
    }

    /// <summary>A then B, once each, in live and persisted history, and each input assigned to one run.</summary>
    private static async Task AssertConsumedOnceInOrderAsync(
        MultiTurnAgentBase loop,
        AssignmentGateStore store,
        string firstReceiptId,
        string secondReceiptId,
        CancellationToken ct
    )
    {
        UserTexts(loop)
            .Where(text => text is "message-A" or "message-B")
            .Should()
            .Equal(["message-A", "message-B"], "taken input stays owned ahead of later input until it reaches history");
        var persisted = (await store.LoadMessagesAsync(loop.ThreadId, ct))
            .Select(row =>
                row.MessageJson.Contains("message-A", StringComparison.Ordinal) ? "A"
                : row.MessageJson.Contains("message-B", StringComparison.Ordinal) ? "B"
                : null
            )
            .OfType<string>();
        persisted.Should().Equal("A", "B");
        var assigned = (await store.ListRunLedgerAsync(loop.ThreadId, ct)).SelectMany(entry => entry.InputIds);
        assigned.Count(id => id == firstReceiptId).Should().Be(1);
        assigned.Count(id => id == secondReceiptId).Should().Be(1);
    }

    /// <summary>
    /// Awaits a run the test has stopped. The Codex and Copilot loops surface the stop as cancellation,
    /// the Claude loop returns; both are a clean stop here.
    /// </summary>
    private static async Task StoppedAsync(Task running)
    {
        try
        {
            await running;
        }
        catch (OperationCanceledException) { }
    }

    private static IEnumerable<string> UserTexts(MultiTurnAgentBase loop) =>
        loop.GetHistorySnapshot().OfType<TextMessage>().Where(m => m.Role == Role.User).Select(m => m.Text);

    private static TextMessage MessageA() => new() { Text = "message-A", Role = Role.User };

    private static TextMessage MessageB() => new() { Text = "message-B", Role = Role.User };

    /// <summary>
    /// Forwards to the test log and signals when the Interactive loop's input watcher has queued a batch locally.
    /// </summary>
    private sealed class QueuedLocallyLogger(TaskCompletionSource queuedLocally, ILogger<ClaudeAgentLoop> inner)
        : ILogger<ClaudeAgentLoop>
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => inner.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            inner.Log(logLevel, eventId, state, exception, formatter);
            if (formatter(state, exception).StartsWith("Queued ", StringComparison.Ordinal))
            {
                _ = queuedLocally.TrySetResult();
            }
        }
    }

    /// <summary>OneShot Claude CLI stand-in: every send answers once and finishes.</summary>
    private sealed class OneShotClaudeClient : IClaudeAgentSdkClient
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
                SessionId = "sess-restart",
                CreatedAt = DateTime.UtcNow,
                ProjectRoot = "test",
            };
            await Task.Yield();
            yield return new TextMessage { Text = "ok", Role = Role.Assistant };
            yield return new ResultEventMessage { IsError = false };
        }

        public IAsyncEnumerable<IMessage> SubscribeToMessagesAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("OneShot only");

        public Task SendAsync(IEnumerable<IMessage> messages, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("OneShot only");

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

    /// <summary>
    /// Interactive Claude CLI stand-in. The first turn stays open until <see cref="FinishFirstTurn"/>, so
    /// input can arrive mid-run; every later send answers at once.
    /// </summary>
    private sealed class InteractiveClaudeClient : IClaudeAgentSdkClient
    {
        private readonly Channel<IMessage> _stream = Channel.CreateUnbounded<IMessage>();
        private int _sends;

        public TaskCompletionSource FirstTurnSent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool IsRunning { get; private set; }

        public SessionInfo? CurrentSession { get; private set; }

        public ClaudeAgentSdkRequest? LastRequest { get; private set; }

        public void FinishFirstTurn() => Answer();

        public Task StartAsync(ClaudeAgentSdkRequest request, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            IsRunning = true;
            return Task.CompletedTask;
        }

        public IAsyncEnumerable<IMessage> SendMessagesAsync(
            IEnumerable<IMessage> messages,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException("Interactive only");

        // The loop keeps one subscription across Stop/Run. It ignores the token of the run that opened it,
        // as the CLI's stdout does, so a restarted run can keep reading it.
        public IAsyncEnumerable<IMessage> SubscribeToMessagesAsync(CancellationToken cancellationToken = default) =>
            _stream.Reader.ReadAllAsync(CancellationToken.None);

        public Task SendAsync(IEnumerable<IMessage> messages, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _sends) == 1)
            {
                _ = FirstTurnSent.TrySetResult();
            }
            else
            {
                Answer();
            }

            return Task.CompletedTask;
        }

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

        private void Answer()
        {
            _ = _stream.Writer.TryWrite(new TextMessage { Text = "ok", Role = Role.Assistant });
            _ = _stream.Writer.TryWrite(new ResultEventMessage { IsError = false });
        }
    }

    /// <summary>Codex CLI stand-in: every turn answers once and completes.</summary>
    private sealed class ScriptedCodexClient : ICodexSdkClient
    {
        public string? CurrentCodexThreadId { get; private set; }

        public string? CurrentTurnId => null;

        public bool IsRunning { get; private set; }

        public string DependencyState => "ready";

        public void ConfigureDynamicToolExecutor(
            Func<CodexDynamicToolCallRequest, CancellationToken, Task<CodexDynamicToolCallResponse>>? executor
        ) { }

        public Task StartOrResumeThreadAsync(CodexBridgeInitOptions options, CancellationToken ct = default)
        {
            IsRunning = true;
            CurrentCodexThreadId = options.ThreadId;
            return Task.CompletedTask;
        }

        public Task EnsureStartedAsync(CodexBridgeInitOptions options, CancellationToken ct = default) =>
            StartOrResumeThreadAsync(options, ct);

        public async IAsyncEnumerable<CodexTurnEventEnvelope> RunStreamingAsync(
            string input,
            [EnumeratorCancellation] CancellationToken ct = default
        )
        {
            await Task.Yield();
            yield return CodexEvent(
                "item.completed",
                """{"type":"item.completed","item":{"id":"msg_1","type":"agent_message","text":"ok"}}"""
            );
            yield return CodexEvent(
                "turn.completed",
                """{"type":"turn.completed","usage":{"input_tokens":1,"cached_input_tokens":0,"output_tokens":1}}"""
            );
        }

        public Task ShutdownAsync(TimeSpan? timeout = null, CancellationToken ct = default)
        {
            IsRunning = false;
            return Task.CompletedTask;
        }

        public Task InterruptTurnAsync(CancellationToken ct = default) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static CodexTurnEventEnvelope CodexEvent(string name, string json) =>
            new()
            {
                Type = name,
                Event = JsonDocument.Parse(json).RootElement.Clone(),
                RequestId = Guid.NewGuid().ToString("N"),
                ThreadId = null,
            };
    }

    /// <summary>Copilot CLI stand-in: every prompt answers once and completes.</summary>
    private sealed class ScriptedCopilotClient : ICopilotSdkClient
    {
        private const string SessionId = "sess_copilot_restart";

        public string? CurrentCopilotSessionId { get; private set; }

        public bool IsRunning { get; private set; }

        public string DependencyState => "ready";

        public void ConfigureDynamicToolExecutor(
            Func<CopilotDynamicToolCallRequest, CancellationToken, Task<CopilotDynamicToolCallResponse>>? executor
        ) { }

        public Task StartOrResumeSessionAsync(CopilotBridgeInitOptions options, CancellationToken ct = default)
        {
            IsRunning = true;
            CurrentCopilotSessionId = SessionId;
            return Task.CompletedTask;
        }

        public Task EnsureStartedAsync(CopilotBridgeInitOptions options, CancellationToken ct = default) =>
            StartOrResumeSessionAsync(options, ct);

        public async IAsyncEnumerable<CopilotTurnEventEnvelope> RunStreamingAsync(
            string input,
            [EnumeratorCancellation] CancellationToken ct = default
        )
        {
            await Task.Yield();
            yield return CopilotEvent(
                """
                {"type":"session/update","sessionId":"sess_copilot_restart","update":{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"ok"}}}
                """,
                SessionId
            );
            yield return CopilotEvent(
                """{"type":"session/prompt/completed","usage":{"inputTokens":1,"outputTokens":1,"cachedInputTokens":0}}""",
                sessionId: null
            );
        }

        public Task InterruptTurnAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task ShutdownAsync(TimeSpan? timeout = null, CancellationToken ct = default)
        {
            IsRunning = false;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static CopilotTurnEventEnvelope CopilotEvent(string json, string? sessionId) =>
            new()
            {
                Type = "event",
                Event = JsonDocument.Parse(json).RootElement.Clone(),
                RequestId = Guid.NewGuid().ToString("N"),
                SessionId = sessionId,
            };
    }
}
