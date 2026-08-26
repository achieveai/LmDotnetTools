using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using LmStreaming.Sample.WebSocket;
using Microsoft.Extensions.Logging;

namespace LmStreaming.Sample.Tests.WebSocket;

/// <summary>
/// Tests for the focused sub-agent WebSocket handler (WI #194, Task 4):
/// <see cref="ChatWebSocketManager.HandleSubAgentConnectionAsync"/>. The handler is
/// presentation-only — it streams a FOCUSED child sub-agent's live+replayed output to the client
/// (reusing the parent's <c>StreamMessagesToClientAsync</c>) and relays inbound text frames to the
/// child via <see cref="SubAgentManager.SendMessageAsync(string, string, bool, CancellationToken)"/>
/// in background mode. It never touches
/// the parent <c>/ws</c> handler or agent execution.
/// </summary>
public sealed class ChatWebSocketManagerSubAgentTests
{
    private const string ParentThreadId = "parent-thread";
    private const string TemplateName = "worker";

    [Fact]
    public async Task HandleSubAgentConnectionAsync_StreamsChildSubscribeAsyncOutput_ToClient()
    {
        // A child whose run stays alive (gated) while it has published two text messages, so the
        // handler's subscription replays + streams them; releasing the gate completes the run and
        // must produce the {"$type":"done"} sentinel.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new ScriptedSubAgentProvider((messages, ct) =>
          TwoMessagesThenBlock(gate.Task, ct));

        await using var loop = CreateParentLoop(() => provider);
        await using var pool = CreatePoolReturning(loop);
        RegisterParent(pool);

        var spawnJson = await loop.SubAgentManager!.SpawnAsync(
          TemplateName, "do work", name: "alpha", runInBackground: true);
        var agentId = ParseAgentId(spawnJson);

        var manager = CreateManager(pool);
        var socket = new FakeWebSocket();
        using var testCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var handlerTask = manager.HandleSubAgentConnectionAsync(
          socket, ParentThreadId, agentId, mayReplayPersistedTranscript: true, testCts.Token);

        // The two live child messages must reach the client (via replay of the in-flight run).
        await socket.WaitUntilAsync(
          () => socket.SentContains("chunk-one") && socket.SentContains("chunk-two"),
          testCts.Token);

        // Complete the child run; the handler must emit the done sentinel after RunCompletedMessage.
        gate.SetResult();
        await socket.WaitUntilAsync(() => socket.SentContains("\"$type\":\"done\""), testCts.Token);

        await testCts.CancelAsync();
        await handlerTask;

