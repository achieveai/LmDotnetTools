using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.Sandbox.Tests.Command;

public sealed class StreamingOperationTests
{
    [Fact]
    public async Task SupportsStreamingAsync_RequiresTheCurrentOwnedSessionProtocol()
    {
        var (client, gateway) = TestSupport.CreateBorrowedClient();
        gateway.OnJson(
            HttpMethod.Get,
            "/sandboxes/sess-1/operations/capabilities",
            """{"streaming":true,"protocol_version":4}"""
        );
        (await client.SupportsStreamingAsync("sess-1")).Should().BeTrue();
        gateway.Requests.Single().SessionId.Should().Be("sess-1");

        var (oldClient, oldGateway) = TestSupport.CreateBorrowedClient();
        oldGateway.OnJson(
            HttpMethod.Get,
            "/sandboxes/sess-1/operations/capabilities",
            """{"streaming":true,"protocol_version":3}"""
        );
        (await oldClient.SupportsStreamingAsync("sess-1")).Should().BeFalse();
    }

    [Fact]
    public async Task ExecuteStreamingAsync_DeliversStdoutBeforeTerminalFrame_AndSendsNativeRequest()
    {
        var (client, gateway) = TestSupport.CreateBorrowedClient();
        RegisterWorkspaceMount(gateway);
        var stdout = "{\"type\":\"stdout\",\"data_base64\":\"aGVsbG8=\"}\n";
        var terminal =
            "{\"type\":\"result\",\"result\":{\"protocol_version\":4,\"outcome\":\"succeeded\",\"exit_code\":0,\"stdout_bytes\":5,\"stderr_bytes\":0}}\n";
        using var stream = new GatedStream(stdout, terminal);
        RegisterStream(gateway, stream);
        var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var chunks = new List<SandboxOutputChunk>();

        var running = client.ExecuteStreamingAsync(
            "sess-1",
            new SandboxCommand(["/workspace/app", "one two"], workingDirectory: "apps/demo"),
            (chunk, _) =>
            {
                chunks.Add(chunk);
                delivered.TrySetResult();
                return ValueTask.CompletedTask;
            },
            stdin: Encoding.UTF8.GetBytes("name=Sam"),
            environment: new Dictionary<string, string> { ["REQUEST_METHOD"] = "POST" },
            maxOutputBytes: 1024
        );

        await delivered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        running.IsCompleted.Should().BeFalse("the terminal frame has not arrived");
        chunks.Should().ContainSingle();
        chunks[0].Stream.Should().Be(SandboxOutputStream.Stdout);
        chunks[0].Data.ToArray().Should().Equal(Encoding.UTF8.GetBytes("hello"));
        stream.ReleaseTerminal();
        var result = await running.WaitAsync(TimeSpan.FromSeconds(5));
        result.ExitCode.Should().Be(0);
        result.StdoutBytes.Should().Be(5);
        result.StderrBytes.Should().Be(0);

        var request = gateway.Requests.Single(r =>
            r.Uri.AbsolutePath.EndsWith("/operations/stream", StringComparison.Ordinal)
        );
        request.Method.Should().Be(HttpMethod.Post);
        request.SbxAppId.Should().Be("app-1");
        request.SessionId.Should().Be("sess-1");
        using var body = JsonDocument.Parse(request.Body!);
        body.RootElement.GetProperty("executable").GetString().Should().Be("/workspace/app");
        body.RootElement.GetProperty("args")[0].GetString().Should().Be("one two");
        body.RootElement.GetProperty("cwd").GetProperty("mount_id").GetInt64().Should().Be(7);
        body.RootElement.GetProperty("cwd").GetProperty("path").GetString().Should().Be("apps/demo");
        body.RootElement.GetProperty("env").GetProperty("REQUEST_METHOD").GetString().Should().Be("POST");
        body.RootElement.GetProperty("stdin_base64").GetString().Should().Be("bmFtZT1TYW0=");
        body.RootElement.GetProperty("max_output_bytes").GetInt64().Should().Be(1024);
        body.RootElement.GetProperty("timeout_secs").GetInt64().Should().Be(30);
    }

