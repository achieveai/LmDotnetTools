using System.Net.WebSockets;
using LmStreaming.Sample.Services;
using LmStreaming.Sample.Tests.TestDoubles;
using LmStreaming.Sample.WebSocket;

namespace LmStreaming.Sample.Tests.WebSocket;

/// <summary>
/// Pins what an already-open <c>/ws</c> connection does when its conversation's pooled agent changes
/// hands underneath it.
/// </summary>
/// <remarks>
/// <para>
/// #399 gave the handshake a structured refusal for a second user, and #376 gave an authorized editor
/// grantee the right to take a pooled agent over mid-session. Together they put the pool's principal
/// guard on the PER-MESSAGE path for the first time: the refresh before each dispatch now asserts the
/// connection's user, so a socket whose thread was handed off throws on the owner's next keystroke -
/// somewhere the connect-time catch cannot see. Unhandled, that leaves the receive pump, the
/// connection handler and the endpoint, and the socket is aborted with no frame at all, which is the
/// exact failure #399 existed to remove.
/// </para>
/// <para>
/// Both halves of a handoff are covered because catching only the conflict would still leave a hole:
/// releasing and recreating are two steps, and a message that lands between them finds no entry
/// rather than someone else's.
/// </para>
/// </remarks>
public sealed class ChatWebSocketManagerPrincipalHandoffTests
{
    private const string Alice = "dir-a:alice";
    private const string Bob = "dir-a:bob";

    [Fact]
    public async Task AMessageOnASocketWhoseAgentWasHandedOff_IsRefusedWithAFrame_NotAnAbort()
    {
        const string threadId = "handoff-conflict";
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var pool = CreatePool();

        var socket = new FakeWebSocket();
        var handlerTask = Connect(pool, socket, threadId, Alice, cts.Token);
        await socket.WaitUntilAsync(
            () => string.Equals(pool.GetAgentOwnerUserId(threadId), Alice, StringComparison.Ordinal),
            cts.Token);

        // The handoff the three mutating REST routes perform for an authorized grantee: release the
        // entry frozen to another user, then let the pool recreate it under the caller taking over.
        await pool.RemoveAgentAsync(threadId);
        _ = pool.GetOrCreateAgent(threadId, DefaultMode(), null, null, ownerUserId: Bob);

        socket.EnqueueTextFrame(/*lang=json,strict*/ """{"Message":"still here?"}""");

        // The trailing quote is load-bearing: matching the bare code would also match any longer code
        // that merely starts with it, which is how a frame-type assertion quietly stops discriminating.
        await socket.WaitUntilAsync(
            () => socket.SentContains("\"code\":\"principal_conflict\""), cts.Token);

        // The refusal must not name the other user. This connection was never authorized to learn who
        // else uses the conversation, and trading an abort for a leak would not be a fix.
        _ = socket.SentFrames.Should().NotContain(frame => frame.Contains(Bob, StringComparison.Ordinal));

        // The connection ends by closing, not by throwing: an exception here escapes to the host and
        // the client sees a bare disconnect no matter what frame preceded it. CloseAsyncCalled alone
        // does not say that - a close carrying an error status is still an abort as far as the client
        // is concerned, so the STATUS is what the assertion has to name.
        await cts.CancelAsync();
        await handlerTask;
        _ = socket.CloseAsyncCalled.Should().BeTrue();
        _ = socket.LastCloseStatus.Should().Be(WebSocketCloseStatus.NormalClosure);
    }

    [Fact]
    public async Task AMessageThatLandsBetweenReleaseAndRecreate_ClosesCleanly_NotAnAbort()
    {
        const string threadId = "handoff-window";
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var pool = CreatePool();

        var socket = new FakeWebSocket();
        var handlerTask = Connect(pool, socket, threadId, Alice, cts.Token);
        await socket.WaitUntilAsync(
            () => string.Equals(pool.GetAgentOwnerUserId(threadId), Alice, StringComparison.Ordinal),
            cts.Token);

        // Released and NOT yet recreated - the window the grantee handoff opens between its two steps.
        await pool.RemoveAgentAsync(threadId);

        socket.EnqueueTextFrame(/*lang=json,strict*/ """{"Message":"still here?"}""");

        // Same answer a replaced sandbox session gets, for the same reason: the client's agent is gone,
        // so it must reconnect. What it must NOT get is the connection dying with nothing said.
        // Matched with its closing quote, so this cannot be satisfied by the DIFFERENT frame the
        // deferred-refresh path emits: "sandbox_session_refresh" is a prefix of
        // "sandbox_session_refresh_deferred", and a substring match would accept either.
        await socket.WaitUntilAsync(
            () => socket.SentContains("\"$type\":\"sandbox_session_refresh\""),
            cts.Token);

        await cts.CancelAsync();
        await handlerTask;
        _ = socket.CloseAsyncCalled.Should().BeTrue();
        _ = socket.LastCloseStatus.Should().Be(WebSocketCloseStatus.NormalClosure);
    }

