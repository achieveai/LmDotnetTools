using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmMultiTurn.ClientTools;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using AchieveAi.LmDotnetTools.LmTestUtils;
using LmStreaming.Sample.WebSocket;

namespace LmStreaming.Sample.Tests.WebSocket;

/// <summary>
/// Tests for the inbound <c>client_tool_result</c> frame handling (issue #246) added to
/// <see cref="ChatWebSocketManager"/> on BOTH the primary (<c>HandleConnectionAsync</c>) and
/// sub-agent (<c>HandleSubAgentConnectionAsync</c>) paths. The frame resolves a previously-deferred
/// client-hosted tool call (e.g. <see cref="AskUserQuestionToolProvider"/>) via
/// <c>MultiTurnAgentLoop.TryResolveToolCallAsync</c>; the target thread/loop is derived from the
/// ROUTE (threadId / sub-agent id), never from the payload. Covers the full
/// <c>ResolveToolCallOutcome</c> → ack/error mapping, malformed-frame validation, and the regression
/// that an ordinary <see cref="ChatRequest"/> frame (no <c>$type</c>) still flows through unchanged.
/// </summary>
public sealed class ChatWebSocketManagerClientToolResultTests
{
    private const string SubAgentParentThreadId = "ctr-parent-thread";
    private const string SubAgentTemplateName = "asker";

    // ----- primary path (HandleConnectionAsync) -----

