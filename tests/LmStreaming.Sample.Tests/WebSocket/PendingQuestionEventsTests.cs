using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmMultiTurn.ClientTools;
using LmStreaming.Sample.Identity;
using LmStreaming.Sample.WebSocket;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace LmStreaming.Sample.Tests.WebSocket;

/// <summary>
/// The app-wide pending-question channel, end to end: <c>/ws/events</c>, the
/// <see cref="PendingQuestionHub"/> behind it, and the agent loop that feeds it.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this is for.</b> A question raised in a conversation the user is not looking at had no
/// socket to arrive on — <c>/ws</c> and <c>/ws/subagent</c> each bind ONE thread — so the client
/// found it by re-reading transcripts on a 30-second/5-minute timer. Polling could not be made
/// faster, because the only cheap change signal, <c>lastUpdated</c>, moves when a run COMPLETES and a
/// run parked on a question has not completed. These tests pin the push that replaces it.
/// </para>
/// <para>
/// The route tests go through a real <see cref="TestServer"/> WebSocket rather than calling the hub,
/// because half of what is being claimed is pipeline behaviour: that the handshake is reached at all,
/// that the snapshot is the FIRST frame, and that a client connected BEFORE a question parks is
/// delivered to. The loop test goes the other way — it drives a real
/// <see cref="MultiTurnAgentLoop"/> onto a parked <c>AskUserQuestion</c> and asserts the observer was
/// told, which is the half the transport tests cannot see.
/// </para>
/// </remarks>
public sealed class PendingQuestionEventsTests
{
    private const string RootThreadId = "root-thread";
    private const string ChildThreadId = "subagent-0123456789ab-agent-2";
    private const string ToolCallId = "tc_q1";

    private static string QuestionArgs(string prompt = "Which colour?") =>
        JsonSerializer.Serialize(
            new
            {
                context = "Picking a paint colour.",
                questions = new[]
                {
                    new
                    {
                        id = "colour",
                        prompt,
                        options = new[] { new { label = "Red" }, new { label = "Blue" } },
                    },
                },
            }
        );

    private static PendingQuestionNotice Notice(string threadId = RootThreadId, string toolCallId = ToolCallId) =>
        PendingQuestionNotices.TryDescribe(
            threadId,
            AskUserQuestionToolProvider.ToolName,
            toolCallId,
            QuestionArgs(),
            DateTimeOffset.UnixEpoch
        )!;

