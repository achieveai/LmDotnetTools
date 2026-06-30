using System.Net;

namespace AchieveAi.LmDotnetTools.LmTestUtils.Http;

/// <summary>
///     An <see cref="HttpResponseMessage"/> that records whether it has been disposed, so tests can
///     assert response/connection lifetime (e.g. that a failed or fully-consumed response is disposed
///     and not leaked). Shared across provider test projects to avoid duplicate definitions.
/// </summary>
public sealed class TrackingHttpResponseMessage : HttpResponseMessage
{
    private int _disposeCount;

    public TrackingHttpResponseMessage(HttpStatusCode statusCode)
        : base(statusCode) { }

    /// <summary>True once <see cref="Dispose(bool)"/> has run at least once.</summary>
    public bool Disposed => Volatile.Read(ref _disposeCount) > 0;

    /// <summary>Optional tracked body stream (success/streaming responses) so its disposal can be asserted too.</summary>
    public TrackingStream? BodyStream { get; init; }

    protected override void Dispose(bool disposing)
    {
        _ = Interlocked.Increment(ref _disposeCount);
        base.Dispose(disposing);
    }
}

/// <summary>A <see cref="MemoryStream"/> that records whether it has been disposed.</summary>
public sealed class TrackingStream : MemoryStream
{
    private int _disposeCount;

    public TrackingStream(byte[] buffer)
        : base(buffer) { }

    /// <summary>True once <see cref="Dispose(bool)"/> has run at least once.</summary>
    public bool Disposed => Volatile.Read(ref _disposeCount) > 0;

    protected override void Dispose(bool disposing)
    {
        _ = Interlocked.Increment(ref _disposeCount);
        base.Dispose(disposing);
    }
}
