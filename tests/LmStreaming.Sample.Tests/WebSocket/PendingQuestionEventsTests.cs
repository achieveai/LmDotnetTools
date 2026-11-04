using System.Collections.Immutable;
using System.Net;
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
        // Its own database directory, because the hub keeps its state file beside the database: a
        // question one test leaves waiting must not appear in another test's snapshot.
        private readonly string _dataDir = Path.Combine(Path.GetTempPath(), $"pending-questions-{Guid.NewGuid():N}");

        public EventsHost() => Environment.SetEnvironmentVariable("LM_PROVIDER_MODE", "test");

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            try
            {
                Directory.Delete(_dataDir, recursive: true);
            }
            catch (IOException) { }
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // Production: no Vite dev-server auto-spawn, matching the other in-process host tests.
            builder.UseEnvironment("Production");
            builder.UseSetting("LmStreaming:ConversationDbPath", Path.Combine(_dataDir, "conversations.db"));
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

    /// <summary>
    /// A pushed question carries its conversation's bindings, so a client can open a conversation its
    /// sidebar has not paged in yet with the right provider, mode and workspace selected — the inbox no
    /// longer reads the conversation list to find them.
    /// </summary>
    [Fact]
    public async Task AQuestion_CarriesItsConversationsTitleAndBindings()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var host = new EventsHost();
        await host
            .Services.GetRequiredService<IConversationStore>()
            .SaveMetadataAsync(RootThreadId, RootMetadata(), cts.Token);
        var hub = host.Services.GetRequiredService<PendingQuestionHub>();

        hub.OnQuestionRaised(Notice());
        await hub.DrainAsync(cts.Token);
        using var socket = await ConnectAsync(host, cts.Token);
        var question = (await ReceiveFrameAsync(socket, cts.Token)).GetProperty("questions")[0];

        question.GetProperty("conversationTitle").GetString().Should().Be("Shed");
        question.GetProperty("provider").GetString().Should().Be("openai");
        question.GetProperty("mode").GetString().Should().Be("coder");
        question.GetProperty("workspace").GetString().Should().Be("repo");
    }

    // ------------------------------------------------------------------ the state file

    private static ThreadMetadata RootMetadata(string threadId = RootThreadId) =>
        new()
        {
            ThreadId = threadId,
            LastUpdated = 1,
            Properties = ImmutableDictionary<string, object>
                .Empty.Add("title", "Shed")
                .Add(MultiTurnAgentPool.ProviderPropertyKey, "openai")
                .Add(MultiTurnAgentPool.ModePropertyKey, "coder")
                .Add(MultiTurnAgentPool.WorkspacePropertyKey, "repo"),
        };

    /// <summary>Persists a question call and its result, the way a loop leaves them in history.</summary>
    private static Task SeedQuestionAsync(
        IConversationStore store,
        string threadId,
        string toolCallId,
        ToolCallResultMessage result
    ) =>
        store.AppendMessagesAsync(
            threadId,
            MessagePersistenceConverter.ToPersistedMessages(
                [
                    new ToolCallMessage
                    {
                        FunctionName = AskUserQuestionToolProvider.ToolName,
                        FunctionArgs = QuestionArgs(),
                        ToolCallId = toolCallId,
                        Role = Role.Assistant,
                    },
                    result with
                    {
                        ToolCallId = toolCallId,
                    },
                ],
                threadId,
                "run-1"
            )
        );

    private static PendingQuestionHub Hub(IConversationStore store, string statePath) =>
        new(
            new ServiceCollection()
                .AddSingleton(store)
                .BuildServiceProvider()
                .GetRequiredService<IServiceScopeFactory>(),
            NullLogger<PendingQuestionHub>.Instance,
            statePath
        );

    /// <summary>
    /// The restart the client now depends on: it no longer sweeps transcripts, so a question the
    /// server forgot on restart would be invisible until someone opened that conversation. The saved
    /// list comes back, but only what its transcript says is still waiting — a question answered just
    /// before the process stopped, or whose conversation is gone, is dropped and the file rewritten.
    /// </summary>
    [Fact]
    public async Task ARestart_KeepsWhatTheTranscriptsStillHaveWaiting_AndDropsTheRest()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var dir = Directory.CreateTempSubdirectory("pending-questions-");
        var statePath = Path.Combine(dir.FullName, "pending.json");
        try
        {
            var store = new InMemoryConversationStore();
            await store.SaveMetadataAsync(RootThreadId, RootMetadata(), cts.Token);
            await SeedQuestionAsync(
                store,
                RootThreadId,
                "tc_waiting",
                new ToolCallResultMessage { Result = "", IsDeferred = true }
            );
            await SeedQuestionAsync(store, RootThreadId, "tc_answered", new ToolCallResultMessage { Result = "Red" });
            await SeedQuestionAsync(
                store,
                ChildThreadId,
                "tc_child",
                new ToolCallResultMessage { Result = "", IsDeferred = true }
            );

            await using (var before = Hub(store, statePath))
            {
                before.OnQuestionRaised(Notice(toolCallId: "tc_waiting"));
                before.OnQuestionRaised(Notice(toolCallId: "tc_answered"));
                before.OnQuestionRaised(Notice(ChildThreadId, "tc_child") with { RootThreadId = RootThreadId });
                before.OnQuestionRaised(Notice(toolCallId: "tc_gone") with { RootThreadId = "deleted-root" });
                await before.DrainAsync(cts.Token);
                before.PendingSnapshot().Should().HaveCount(4);
            }

            await using var after = Hub(store, statePath);
            await after.DrainAsync(cts.Token);

            after.PendingSnapshot().Select(q => q.ToolCallId).Should().BeEquivalentTo("tc_waiting", "tc_child");
            after.PendingSnapshot().Single(q => q.ToolCallId == "tc_child").ChildThreadId.Should().Be(ChildThreadId);
            after.PendingSnapshot().Single(q => q.ToolCallId == "tc_waiting").Provider.Should().Be("openai");
            (await File.ReadAllTextAsync(statePath, cts.Token))
                .Should()
                .NotContain("tc_answered")
                .And.NotContain("tc_gone");
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    /// <summary>
    /// A deleted conversation's agent is evicted, so nothing would ever settle its questions; the
    /// delete route forgets them instead, and only them.
    /// </summary>
    /// <summary>
    /// Through the real DELETE route: the evicted agent never settles its questions, so the controller
    /// has to tell the hub, or every open inbox keeps a question nobody can answer.
    /// </summary>
    [Fact]
    public async Task DeletingAConversation_DropsOnlyItsQuestions()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var host = new EventsHost();
        await host
            .Services.GetRequiredService<IConversationStore>()
            .SaveMetadataAsync(RootThreadId, RootMetadata(), cts.Token);
        var hub = host.Services.GetRequiredService<PendingQuestionHub>();
        hub.OnQuestionRaised(Notice(toolCallId: "tc_a1"));
        hub.OnQuestionRaised(Notice(toolCallId: "tc_a2"));
        hub.OnQuestionRaised(Notice(toolCallId: "tc_b") with { RootThreadId = "other-root" });
        await hub.DrainAsync(cts.Token);
        using var socket = await ConnectAsync(host, cts.Token);
        _ = await ReceiveFrameAsync(socket, cts.Token);

        using var client = host.CreateClient();
        var deleted = await client.DeleteAsync($"/api/conversations/{RootThreadId}", cts.Token);
        deleted.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var settled = new[] { await ReceiveFrameAsync(socket, cts.Token), await ReceiveFrameAsync(socket, cts.Token) };
        settled.Select(TypeOf).Should().AllBe(PendingQuestionHub.SettledType);
        settled.Select(f => f.GetProperty("toolCallId").GetString()).Should().BeEquivalentTo("tc_a1", "tc_a2");
        await hub.DrainAsync(cts.Token);
        hub.PendingSnapshot().Should().ContainSingle().Which.ToolCallId.Should().Be("tc_b");
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