    /// <summary>
    /// The same release, met during CONNECTION SETUP rather than on a later message. Setup calls
    /// <c>GetOrCreateAgent</c> and then <c>EnsureCurrentAgentAsync</c>; a handoff removing the entry in
    /// between - or, as here, while the refresh is awaiting the live-session resolver - leaves the
    /// second call with nothing to refresh.
    /// </summary>
    /// <remarks>
    /// The connect-time catch list already handles the principal conflict and the credential conflict.
    /// This is the third exception the same window can produce, and it was the one that still aborted
    /// the socket frameless. The resolver is the seam that makes the race deterministic: it removes the
    /// entry and then reports a DIFFERENT session id, so the refresh is obliged to look the entry up a
    /// second time and finds it gone.
    /// </remarks>
    [Fact]
    public async Task AConnectionSetupThatMeetsAReleasedAgent_ClosesCleanly_NotAnAbort()
    {
        const string threadId = "handoff-at-connect";
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        MultiTurnAgentPool? pool = null;
        var binding = new SandboxEstablishedBinding(
            new WorkspaceRef("ws-handoff"),
            new SandboxCredential("test-app", string.Empty),
            SessionId: "session-before");

        // Typed local rather than an inline lambda: the pool has a four-arg factory overload too, and
        // naming the delegate is what picks the context-shaped one without an explicit parameter type.
        Func<MultiTurnAgentPool.AgentCreationContext, MultiTurnAgentPool.AgentCreationResult> factory =
            ctx => new MultiTurnAgentPool.AgentCreationResult(
                new FakeMultiTurnAgent(ctx.ThreadId) { KeepSubscriptionOpen = true })
            {
                StagedBinding = binding,
            };

        pool = new MultiTurnAgentPool(
            factory,
            providerRegistry: null,
            conversationStore: null,
            NullLogger<MultiTurnAgentPool>.Instance,
            bindingSink: null,
            liveSessionResolver: async (_, _) =>
            {
                // The grantee handoff, landing exactly inside the refresh's await.
                await pool!.RemoveAgentAsync(threadId);
                return new SandboxSession("ws-handoff", "session-after", "/w", "/host");
            });

        await using (pool)
        {
            var socket = new FakeWebSocket();
            var handlerTask = Connect(pool, socket, threadId, Alice, cts.Token);

            await socket.WaitUntilAsync(
                () => socket.SentContains("\"$type\":\"sandbox_session_refresh\""),
                cts.Token);

            await handlerTask;
            _ = socket.CloseAsyncCalled.Should().BeTrue();
            _ = socket.LastCloseStatus.Should().Be(WebSocketCloseStatus.NormalClosure);
        }
    }

    /// <summary>The mode a conversation gets when nothing pinned one - what these threads run under.</summary>
    private static AgentProfile DefaultMode() =>
        SystemChatModes.GetById(SystemChatModes.DefaultModeId)!;

    /// <summary>
    /// A pool whose agents keep their subscription open, so completing the OUTBOUND pump cannot tear
    /// the connection down before the inbound frame under test is processed. Without it the only
    /// interleave reachable here is the one where the socket is already gone, which says nothing about
    /// what a message arriving during a handoff does.
    /// </summary>
    private static MultiTurnAgentPool CreatePool() =>
        new(
            (threadId, _, _) => new MultiTurnAgentPool.AgentCreationResult(
                new FakeMultiTurnAgent(threadId) { KeepSubscriptionOpen = true }),
            NullLogger<MultiTurnAgentPool>.Instance);

    private static ChatWebSocketManager CreateManager(MultiTurnAgentPool pool) =>
        new(
            pool,
            new WebSocketConnectionRegistry(),
            new WorkflowRunRegistry(),
            new PendingAuthCoordinator(
                Mock.Of<IAuthEventNotifier>(),
                new AuthOptions(),
                NullLogger<PendingAuthCoordinator>.Instance),
            new InMemoryConversationStore(),
            NullLogger<ChatWebSocketManager>.Instance);

    /// <summary>Opens a connection for one user, leaving only the line that differs in each test.</summary>
    private static Task Connect(
        MultiTurnAgentPool pool,
        System.Net.WebSockets.WebSocket socket,
        string threadId,
        string ownerUserId,
        CancellationToken ct) =>
        CreateManager(pool).HandleConnectionAsync(
            socket,
            threadId,
            mode: null,
            providerId: null,
            requestResponseDumpFileName: null,
            recordWriter: null,
            cancellationToken: ct,
            workspaceId: null,
            ownerUserId: ownerUserId);
}