        socket.SentFrames.Should().Contain(f => f.Contains("chunk-one"));
        socket.SentFrames.Should().Contain(f => f.Contains("chunk-two"));
        socket.SentFrames.Should().Contain(f => f.Contains("\"$type\":\"done\""));
    }

    [Fact]
    public async Task HandleSubAgentConnectionAsync_RelaysInboundTextFrame_ToSendMessageAsync_InBackgroundMode()
    {
        // The child's first (spawn) run blocks forever, so the child stays long-running. Two inbound
        // frames are relayed while that run is in flight: because relaying uses background mode, the
        // receive loop must NOT block on the first SendMessageAsync — it must go on to read the second
        // frame. We prove this by observing that BOTH frames were dequeued from the socket while the
        // child run is still blocked. We then release the block and confirm the child actually received
        // the relayed prompts (the injected user messages surface as input to the provider).
        var firstRunGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new RecordingSubAgentProvider(firstRunGate.Task, blockOnPromptText: "seed-task");

        await using var loop = CreateParentLoop(() => provider);
        await using var pool = CreatePoolReturning(loop);
        RegisterParent(pool);

        var spawnJson = await loop.SubAgentManager!.SpawnAsync(
          TemplateName, "seed-task", name: "alpha", runInBackground: true);
        var agentId = ParseAgentId(spawnJson);

        // The spawn run must be in flight (blocked) before we relay.
        await provider.WaitUntilAsync(() => provider.ReceivedContains("seed-task"), TimeSpan.FromSeconds(30));

        var manager = CreateManager(pool);
        var socket = new FakeWebSocket();
        socket.EnqueueTextFrame(JsonSerializer.Serialize(new ChatRequest("relayed-one")));
        socket.EnqueueTextFrame(JsonSerializer.Serialize(new ChatRequest("relayed-two")));
        using var testCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var handlerTask = manager.HandleSubAgentConnectionAsync(
          socket, ParentThreadId, agentId, mayReplayPersistedTranscript: true, testCts.Token);

        // Non-blocking proof: both frames are read even though the child's first run is still blocked.
        await socket.WaitUntilAsync(() => socket.ReceivedFrameCount >= 2, testCts.Token);
        firstRunGate.Task.IsCompleted.Should().BeFalse("the child's first run must still be in flight");

        // Now let the child drain: the relayed prompts must reach the provider (child received input).
        firstRunGate.SetResult();
        await provider.WaitUntilAsync(
          () => provider.ReceivedContains("relayed-one") && provider.ReceivedContains("relayed-two"),
          TimeSpan.FromSeconds(30));

        await testCts.CancelAsync();
        await handlerTask;

        provider.ReceivedPrompts.Should().Contain("relayed-one");
        provider.ReceivedPrompts.Should().Contain("relayed-two");
    }

    [Fact]
    public async Task HandleSubAgentConnectionAsync_KeepsStreamOpen_WhenRelayThrows()
    {
        // A relay can fail per-frame after the socket is open: the focused child's lifetime is
        // independent, so a restart can throw (e.g. its provider fails to recreate). Such a failure
        // must be isolated to the frame — it must NOT tear down the whole presentation-only view.
        // The child's FIRST run completes immediately; the SECOND provider creation (triggered by the
        // relay's restart of the finished child) throws. We prove the receive loop survives by
        // observing it goes on to read a SECOND frame after the throwing relay.
        var creation = 0;
        Func<IStreamingAgent> factory = () =>
        {
            var call = Interlocked.Increment(ref creation);
            if (call == 1)
            {
                return new ScriptedSubAgentProvider((messages, ct) => OneMessageThenComplete());
            }

            throw new InvalidOperationException("provider recreation failed");
        };

        await using var loop = CreateParentLoop(factory);
        await using var pool = CreatePoolReturning(loop);
        RegisterParent(pool);

        using var testCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var spawnJson = await loop.SubAgentManager!.SpawnAsync(
          TemplateName, "do work", name: "alpha", runInBackground: true);
        var agentId = ParseAgentId(spawnJson);

        // Ensure the first run has finished so the relay takes the (throwing) restart path.
        _ = await loop.SubAgentManager!.ObserveCompletionAsync(agentId, testCts.Token);

        var manager = CreateManager(pool);
        var socket = new FakeWebSocket();
        socket.EnqueueTextFrame(JsonSerializer.Serialize(new ChatRequest("relayed-one")));
        socket.EnqueueTextFrame(JsonSerializer.Serialize(new ChatRequest("relayed-two")));

        var handlerTask = manager.HandleSubAgentConnectionAsync(
          socket, ParentThreadId, agentId, mayReplayPersistedTranscript: true, testCts.Token);

        // Both frames are read: the first relay throws (restart recreation fails) but is isolated, so
        // the loop stays alive and reads the second frame instead of faulting the connection.
        await socket.WaitUntilAsync(() => socket.ReceivedFrameCount >= 2, testCts.Token);
        handlerTask.IsCompleted.Should().BeFalse("an isolated relay failure must not end the connection");

        await testCts.CancelAsync();
        await handlerTask;
    }

    [Fact]
    public async Task HandleSubAgentConnectionAsync_SendsStructuredError_WhenAgentIdUnknown()
    {
        var provider = new ScriptedSubAgentProvider((messages, ct) => EmptyStream());
        await using var loop = CreateParentLoop(() => provider);
        await using var pool = CreatePoolReturning(loop);
        RegisterParent(pool);

        var manager = CreateManager(pool);
        var socket = new FakeWebSocket();
        using var testCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await manager.HandleSubAgentConnectionAsync(
          socket, ParentThreadId, "does-not-exist", mayReplayPersistedTranscript: true, testCts.Token);

        socket.SentFrames.Should().ContainSingle();
        var frame = socket.SentFrames[0];
        frame.Should().Contain("\"code\":\"subagent_unavailable\"");
        frame.Should().Contain("does-not-exist");
        socket.CloseAsyncCalled.Should().BeTrue("the socket must be closed after the structured error");
    }

    [Fact]
    public async Task HandleSubAgentConnectionAsync_ReplaysPersistedHistory_WhenNoLiveStreamButHistoryExists()
    {
        // A COMPLETED sub-agent whose parent loop was evicted from the pool (app restart / conversation
        // aged out) resolves NO live stream, but its transcript persists under "subagent-{agentId}" and
        // the client renders that history from REST. Instead of the scary "unavailable" error, the handler
        // must settle the client with the done sentinel and hold the read-only socket OPEN so the persisted
        // transcript replays. (Approach C: the socket carries NO content — the transcript comes from REST —
        // so there is no merge-key/duplicate-pill risk.)
        const string agentId = "completed-child";
        var threadId = $"subagent-{agentId}";
        var store = new InMemoryConversationStore();
        await store.AppendMessagesAsync(threadId,
        [
            MessagePersistenceConverter.ToPersistedMessage(
              new TextMessage { Role = Role.Assistant, Text = "persisted-answer" }, threadId, runId: "run-1"),
        ]);

        // A parent loop IS registered (so the first resolution branch runs), but agentId was never spawned,
        // so no live stream resolves and the handler falls through to the persisted-history branch.
        var provider = new ScriptedSubAgentProvider((messages, ct) => EmptyStream());
        await using var loop = CreateParentLoop(() => provider);
        await using var pool = CreatePoolReturning(loop);
        RegisterParent(pool);

        var manager = CreateManager(pool, store: store);
        var socket = new FakeWebSocket();
        using var testCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var handlerTask = manager.HandleSubAgentConnectionAsync(
          socket, ParentThreadId, agentId, mayReplayPersistedTranscript: true, testCts.Token);

        // The handler settles the client with the done sentinel and does NOT surface an unavailable error.
        await socket.WaitUntilAsync(() => socket.SentContains("\"$type\":\"done\""), testCts.Token);
        socket.SentContains("subagent_unavailable").Should()
          .BeFalse("a persisted transcript must replay read-only, not surface an error");
        handlerTask.IsCompleted.Should()
          .BeFalse("the read-only replay socket stays open until the client disconnects");
        socket.CloseAsyncCalled.Should()
          .BeFalse("the server must not close a read-only replay socket");

        // Client disconnect (here: shutdown) tears the read-only socket down cleanly.
        await testCts.CancelAsync();
        await handlerTask;
    }

    // ----- shared bounded receive pump (PR #209 findings #4/#5/#9/#10) -----

    [Fact]
    public async Task ReceivePump_AssemblesMessageSplitAcrossFragments()
    {
        // A single logical message delivered as two text fragments must be assembled and relayed whole.
        var firstRunGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new RecordingSubAgentProvider(firstRunGate.Task, blockOnPromptText: "seed-task");

        await using var loop = CreateParentLoop(() => provider);
        await using var pool = CreatePoolReturning(loop);
        RegisterParent(pool);

        var spawnJson = await loop.SubAgentManager!.SpawnAsync(
          TemplateName, "seed-task", name: "alpha", runInBackground: true);
        var agentId = ParseAgentId(spawnJson);
        await provider.WaitUntilAsync(() => provider.ReceivedContains("seed-task"), TimeSpan.FromSeconds(30));

        var manager = CreateManager(pool);
        var socket = new FakeWebSocket();
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new ChatRequest("split-message")));
        var mid = bytes.Length / 2;
        socket.EnqueueTextFragment(bytes[..mid], endOfMessage: false);
        socket.EnqueueTextFragment(bytes[mid..], endOfMessage: true);
        using var testCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var handlerTask = manager.HandleSubAgentConnectionAsync(
          socket, ParentThreadId, agentId, mayReplayPersistedTranscript: true, testCts.Token);
        firstRunGate.SetResult();
        await provider.WaitUntilAsync(() => provider.ReceivedContains("split-message"), TimeSpan.FromSeconds(30));

        await testCts.CancelAsync();
        await handlerTask;

        provider.ReceivedPrompts.Should().Contain("split-message");
    }

    [Fact]
    public async Task ReceivePump_DoesNotCorruptMultiByteChar_SplitAcrossFragmentBoundary()
    {
        // A 4-byte UTF-8 char (🚀) split mid-sequence across a fragment boundary must survive: the pump
        // must accumulate raw bytes and decode ONCE at end-of-message (per-fragment decode corrupts it).
        var firstRunGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new RecordingSubAgentProvider(firstRunGate.Task, blockOnPromptText: "seed-task");

        await using var loop = CreateParentLoop(() => provider);
        await using var pool = CreatePoolReturning(loop);
        RegisterParent(pool);

        var spawnJson = await loop.SubAgentManager!.SpawnAsync(
          TemplateName, "seed-task", name: "alpha", runInBackground: true);
        var agentId = ParseAgentId(spawnJson);
        await provider.WaitUntilAsync(() => provider.ReceivedContains("seed-task"), TimeSpan.FromSeconds(30));

        var manager = CreateManager(pool);
        var socket = new FakeWebSocket();
        const string payload = "AB🚀CD";
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new ChatRequest(payload)));
        var rocketStart = Array.IndexOf(bytes, (byte)0xF0);
        var split = rocketStart + 2; // Split INSIDE the 4-byte 🚀 sequence.
        socket.EnqueueTextFragment(bytes[..split], endOfMessage: false);
        socket.EnqueueTextFragment(bytes[split..], endOfMessage: true);
        using var testCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var handlerTask = manager.HandleSubAgentConnectionAsync(
          socket, ParentThreadId, agentId, mayReplayPersistedTranscript: true, testCts.Token);
        firstRunGate.SetResult();
        await provider.WaitUntilAsync(() => provider.ReceivedContains(payload), TimeSpan.FromSeconds(30));

        await testCts.CancelAsync();
        await handlerTask;

        provider.ReceivedPrompts.Should().Contain(payload);
    }

    [Fact]
    public async Task ReceivePump_ClosesWithMessageTooBig_WhenMessageExceedsLimit()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new ScriptedSubAgentProvider((messages, ct) => TwoMessagesThenBlock(gate.Task, ct));

        await using var loop = CreateParentLoop(() => provider);
        await using var pool = CreatePoolReturning(loop);
        RegisterParent(pool);

        var spawnJson = await loop.SubAgentManager!.SpawnAsync(
          TemplateName, "do work", name: "alpha", runInBackground: true);
        var agentId = ParseAgentId(spawnJson);

        var manager = CreateManager(pool);
        manager.MaxInboundMessageBytes = 16;
        var socket = new FakeWebSocket();
        socket.EnqueueTextFrame(
          JsonSerializer.Serialize(new ChatRequest("this-message-is-far-larger-than-the-configured-limit")));
        using var testCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var handlerTask = manager.HandleSubAgentConnectionAsync(
          socket, ParentThreadId, agentId, mayReplayPersistedTranscript: true, testCts.Token);
        await handlerTask;

        socket.LastCloseStatus.Should().Be(WebSocketCloseStatus.MessageTooBig);
        gate.SetResult();
    }

    [Fact]
    public async Task ReceivePump_RejectsBinaryFrame_WithInvalidMessageType()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new ScriptedSubAgentProvider((messages, ct) => TwoMessagesThenBlock(gate.Task, ct));

        await using var loop = CreateParentLoop(() => provider);
        await using var pool = CreatePoolReturning(loop);
        RegisterParent(pool);

        var spawnJson = await loop.SubAgentManager!.SpawnAsync(
          TemplateName, "do work", name: "alpha", runInBackground: true);
        var agentId = ParseAgentId(spawnJson);

        var manager = CreateManager(pool);
        var socket = new FakeWebSocket();
        socket.EnqueueBinaryFrame([0x01, 0x02, 0x03, 0x04]);
        using var testCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var handlerTask = manager.HandleSubAgentConnectionAsync(
          socket, ParentThreadId, agentId, mayReplayPersistedTranscript: true, testCts.Token);
        await handlerTask;

        socket.LastCloseStatus.Should().Be(WebSocketCloseStatus.InvalidMessageType);
        gate.SetResult();
    }

    [Fact]
    public async Task ReceivePump_ClosesOnAssemblyDeadline_WhenPartialMessageStalls()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new ScriptedSubAgentProvider((messages, ct) => TwoMessagesThenBlock(gate.Task, ct));

        await using var loop = CreateParentLoop(() => provider);
        await using var pool = CreatePoolReturning(loop);
        RegisterParent(pool);

        var spawnJson = await loop.SubAgentManager!.SpawnAsync(
          TemplateName, "do work", name: "alpha", runInBackground: true);
        var agentId = ParseAgentId(spawnJson);

        var manager = CreateManager(pool);
        manager.InboundAssemblyDeadline = TimeSpan.FromMilliseconds(200);
        var socket = new FakeWebSocket();
        // A partial fragment that never completes (no EndOfMessage) must trip the assembly deadline.
        socket.EnqueueTextFragment(Encoding.UTF8.GetBytes("{\"message\":\"partial"), endOfMessage: false);
        using var testCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var handlerTask = manager.HandleSubAgentConnectionAsync(
          socket, ParentThreadId, agentId, mayReplayPersistedTranscript: true, testCts.Token);
        await handlerTask;

        socket.LastCloseStatus.Should().Be(WebSocketCloseStatus.PolicyViolation);
        gate.SetResult();
    }

    [Fact]
    public async Task ReceivePump_DoesNotCloseIdleConnection_WhenNoFragmentArrives()
    {
        // The assembly deadline must apply ONLY while assembling a partial message — an idle connection
        // simply waiting for the user's next message must never be closed.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new ScriptedSubAgentProvider((messages, ct) => TwoMessagesThenBlock(gate.Task, ct));

        await using var loop = CreateParentLoop(() => provider);
        await using var pool = CreatePoolReturning(loop);
        RegisterParent(pool);

        var spawnJson = await loop.SubAgentManager!.SpawnAsync(
          TemplateName, "do work", name: "alpha", runInBackground: true);
        var agentId = ParseAgentId(spawnJson);

        var manager = CreateManager(pool);
        manager.InboundAssemblyDeadline = TimeSpan.FromMilliseconds(200);
        var socket = new FakeWebSocket(); // No frames enqueued: the connection stays idle.
        using var testCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var handlerTask = manager.HandleSubAgentConnectionAsync(
          socket, ParentThreadId, agentId, mayReplayPersistedTranscript: true, testCts.Token);

        // Wait well past the assembly deadline; an idle wait must NOT close the socket.
        await Task.Delay(TimeSpan.FromMilliseconds(700), testCts.Token);
        socket.CloseAsyncCalled.Should().BeFalse("an idle connection must not be closed by the assembly deadline");
        handlerTask.IsCompleted.Should().BeFalse("an idle connection must stay open");

        await testCts.CancelAsync();
        await handlerTask;
        gate.SetResult();
    }

    [Fact]
    public async Task ReceivePump_NeverLogsPromptBody_OnSubAgentRelayPath()
    {
        // EUII: the sub-agent relay path must log only content-free metadata, never the prompt body.
        const string sentinel = "SENTINEL-SECRET-9f83c2a1-prompt-body";
        var firstRunGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new RecordingSubAgentProvider(firstRunGate.Task, blockOnPromptText: "seed-task");

        await using var loop = CreateParentLoop(() => provider);
        await using var pool = CreatePoolReturning(loop);
        RegisterParent(pool);

        var spawnJson = await loop.SubAgentManager!.SpawnAsync(
          TemplateName, "seed-task", name: "alpha", runInBackground: true);
        var agentId = ParseAgentId(spawnJson);
        await provider.WaitUntilAsync(() => provider.ReceivedContains("seed-task"), TimeSpan.FromSeconds(30));

        var capture = new CapturingLogger<ChatWebSocketManager>();
        var manager = CreateManager(pool, capture);
        var socket = new FakeWebSocket();
        socket.EnqueueTextFrame(JsonSerializer.Serialize(new ChatRequest(sentinel)));
        using var testCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var handlerTask = manager.HandleSubAgentConnectionAsync(
          socket, ParentThreadId, agentId, mayReplayPersistedTranscript: true, testCts.Token);
        firstRunGate.SetResult();
        await provider.WaitUntilAsync(() => provider.ReceivedContains(sentinel), TimeSpan.FromSeconds(30));

        await testCts.CancelAsync();
        await handlerTask;

        capture.Entries.Should().NotContain(e => e.Contains(sentinel, StringComparison.Ordinal),
          "the relay path must never log the prompt body");
    }

    [Fact]
    public async Task ReceivePump_SendsStructuredRelayError_WhenRelayFailsTransiently_AndKeepsLoopAlive()
    {
        // A transient relay failure must surface a structured, correlated error frame to the client
        // (input is not silently lost) while the receive loop stays alive for subsequent frames.
        // Deterministic transient failure: with a SINGLE concurrency slot, restarting a FINISHED child
        // cannot acquire a slot (held by a second, still-running child), so SendMessageAsync throws
        // InvalidOperationException synchronously — a transient failure the relay must not swallow.
        var betaGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var creation = 0;
        Func<IStreamingAgent> factory = () =>
        {
            var call = Interlocked.Increment(ref creation);
            return call == 1
              ? new ScriptedSubAgentProvider((messages, ct) => OneMessageThenComplete())
              : new ScriptedSubAgentProvider((messages, ct) => TwoMessagesThenBlock(betaGate.Task, ct));
        };

        await using var loop = CreateParentLoop(factory, maxConcurrent: 1);
        await using var pool = CreatePoolReturning(loop);
        RegisterParent(pool);

        using var testCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var spawnJson = await loop.SubAgentManager!.SpawnAsync(
          TemplateName, "do work", name: "alpha", runInBackground: true);
        var agentId = ParseAgentId(spawnJson);
        _ = await loop.SubAgentManager!.ObserveCompletionAsync(agentId, testCts.Token);

        // A second child holds the single concurrency slot so alpha's restart cannot acquire one.
        _ = await loop.SubAgentManager!.SpawnAsync(
          TemplateName, "hold-slot", name: "beta", runInBackground: true);

        var manager = CreateManager(pool);
        var socket = new FakeWebSocket();
        socket.EnqueueTextFrame(JsonSerializer.Serialize(new ChatRequest("relayed-one")));

        var handlerTask = manager.HandleSubAgentConnectionAsync(
          socket, ParentThreadId, agentId, mayReplayPersistedTranscript: true, testCts.Token);

        // A structured relay_failed error frame must reach the client (not silent) ...
        await socket.WaitUntilAsync(() => socket.SentContains("\"code\":\"relay_failed\""), testCts.Token);
        // ... and the connection must stay open (the receive loop was not torn down).
        handlerTask.IsCompleted.Should().BeFalse("a transient relay failure must not end the connection");

        var frame = socket.SentFrames.First(f => f.Contains("\"code\":\"relay_failed\"", StringComparison.Ordinal));
        frame.Should().Contain("\"$type\":\"error\"");
        frame.Should().Contain(agentId);

        await testCts.CancelAsync();
        betaGate.SetResult();
        await handlerTask;
    }

    [Fact]
    public async Task PumpSubAgentStream_OnNonCancellationSourceFault_SendsStructuredError_AndClosesAbnormally()
    {
        // Finding #6: a NON-cancellation fault from the sub-agent message source (or serialization)
        // must surface a content-free structured `subagent_stream_failed` frame and close with an
        // ABNORMAL status, so the client can tell a hard failure apart from a clean backpressure close.
        const string secret = "SENTINEL-STREAM-FAULT-7c19-do-not-leak";
        const string agentId = "alpha";

        var socket = new FakeWebSocket();
        var connection = new WebSocketConnectionRegistry().Register($"subagent-{agentId}", socket);
        var manager = CreateManager(EmptyPool());
        using var testCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await manager.PumpSubAgentStreamAsync(
          connection, OneMessageThenThrow(secret), agentId, testCts.Token);

        socket.SentContains("\"code\":\"subagent_stream_failed\"").Should()
          .BeTrue("a hard sub-agent stream failure must surface a structured error frame");

        var frame = socket.SentFrames.First(
          f => f.Contains("\"code\":\"subagent_stream_failed\"", StringComparison.Ordinal));
        frame.Should().Contain("\"$type\":\"error\"");
        frame.Should().Contain(agentId);
        frame.Should().NotContain(secret, "the error frame must be content-free (no exception detail)");

        socket.LastCloseStatus.Should().Be(WebSocketCloseStatus.InternalServerError,
          "a hard failure must close abnormally, not with NormalClosure");
        socket.LastCloseStatus.Should().NotBe(WebSocketCloseStatus.NormalClosure);
    }

    [Fact]
    public async Task PumpSubAgentStream_OnCancellation_ClosesCleanly_WithoutErrorFrame()
    {
        // Finding #6: caller cancellation is the NORMAL teardown path — it must NOT be treated as a
        // stream failure. No `subagent_stream_failed` frame and no abnormal close from the pump wrapper
        // (the route performs the clean NormalClosure).
        const string agentId = "alpha";

        var socket = new FakeWebSocket();
        var connection = new WebSocketConnectionRegistry().Register($"subagent-{agentId}", socket);
        var manager = CreateManager(EmptyPool());
        using var cts = new CancellationTokenSource();

        var pumpTask = manager.PumpSubAgentStreamAsync(
          connection, BlockUntilCancelled(cts.Token), agentId, cts.Token);

        await cts.CancelAsync();
        await pumpTask;

        socket.SentContains("subagent_stream_failed").Should()
          .BeFalse("cancellation is normal teardown, not a stream failure");
        socket.CloseAsyncCalled.Should().BeFalse("the pump wrapper must not close on cancellation");
        socket.LastCloseStatus.Should().NotBe(WebSocketCloseStatus.InternalServerError);
    }

    // ----- Task 3: stream_recovery resync control (shared by BOTH the primary /ws pump,
    // StreamMessagesToClientAsync, and this sub-agent focus pump, PumpSubAgentStreamAsync - both
    // route through the same private PumpMessagesToClientAsync with no per-caller branching, so
    // exercising it here proves parity for both) -----

    [Fact]
    public async Task PumpSubAgentStream_OnStreamRecoveryMessage_SendsContentFreeFrame_ClosesResyncRequired_NeverEmitsDone()
    {
        // A StreamRecoveryMessage is the terminal control MultiTurnAgentBase.PublishToSubscriber yields
        // when a slow subscriber's bounded channel is dropped (see MultiTurnAgentBase.SubscribeAsync). It
        // is NOT a run completion, so the shared pump must forward it as a content-free frame, close the
        // socket with the dedicated "resync_required" reason, and never emit the {"$type":"done"}
        // sentinel that an ordinary RunCompletedMessage would trigger.
        const string agentId = "alpha";
        const string secretPromptText = "SENTINEL-PROMPT-b3f1-must-not-reach-recovery-frame";

        var socket = new FakeWebSocket();
        var connection = new WebSocketConnectionRegistry().Register($"subagent-{agentId}", socket);
        var manager = CreateManager(EmptyPool());
        using var testCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await manager.PumpSubAgentStreamAsync(
          connection,
          TextThenRecovery(
            secretPromptText,
            new StreamRecoveryMessage("thread-1", "run-1", "gen-1", StreamRecoveryReason.SlowConsumer)),
          agentId,
          testCts.Token);

        // Exactly the preceding content frame plus the recovery frame - no done sentinel.
        socket.SentFrames.Should().HaveCount(2, "the ordinary delta plus the recovery frame, and nothing else");
        socket.SentContains("\"$type\":\"done\"").Should().BeFalse("a dropped subscriber must never see the done sentinel");

        var recoveryFrame = socket.SentFrames[1];
        recoveryFrame.Should().NotContain(secretPromptText, "the recovery control must carry no conversation content");

        using var doc = JsonDocument.Parse(recoveryFrame);
        doc.RootElement.GetProperty("$type").GetString().Should().Be("stream_recovery");
        doc.RootElement.GetProperty("reason").GetString().Should().Be("slow_consumer");
        doc.RootElement.GetProperty("threadId").GetString().Should().Be("thread-1");
        doc.RootElement.GetProperty("runId").GetString().Should().Be("run-1");
        doc.RootElement.GetProperty("generationId").GetString().Should().Be("gen-1");

        // Exact wire contract: content-free means ONLY these five properties - not merely "these are
        // present", but nothing else (e.g. IMessage.role/fromAgent/metadata) leaks alongside them.
        doc.RootElement.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(
          ["$type", "reason", "threadId", "runId", "generationId"],
          "a dropped subscriber's recovery control must carry identifiers and reason only");

        socket.LastCloseStatus.Should().Be(WebSocketCloseStatus.NormalClosure);
        socket.LastCloseStatusDescription.Should().Be("resync_required");
        socket.CloseAsyncCalled.Should().BeTrue("the connection must close deliberately after the recovery frame");
    }

    [Fact]
    public async Task PumpSubAgentStream_OnStreamRecoveryMessage_StopsPumping_AnyLaterSourceMessagesAreNeverSent()
    {
        // The recovery control is terminal: even if the wrapped source enumerates further messages after
        // it (defensive - MultiTurnAgentBase.SubscribeAsync never does this, but the pump itself must not
        // rely on that), PumpMessagesToClientAsync must break immediately and send nothing more.
        const string agentId = "beta";
        const string laterSecret = "SENTINEL-LATER-9c02-must-never-be-sent-after-recovery";

        var socket = new FakeWebSocket();
        var connection = new WebSocketConnectionRegistry().Register($"subagent-{agentId}", socket);
        var manager = CreateManager(EmptyPool());
        using var testCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await manager.PumpSubAgentStreamAsync(
          connection,
          RecoveryThenMoreMessages(laterSecret),
          agentId,
          testCts.Token);

        socket.SentFrames.Should().ContainSingle("the recovery frame is terminal; nothing after it is ever pumped");
        socket.SentContains(laterSecret).Should()
          .BeFalse("a message enumerated after the recovery control must never reach the client");
        socket.CloseAsyncCalled.Should().BeTrue();
        socket.LastCloseStatusDescription.Should().Be("resync_required");
    }

    [Fact]
    public async Task PumpSubAgentStream_OnStreamRecoveryMessage_ClosesResyncRequired_EvenWhenConnectionTokenCancelledRightAfterTheSend()
    {
        // Finding #2: a cancellation landing between the recovery frame's send and its deliberate close
        // must not be able to suppress that close (and the "resync_required" reason it carries) - the
        // close must use its own uncancellable token, the same way SendSubAgentStreamFailedErrorAsync
        // already does for its terminal close. FakeWebSocket.CloseAsync throws OperationCanceledException
        // for an already-cancelled token (as a real socket would); PumpMessagesToClientAsync swallows that
        // exception internally as normal teardown, so - if the close call used the connection's (now
        // cancelled) token - the close would silently never happen: CloseAsyncCalled would stay false and
        // "resync_required" would never reach the client.
        const string agentId = "gamma";
        var socket = new FakeWebSocket();
        var connection = new WebSocketConnectionRegistry().Register($"subagent-{agentId}", socket);
        var manager = CreateManager(EmptyPool());
        using var testCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // Simulate the race: cancel the connection token the instant the recovery frame's send completes,
        // before the pump's own close call runs.
        socket.OnSend = text =>
        {
            if (text.Contains("stream_recovery", StringComparison.Ordinal))
            {
                testCts.Cancel();
            }
        };

        await manager.PumpSubAgentStreamAsync(
          connection,
          RecoveryThenMoreMessages("irrelevant"),
          agentId,
          testCts.Token);

        socket.CloseAsyncCalled.Should().BeTrue(
          "a cancellation racing right after the recovery frame's send must not suppress the deliberate close");
        socket.LastCloseStatusDescription.Should().Be("resync_required");
    }

    [Fact]
    public async Task PumpSubAgentStream_OnReplayTruncatedRecovery_ForwardsFrame_KeepsPumping_NeverCloses()
    {
        // The truncated-replay advisory is NON-terminal (see StreamRecoveryReason.ReplayTruncated): only
        // the run's already-published PREFIX is missing, and the same subscription goes on to carry the
        // live tail. Closing here would force every consumer to reconnect, land on the same still-
        // truncated buffer, and be advised again for the rest of the run - a reconnect storm. So the pump
        // must forward the frame and KEEP GOING; only the slow-consumer drop (which really is terminal)
        // gets the "resync_required" close.
        const string agentId = "delta";
        const string liveTailText = "SENTINEL-LIVE-TAIL-71ab-must-reach-the-client";

        var socket = new FakeWebSocket();
        var connection = new WebSocketConnectionRegistry().Register($"subagent-{agentId}", socket);
        var manager = CreateManager(EmptyPool());
        using var testCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await manager.PumpSubAgentStreamAsync(
          connection,
          TruncationAdvisoryThenLiveTail(liveTailText),
          agentId,
          testCts.Token);

        socket.SentFrames.Should().HaveCount(2, "the advisory frame plus the live tail that follows it");

        using var doc = JsonDocument.Parse(socket.SentFrames[0]);
        doc.RootElement.GetProperty("$type").GetString().Should().Be("stream_recovery");
        doc.RootElement.GetProperty("reason").GetString().Should().Be("replay_truncated");

        socket.SentContains(liveTailText).Should()
          .BeTrue("the live tail after a truncated-replay advisory must still reach the client");
        socket.CloseAsyncCalled.Should().BeFalse(
          "a non-terminal advisory must not tear the socket down - that is what causes the reconnect storm");
        socket.LastCloseStatusDescription.Should().NotBe("resync_required");
    }

    [Fact]
    public void LogSubAgentRelayFailure_NeverLeaksExceptionText_WhenExceptionMessageCarriesSecret()
    {
        // Finding #793 (EUII): the relay-failure log must record only a stable category + content-free
        // identifiers, never the exception (its Message/ToString can echo prompt/transcript/tool
        // content). The capturing logger now inspects BOTH the formatted state AND exception text.
        const string sentinel = "SENTINEL-SECRET-relay-transcript-4d2a";
        var capture = new CapturingLogger<ChatWebSocketManager>();
        var manager = CreateManager(EmptyPool(), capture);

        // The exception message carries the secret (as a downstream provider/store fault would); the
        // type name is content-free.
        manager.LogSubAgentRelayFailure("alpha", byteCount: 42, new InvalidOperationException(sentinel));

        capture.Entries.Should().NotBeEmpty("the relay failure must still be logged (category only)");
        capture.Entries.Should().Contain(e => e.Contains("relay_failed", StringComparison.Ordinal),
          "the stable category must be logged");
        capture.Entries.Should().NotContain(e => e.Contains(sentinel, StringComparison.Ordinal),
          "no captured entry (state OR exception text) may contain the secret");
    }

    // ----- helpers -----

    private static ChatWebSocketManager CreateManager(
      MultiTurnAgentPool pool,
      ILogger<ChatWebSocketManager>? logger = null,
      IConversationStore? store = null) =>
      new(
        pool,
        new WebSocketConnectionRegistry(),
        new LmStreaming.Sample.Services.WorkflowRunRegistry(),
        new PendingAuthCoordinator(Mock.Of<IAuthEventNotifier>(), new AuthOptions(), NullLogger<PendingAuthCoordinator>.Instance),
        store ?? new InMemoryConversationStore(),
        logger ?? NullLogger<ChatWebSocketManager>.Instance);

    /// <summary>
    /// Captures every formatted log message AND the exception text (<c>exception?.ToString()</c>) so
    /// tests can assert on the ABSENCE of content across BOTH the message template state and any
    /// exception handed to the logger — a real logging provider serializes the exception, so a leak via
    /// the exception object (not the formatted state) must be caught too.
    /// </summary>
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly List<string> _entries = [];
        private readonly Lock _lock = new();

        public IReadOnlyList<string> Entries
        {
            get { lock (_lock) { return [.. _entries]; } }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
          LogLevel logLevel,
          EventId eventId,
          TState state,
          Exception? exception,
          Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            lock (_lock)
            {
                _entries.Add(message);
                if (exception is not null)
                {
                    _entries.Add(exception.ToString());
                }
            }
        }
    }

    private static MultiTurnAgentLoop CreateParentLoop(Func<IStreamingAgent> childFactory, int maxConcurrent = 5)
    {
        var options = new SubAgentOptions
        {
            Templates = new Dictionary<string, SubAgentTemplate>
            {
                [TemplateName] = new SubAgentTemplate
                {
                    Name = TemplateName,
                    SystemPrompt = "You are a worker.",
                    AgentFactory = childFactory,
                },
            },
            MaxConcurrentSubAgents = maxConcurrent,
        };

        return new MultiTurnAgentLoop(
          new ScriptedSubAgentProvider((messages, ct) => EmptyStream()),
          new FunctionRegistry(),
          threadId: ParentThreadId,
          subAgentOptions: options);
    }

    private static MultiTurnAgentPool CreatePoolReturning(IMultiTurnAgent agent) =>
      new((_, _, _) => new MultiTurnAgentPool.AgentCreationResult(agent), NullLogger<MultiTurnAgentPool>.Instance);

    /// <summary>A pool whose creation factory is never invoked — for tests exercising only the seam
    /// methods (pump/relay logging) that do not resolve an agent.</summary>
    private static MultiTurnAgentPool EmptyPool() =>
      new((_, _, _) => throw new InvalidOperationException("agent creation is unused in this test"),
        NullLogger<MultiTurnAgentPool>.Instance);

    private static void RegisterParent(MultiTurnAgentPool pool) =>
      _ = pool.GetOrCreateAgent(ParentThreadId, SystemChatModes.GetById(SystemChatModes.DefaultModeId)!);

    private static string ParseAgentId(string spawnJson)
    {
        using var doc = JsonDocument.Parse(spawnJson);
        return doc.RootElement.GetProperty("agent_id").GetString()!;
    }

    private static async IAsyncEnumerable<IMessage> TwoMessagesThenBlock(
      Task gate, [EnumeratorCancellation] CancellationToken ct)
    {
        yield return new TextMessage { Role = Role.Assistant, Text = "chunk-one" };
        yield return new TextMessage { Role = Role.Assistant, Text = "chunk-two" };
        await gate.WaitAsync(ct);
    }

    private static async IAsyncEnumerable<IMessage> EmptyStream()
    {
        await Task.CompletedTask;
        yield break;
    }

    private static async IAsyncEnumerable<IMessage> OneMessageThenComplete()
    {
        await Task.CompletedTask;
        yield return new TextMessage { Role = Role.Assistant, Text = "ack" };
    }

    /// <summary>Yields one frame, then faults with a NON-cancellation exception whose message carries a
    /// secret — models a source-enumeration/serialization fault that must NOT reach the client frame.</summary>
    private static async IAsyncEnumerable<IMessage> OneMessageThenThrow(string secret)
    {
        await Task.CompletedTask;
        yield return new TextMessage { Role = Role.Assistant, Text = "chunk-one" };
        throw new InvalidOperationException(secret);
    }

    /// <summary>Yields one ordinary content frame, then the terminal recovery control (Task 3) - models a
    /// dropped subscriber whose already-buffered backlog is delivered before the resync signal.</summary>
    private static async IAsyncEnumerable<IMessage> TextThenRecovery(string precedingText, StreamRecoveryMessage recovery)
    {
        await Task.CompletedTask;
        yield return new TextMessage { Role = Role.Assistant, Text = precedingText };
        yield return recovery;
    }

    /// <summary>Yields the terminal recovery control first, then a further message - proves the pump
    /// stops at the recovery control regardless of what the source does afterward.</summary>
    private static async IAsyncEnumerable<IMessage> RecoveryThenMoreMessages(string laterText)
    {
        await Task.CompletedTask;
        yield return new StreamRecoveryMessage("thread-2", "run-2", "gen-2", StreamRecoveryReason.SlowConsumer);
        yield return new TextMessage { Role = Role.Assistant, Text = laterText };
    }

    /// <summary>Yields the NON-terminal truncated-replay advisory first, then the run's live tail -
    /// exactly the shape <c>MultiTurnAgentBase.SubscribeAsync</c> produces for a subscription that joins
    /// a run whose replay buffer has been capped.</summary>
    private static async IAsyncEnumerable<IMessage> TruncationAdvisoryThenLiveTail(string laterText)
    {
        await Task.CompletedTask;
        yield return new StreamRecoveryMessage("thread-3", "run-3", "gen-3", StreamRecoveryReason.ReplayTruncated);
        yield return new TextMessage { Role = Role.Assistant, Text = laterText };
    }

    /// <summary>Blocks until the enumeration is cancelled (the normal-teardown path).</summary>
    private static async IAsyncEnumerable<IMessage> BlockUntilCancelled(
      [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Delay(Timeout.Infinite, ct);
        yield break;
    }

    /// <summary>
    /// A provider whose stream is scripted per invocation. Only <c>GenerateReplyStreamingAsync</c> is
    /// exercised by <see cref="MultiTurnAgentLoop"/>.
    /// </summary>
    private sealed class ScriptedSubAgentProvider : IStreamingAgent
    {
        private readonly Func<IEnumerable<IMessage>, CancellationToken, IAsyncEnumerable<IMessage>> _script;

        public ScriptedSubAgentProvider(Func<IEnumerable<IMessage>, CancellationToken, IAsyncEnumerable<IMessage>> script)
          => _script = script;

        public Task<IEnumerable<IMessage>> GenerateReplyAsync(
          IEnumerable<IMessage> messages,
          GenerateReplyOptions? options = null,
          CancellationToken cancellationToken = default)
          => Task.FromResult<IEnumerable<IMessage>>([]);

        public Task<IAsyncEnumerable<IMessage>> GenerateReplyStreamingAsync(
          IEnumerable<IMessage> messages,
          GenerateReplyOptions? options = null,
          CancellationToken cancellationToken = default)
          => Task.FromResult(_script(messages, cancellationToken));
    }

    /// <summary>
    /// A provider that records every distinct user prompt it is invoked with. Its first run (whose
    /// last user text equals <c>blockOnPromptText</c>) blocks on a gate so the child stays
    /// long-running; every later run completes immediately so injected/relayed prompts drain.
    /// </summary>
    private sealed class RecordingSubAgentProvider : IStreamingAgent
    {
        private readonly Task _blockGate;
        private readonly string _blockOnPromptText;
        private readonly HashSet<string> _received = [];
        private readonly Lock _lock = new();
        private readonly SemaphoreSlim _activity = new(0);

        public RecordingSubAgentProvider(Task blockGate, string blockOnPromptText)
        {
            _blockGate = blockGate;
            _blockOnPromptText = blockOnPromptText;
        }

        public IReadOnlyList<string> ReceivedPrompts
        {
            get { lock (_lock) { return [.. _received]; } }
        }

        public bool ReceivedContains(string text)
        {
            lock (_lock) { return _received.Contains(text); }
        }

        public async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            while (!condition())
            {
                await _activity.WaitAsync(cts.Token);
            }
        }

        public Task<IEnumerable<IMessage>> GenerateReplyAsync(
          IEnumerable<IMessage> messages,
          GenerateReplyOptions? options = null,
          CancellationToken cancellationToken = default)
          => Task.FromResult<IEnumerable<IMessage>>([]);

        public Task<IAsyncEnumerable<IMessage>> GenerateReplyStreamingAsync(
          IEnumerable<IMessage> messages,
          GenerateReplyOptions? options = null,
          CancellationToken cancellationToken = default)
        {
            var userTexts = messages
              .OfType<TextMessage>()
              .Where(m => m.Role == Role.User && !string.IsNullOrEmpty(m.Text))
              .Select(m => m.Text)
              .ToList();

            lock (_lock)
            {
                foreach (var t in userTexts)
                {
                    _ = _received.Add(t);
                }
            }
            _ = _activity.Release();

            var shouldBlock = userTexts.Count > 0 && userTexts[^1] == _blockOnPromptText;
            return Task.FromResult(Stream(shouldBlock, cancellationToken));
        }

        private async IAsyncEnumerable<IMessage> Stream(
          bool shouldBlock, [EnumeratorCancellation] CancellationToken ct)
        {
            yield return new TextMessage { Role = Role.Assistant, Text = "ack" };
            if (shouldBlock)
            {
                await _blockGate.WaitAsync(ct);
            }
        }
    }

    /// <summary>
    /// A minimal in-memory <see cref="System.Net.WebSockets.WebSocket"/> test double: captures every
    /// outbound text frame, feeds inbound frames from an in-memory queue, and tracks how many frames
    /// the receive loop has dequeued (to prove non-blocking relaying).
    /// </summary>
    private sealed class FakeWebSocket : System.Net.WebSockets.WebSocket
    {
        private readonly Channel<InboundFrame> _inbound =
          Channel.CreateUnbounded<InboundFrame>(new UnboundedChannelOptions { SingleReader = true });

        private readonly List<string> _sent = [];
        private readonly Lock _lock = new();
        private readonly SemaphoreSlim _activity = new(0);
        private WebSocketState _state = WebSocketState.Open;
        private int _receivedFrameCount;
        private InboundFrame? _current;
        private int _currentOffset;

        private readonly record struct InboundFrame(byte[] Payload, WebSocketMessageType Type, bool EndOfMessage);

        public bool CloseAsyncCalled { get; private set; }

        public WebSocketCloseStatus? LastCloseStatus { get; private set; }

        /// <summary>Captures the close description passed to CloseAsync (Task 3: proves a
        /// stream_recovery close uses the dedicated "resync_required" reason, not an ad-hoc string).</summary>
        public string? LastCloseStatusDescription { get; private set; }

        public int ReceivedFrameCount => Volatile.Read(ref _receivedFrameCount);

        public IReadOnlyList<string> SentFrames
        {
            get { lock (_lock) { return [.. _sent]; } }
        }

        /// <summary>
        /// Test seam (Task 3, Finding #2): invoked synchronously with each sent frame's text, after it is
        /// recorded, letting a test simulate a real socket's send completing right before the caller's
        /// cancellation token is cancelled - e.g. to prove a subsequent close call must use its own,
        /// uncancellable token rather than the connection's, or the close (and its "resync_required"
        /// reason) can be silently lost to <see cref="OperationCanceledException"/>.
        /// </summary>
        public Action<string>? OnSend { get; set; }

        public bool SentContains(string fragment)
        {
            lock (_lock) { return _sent.Any(f => f.Contains(fragment, StringComparison.Ordinal)); }
        }

        public void EnqueueTextFrame(string text) =>
          _inbound.Writer.TryWrite(
            new InboundFrame(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, EndOfMessage: true));

        /// <summary>Enqueues a text fragment; set <paramref name="endOfMessage"/> false to split a message.</summary>
        public void EnqueueTextFragment(byte[] payload, bool endOfMessage) =>
          _inbound.Writer.TryWrite(new InboundFrame(payload, WebSocketMessageType.Text, endOfMessage));

        /// <summary>Enqueues a single binary frame (rejected by the text-only receive pump).</summary>
        public void EnqueueBinaryFrame(byte[] payload) =>
          _inbound.Writer.TryWrite(new InboundFrame(payload, WebSocketMessageType.Binary, EndOfMessage: true));

        public async Task WaitUntilAsync(Func<bool> condition, CancellationToken ct)
        {
            while (!condition())
            {
                await _activity.WaitAsync(ct);
            }
        }

        public override WebSocketState State => _state;
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override string? SubProtocol => null;

        public override Task SendAsync(
          ArraySegment<byte> buffer,
          WebSocketMessageType messageType,
          bool endOfMessage,
          CancellationToken cancellationToken)
        {
            var text = Encoding.UTF8.GetString(buffer.Array!, buffer.Offset, buffer.Count);
            lock (_lock) { _sent.Add(text); }
            _ = _activity.Release();
            OnSend?.Invoke(text);
            return Task.CompletedTask;
        }

        public override async Task<WebSocketReceiveResult> ReceiveAsync(
          ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            if (_current is null)
            {
                _current = await _inbound.Reader.ReadAsync(cancellationToken);
                _currentOffset = 0;
            }

            _ = Interlocked.Increment(ref _receivedFrameCount);
            _ = _activity.Release();

            var frame = _current.Value;

            if (frame.Type == WebSocketMessageType.Close)
            {
                _current = null;
                _state = WebSocketState.CloseReceived;
                return new WebSocketReceiveResult(0, WebSocketMessageType.Close, endOfMessage: true);
            }

            var remaining = frame.Payload.Length - _currentOffset;
            var count = Math.Min(remaining, buffer.Count);
            Array.Copy(frame.Payload, _currentOffset, buffer.Array!, buffer.Offset, count);
            _currentOffset += count;

            bool endOfMessage;
            if (_currentOffset >= frame.Payload.Length)
            {
                // This frame is fully delivered: honor its own end-of-message marker (false when the
                // caller split a single logical message into multiple fragments).
                endOfMessage = frame.EndOfMessage;
                _current = null;
            }
            else
            {
                // The frame was larger than the receive buffer, so more chunks of it remain.
                endOfMessage = false;
            }

            return new WebSocketReceiveResult(count, frame.Type, endOfMessage);
        }

        public override Task CloseAsync(
            WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            // A real WebSocket implementation throws OperationCanceledException for an
            // already-cancelled token (Task 3, Finding #2's close-seam test relies on this).
            cancellationToken.ThrowIfCancellationRequested();
            CloseAsyncCalled = true;
            LastCloseStatus = closeStatus;
            LastCloseStatusDescription = statusDescription;
            _state = WebSocketState.Closed;
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(
          WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            _state = WebSocketState.Closed;
            return Task.CompletedTask;
        }

        public override void Abort() => _state = WebSocketState.Aborted;

        public override void Dispose() { }
    }
}
