namespace LmStreaming.Sample.FileBrowser;

/// <summary>
/// The fixed size/paging limits the workspace file browser enforces (WI #195). All values are exact and
/// shared by the controller, Kestrel, and the multipart form options so the declared ceiling is identical
/// everywhere.
/// </summary>
public static class FileBrowserLimits
{
    /// <summary>Inclusive per-file upload ceiling: exactly 64 MiB. A file of exactly this size succeeds; one byte more is rejected.</summary>
    public const long MaxFileBytes = 67_108_864;

    /// <summary>Bounded-buffered download ceiling: exactly 64 MiB. A file larger than this is refused (413) without truncation.</summary>
    public const long MaxDownloadBytes = 67_108_864;

    /// <summary>
    /// The whole multipart upload request ceiling: <see cref="MaxFileBytes"/> plus a fixed 8 KiB allowance
    /// for multipart framing overhead. Applied identically to the upload endpoint, Kestrel's
    /// <c>MaxRequestBodySize</c>, and the form options' <c>MultipartBodyLengthLimit</c>.
    /// </summary>
    public const long MaxUploadRequestBytes = MaxFileBytes + 8_192;

    /// <summary>Text preview byte cap: 256 KiB. A larger file (by listed size or streamed bytes) is not previewed.</summary>
    public const long PreviewByteCap = 262_144;

    /// <summary>Text preview line cap: 5000 lines.</summary>
    public const int PreviewLineCap = 5000;

    /// <summary>Maximum directory rows returned in one listing; the remainder is reported as a count.</summary>
    public const int MaxListingRows = 500;
}
