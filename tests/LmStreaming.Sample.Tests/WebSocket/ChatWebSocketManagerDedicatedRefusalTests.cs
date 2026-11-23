using System.Net.WebSockets;
using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
using LmStreaming.Sample.Services;
using LmStreaming.Sample.WebSocket;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace LmStreaming.Sample.Tests.WebSocket;

/// <summary>
/// The close that follows a <c>dedicated_sandbox_mode_unsupported</c> frame on <c>/ws</c>: it ends the connection
/// even when the peer never answers the close, and even when the recording of the frame fails.
/// </summary>
public sealed class ChatWebSocketManagerDedicatedRefusalTests : LoggingTestBase
{
    private const string Refusal =
        "A conversation with a dedicated sandbox cannot use a mode with browser debugging: it runs Mini Apps "
        + "through the gateway's operations API, which is not confined to the conversation's working directory.";

    public ChatWebSocketManagerDedicatedRefusalTests(ITestOutputHelper output)
        : base(output) { }

    [Fact]
    public async Task A_refusal_whose_peer_never_answers_the_close_aborts_the_socket_and_unregisters_it()
    {
        var registry = new WebSocketConnectionRegistry();
        var socket = new RefusalSocket { AnswersClose = false };
        var manager = CreateManager(registry);
        manager.RefusalCloseTimeout = TimeSpan.FromMilliseconds(100);

        var handled = Connect(manager, socket, recordWriter: null);

        await handled.WaitAsync(TimeSpan.FromSeconds(10));
        Logger.LogDebug(
            "Handler ended: sent {Frames}, close {CloseStatus}, aborted {Aborted}",
            socket.SentFrames,
            socket.CloseDescription,
            socket.Aborted
        );
        socket
            .SentFrames.Should()
            .ContainSingle(frame => frame.Contains("\"code\":\"dedicated_sandbox_mode_unsupported\""));
        socket.Aborted.Should().BeTrue("a close the peer never answers ends in an abort");
        registry.Snapshot().Should().BeEmpty("the handler's cleanup ran");
    }

    [Fact]
    public async Task A_refusal_whose_recording_fails_still_closes_with_its_reason_and_unregisters()
    {
        var registry = new WebSocketConnectionRegistry();
        var socket = new RefusalSocket();
        var recorder = new StreamWriter(new FailingStream());

        var handled = () => Connect(CreateManager(registry), socket, recorder);

        // The recording failure still surfaces, after the close: the route logs it, the client got its reason.
        await handled.Should().ThrowAsync<IOException>();
        Logger.LogDebug("Sent {Frames}, then closed {CloseDescription}", socket.SentFrames, socket.CloseDescription);
        socket
            .SentFrames.Should()
            .ContainSingle(frame => frame.Contains("\"code\":\"dedicated_sandbox_mode_unsupported\""));
        socket.CloseStatus.Should().Be(WebSocketCloseStatus.NormalClosure);
        socket.CloseDescription.Should().Be("Dedicated sandbox mode unsupported");
        registry.Snapshot().Should().BeEmpty("the handler's cleanup ran");
    }

    /// <summary>A pool whose factory refuses every build, as it refuses a dedicated thread in a Mini App mode.</summary>
    private static MultiTurnAgentPool CreatePool() =>
        new(
            (_, _, _) => throw new DedicatedSandboxModeUnsupportedException(Refusal),
            NullLogger<MultiTurnAgentPool>.Instance
        );

    private static ChatWebSocketManager CreateManager(WebSocketConnectionRegistry registry) =>
        new(
            CreatePool(),
            registry,
            new WorkflowRunRegistry(),
            new PendingAuthCoordinator(
                Mock.Of<IAuthEventNotifier>(),
                new AuthOptions(),
                NullLogger<PendingAuthCoordinator>.Instance
            ),
            new InMemoryConversationStore(),
            new LmStreaming.Sample.Tests.Services.NoOpSandboxEnvApplier(),
            NullLogger<ChatWebSocketManager>.Instance
        );

    private static Task Connect(ChatWebSocketManager manager, RefusalSocket socket, StreamWriter? recordWriter) =>
        manager.HandleConnectionAsync(
            socket,
            "dedicated-refused",
            mode: null,
            providerId: null,
            requestResponseDumpFileName: null,
            recordWriter: recordWriter,
            cancellationToken: CancellationToken.None,
            workspaceId: null,
            ownerUserId: null
        );
}