    [Fact]
    public async Task PrimaryPath_ValidResolution_SendsAckResolved()
    {
        const string threadId = "ctr-primary-resolved";
        const string toolCallId = "tc_1";
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var pool = await CreatePoolWithParkedAskUserQuestionAsync(threadId, toolCallId, ct.Token);

        var manager = CreateManager(pool);
        var socket = new FakeWebSocket();
        socket.EnqueueTextFrame(
            /*lang=json,strict*/
            """{"$type":"client_tool_result","toolCallId":"tc_1","result":"blue","isError":false}""");

        var handlerTask = manager.HandleConnectionAsync(
            socket, threadId, null, null, null, null, ct.Token);

        await socket.WaitUntilAsync(() => socket.SentContains("client_tool_result_ack"), ct.Token);

        await ct.CancelAsync();
        await handlerTask;

        var frame = socket.SentFrames.First(f => f.Contains("client_tool_result_ack"));
        frame.Should().Contain("\"toolCallId\":\"tc_1\"");
        frame.Should().Contain("\"status\":\"resolved\"");

        pool.TryGet(threadId, out var agent).Should().BeTrue();
        (await ((MultiTurnAgentLoop)agent!).GetDeferredToolCallsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task PrimaryPath_DuplicateResolution_SendsAckDuplicate()
    {
        const string threadId = "ctr-primary-duplicate";
        const string toolCallId = "tc_1";
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var pool = await CreatePoolWithParkedAskUserQuestionAsync(threadId, toolCallId, ct.Token);

        pool.TryGet(threadId, out var agent).Should().BeTrue();
        var loop = (MultiTurnAgentLoop)agent!;

        // A prior delivery already resolved this call (e.g. a REST fallback) with this exact content.
        await loop.ResolveToolCallAsync(toolCallId, "blue", isError: false);

        var manager = CreateManager(pool);
        var socket = new FakeWebSocket();
        socket.EnqueueTextFrame(
            /*lang=json,strict*/
            """{"$type":"client_tool_result","toolCallId":"tc_1","result":"blue","isError":false}""");

        var handlerTask = manager.HandleConnectionAsync(
            socket, threadId, null, null, null, null, ct.Token);

        await socket.WaitUntilAsync(() => socket.SentContains("client_tool_result_ack"), ct.Token);

        await ct.CancelAsync();
        await handlerTask;

        var frame = socket.SentFrames.First(f => f.Contains("client_tool_result_ack"));
        frame.Should().Contain("\"status\":\"duplicate\"");
    }

    [Fact]
    public async Task PrimaryPath_ConflictingResolution_SendsErrorConflict()
    {
        const string threadId = "ctr-primary-conflict";
        const string toolCallId = "tc_1";
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var pool = await CreatePoolWithParkedAskUserQuestionAsync(threadId, toolCallId, ct.Token);

        pool.TryGet(threadId, out var agent).Should().BeTrue();
        var loop = (MultiTurnAgentLoop)agent!;

        await loop.ResolveToolCallAsync(toolCallId, "blue", isError: false);

        var manager = CreateManager(pool);
        var socket = new FakeWebSocket();
        socket.EnqueueTextFrame(
            /*lang=json,strict*/
            """{"$type":"client_tool_result","toolCallId":"tc_1","result":"red","isError":false}""");

        var handlerTask = manager.HandleConnectionAsync(
            socket, threadId, null, null, null, null, ct.Token);

        await socket.WaitUntilAsync(() => socket.SentContains("client_tool_result_error"), ct.Token);

        await ct.CancelAsync();
        await handlerTask;

        var frame = socket.SentFrames.First(f => f.Contains("client_tool_result_error"));
        frame.Should().Contain("\"code\":\"conflict\"");
    }

    /// <summary>
    /// Issue #246 cancellation contract: the client cancels an <see cref="AskUserQuestionToolProvider"/>
    /// question by sending the canonical <c>{"error":"Question cancelled by user.","cancelled":true}</c>
    /// body with <c>isError:true</c>. That is an ordinary resolution as far as
    /// <c>TryResolveToolCallAsync</c> is concerned — it persists as an errored, no-longer-deferred
    /// result and legitimately wakes the parked run exactly once (the LLM turn that follows the
    /// error). What must NOT happen is a SUBSEQUENT, differently-worded "answer" for the same
    /// <c>toolCallId</c> arriving late (e.g. a slow client double-submit racing the cancel) being
    /// accepted as a second resolution: history already disagrees with it, so it must come back as
    /// a <c>conflict</c> and must not trigger a second wake/continuation of the run.
    /// </summary>
    [Fact]
    public async Task PrimaryPath_CancelledQuestion_PersistsResolvedError_AndLateAnswerConflictsWithoutResumingRun()
    {
        const string threadId = "ctr-primary-cancelled";
        const string toolCallId = "tc_1";
        const string cancelBody = /*lang=json,strict*/ """{"error":"Question cancelled by user.","cancelled":true}""";
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var toolCall = new ToolCallMessage
        {
            FunctionName = AskUserQuestionToolProvider.ToolName,
            FunctionArgs = AskUserQuestionArgs(),
            ToolCallId = toolCallId,
            Role = Role.Assistant,
        };
        var finalText = new TextMessage { Role = Role.Assistant, Text = "Understood, cancelling the question." };

        var callCount = 0;
        var mockAgent = new Mock<IStreamingAgent>();
        mockAgent
            .Setup(a => a.GenerateReplyStreamingAsync(
                It.IsAny<IEnumerable<IMessage>>(), It.IsAny<GenerateReplyOptions>(), It.IsAny<CancellationToken>()))
            .Returns<IEnumerable<IMessage>, GenerateReplyOptions, CancellationToken>((_, _, _) =>
            {
                callCount++;
                IMessage msg = callCount == 1 ? toolCall : finalText;
                return Task.FromResult(ToAsyncEnumerable([msg]));
            });

        var loop = new MultiTurnAgentLoop(mockAgent.Object, new FunctionRegistry(), threadId);
        var pool = CreatePoolReturning(loop);
        _ = pool.GetOrCreateAgent(threadId, SystemChatModes.GetById(SystemChatModes.DefaultModeId)!);

        ToolCallResultMessage? resolvedResult = null;
        var completions = 0;
        var firstRunCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondRunCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = ObserveAsync(loop, msg =>
        {
            if (msg is ToolCallResultMessage { IsDeferred: false } result && result.ToolCallId == toolCallId)
            {
                resolvedResult ??= result;
            }

            if (msg is RunCompletedMessage)
            {
                completions++;
                if (completions == 1)
                {
                    firstRunCompleted.TrySetResult();
                }
                else
                {
                    secondRunCompleted.TrySetResult();
                }
            }
        }, ct.Token);

        await loop.SendAsync([new TextMessage { Text = "Which color should I use?", Role = Role.User }]);
        await firstRunCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10), ct.Token);
        callCount.Should().Be(1);

        var manager = CreateManager(pool);
        var socket = new FakeWebSocket();
        var cancelFrame = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["$type"] = "client_tool_result",
            ["toolCallId"] = toolCallId,
            ["result"] = cancelBody,
            ["isError"] = true,
        });
        socket.EnqueueTextFrame(cancelFrame);

        var handlerTask = manager.HandleConnectionAsync(
            socket, threadId, null, null, null, null, ct.Token);

        await socket.WaitUntilAsync(() => socket.SentContains("client_tool_result_ack"), ct.Token);
        var ackFrame = socket.SentFrames.First(f => f.Contains("client_tool_result_ack"));
        ackFrame.Should().Contain("\"status\":\"resolved\"");

        // The cancellation is a legitimate resolution: it persists as an errored, no-longer-deferred
        // result and the previously-parked run is allowed to continue exactly once.
        await secondRunCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10), ct.Token);
        callCount.Should().Be(2);

        resolvedResult.Should().NotBeNull("the cancellation must persist as a resolved (non-deferred) result");
        resolvedResult!.IsError.Should().BeTrue("a cancelled question is recorded as an errored tool result");
        resolvedResult.Result.Should().Be(cancelBody, "the canonical cancellation body must be persisted verbatim");

        // A LATE, differently-worded "answer" for the SAME toolCallId must Conflict — and must not
        // wake/continue the run a second time.
        var framesBeforeLateAnswer = socket.SentFrames.Count;
        socket.EnqueueTextFrame(
            $$"""{"$type":"client_tool_result","toolCallId":"{{toolCallId}}","result":"blue","isError":false}""");

        await socket.WaitUntilAsync(
            () => socket.SentFrames.Skip(framesBeforeLateAnswer).Any(f => f.Contains("client_tool_result_error")),
            ct.Token);

        await ct.CancelAsync();
        await handlerTask;

        var lateFrame = socket.SentFrames.Skip(framesBeforeLateAnswer).First(f => f.Contains("client_tool_result_error"));
        lateFrame.Should().Contain("\"code\":\"conflict\"");

        callCount.Should().Be(
            2, "a late answer arriving after the question was already cancelled must not resume the run");
    }

    [Fact]
    public async Task PrimaryPath_UnknownToolCallId_SendsErrorNotFound()
    {
        const string threadId = "ctr-primary-notfound";
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var pool = CreatePoolReturning(SimpleLoop(threadId));
        _ = pool.GetOrCreateAgent(threadId, SystemChatModes.GetById(SystemChatModes.DefaultModeId)!);

        var manager = CreateManager(pool);
        var socket = new FakeWebSocket();
        socket.EnqueueTextFrame(
            /*lang=json,strict*/
            """{"$type":"client_tool_result","toolCallId":"does-not-exist","result":"x","isError":false}""");

        var handlerTask = manager.HandleConnectionAsync(
            socket, threadId, null, null, null, null, ct.Token);

        await socket.WaitUntilAsync(() => socket.SentContains("client_tool_result_error"), ct.Token);

        await ct.CancelAsync();
        await handlerTask;

        var frame = socket.SentFrames.First(f => f.Contains("client_tool_result_error"));
        frame.Should().Contain("\"code\":\"not_found\"");
    }

    /// <summary>
    /// Reachable race (issue #246 test-review finding): the browser already has this PRIMARY socket
    /// open and bound to <c>agent</c>, then <c>ConversationsController.Delete</c> races in and disposes
    /// that exact agent (<c>MultiTurnAgentPool.RemoveAgentAsync</c> → <c>AgentEntry.DisposeAsync</c> →
    /// <c>Agent.DisposeAsync</c>) before the in-flight <c>client_tool_result</c> frame is processed.
    /// Unlike the <c>ChatRequest</c> path, <c>HandleClientToolResultAsync</c> never re-resolves the agent
    /// from the pool — it resolves against the connection's already-captured agent reference — so
    /// this is exactly the object <c>TryResolveToolCallAsync</c>'s <c>ThrowIfDisposed()</c> guard now sees.
    /// Before the fix this threw an unhandled <see cref="ObjectDisposedException"/> out of the receive
    /// loop (no catch for it existed), so no typed frame ever reached the client and the connection's
    /// receive task simply faulted. The fix must translate it into a <c>client_tool_result_error</c> with a
    /// stable code, WITHOUT closing the connection or crashing the receive loop.
    ///
    /// This drives <c>HandleClientToolResultAsync</c> directly (it is <c>internal</c> for exactly this
    /// reason) rather than through <c>HandleConnectionAsync</c>: disposing the connection's own bound
    /// agent also completes that agent's <c>SubscribeAsync</c> subscriber channel, which legitimately
    /// (and near-instantly) ends the WHOLE connection via its <c>Task.WhenAny</c> race — before a queued
    /// inbound frame could ever reach the receive pump. Calling the handler directly isolates the exact
    /// unit the bug lives in, without racing that unrelated (and correct) teardown behavior.
    /// </summary>
    [Fact]
    public async Task PrimaryPath_AgentDisposedBeforeFrameArrives_SendsErrorNotFound_WithoutCrashingConnection()
    {
        const string threadId = "ctr-primary-disposed";
        const string toolCallId = "tc_1";
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var pool = await CreatePoolWithParkedAskUserQuestionAsync(threadId, toolCallId, ct.Token);

        pool.TryGet(threadId, out var agent).Should().BeTrue();

        // Simulate the concurrent delete: dispose the SAME agent instance the connection is bound to
        // (mirrors ConversationsController.Delete racing an in-flight client_tool_result for a
        // conversation the user just closed/deleted), then feed the frame straight into the handler
        // with that now-disposed reference — exactly what ProcessClientMessageAsync would do with its
        // already-captured `agent` field.
        await ((MultiTurnAgentLoop)agent!).DisposeAsync();

        var manager = CreateManager(pool);
        var socket = new FakeWebSocket();
        var connection = new WebSocketConnectionRegistry().Register(threadId, socket);

        await manager.HandleClientToolResultAsync(
            connection,
            agent!,
            threadId,
            /*lang=json,strict*/
            $$"""{"$type":"client_tool_result","toolCallId":"{{toolCallId}}","result":"blue","isError":false}""",
            ct.Token);

        var frame = socket.SentFrames.First(f => f.Contains("client_tool_result_error"));
        frame.Should().Contain($"\"toolCallId\":\"{toolCallId}\"");
        frame.Should().Contain("\"code\":\"not_found\"");
    }

    [Fact]
    public async Task PrimaryPath_MissingToolCallId_SendsErrorInvalid()
    {
        const string threadId = "ctr-primary-invalid-1";
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var pool = CreatePoolReturning(SimpleLoop(threadId));
        _ = pool.GetOrCreateAgent(threadId, SystemChatModes.GetById(SystemChatModes.DefaultModeId)!);

        var manager = CreateManager(pool);
        var socket = new FakeWebSocket();
        socket.EnqueueTextFrame(/*lang=json,strict*/ """{"$type":"client_tool_result","result":"x"}""");

        var handlerTask = manager.HandleConnectionAsync(
            socket, threadId, null, null, null, null, ct.Token);

        await socket.WaitUntilAsync(() => socket.SentContains("client_tool_result_error"), ct.Token);

        await ct.CancelAsync();
        await handlerTask;

        var frame = socket.SentFrames.First(f => f.Contains("client_tool_result_error"));
        frame.Should().Contain("\"code\":\"invalid\"");
    }

    [Fact]
    public async Task PrimaryPath_MissingResult_SendsErrorInvalid()
    {
        const string threadId = "ctr-primary-invalid-2";
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var pool = CreatePoolReturning(SimpleLoop(threadId));
        _ = pool.GetOrCreateAgent(threadId, SystemChatModes.GetById(SystemChatModes.DefaultModeId)!);

        var manager = CreateManager(pool);
        var socket = new FakeWebSocket();
        socket.EnqueueTextFrame(
            /*lang=json,strict*/ """{"$type":"client_tool_result","toolCallId":"tc_1"}""");

        var handlerTask = manager.HandleConnectionAsync(
            socket, threadId, null, null, null, null, ct.Token);

        await socket.WaitUntilAsync(() => socket.SentContains("client_tool_result_error"), ct.Token);

        await ct.CancelAsync();
        await handlerTask;

        var frame = socket.SentFrames.First(f => f.Contains("client_tool_result_error"));
        frame.Should().Contain("\"code\":\"invalid\"");
    }

    [Fact]
    public async Task PrimaryPath_PlainChatRequestFrame_StillFlowsThrough_Unaffected()
    {
        // Regression: the $type peek must leave the pre-existing ChatRequest shape/flow completely
        // untouched for ordinary chat frames (no "$type" property at all).
        const string threadId = "ctr-primary-regression";
        var mockAgent = new Mock<IStreamingAgent>();
        mockAgent
            .Setup(a => a.GenerateReplyStreamingAsync(
                It.IsAny<IEnumerable<IMessage>>(), It.IsAny<GenerateReplyOptions>(), It.IsAny<CancellationToken>()))
            .Returns(Task.FromResult(ToAsyncEnumerable(
                [new TextMessage { Role = Role.Assistant, Text = "hi there" }])));

        await using var pool = CreatePoolReturning(
            new MultiTurnAgentLoop(mockAgent.Object, new FunctionRegistry(), threadId));
        _ = pool.GetOrCreateAgent(threadId, SystemChatModes.GetById(SystemChatModes.DefaultModeId)!);

        var manager = CreateManager(pool);
        var socket = new FakeWebSocket();
        socket.EnqueueTextFrame(JsonSerializer.Serialize(new ChatRequest("hello")));
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var handlerTask = manager.HandleConnectionAsync(
            socket, threadId, null, null, null, null, ct.Token);

        await socket.WaitUntilAsync(() => socket.SentContains("hi there"), ct.Token);

        await ct.CancelAsync();
        await handlerTask;

        socket.SentContains("client_tool_result").Should().BeFalse(
            "an ordinary chat frame must never be treated as a client_tool_result");
    }

    // ----- sub-agent path (HandleSubAgentConnectionAsync) -----

    [Fact]
    public async Task SubAgentPath_ValidResolution_SendsAckResolved()
    {
        const string toolCallId = "tc_child_1";
        await using var parentLoop = CreateParentLoopForSubAgent(() => CreateAskUserQuestionChildAgent(toolCallId));
        await using var pool = CreatePoolReturning(parentLoop);
        _ = pool.GetOrCreateAgent(SubAgentParentThreadId, SystemChatModes.GetById(SystemChatModes.DefaultModeId)!);

        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var spawnJson = await parentLoop.SubAgentManager!.SpawnAsync(
            SubAgentTemplateName, "ask the user", name: "asker", runInBackground: true);
        var agentId = ParseAgentId(spawnJson);

        // The deferred AskUserQuestion call ends the child's run — wait for it before resolving.
        await WaitUntilChildAwaitingQuestionAsync(parentLoop.SubAgentManager!, agentId, ct.Token);

        var manager = CreateManager(pool);
        var socket = new FakeWebSocket();
        socket.EnqueueTextFrame(
            /*lang=json,strict*/
            $$"""{"$type":"client_tool_result","toolCallId":"{{toolCallId}}","result":"blue","isError":false}""");

        var handlerTask = manager.HandleSubAgentConnectionAsync(socket, SubAgentParentThreadId, agentId, ct.Token);

        await socket.WaitUntilAsync(() => socket.SentContains("client_tool_result_ack"), ct.Token);

        await ct.CancelAsync();
        await handlerTask;

        var frame = socket.SentFrames.First(f => f.Contains("client_tool_result_ack"));
        frame.Should().Contain($"\"toolCallId\":\"{toolCallId}\"");
        frame.Should().Contain("\"status\":\"resolved\"");
    }

    [Fact]
    public async Task SubAgentPath_UnknownToolCallId_SendsErrorNotFound()
    {
        const string toolCallId = "tc_child_2";
        await using var parentLoop = CreateParentLoopForSubAgent(() => CreateAskUserQuestionChildAgent(toolCallId));
        await using var pool = CreatePoolReturning(parentLoop);
        _ = pool.GetOrCreateAgent(SubAgentParentThreadId, SystemChatModes.GetById(SystemChatModes.DefaultModeId)!);

        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var spawnJson = await parentLoop.SubAgentManager!.SpawnAsync(
            SubAgentTemplateName, "ask the user", name: "asker2", runInBackground: true);
        var agentId = ParseAgentId(spawnJson);
        await WaitUntilChildAwaitingQuestionAsync(parentLoop.SubAgentManager!, agentId, ct.Token);

        var manager = CreateManager(pool);
        var socket = new FakeWebSocket();
        socket.EnqueueTextFrame(
            /*lang=json,strict*/
            """{"$type":"client_tool_result","toolCallId":"does-not-exist","result":"x","isError":false}""");

        var handlerTask = manager.HandleSubAgentConnectionAsync(socket, SubAgentParentThreadId, agentId, ct.Token);

        await socket.WaitUntilAsync(() => socket.SentContains("client_tool_result_error"), ct.Token);

        await ct.CancelAsync();
        await handlerTask;

        var frame = socket.SentFrames.First(f => f.Contains("client_tool_result_error"));
        frame.Should().Contain("\"code\":\"not_found\"");
    }

    /// <summary>
    /// Same reachable race as <see cref="PrimaryPath_AgentDisposedBeforeFrameArrives_SendsErrorNotFound_WithoutCrashingConnection"/>,
    /// on the SUB-AGENT path: <c>HandleSubAgentClientToolResultAsync</c> re-resolves the child via
    /// <see cref="SubAgentManager.TryGetAgent"/> on every frame, but a disposed-yet-still-registered child
    /// (e.g. a completed sub-agent the manager has not yet evicted — the same state this file's
    /// <c>HandleSubAgentConnectionAsync</c> doc comments describe for a "COMPLETED sub-agent" whose
    /// transcript persists after eviction) is returned as-is. Calling <c>TryResolveToolCallAsync</c> on it
    /// must not crash the receive loop before a typed frame reaches the client.
    ///
    /// Drives <c>HandleSubAgentClientToolResultAsync</c> directly for the same reason as the primary-path
    /// test above: disposing the child through the full <c>HandleSubAgentConnectionAsync</c> path also
    /// completes that connection's own subscription and races away the connection before the queued
    /// frame can be processed.
    /// </summary>
    [Fact]
    public async Task SubAgentPath_AgentDisposedBeforeFrameArrives_SendsErrorNotFound_WithoutCrashingConnection()
    {
        const string toolCallId = "tc_child_4";
        await using var parentLoop = CreateParentLoopForSubAgent(() => CreateAskUserQuestionChildAgent(toolCallId));
        await using var pool = CreatePoolReturning(parentLoop);
        _ = pool.GetOrCreateAgent(SubAgentParentThreadId, SystemChatModes.GetById(SystemChatModes.DefaultModeId)!);

        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var spawnJson = await parentLoop.SubAgentManager!.SpawnAsync(
            SubAgentTemplateName, "ask the user", name: "asker4", runInBackground: true);
        var agentId = ParseAgentId(spawnJson);
        await WaitUntilChildAwaitingQuestionAsync(parentLoop.SubAgentManager!, agentId, ct.Token);

        // Simulate the concurrent disposal: the child stays registered (still resolvable by agentId) but
        // its underlying loop is disposed before the frame is processed.
        parentLoop.SubAgentManager!.TryGetAgent(agentId, out var childAgent).Should().BeTrue();
        await ((MultiTurnAgentLoop)childAgent!).DisposeAsync();

        var manager = CreateManager(pool);
        var socket = new FakeWebSocket();
        var connection = new WebSocketConnectionRegistry().Register($"subagent-{agentId}", socket);

        await manager.HandleSubAgentClientToolResultAsync(
            connection,
            parentLoop.SubAgentManager!,
            agentId,
            /*lang=json,strict*/
            $$"""{"$type":"client_tool_result","toolCallId":"{{toolCallId}}","result":"blue","isError":false}""",
            ct.Token);

        var frame = socket.SentFrames.First(f => f.Contains("client_tool_result_error"));
        frame.Should().Contain("\"code\":\"not_found\"");
    }

    [Fact]
    public async Task SubAgentPath_MalformedFrame_SendsErrorInvalid()
    {
        const string toolCallId = "tc_child_3";
        await using var parentLoop = CreateParentLoopForSubAgent(() => CreateAskUserQuestionChildAgent(toolCallId));
        await using var pool = CreatePoolReturning(parentLoop);
        _ = pool.GetOrCreateAgent(SubAgentParentThreadId, SystemChatModes.GetById(SystemChatModes.DefaultModeId)!);

        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var spawnJson = await parentLoop.SubAgentManager!.SpawnAsync(
            SubAgentTemplateName, "ask the user", name: "asker3", runInBackground: true);
        var agentId = ParseAgentId(spawnJson);
        await WaitUntilChildAwaitingQuestionAsync(parentLoop.SubAgentManager!, agentId, ct.Token);

        var manager = CreateManager(pool);
        var socket = new FakeWebSocket();
        socket.EnqueueTextFrame(/*lang=json,strict*/ """{"$type":"client_tool_result","result":"x"}""");

        var handlerTask = manager.HandleSubAgentConnectionAsync(socket, SubAgentParentThreadId, agentId, ct.Token);

        await socket.WaitUntilAsync(() => socket.SentContains("client_tool_result_error"), ct.Token);

        await ct.CancelAsync();
        await handlerTask;

        var frame = socket.SentFrames.First(f => f.Contains("client_tool_result_error"));
        frame.Should().Contain("\"code\":\"invalid\"");
    }

    // ----- shared helpers -----

    private static ChatWebSocketManager CreateManager(MultiTurnAgentPool pool) =>
        new(
            pool,
            new WebSocketConnectionRegistry(),
            new LmStreaming.Sample.Services.WorkflowRunRegistry(),
            new PendingAuthCoordinator(Mock.Of<IAuthEventNotifier>(), new AuthOptions(), NullLogger<PendingAuthCoordinator>.Instance),
            new InMemoryConversationStore(),
            NullLogger<ChatWebSocketManager>.Instance);

    private static MultiTurnAgentPool CreatePoolReturning(IMultiTurnAgent agent) =>
        new((_, _, _) => new MultiTurnAgentPool.AgentCreationResult(agent), NullLogger<MultiTurnAgentPool>.Instance);

    /// <summary>A loop whose mock LLM is never expected to be invoked (used by tests that only need
    /// SOME registered agent for the thread — e.g. malformed-frame / unknown-toolCallId cases).</summary>
    private static MultiTurnAgentLoop SimpleLoop(string threadId)
    {
        var mockAgent = new Mock<IStreamingAgent>();
        mockAgent
            .Setup(a => a.GenerateReplyStreamingAsync(
                It.IsAny<IEnumerable<IMessage>>(), It.IsAny<GenerateReplyOptions>(), It.IsAny<CancellationToken>()))
            .Returns(Task.FromResult(EmptyStream()));

        return new MultiTurnAgentLoop(mockAgent.Object, new FunctionRegistry(), threadId);
    }

    private static string AskUserQuestionArgs() => JsonSerializer.Serialize(new
    {
        context = "Need to know which color to use.",
        questions = new[]
        {
            new
            {
                prompt = "Which color?",
                options = new object[] { new { label = "Red" }, new { label = "Blue" } },
            },
        },
    });

    /// <summary>
    /// Builds a pool-registered loop whose mock LLM emits a single <c>AskUserQuestion</c> tool call on
    /// the FIRST turn (parking a deferred placeholder and ending the run) and a plain final text
    /// message on every subsequent (auto-resumed) turn. The loop is registered via
    /// <c>pool.GetOrCreateAgent</c> exactly once here, so the pool's own background
    /// <c>RunAsync</c> starts exactly once — the later <c>HandleConnectionAsync</c> call for the same
    /// threadId reuses this same cached entry instead of starting a second, concurrent run loop.
    /// </summary>
    private static async Task<MultiTurnAgentPool> CreatePoolWithParkedAskUserQuestionAsync(
        string threadId, string toolCallId, CancellationToken ct)
    {
        var toolCall = new ToolCallMessage
        {
            FunctionName = AskUserQuestionToolProvider.ToolName,
            FunctionArgs = AskUserQuestionArgs(),
            ToolCallId = toolCallId,
            Role = Role.Assistant,
        };
        var finalText = new TextMessage { Role = Role.Assistant, Text = "Thanks, noted." };

        var callCount = 0;
        var mockAgent = new Mock<IStreamingAgent>();
        mockAgent
            .Setup(a => a.GenerateReplyStreamingAsync(
                It.IsAny<IEnumerable<IMessage>>(), It.IsAny<GenerateReplyOptions>(), It.IsAny<CancellationToken>()))
            .Returns<IEnumerable<IMessage>, GenerateReplyOptions, CancellationToken>((_, _, _) =>
            {
                callCount++;
                IMessage msg = callCount == 1 ? toolCall : finalText;
                return Task.FromResult(ToAsyncEnumerable([msg]));
            });

        var loop = new MultiTurnAgentLoop(mockAgent.Object, new FunctionRegistry(), threadId);
        var pool = CreatePoolReturning(loop);
        _ = pool.GetOrCreateAgent(threadId, SystemChatModes.GetById(SystemChatModes.DefaultModeId)!);

        var runCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = ObserveAsync(loop, msg =>
        {
            if (msg is RunCompletedMessage)
            {
                runCompleted.TrySetResult();
            }
        }, ct);

        await loop.SendAsync([new TextMessage { Text = "Which color should I use?", Role = Role.User }]);
        await runCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);

        return pool;
    }

    private static MultiTurnAgentLoop CreateParentLoopForSubAgent(Func<IStreamingAgent> childFactory)
    {
        var options = new SubAgentOptions
        {
            Templates = new Dictionary<string, SubAgentTemplate>
            {
                [SubAgentTemplateName] = new SubAgentTemplate
                {
                    Name = SubAgentTemplateName,
                    SystemPrompt = "You ask the user a clarifying question.",
                    AgentFactory = childFactory,
                },
            },
            MaxConcurrentSubAgents = 5,
        };

        var parentMockAgent = new Mock<IStreamingAgent>();
        parentMockAgent
            .Setup(a => a.GenerateReplyStreamingAsync(
                It.IsAny<IEnumerable<IMessage>>(), It.IsAny<GenerateReplyOptions>(), It.IsAny<CancellationToken>()))
            .Returns(Task.FromResult(EmptyStream()));

        return new MultiTurnAgentLoop(
            parentMockAgent.Object,
            new FunctionRegistry(),
            threadId: SubAgentParentThreadId,
            subAgentOptions: options);
    }

    private static IStreamingAgent CreateAskUserQuestionChildAgent(string toolCallId)
    {
        var mockAgent = new Mock<IStreamingAgent>();
        mockAgent
            .Setup(a => a.GenerateReplyStreamingAsync(
                It.IsAny<IEnumerable<IMessage>>(), It.IsAny<GenerateReplyOptions>(), It.IsAny<CancellationToken>()))
            .Returns(Task.FromResult(ToAsyncEnumerable(
            [
                new ToolCallMessage
                {
                    FunctionName = AskUserQuestionToolProvider.ToolName,
                    FunctionArgs = AskUserQuestionArgs(),
                    ToolCallId = toolCallId,
                    Role = Role.Assistant,
                },
            ])));
        return mockAgent.Object;
    }

    private static string ParseAgentId(string spawnJson)
    {
        using var doc = JsonDocument.Parse(spawnJson);
        return doc.RootElement.GetProperty("agent_id").GetString()!;
    }

    /// <summary>
    /// Waits for a spawned sub-agent to park on its own <c>AskUserQuestion</c> call. Since the
    /// <c>SubAgentManager</c> fix that keeps a parked AskUserQuestion non-terminal, <c>state.Completion</c>
    /// (what <see cref="SubAgentManager.ObserveCompletionAsync"/> awaits) is deliberately never resolved
    /// while parked — only the answer-triggered run performs the one true final completion. So tests that
    /// need the child parked (not finished) must instead poll the child loop's own deferred-call registry
    /// directly, mirroring the production-side <c>HasPendingAskUserQuestionAsync</c> check, rather than
    /// waiting on a completion that will never come.
    /// </summary>
    private static Task WaitUntilChildAwaitingQuestionAsync(
        SubAgentManager subAgentManager, string agentId, CancellationToken ct)
    {
        return Wait.UntilAsync(
            async () =>
                subAgentManager.TryGetAgent(agentId, out var childAgent)
                && childAgent is MultiTurnAgentLoop childLoop
                && (await childLoop.GetDeferredToolCallsAsync(ct)).Count > 0,
            $"the spawned child '{agentId}' parked on its own AskUserQuestion, i.e. registered a deferred tool call",
            TimeSpan.FromSeconds(30),
            TimeSpan.FromMilliseconds(20),
            cancellationToken: ct);
    }

    private static Task ObserveAsync(MultiTurnAgentLoop loop, Action<IMessage> onMessage, CancellationToken ct)
    {
        var messages = loop.SubscribeAsync(ct).GetAsyncEnumerator(ct);
        var first = messages.MoveNextAsync();

        // Not `ct`: a cancelled token would skip this body entirely, leaving the subscription
        // attached and the pending move unobserved.
        return Task.Run(async () =>
        {
            try
            {
                for (var hasMessage = await first; hasMessage; hasMessage = await messages.MoveNextAsync())
                {
                    onMessage(messages.Current);
                }
            }
            catch (OperationCanceledException)
            {
                // Cancelling the token is how these tests end the subscription.
            }
            finally
            {
                await messages.DisposeAsync();
            }
        }, CancellationToken.None);
    }

    private static async IAsyncEnumerable<IMessage> ToAsyncEnumerable(
        IEnumerable<IMessage> messages, [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var msg in messages)
        {
            ct.ThrowIfCancellationRequested();
            yield return msg;
            await Task.Yield();
        }
    }

    private static async IAsyncEnumerable<IMessage> EmptyStream()
    {
        await Task.CompletedTask;
        yield break;
    }

    /// <summary>
    /// A minimal in-memory <see cref="System.Net.WebSockets.WebSocket"/> test double: captures every
    /// outbound text frame and feeds inbound frames from an in-memory queue. Copied (not shared) from
    /// <c>ChatWebSocketManagerSubAgentTests.FakeWebSocket</c> — both are private test doubles scoped to
    /// their own test class.
    /// </summary>
    private sealed class FakeWebSocket : System.Net.WebSockets.WebSocket
    {
        private readonly Channel<InboundFrame> _inbound =
            Channel.CreateUnbounded<InboundFrame>(new UnboundedChannelOptions { SingleReader = true });

        private readonly List<string> _sent = [];
        private readonly Lock _lock = new();
        private readonly SemaphoreSlim _activity = new(0);
        private WebSocketState _state = WebSocketState.Open;
        private InboundFrame? _current;
        private int _currentOffset;

        private readonly record struct InboundFrame(byte[] Payload, WebSocketMessageType Type, bool EndOfMessage);

        public bool CloseAsyncCalled { get; private set; }

        public WebSocketCloseStatus? LastCloseStatus { get; private set; }

        public IReadOnlyList<string> SentFrames
        {
            get { lock (_lock) { return [.. _sent]; } }
        }

        public bool SentContains(string fragment)
        {
            lock (_lock) { return _sent.Any(f => f.Contains(fragment, StringComparison.Ordinal)); }
        }

        public void EnqueueTextFrame(string text) =>
            _inbound.Writer.TryWrite(
                new InboundFrame(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, EndOfMessage: true));

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
            if (_current is null)
            {
                _current = await _inbound.Reader.ReadAsync(cancellationToken);
                _currentOffset = 0;
            }

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
                endOfMessage = frame.EndOfMessage;
                _current = null;
            }
            else
            {
                endOfMessage = false;
            }

            return new WebSocketReceiveResult(count, frame.Type, endOfMessage);
        }

        public override Task CloseAsync(
            WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            CloseAsyncCalled = true;
            LastCloseStatus = closeStatus;
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
