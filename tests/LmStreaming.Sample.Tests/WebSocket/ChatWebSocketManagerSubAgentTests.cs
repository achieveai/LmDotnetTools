using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using LmStreaming.Sample.WebSocket;

namespace LmStreaming.Sample.Tests.WebSocket;

/// <summary>
/// Tests for the focused sub-agent WebSocket handler (WI #194, Task 4):
/// <see cref="ChatWebSocketManager.HandleSubAgentConnectionAsync"/>. The handler is
/// presentation-only — it streams a FOCUSED child sub-agent's live+replayed output to the client
/// (reusing the parent's <c>StreamMessagesToClientAsync</c>) and relays inbound text frames to the
/// child via <see cref="SubAgentManager.SendMessageAsync"/> in background mode. It never touches
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
          socket, ParentThreadId, agentId, testCts.Token);

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
          socket, ParentThreadId, agentId, testCts.Token);

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
          socket, ParentThreadId, agentId, testCts.Token);

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
          socket, ParentThreadId, "does-not-exist", testCts.Token);

        socket.SentFrames.Should().ContainSingle();
        var frame = socket.SentFrames[0];
        frame.Should().Contain("\"code\":\"subagent_unavailable\"");
        frame.Should().Contain("does-not-exist");
        socket.CloseAsyncCalled.Should().BeTrue("the socket must be closed after the structured error");
    }

    // ----- helpers -----

    private static ChatWebSocketManager CreateManager(MultiTurnAgentPool pool) =>
      new(
        pool,
        new WebSocketConnectionRegistry(),
        new PendingAuthCoordinator(Mock.Of<IAuthEventNotifier>(), new AuthOptions(), NullLogger<PendingAuthCoordinator>.Instance),
        NullLogger<ChatWebSocketManager>.Instance);

    private static MultiTurnAgentLoop CreateParentLoop(Func<IStreamingAgent> childFactory)
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
            MaxConcurrentSubAgents = 5,
        };

        return new MultiTurnAgentLoop(
          new ScriptedSubAgentProvider((messages, ct) => EmptyStream()),
          new FunctionRegistry(),
          threadId: ParentThreadId,
          subAgentOptions: options);
    }

    private static MultiTurnAgentPool CreatePoolReturning(IMultiTurnAgent agent) =>
      new((_, _, _) => new MultiTurnAgentPool.AgentCreationResult(agent), NullLogger<MultiTurnAgentPool>.Instance);

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

        private readonly record struct InboundFrame(byte[] Payload, WebSocketMessageType Type);

        public bool CloseAsyncCalled { get; private set; }

        public int ReceivedFrameCount => Volatile.Read(ref _receivedFrameCount);

        public IReadOnlyList<string> SentFrames
        {
            get { lock (_lock) { return [.. _sent]; } }
        }

        public bool SentContains(string fragment)
        {
            lock (_lock) { return _sent.Any(f => f.Contains(fragment, StringComparison.Ordinal)); }
        }

        public void EnqueueTextFrame(string text) =>
          _inbound.Writer.TryWrite(new InboundFrame(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text));

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
            return Task.CompletedTask;
        }

        public override async Task<WebSocketReceiveResult> ReceiveAsync(
          ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            var frame = await _inbound.Reader.ReadAsync(cancellationToken);
            _ = Interlocked.Increment(ref _receivedFrameCount);
            _ = _activity.Release();

            if (frame.Type == WebSocketMessageType.Close)
            {
                _state = WebSocketState.CloseReceived;
                return new WebSocketReceiveResult(0, WebSocketMessageType.Close, endOfMessage: true);
            }

            var count = Math.Min(frame.Payload.Length, buffer.Count);
            Array.Copy(frame.Payload, 0, buffer.Array!, buffer.Offset, count);
            return new WebSocketReceiveResult(count, frame.Type, endOfMessage: true);
        }

        public override Task CloseAsync(
          WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            CloseAsyncCalled = true;
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
