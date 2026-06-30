using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;
using AchieveAi.LmDotnetTools.GithubCopilotProvider.Agents;
using AchieveAi.LmDotnetTools.GithubCopilotProvider.Auth;
using AchieveAi.LmDotnetTools.LmCore.Http;
using AchieveAi.LmDotnetTools.OpenAiResponsesProvider.Models;
using FluentAssertions;

namespace AchieveAi.LmDotnetTools.GithubCopilotProvider.Tests.Agents;

/// <summary>
///     Connect-retry + close/fault behaviour for <see cref="CopilotResponsesWebSocketClient"/>:
///     transient connect failures are retried with a FRESH socket per attempt (bounded by
///     <see cref="RetryOptions"/>); non-retryable failures and cancellation surface immediately; a
///     mid-turn peer close before the terminal lifecycle event is reported as an error; and a
///     truncated turn never advances the <c>previous_response_id</c> chain.
/// </summary>
public sealed class CopilotResponsesWebSocketClientRetryTests
{
    private sealed class StubTokenProvider : ICopilotTokenProvider
    {
        public Task<string> GetTokenAsync(CancellationToken cancellationToken = default) => Task.FromResult("gho_test");
    }

    /// <summary>Configurable in-memory socket: scriptable connect failure, frames, receive fault, disposal.</summary>
    private sealed class FakeSocket : ICopilotResponsesSocket
    {
        private readonly Queue<string> _incoming = new();

        /// <summary>Exception thrown from <see cref="ConnectAsync"/> (after <see cref="OnConnect"/>).</summary>
        public Exception? ConnectError { get; set; }

        /// <summary>Callback run inside <see cref="ConnectAsync"/> (e.g. to cancel a token).</summary>
        public Action? OnConnect { get; set; }

        /// <summary>Frames to enqueue in response to a sent <c>response.create</c>.</summary>
        public Func<string, IEnumerable<string>>? Respond { get; set; }

        /// <summary>Exception thrown by <see cref="ReceiveTextAsync"/> once the queue drains.</summary>
        public Exception? ReceiveError { get; set; }

        public List<string> Sent { get; } = [];
        public List<IReadOnlyDictionary<string, string>> Connects { get; } = [];
        public bool IsConnected { get; private set; }
        public int DisposeCount { get; private set; }
        public bool Disposed => DisposeCount > 0;

        public Task ConnectAsync(Uri endpoint, IReadOnlyDictionary<string, string> headers, CancellationToken ct)
        {
            Connects.Add(headers);
            OnConnect?.Invoke();

            // ConnectError models an upgrade/transport failure; a cancelled token models a cancelled connect.
            if (ConnectError is not null)
            {
                throw ConnectError;
            }

            ct.ThrowIfCancellationRequested();
            IsConnected = true;
            return Task.CompletedTask;
        }

        public Task SendTextAsync(string text, CancellationToken ct)
        {
            Sent.Add(text);
            if (Respond is not null)
            {
                foreach (var frame in Respond(text))
                {
                    _incoming.Enqueue(frame);
                }
            }

            return Task.CompletedTask;
        }

        public Task<string?> ReceiveTextAsync(CancellationToken ct)
        {
            if (_incoming.Count > 0)
            {
                return Task.FromResult<string?>(_incoming.Dequeue());
            }

            if (ReceiveError is not null)
            {
                throw ReceiveError;
            }

            // Drained with no terminal event => peer closed mid-turn.
            IsConnected = false;
            return Task.FromResult<string?>(null);
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            IsConnected = false;
            return ValueTask.CompletedTask;
        }
    }

    private static WebSocketException RetryableConnect() =>
        new(WebSocketError.Faulted, new SocketException((int)SocketError.ConnectionRefused));

    /// <summary>A 502 returned during the WebSocket upgrade — the core gateway case from #114.</summary>
    private static WebSocketException BadGatewayUpgrade() =>
        new("upgrade rejected", new HttpRequestException("Bad Gateway", null, HttpStatusCode.BadGateway));

    private static IEnumerable<string> ScriptedTurn(int turnIndex)
    {
        var id = "resp-" + turnIndex;
        yield return "{\"type\":\"response.created\",\"sequence_number\":0,\"response\":{\"id\":\"" + id + "\"}}";
        yield return "{\"type\":\"response.output_text.delta\",\"sequence_number\":1,\"item_id\":\"i\",\"output_index\":0,\"content_index\":0,\"delta\":\"hi\"}";
        yield return "{\"type\":\"response.completed\",\"sequence_number\":2,\"response\":{\"id\":\"" + id + "\"}}";
    }

