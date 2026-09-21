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
    /// small workbook. This bounds only what is read off disk; what the package EXPANDS to is bounded
    /// separately by <see cref="SpreadsheetExpandedByteCap"/>, because deflate makes the two unrelated.
    /// </summary>
    public const long SpreadsheetPreviewByteCap = 4_194_304;

    /// <summary>
    /// Expanded (decompressed) spreadsheet input cap: exactly 64 MiB, the total of every part inside the
    /// OOXML package. Deflate reaches roughly 1000:1 on repetitive bytes, so a workbook well under
    /// <see cref="SpreadsheetPreviewByteCap"/> can still inflate to hundreds of MB; without this, the
    /// row/column/sheet caps bound the OUTPUT while the reader has already paid for the whole input. Sized
    /// to <see cref="MaxFileBytes"/> so a workbook can never cost more to expand than the largest file the
    /// workspace would have accepted uncompressed in the first place.
    /// </summary>
    public const long SpreadsheetExpandedByteCap = MaxFileBytes;

    /// <summary>
    /// Characters kept from one preview cell: 4096. A grid cell renders a line or two, so this is already
    /// far past readable, while staying well under the 32767 a real spreadsheet cell may hold — the point
    /// is only that ONE shared string cannot carry the whole response. A shortened cell marks its sheet
    /// truncated.
    /// </summary>
    public const int PreviewCellCharCap = 4_096;

    /// <summary>
    /// Characters retained across the WHOLE preview: 2 Mi characters (4 MiB as UTF-16, and about as much
    /// again once JSON-encoded). The per-cell, row, column, and sheet caps each bound one dimension, and
    /// their product is far larger than any response worth sending; this is the one number that bounds what
    /// the client actually receives. Reading stops at the budget and the table is marked truncated.
    /// </summary>
    public const long PreviewOutputCharCap = 2_097_152;

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