    [Fact]
    public async Task ExecuteStreamingAsync_UsesAppLimitEvenWhenGeneralOperationTimeoutIsShorter()
    {
        var (client, gateway) = TestSupport.CreateBorrowedClient(executionTimeout: TimeSpan.FromSeconds(5));
        RegisterWorkspaceMount(gateway);
        RegisterStream(
            gateway,
            new MemoryStream(
                Encoding.UTF8.GetBytes(
                    "{\"type\":\"result\",\"result\":{\"protocol_version\":4,\"outcome\":\"succeeded\",\"exit_code\":0,\"stdout_bytes\":0,\"stderr_bytes\":0}}\n"
                )
            )
        );

        await client.ExecuteStreamingAsync("sess-1", new SandboxCommand(["app"]), (_, _) => ValueTask.CompletedTask);

        var request = gateway.Requests.Single(r =>
            r.Uri.AbsolutePath.EndsWith("/operations/stream", StringComparison.Ordinal)
        );
        using var body = JsonDocument.Parse(request.Body!);
        body.RootElement.GetProperty("timeout_secs").GetInt64().Should().Be(30);
    }

    [Fact]
    public async Task ExecuteStreamingAsync_PreservesStderrAndWaitsForSlowCallback()
    {
        var (client, gateway) = TestSupport.CreateBorrowedClient();
        RegisterWorkspaceMount(gateway);
        using var stream = new GatedStream(
            "{\"type\":\"stdout\",\"data_base64\":\"YQ==\"}\n",
            "{\"type\":\"stderr\",\"data_base64\":\"Yg==\"}\n"
                + "{\"type\":\"result\",\"result\":{\"protocol_version\":4,\"outcome\":\"failed\",\"exit_code\":7,\"stdout_bytes\":1,\"stderr_bytes\":1}}\n"
        );
        RegisterStream(gateway, stream);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCallback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var received = new List<SandboxOutputChunk>();
        var running = client.ExecuteStreamingAsync(
            "sess-1",
            new SandboxCommand(["app"]),
            async (chunk, ct) =>
            {
                received.Add(chunk);
                if (chunk.Stream == SandboxOutputStream.Stdout)
                {
                    entered.TrySetResult();
                    await releaseCallback.Task.WaitAsync(ct);
                }
            }
        );

        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        stream.ReleaseTerminal();
        received.Should().ContainSingle("the SDK waits for the stdout callback before reading stderr");
        running.IsCompleted.Should().BeFalse();
        releaseCallback.TrySetResult();
        var result = await running.WaitAsync(TimeSpan.FromSeconds(5));
        result.ExitCode.Should().Be(7);
        received.Select(chunk => chunk.Stream).Should().Equal(SandboxOutputStream.Stdout, SandboxOutputStream.Stderr);
        received[1].Data.ToArray().Should().Equal("b"u8.ToArray());
    }

    [Theory]
    [InlineData("{\"type\":\"stdout\",\"data_base64\":\"$\"}\n")]
    [InlineData("{\"type\":\"unknown\"}\n")]
    [InlineData(
        "{\"type\":\"result\",\"result\":{\"protocol_version\":1,\"outcome\":\"succeeded\",\"exit_code\":0,\"stdout_bytes\":0,\"stderr_bytes\":0}}\n"
    )]
    [InlineData(
        "{\"type\":\"result\",\"result\":{\"protocol_version\":2,\"outcome\":\"succeeded\",\"exit_code\":0,\"stdout_bytes\":0,\"stderr_bytes\":0}}\n"
    )]
    [InlineData(
        "{\"type\":\"result\",\"result\":{\"protocol_version\":4,\"outcome\":\"succeeded\",\"exit_code\":0,\"stdout_bytes\":1,\"stderr_bytes\":0}}\n"
    )]
    public async Task ExecuteStreamingAsync_RejectsMalformedFrames(string frame)
    {
        var (client, gateway) = TestSupport.CreateBorrowedClient();
        RegisterWorkspaceMount(gateway);
        RegisterStream(gateway, new MemoryStream(Encoding.UTF8.GetBytes(frame)));

        var act = () =>
            client.ExecuteStreamingAsync("sess-1", new SandboxCommand(["app"]), (_, _) => ValueTask.CompletedTask);
        (await act.Should().ThrowAsync<SandboxException>()).Which.Kind.Should().Be(SandboxErrorKind.Protocol);
    }

