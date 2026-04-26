namespace AchieveAi.LmDotnetTools.MockProviderHost.Tests.Infrastructure;

/// <summary>
/// <see cref="DelegatingHandler"/> that tees the inner handler's response body into a
/// <see cref="MemoryStream"/>, so tests can assert that bytes returned over the wire are
/// byte-identical to bytes the inner scripted handler produced.
/// </summary>
internal sealed class TeeHandler : DelegatingHandler
{
    private readonly MemoryStream _captured = new();

    public TeeHandler(HttpMessageHandler inner)
        : base(inner)
    {
    }

    /// <summary>Bytes written to the response stream by the inner handler.</summary>
    public byte[] CapturedBytes
    {
        get
        {
            lock (_captured)
            {
                return _captured.ToArray();
            }
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.Content is null)
        {
            return response;
        }

        // Read the inner stream once, capture it, and substitute a ByteArrayContent so the
        // forwarder downstream sees the same bytes. Doing this at the SendAsync boundary keeps
        // the capture and the served bytes provably identical.
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        lock (_captured)
        {
            _captured.Write(bytes, 0, bytes.Length);
        }

        var newContent = new ByteArrayContent(bytes);
        foreach (var header in response.Content.Headers)
        {
            newContent.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        response.Content.Dispose();
        response.Content = newContent;
        return response;
    }
}
