using System.Net;
using System.Text;

namespace AchieveAi.LmDotnetTools.CopilotAnthropicProxy.Tests;

/// <summary>Builders for fake upstream Copilot responses used by the proxy tests.</summary>
internal static class TestUpstream
{
    /// <summary>An <c>application/json</c> response with an optional status and extra response headers.</summary>
    public static HttpResponseMessage Json(
        string body,
        HttpStatusCode status = HttpStatusCode.OK,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

        if (headers is not null)
        {
            foreach (var (name, value) in headers)
            {
                _ = response.Headers.TryAddWithoutValidation(name, value);
            }
        }

        return response;
    }

    /// <summary>A buffered <c>text/event-stream</c> response carrying <paramref name="body"/> verbatim.</summary>
    public static HttpResponseMessage Sse(string body)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/event-stream"),
        };
    }

    /// <summary>A <c>text/event-stream</c> response backed by a caller-controlled stream.</summary>
    public static HttpResponseMessage SseStream(Stream stream)
    {
        var content = new StreamContent(stream);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }
}

/// <summary>
///     A stream that emits a fixed prefix once, then blocks until its read is cancelled, recording that
///     the cancellation was observed. Used to prove client cancellation propagates to the upstream read.
/// </summary>
internal sealed class CancellationObservingStream : Stream
{
    private readonly byte[] _prefix;
    private readonly TaskCompletionSource _cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _position;

    public CancellationObservingStream(string prefix) => _prefix = Encoding.UTF8.GetBytes(prefix);

    /// <summary>Completes when a read is cancelled (i.e. the proxy cancelled the upstream).</summary>
    public Task Cancelled => _cancelled.Task;

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_position < _prefix.Length)
        {
            var count = Math.Min(buffer.Length, _prefix.Length - _position);
            _prefix.AsMemory(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }

        try
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            _ = _cancelled.TrySetResult();
            throw;
        }

        return 0;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>
///     Emits <c>first</c> immediately, then blocks the next read until <see cref="Release"/> is called,
///     then emits <c>second</c>. Lets a test prove the proxy flushes the first frame to the client
///     before the upstream has produced the rest (i.e. it streams incrementally, not buffer-to-end).
/// </summary>
internal sealed class GatedStream : Stream
{
    private readonly byte[] _first;
    private readonly byte[] _second;
    private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _firstPos;
    private int _secondPos;

    public GatedStream(string first, string second)
    {
        _first = Encoding.UTF8.GetBytes(first);
        _second = Encoding.UTF8.GetBytes(second);
    }

    /// <summary>Unblocks emission of the second frame.</summary>
    public void Release() => _gate.TrySetResult();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_firstPos < _first.Length)
        {
            var count = Math.Min(buffer.Length, _first.Length - _firstPos);
            _first.AsMemory(_firstPos, count).CopyTo(buffer);
            _firstPos += count;
            return count;
        }

        if (_secondPos == 0)
        {
            await _gate.Task.WaitAsync(cancellationToken);
        }

        if (_secondPos < _second.Length)
        {
            var count = Math.Min(buffer.Length, _second.Length - _secondPos);
            _second.AsMemory(_secondPos, count).CopyTo(buffer);
            _secondPos += count;
            return count;
        }

        return 0;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>A stream that emits a fixed prefix once, then throws on the next read (mid-stream failure).</summary>
internal sealed class ThrowingStream : Stream
{
    private readonly byte[] _prefix;
    private int _position;

    public ThrowingStream(string prefix) => _prefix = Encoding.UTF8.GetBytes(prefix);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_position < _prefix.Length)
        {
            var count = Math.Min(buffer.Length, _prefix.Length - _position);
            _prefix.AsMemory(_position, count).CopyTo(buffer);
            _position += count;
            return ValueTask.FromResult(count);
        }

        throw new IOException("Simulated mid-stream upstream failure.");
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