    [Fact]
    public async Task ExecuteStreamingAsync_RejectsOversizedFrameBeforeCallback()
    {
        var (client, gateway) = TestSupport.CreateBorrowedClient();
        RegisterWorkspaceMount(gateway);
        var frame = "{\"type\":\"stdout\",\"data_base64\":\"" + new string('A', 128 * 1024) + "\"}\n";
        RegisterStream(gateway, new MemoryStream(Encoding.UTF8.GetBytes(frame)));
        var callbacks = 0;

        var act = () =>
            client.ExecuteStreamingAsync(
                "sess-1",
                new SandboxCommand(["app"]),
                (_, _) =>
                {
                    callbacks++;
                    return ValueTask.CompletedTask;
                }
            );
        (await act.Should().ThrowAsync<SandboxException>()).Which.Kind.Should().Be(SandboxErrorKind.Protocol);
        callbacks.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteStreamingAsync_CancellationDisposesResponseStream()
    {
        var (client, gateway) = TestSupport.CreateBorrowedClient();
        RegisterWorkspaceMount(gateway);
        using var stream = new GatedStream("{\"type\":\"stdout\",\"data_base64\":\"YQ==\"}\n", "");
        RegisterStream(gateway, stream);
        using var cts = new CancellationTokenSource();
        var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = client.ExecuteStreamingAsync(
            "sess-1",
            new SandboxCommand(["app"]),
            (_, _) =>
            {
                delivered.TrySetResult();
                return ValueTask.CompletedTask;
            },
            ct: cts.Token
        );

        await delivered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();
        Func<Task> act = async () => await running;
        await act.Should().ThrowAsync<OperationCanceledException>();
        stream.Disposed.Should().BeTrue();
    }

    [Theory]
    [InlineData("timed_out", SandboxErrorKind.ExecutionTimeout)]
    [InlineData("output_limit_exceeded", SandboxErrorKind.OutputLimitExceeded)]
    [InlineData("internal_failure", SandboxErrorKind.OperationFailed)]
    [InlineData("cancelled", SandboxErrorKind.OperationFailed)]
    public async Task ExecuteStreamingAsync_MapsTerminalFailure(string outcome, SandboxErrorKind kind)
    {
        var (client, gateway) = TestSupport.CreateBorrowedClient();
        RegisterWorkspaceMount(gateway);
        var frame =
            "{\"type\":\"result\",\"result\":{\"protocol_version\":4,\"outcome\":\""
            + outcome
            + "\",\"stdout_bytes\":0,\"stderr_bytes\":0}}\n";
        RegisterStream(gateway, new MemoryStream(Encoding.UTF8.GetBytes(frame)));

        var act = () =>
            client.ExecuteStreamingAsync("sess-1", new SandboxCommand(["app"]), (_, _) => ValueTask.CompletedTask);
        (await act.Should().ThrowAsync<SandboxException>()).Which.Kind.Should().Be(kind);
    }

    private static void RegisterWorkspaceMount(FakeGatewayHandler gateway) =>
        gateway.OnJson(
            HttpMethod.Get,
            "/sandboxes/sess-1",
            """{"session_id":"sess-1","container_id":null,"volumes":{"workspace":{"container_path":"/workspace","read_only":false,"id":7}}}"""
        );

    private static void RegisterStream(FakeGatewayHandler gateway, Stream stream) =>
        gateway.On(
            req =>
                req.Method == HttpMethod.Post
                && req.RequestUri!.AbsolutePath.EndsWith("/operations/stream", StringComparison.Ordinal),
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) }
        );

    private sealed class GatedStream(string first, string terminal) : Stream
    {
        private readonly byte[] _first = Encoding.UTF8.GetBytes(first);
        private readonly byte[] _terminal = Encoding.UTF8.GetBytes(terminal);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _firstOffset;
        private int _terminalOffset;
        public bool Disposed { get; private set; }

        public void ReleaseTerminal() => _release.TrySetResult();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default
        )
        {
            if (_firstOffset < _first.Length)
            {
                var n = Math.Min(buffer.Length, _first.Length - _firstOffset);
                _first.AsMemory(_firstOffset, n).CopyTo(buffer);
                _firstOffset += n;
                return n;
            }

            await _release.Task.WaitAsync(cancellationToken);
            if (_terminalOffset == _terminal.Length)
            {
                return 0;
            }

            var amount = Math.Min(buffer.Length, _terminal.Length - _terminalOffset);
            _terminal.AsMemory(_terminalOffset, amount).CopyTo(buffer);
            _terminalOffset += amount;
            return amount;
        }
    }
}