    private static IEnumerable<string> TruncatedTurn()
    {
        yield return "{\"type\":\"response.created\",\"sequence_number\":0,\"response\":{\"id\":\"resp-trunc\"}}";
        yield return "{\"type\":\"response.output_text.delta\",\"sequence_number\":1,\"item_id\":\"i\",\"output_index\":0,\"content_index\":0,\"delta\":\"hi\"}";
        // No response.completed — the socket then returns null (peer close).
    }

    private static CopilotResponsesWebSocketClient NewClient(Func<ICopilotResponsesSocket> factory) =>
        new(
            new Uri("wss://host/responses"),
            new StubTokenProvider(),
            new CopilotSessionContext("m", "s"),
            options: null,
            logger: null,
            socketFactory: factory,
            retryOptions: RetryOptions.FastForTests
        );

    private static ResponseCreateRequest Request() =>
        new()
        {
            Model = "gpt-5.5",
            Input = [new ResponseInputItem { Role = "user", Content = [new ResponseInputContent { Text = "hi" }] }],
        };

    /// <summary>Transient connect/upgrade failures that must be retried (socket refused, 502 upgrade).</summary>
    public enum TransientConnectError
    {
        SocketConnectionRefused,
        BadGatewayUpgrade,
    }

    private static WebSocketException MakeConnectError(TransientConnectError kind) =>
        kind switch
        {
            TransientConnectError.SocketConnectionRefused => RetryableConnect(),
            TransientConnectError.BadGatewayUpgrade => BadGatewayUpgrade(),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

    [Theory]
    [InlineData(TransientConnectError.SocketConnectionRefused)]
    [InlineData(TransientConnectError.BadGatewayUpgrade)]
    public async Task Connect_retries_transient_failure_with_fresh_socket(TransientConnectError kind)
    {
        var created = new List<FakeSocket>();
        var turn = 0;
        await using var client = NewClient(() =>
        {
            var socket = new FakeSocket();
            if (created.Count == 0)
            {
                socket.ConnectError = MakeConnectError(kind);
            }
            else
            {
                var index = turn++;
                socket.Respond = _ => ScriptedTurn(index);
            }

            created.Add(socket);
            return socket;
        });

        var events = await CollectAsync(client.StreamResponseAsync(Request()));

        events
            .Select(e => e.Type)
            .Should()
            .ContainInOrder(
                ResponseEventTypes.ResponseCreated,
                ResponseEventTypes.OutputTextDelta,
                ResponseEventTypes.ResponseCompleted
            );

        created.Should().HaveCount(2);
        created[0].Should().NotBeSameAs(created[1]); // a FRESH socket per attempt
        created[0].Disposed.Should().BeTrue("the failed socket must be disposed");
        created[0].Sent.Should().BeEmpty("response.create must not be sent on a socket that never connected");
        created[1].Sent.Should().ContainSingle("response.create is sent once, on the connected socket");
    }

    [Theory]
    [InlineData(TransientConnectError.SocketConnectionRefused)]
    [InlineData(TransientConnectError.BadGatewayUpgrade)]
    public async Task Connect_persistent_failure_throws_after_bounded_attempts(TransientConnectError kind)
    {
        var created = new List<FakeSocket>();
        await using var client = NewClient(() =>
        {
            var socket = new FakeSocket { ConnectError = MakeConnectError(kind) };
            created.Add(socket);
            return socket;
        });

        var act = async () => await CollectAsync(client.StreamResponseAsync(Request()));

        await act.Should().ThrowAsync<WebSocketException>();

        created.Should().HaveCount(RetryOptions.FastForTests.MaxRetries + 1);
        created.Should().OnlyContain(s => s.Disposed);
        created.Should().OnlyContain(s => s.Sent.Count == 0, "no response.create is sent before a successful connect");
    }

    [Fact]
    public async Task Connect_non_retryable_failure_throws_immediately()
    {
        var created = new List<FakeSocket>();
        var nonRetryable = new WebSocketException(
            "upgrade rejected",
            new HttpRequestException("Unauthorized", null, HttpStatusCode.Unauthorized)
        );
        await using var client = NewClient(() =>
        {
            var socket = new FakeSocket { ConnectError = nonRetryable };
            created.Add(socket);
            return socket;
        });

        var act = async () => await CollectAsync(client.StreamResponseAsync(Request()));

        await act.Should().ThrowAsync<WebSocketException>();

        created.Should().ContainSingle("a non-retryable connect failure is not retried");
        created[0].Disposed.Should().BeTrue();
        created[0].Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task Connect_dns_host_not_found_is_not_retried()
    {
        var created = new List<FakeSocket>();
        // WSAHOST_NOT_FOUND — an authoritative DNS "no such host" name-resolution failure (permanent),
        // distinct from the transient WSATRY_AGAIN. It must NOT consume the retry budget.
        var dnsFailure = new WebSocketException(
            WebSocketError.Faulted,
            new SocketException((int)SocketError.HostNotFound)
        );
        await using var client = NewClient(() =>
        {
            var socket = new FakeSocket { ConnectError = dnsFailure };
            created.Add(socket);
            return socket;
        });

        var act = async () => await CollectAsync(client.StreamResponseAsync(Request()));

        await act.Should().ThrowAsync<WebSocketException>();

        created.Should().ContainSingle("a DNS name-resolution failure (HostNotFound) is permanent and not retried");
        created[0].Disposed.Should().BeTrue();
        created[0].Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task Cancel_during_connect_throws_and_disposes_socket()
    {
        using var cts = new CancellationTokenSource();
        var created = new List<FakeSocket>();
        await using var client = NewClient(() =>
        {
            var socket = new FakeSocket { OnConnect = cts.Cancel };
            created.Add(socket);
            return socket;
        });

        var act = async () => await CollectAsync(client.StreamResponseAsync(Request(), cts.Token));

        await act.Should().ThrowAsync<OperationCanceledException>();

        created.Should().ContainSingle();
        created[0].Disposed.Should().BeTrue();
        created[0].Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task Cancel_during_backoff_throws_and_stops()
    {
        using var cts = new CancellationTokenSource();
        var created = new List<FakeSocket>();
        await using var client = NewClient(() =>
        {
            // First attempt fails retryably AND cancels the token, so the backoff delay is cancelled.
            var socket = new FakeSocket { ConnectError = RetryableConnect() };
            if (created.Count == 0)
            {
                socket.OnConnect = cts.Cancel;
            }

            created.Add(socket);
            return socket;
        });

        var act = async () => await CollectAsync(client.StreamResponseAsync(Request(), cts.Token));

        await act.Should().ThrowAsync<OperationCanceledException>();

        created.Should().ContainSingle("the retry never creates a second socket after cancellation");
        created[0].Disposed.Should().BeTrue();
    }

    [Fact]
    public async Task Mid_turn_close_before_completion_throws()
    {
        var fake = new FakeSocket { Respond = _ => TruncatedTurn() };
        await using var client = NewClient(() => fake);

        var act = async () => await CollectAsync(client.StreamResponseAsync(Request()));

        var ex = (await act.Should().ThrowAsync<IOException>()).Which;
        ex.Message.Should().Contain("closed before", "the truncated turn must surface as a clear error");
    }

    [Fact]
    public async Task Receive_websocket_exception_propagates_with_detail()
    {
        const string detail = "boom-1011";
        var fake = new FakeSocket
        {
            Respond = _ => TruncatedTurn(),
            ReceiveError = new WebSocketException(WebSocketError.ConnectionClosedPrematurely, detail),
        };
        await using var client = NewClient(() => fake);

        var act = async () => await CollectAsync(client.StreamResponseAsync(Request()));

        var ex = (await act.Should().ThrowAsync<WebSocketException>()).Which;
        ex.Message.Should().Contain(detail);
    }

    [Fact]
    public async Task Truncated_turn_does_not_advance_previous_response_id()
    {
        var created = new List<FakeSocket>();
        var turn = 0;
        await using var client = NewClient(() =>
        {
            FakeSocket socket;
            if (created.Count == 0)
            {
                socket = new FakeSocket { Respond = _ => TruncatedTurn() };
            }
            else
            {
                var index = turn++;
                socket = new FakeSocket { Respond = _ => ScriptedTurn(index) };
            }

            created.Add(socket);
            return socket;
        });

        // First turn truncates -> throws and must NOT advance _previousResponseId.
        var act = async () => await CollectAsync(client.StreamResponseAsync(Request()));
        await act.Should().ThrowAsync<IOException>();

        // Second turn reconnects (peer closed the first socket) and completes; its frame must carry
        // no previous_response_id because the truncated turn never reached response.completed.
        _ = await CollectAsync(client.StreamResponseAsync(Request()));

        created.Should().HaveCount(2);
        created[0]
            .Disposed.Should()
            .BeTrue("the stale, disconnected socket from the truncated turn must be disposed on reconnect, not leaked");
        var secondFrame = JsonDocument.Parse(created[1].Sent.Single()).RootElement;
        secondFrame.TryGetProperty("previous_response_id", out _).Should().BeFalse();
    }

    private static async Task<List<ResponseEvent>> CollectAsync(IAsyncEnumerable<ResponseEvent> stream)
    {
        var list = new List<ResponseEvent>();
        await foreach (var ev in stream)
        {
            list.Add(ev);
        }

        return list;
    }
}
