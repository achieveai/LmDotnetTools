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

    /// <summary>
    /// Spreadsheet preview byte cap: 4 MiB. Deliberately separate from <see cref="PreviewByteCap"/> and much
    /// larger: an <c>.xlsx</c> is a deflate-compressed OOXML package, so 256 KiB of bytes is an arbitrarily
    /// small workbook, while what actually bounds the work and the response is <see cref="PreviewLineCap"/>
    /// rows x <see cref="PreviewColumnCap"/> columns x <see cref="PreviewSheetCap"/> sheets.
    /// </summary>
    public const long SpreadsheetPreviewByteCap = 4_194_304;

    /// <summary>Columns kept per spreadsheet row; cells beyond this are dropped and the sheet is marked truncated.</summary>
    public const int PreviewColumnCap = 256;

    /// <summary>Sheets read from one workbook; the remainder is dropped and the table is marked truncated.</summary>
    public const int PreviewSheetCap = 32;

    /// <summary>Maximum directory rows returned in one listing; the remainder is reported as a count.</summary>
    public const int MaxListingRows = 500;

    /// <summary>
    /// How long a minted workspace READ grant stays valid (Bug#15): one hour. Long enough that a person can
    /// read a rendered report without the page's images dying underneath them, short enough that a grant
    /// copied out of a URL bar is worth little by the time it is pasted anywhere. A <c>TimeSpan</c> rather
    /// than a count of seconds because both the minting clock and the client's refresh margin do arithmetic
    /// with it.
    /// </summary>
    public static readonly TimeSpan WorkspaceGrantLifetime = TimeSpan.FromHours(1);

    /// <summary>
    /// How far ahead of <see cref="WorkspaceGrantLifetime"/> the client refreshes a cached grant: five
    /// minutes. Sized for the page, not the round trip — an <c>&lt;iframe&gt;</c> that is already open keeps
    /// loading subresources with the grant it was given, so the margin has to cover a user reading a
    /// rendered page for a few minutes after the app last minted one.
    /// </summary>
    public static readonly TimeSpan WorkspaceGrantRefreshMargin = TimeSpan.FromMinutes(5);
}
