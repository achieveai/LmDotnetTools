namespace AchieveAi.LmDotnetTools.Sandbox.Tests;

/// <summary>
/// A non-seekable body (so no Content-Length) that serves <c>content</c> in chunks, counts what it hands out, and
/// can throw part-way (<see cref="FailAfterBytes"/>) or stall until the read's token fires (<see cref="Stall"/>).
/// </summary>
internal sealed class ScriptedBodyStream(byte[] content, int chunkSize) : Stream
{
    public int BytesRead { get; private set; }

    public int? FailAfterBytes { get; init; }

    public bool Stall { get; init; }

    public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        _ = ReadStarted.TrySetResult();
        if (Stall)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        }

        if (FailAfterBytes is { } failAt && BytesRead >= failAt)
        {
            throw new IOException("connection reset: token=SECRET");
        }

        var count = Math.Min(Math.Min(buffer.Length, chunkSize), content.Length - BytesRead);
        content.AsMemory(BytesRead, count).CopyTo(buffer);
        BytesRead += count;
        return count;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override void Flush() { }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
