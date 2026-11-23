namespace LmStreaming.Sample.Tests.WebSocket;

/// <summary>A stream every write and flush of which fails, like a recording on a full disk.</summary>
internal sealed class FailingStream : Stream
{
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => 0;
    public override long Position { get; set; }

    public override void Flush() => throw new IOException("The recording disk is full.");

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new IOException("The recording disk is full.");
}