    private sealed class EventsHost : WebApplicationFactory<Program>
    {
        public EventsHost() => Environment.SetEnvironmentVariable("LM_PROVIDER_MODE", "test");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // Production: no Vite dev-server auto-spawn, matching the other in-process host tests.
            builder.UseEnvironment("Production");
            builder.UseSetting("SandboxGateway:BaseUrl", "http://127.0.0.1:1");
            builder.UseSetting("SandboxGateway:AutoSpawn", "false");
            builder.UseSetting("Identity:Enforce", "false");
        }
    }

    private static async Task<System.Net.WebSockets.WebSocket> ConnectAsync(EventsHost host, CancellationToken ct)
    {
        var client = host.Server.CreateWebSocketClient();
        return await client.ConnectAsync(new Uri(host.Server.BaseAddress, "/ws/events"), ct);
    }

    /// <summary>
    /// Reads exactly one text frame. Bounded by <paramref name="ct"/>: a test that expects a frame and
    /// gets none must fail on the timeout rather than hang the suite.
    /// </summary>
    private static async Task<JsonElement> ReceiveFrameAsync(
        System.Net.WebSockets.WebSocket socket,
        CancellationToken ct
    )
    {
        var buffer = new byte[16 * 1024];
        var text = new StringBuilder();
        WebSocketReceiveResult received;
        do
        {
            received = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
            _ = text.Append(Encoding.UTF8.GetString(buffer, 0, received.Count));
        } while (!received.EndOfMessage);

        return JsonDocument.Parse(text.ToString()).RootElement.Clone();
    }

    private static string TypeOf(JsonElement frame) => frame.GetProperty("$type").GetString()!;

    // ------------------------------------------------------------------ the socket

    /// <summary>
    /// A client that connects while a question is ALREADY parked is correct without sweeping anything:
    /// the first frame lists it. This is what makes a reload or a reconnect cheap — before this, the
    /// only way to learn about an existing question was to re-read every transcript.
    /// </summary>
    [Fact]
    public async Task Connecting_ReceivesASnapshotOfEverythingAlreadyParked()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var host = new EventsHost();
        var hub = host.Services.GetRequiredService<PendingQuestionHub>();

        hub.OnQuestionRaised(Notice());
        await hub.DrainAsync(cts.Token);

        using var socket = await ConnectAsync(host, cts.Token);
        var frame = await ReceiveFrameAsync(socket, cts.Token);

        TypeOf(frame).Should().Be(PendingQuestionHub.SnapshotType);
        var questions = frame.GetProperty("questions").EnumerateArray().ToList();
        questions.Should().ContainSingle();
        questions[0].GetProperty("rootThreadId").GetString().Should().Be(RootThreadId);
        questions[0].GetProperty("toolCallId").GetString().Should().Be(ToolCallId);
        questions[0].GetProperty("prompt").GetString().Should().Be("Which colour?");
    }

    /// <summary>
    /// The whole point: a question that parks while the client is already connected reaches it as a
    /// push, with no request from the client at all. A client that had to ask would be back to polling.
    /// </summary>
    [Fact]
    public async Task AQuestionParkedAfterTheClientConnected_ArrivesAsQuestionPending()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var host = new EventsHost();
        var hub = host.Services.GetRequiredService<PendingQuestionHub>();

        using var socket = await ConnectAsync(host, cts.Token);
        var snapshot = await ReceiveFrameAsync(socket, cts.Token);
        TypeOf(snapshot).Should().Be(PendingQuestionHub.SnapshotType);
        snapshot.GetProperty("questions").GetArrayLength().Should().Be(0);

        hub.OnQuestionRaised(Notice());

        var pushed = await ReceiveFrameAsync(socket, cts.Token);
        TypeOf(pushed).Should().Be(PendingQuestionHub.PendingType);
        pushed.GetProperty("rootThreadId").GetString().Should().Be(RootThreadId);
        pushed.GetProperty("toolCallId").GetString().Should().Be(ToolCallId);
        pushed.GetProperty("agentId").ValueKind.Should().Be(JsonValueKind.Null);
        pushed.GetProperty("childThreadId").ValueKind.Should().Be(JsonValueKind.Null);
    }

    /// <summary>
    /// Settling is pushed too, and it has to be: a client that only ever learned of arrivals would keep
    /// showing an answered question until the fallback sweep caught up — the same five-minute wait, in
    /// the other direction.
    /// </summary>
    [Fact]
    public async Task SettlingAQuestion_ArrivesAsQuestionSettledAndClearsTheSnapshot()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var host = new EventsHost();
        var hub = host.Services.GetRequiredService<PendingQuestionHub>();

        using var socket = await ConnectAsync(host, cts.Token);
        _ = await ReceiveFrameAsync(socket, cts.Token);

        hub.OnQuestionRaised(Notice());
        _ = await ReceiveFrameAsync(socket, cts.Token);

        hub.OnQuestionSettled(RootThreadId, ToolCallId);

        var settled = await ReceiveFrameAsync(socket, cts.Token);
        TypeOf(settled).Should().Be(PendingQuestionHub.SettledType);
        settled.GetProperty("rootThreadId").GetString().Should().Be(RootThreadId);
        settled.GetProperty("toolCallId").GetString().Should().Be(ToolCallId);

        await hub.DrainAsync(cts.Token);
        hub.PendingSnapshot().Should().BeEmpty();
    }

    /// <summary>
    /// A sub-agent's question names the agent and its transcript thread, which is what lets the client
    /// navigate to the right TAB rather than only the right conversation. Derived from the reporting
    /// thread id, so it holds at any depth.
    /// </summary>
    [Fact]
    public async Task ASubAgentsQuestion_NamesItsAgentIdAndChildThread()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var host = new EventsHost();
        var hub = host.Services.GetRequiredService<PendingQuestionHub>();

        using var socket = await ConnectAsync(host, cts.Token);
        _ = await ReceiveFrameAsync(socket, cts.Token);

        // What the loop reports (thread = the CHILD), rewritten by the scope wrappers the hierarchy
        // installs: the root thread the client navigates to, and the child's display name.
        var scoped = new ScopedPendingQuestionObserver(
            new ScopedPendingQuestionObserver(hub, rootThreadId: RootThreadId),
            agentName: "Reviewer"
        );
        scoped.OnQuestionRaised(Notice(ChildThreadId));

        var pushed = await ReceiveFrameAsync(socket, cts.Token);
        TypeOf(pushed).Should().Be(PendingQuestionHub.PendingType);
        pushed.GetProperty("rootThreadId").GetString().Should().Be(RootThreadId);
        pushed.GetProperty("agentId").GetString().Should().Be("agent-2");
        pushed.GetProperty("childThreadId").GetString().Should().Be(ChildThreadId);
        pushed.GetProperty("agentName").GetString().Should().Be("Reviewer");
    }

    /// <summary>
    /// The events route is INSIDE the identity boundary, by the same predicate that puts <c>/ws</c>
    /// and <c>/ws/subagent</c> there.
    /// </summary>
    /// <remarks>
    /// This is the half of the gate that is specific to the new route. What the boundary then DOES to
    /// an unauthenticated guarded WebSocket path — refuse the handshake with 403, never 401, before it
    /// becomes a socket — is <see cref="Identity.IdentityMiddlewareTests"/>'s claim, over the real
    /// middleware. So the failure this catches is the one that could actually be introduced here:
    /// mapping the channel at a path the predicate does not cover (<c>/events</c>, <c>/api/events</c>),
    /// which would publish every conversation's questions to an unauthenticated caller while every
    /// other socket stayed shut.
    /// </remarks>
    [Fact]
    public void TheEventsRoute_IsInsideTheGuardedWebSocketBoundary()
    {
        IdentityMiddleware.IsGuardedWebSocketPath(new PathString("/ws/events")).Should().BeTrue();

        // The predicate is segment-based, so this is not merely a prefix match on the string.
        IdentityMiddleware.IsGuardedWebSocketPath(new PathString("/wsevents")).Should().BeFalse();
    }

    // ------------------------------------------------------------------ the depth rule

    /// <summary>
    /// The composition rule the hierarchy relies on: each level stamps the root it knows (the LAST
    /// one applied wins, and that is the level closest to the true root), while a name only FILLS —
    /// so a grandchild's own name survives the intermediate agent that would otherwise stamp its own.
    /// Get either direction backwards and a deep question is attributed to the wrong agent or the
    /// wrong conversation.
    /// </summary>
    [Fact]
    public void ScopedObserver_StampsTheOutermostRootAndKeepsTheInnermostName()
    {
        var recorder = new RecordingObserver();

        // Root loop R wraps with R; child loop C wraps with C; each manager wraps with its spawn name.
        var forGrandchild = new ScopedPendingQuestionObserver(
            new ScopedPendingQuestionObserver(
                new ScopedPendingQuestionObserver(
                    new ScopedPendingQuestionObserver(recorder, rootThreadId: "R"),
                    agentName: "Child"
                ),
                rootThreadId: "C"
            ),
            agentName: "Grandchild"
        );

        forGrandchild.OnQuestionRaised(Notice(ChildThreadId));
        forGrandchild.OnQuestionSettled(ChildThreadId, ToolCallId);

        recorder.Raised.Should().ContainSingle();
        recorder.Raised[0].RootThreadId.Should().Be("R");
        recorder.Raised[0].AgentName.Should().Be("Grandchild");
        recorder.Settled.Should().ContainSingle().Which.Should().Be(("R", ToolCallId));
    }

    // ------------------------------------------------------------------ the loop

    /// <summary>
    /// The half the transport tests cannot see: a real loop parking on <c>AskUserQuestion</c> reports
    /// it, and resolving the call reports the settle. Without this, every socket test above could pass
    /// against a channel nothing ever writes to.
    /// </summary>
    [Fact]
    public async Task TheLoopReportsAParkedAskUserQuestionAndItsResolution()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var recorder = new RecordingObserver();
        var provider = new Mock<IStreamingAgent>();
        var turn = 0;
        _ = provider
            .Setup(a =>
                a.GenerateReplyStreamingAsync(
                    It.IsAny<IEnumerable<IMessage>>(),
                    It.IsAny<GenerateReplyOptions>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Returns(() =>
                Task.FromResult(
                    Interlocked.Increment(ref turn) == 1
                        ? Stream(
                            new ToolCallMessage
                            {
                                FunctionName = AskUserQuestionToolProvider.ToolName,
                                FunctionArgs = QuestionArgs(),
                                ToolCallId = ToolCallId,
                                Role = Role.Assistant,
                            }
                        )
                        : Stream(new TextMessage { Text = "thanks", Role = Role.Assistant })
                )
            );

        await using var loop = new MultiTurnAgentLoop(
            provider.Object,
            new FunctionRegistry(),
            threadId: RootThreadId,
            includeAskUserQuestionTool: true,
            includeNotifyClientTool: false,
            logger: NullLogger<MultiTurnAgentLoop>.Instance
        )
        {
            PendingQuestionObserver = recorder,
        };

        _ = loop.RunAsync(cts.Token);
        await loop.SendAsync([new TextMessage { Text = "paint the shed", Role = Role.User }]);

        await WaitForAsync(() => recorder.Raised.Count == 1, cts.Token);
        recorder.Raised[0].ToolCallId.Should().Be(ToolCallId);
        recorder.Raised[0].RootThreadId.Should().Be(RootThreadId);
        recorder.Raised[0].AgentId.Should().BeNull();
        PendingQuestionHub.FirstQuestionText(recorder.Raised[0].FunctionArgs).Should().Be("Which colour?");

        await loop.ResolveToolCallAsync(ToolCallId, "Red", ct: cts.Token);

        await WaitForAsync(() => recorder.Settled.Count == 1, cts.Token);
        recorder.Settled[0].Should().Be((RootThreadId, ToolCallId));
    }

    private static async Task WaitForAsync(Func<bool> condition, CancellationToken ct)
    {
        while (!condition())
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(20, ct);
        }
    }

    /// <summary>One message as a provider stream. The loop only needs the tool call and one reply.</summary>
    private static async IAsyncEnumerable<IMessage> Stream(
        IMessage message,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        yield return message;
        await Task.Yield();
    }

    private sealed class RecordingObserver : IPendingQuestionObserver
    {
        private readonly List<PendingQuestionNotice> _raised = [];
        private readonly List<(string RootThreadId, string ToolCallId)> _settled = [];

        public IReadOnlyList<PendingQuestionNotice> Raised
        {
            get
            {
                lock (_raised)
                {
                    return [.. _raised];
                }
            }
        }

        public IReadOnlyList<(string RootThreadId, string ToolCallId)> Settled
        {
            get
            {
                lock (_settled)
                {
                    return [.. _settled];
                }
            }
        }

        public void OnQuestionRaised(PendingQuestionNotice notice)
        {
            lock (_raised)
            {
                _raised.Add(notice);
            }
        }

        public void OnQuestionSettled(string rootThreadId, string toolCallId)
        {
            lock (_settled)
            {
                _settled.Add((rootThreadId, toolCallId));
            }
        }
    }
}
