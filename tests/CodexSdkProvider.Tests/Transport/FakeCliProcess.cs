using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using AchieveAi.LmDotnetTools.ProcessLauncher;

namespace AchieveAi.LmDotnetTools.Testing.Transport;

/// <summary>A JSON-RPC response the transport under test wrote to its CLI's stdin.</summary>
internal sealed record FakeCliResponse(long Id, JsonElement Result);

/// <summary>
/// A stdio CLI the test drives directly: what it writes to stdout the transport reads, and what
/// the transport writes to its stdin the test reads back. Shared by the Codex and Copilot transport
/// tests, which drive byte-identical stdio JSON-RPC.
/// </summary>
/// <remarks>
/// The streams are in-memory rather than real pipes. A pipe would be the more faithful fake, but an
/// anonymous pipe handle on Windows is not opened for overlapped I/O, so a read already in flight
/// ignores its cancellation token and only ends when the write handle closes — which leaves a test
/// that fails looking exactly like a test that hangs. These streams honour the token and reach
/// end-of-stream on demand, so shutdown is observable either way.
/// </remarks>
internal sealed class FakeCliProcess : IDisposable
{
    private readonly ScriptedOutputStream _stdout = new();
    private readonly CapturedInputStream _stdin = new();
    private readonly Handle _handle;

    public FakeCliProcess()
    {
        _handle = new Handle(
            stdin: new StreamWriter(_stdin, new UTF8Encoding(false)) { AutoFlush = true },
            stdout: new StreamReader(_stdout, Encoding.UTF8),
            stderr: new StreamReader(new MemoryStream()),
            // A real process closes stdout when it exits, and that end-of-stream is what lets the
            // transport's read loop finish.
            onExit: _stdout.SignalEndOfStream
        );

        Launcher = new Handoff(_handle);
    }

    /// <summary>Hand this to the options under test in place of the real process launcher.</summary>
    public IProcessLauncher Launcher { get; }

    /// <summary>Emits one line of CLI stdout for the transport to read.</summary>
    public ValueTask WriteStdoutLineAsync(string line) => _stdout.EmitLineAsync(line);

    /// <summary>Reads the next line the transport wrote and parses its id and result.</summary>
    public async Task<FakeCliResponse> ReadResponseAsync(TimeSpan timeout)
    {
        var line = await _stdin.ReadLineAsync(timeout);

        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        Assert.False(root.TryGetProperty("error", out var error), $"expected a result, got an error: {error}");

        return new FakeCliResponse(root.GetProperty("id").GetInt64(), root.GetProperty("result").Clone());
    }

    public void Dispose()
    {
        _handle.Dispose();
        _stdout.Dispose();
        _stdin.Dispose();
    }

    /// <summary>Stdout as the transport sees it: lines on demand, then end-of-stream.</summary>
    private sealed class ScriptedOutputStream : Stream
    {
        private readonly Channel<ReadOnlyMemory<byte>> _chunks = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();

        private ReadOnlyMemory<byte> _unread;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public ValueTask EmitLineAsync(string line) => _chunks.Writer.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"));

        public void SignalEndOfStream() => _chunks.Writer.TryComplete();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default
        )
        {
            if (_unread.IsEmpty)
            {
                if (!await _chunks.Reader.WaitToReadAsync(cancellationToken) || !_chunks.Reader.TryRead(out var chunk))
                {
                    return 0;
                }

                _unread = chunk;
            }

            var taken = Math.Min(buffer.Length, _unread.Length);
            _unread[..taken].CopyTo(buffer);
            _unread = _unread[taken..];
            return taken;
        }

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken
        ) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

        public override void Flush() { }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            SignalEndOfStream();
            base.Dispose(disposing);
        }
    }

    /// <summary>Stdin as the transport sees it: whole lines, handed to whoever asks for them.</summary>
    private sealed class CapturedInputStream : Stream
    {
        private readonly Channel<string> _lines = Channel.CreateUnbounded<string>();
        private readonly StringBuilder _partial = new();
        private readonly Lock _gate = new();

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public async Task<string> ReadLineAsync(TimeSpan timeout)
        {
            using var expiry = new CancellationTokenSource(timeout);
            try
            {
                return await _lines.Reader.ReadAsync(expiry.Token);
            }
            catch (OperationCanceledException)
            {
                Assert.Fail($"the transport wrote nothing within {timeout}");
                throw;
            }
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        // The transport serializes its own writes, so a whole line arrives before the next one
        // starts; the lock only guards against a flush racing that on another thread.
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            lock (_gate)
            {
                foreach (var character in Encoding.UTF8.GetString(buffer))
                {
                    if (character == '\n')
                    {
                        _ = _lines.Writer.TryWrite(_partial.ToString().TrimEnd('\r'));
                        _ = _partial.Clear();
                    }
                    else
                    {
                        _ = _partial.Append(character);
                    }
                }
            }
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Write(buffer.Span);
            return ValueTask.CompletedTask;
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            Write(buffer.AsSpan(offset, count));
            return Task.CompletedTask;
        }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private sealed class Handoff(IProcessHandle handle) : IProcessLauncher
    {
        public Task<IProcessHandle> LaunchAsync(
            ProcessLaunchRequest request,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(handle);
    }

    private sealed class Handle(StreamWriter stdin, StreamReader stdout, StreamReader stderr, Action onExit)
        : IProcessHandle
    {
        private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool HasExited { get; private set; }

        public int? ExitCode => HasExited ? 0 : null;

        public int? ProcessId => null;

        public event EventHandler? Exited;

        public StreamWriter StandardInput { get; } = stdin;

        public StreamReader StandardOutput { get; } = stdout;

        public StreamReader StandardError { get; } = stderr;

        public Task<int> WaitForExitAsync(CancellationToken cancellationToken = default) =>
            _exited.Task.WaitAsync(cancellationToken);

        public bool WaitForExit(TimeSpan timeout) => _exited.Task.Wait(timeout);

        public void Kill(bool entireProcessTree = true)
        {
            if (HasExited)
            {
                return;
            }

            HasExited = true;
            onExit();
            _ = _exited.TrySetResult(0);
            Exited?.Invoke(this, EventArgs.Empty);
        }

        public void Dispose()
        {
            Kill();
            StandardError.Dispose();
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
