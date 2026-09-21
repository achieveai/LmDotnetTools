using System.Text.Json.Serialization;
using AchieveAi.LmDotnetTools.Sandbox;

namespace LmStreaming.Sample.FileBrowser;

/// <summary>One directory entry returned to the client. <see cref="Type"/> is the lowercase gateway kind (<c>file</c>/<c>directory</c>/<c>symlink</c>).</summary>
public sealed record FileEntryDto(string Name, string Type, long? Size, bool NameLossy)
{
    public static FileEntryDto From(SandboxDirectoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return new(entry.Name, TypeString(entry.Type), entry.Size, entry.NameLossy);
    }

    public static string TypeString(SandboxEntryType type) =>
        type switch
        {
            SandboxEntryType.File => "file",
            SandboxEntryType.Directory => "directory",
            SandboxEntryType.Symlink => "symlink",
            _ => "file",
        };
}

/// <summary>A directory listing: the resolved <see cref="Path"/>, up to <c>MaxListingRows</c> entries, and how many more were omitted.</summary>
public sealed record DirectoryListingDto(
    string WorkspaceId,
    string Path,
    IReadOnlyList<FileEntryDto> Entries,
    int MoreCount
);

/// <summary>The structured "no sandbox session yet" state a listing returns with HTTP 200 (actions return 409 instead).</summary>
public sealed record NoSessionStateDto(string State, string? WorkspaceId)
{
    public const string StateValue = "no_session_yet";

    public static NoSessionStateDto For(string? workspaceId) => new(StateValue, workspaceId);
}

/// <summary>One worksheet of a <see cref="TablePreviewDto"/>: its <see cref="Name"/> and its already-capped
/// <see cref="Rows"/> of cell strings. <see cref="Truncated"/> is true when rows or columns were dropped,
/// or when a cell was shortened to the per-cell character cap.</summary>
public sealed record SheetPreviewDto(string Name, IReadOnlyList<IReadOnlyList<string>> Rows, bool Truncated);

/// <summary>A tabular preview: one entry per worksheet, in workbook order. A delimited text file has exactly one.
/// <see cref="Truncated"/> is true when whole sheets past the sheet cap were dropped, or when the
/// whole-preview character budget stopped the read partway — a per-sheet
/// <see cref="SheetPreviewDto.Truncated"/> can say neither, and dropping them silently would be a lie.</summary>
public sealed record TablePreviewDto(IReadOnlyList<SheetPreviewDto> Sheets, bool Truncated = false);

/// <summary>A file-preview result. When <see cref="Previewable"/> is false, <see cref="Reason"/> explains why (binary/too_large/not_utf8/not_a_file/excluded/corrupt_spreadsheet/spreadsheet_too_large).</summary>
/// <remarks>
/// <see cref="Table"/> is populated ONLY for a spreadsheet preview, where <see cref="Text"/> and
/// <see cref="LineCount"/> stay null. It is omitted from the JSON when null (rather than written as
/// <c>"table": null</c>) so a text preview's response body is unchanged by this member's existence.
/// </remarks>
public sealed record PreviewResultDto(
    bool Previewable,
    string? Reason,
    string? Text,
    int? LineCount,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] TablePreviewDto? Table = null
);

/// <summary>A chat file link resolved to a real workspace entry: its server <see cref="Path"/>, lowercase gateway <see cref="Type"/>, and listed <see cref="Size"/>.</summary>
public sealed record ResolvedLinkDto(string Path, string Type, long? Size);

/// <summary>The per-file upload outcome (one file per request). <see cref="Name"/> echoes the relative path when the upload carried one, otherwise the base file name.</summary>
public sealed record UploadResultDto(string Name, long Size);

/// <summary>The JSON body of a create-directory request: the new folder's base name (a single path component).</summary>
public sealed record CreateDirectoryRequest(string Name);

/// <summary>The create-directory outcome: the resolved server path of the created (or already-existing) directory.</summary>
public sealed record CreateDirectoryResultDto(string Path);

/// <summary>
/// What the client asks <c>POST .../files/grant</c> for: which TRANSPORT the minted token should travel in.
/// </summary>
/// <param name="Transport">
/// <c>"cookie"</c> for the <c>HttpOnly</c> cookie, anything else (including absent) for the URL-path token.
/// A free string rather than an enum so that an older client, a probe, or a body this build does not
/// recognise degrades to the transport that always works instead of to a 400.
/// </param>
/// <remarks>
/// The whole body is optional: <c>POST</c> with no body at all is the pre-cookie client, and it must keep
/// getting exactly what it used to get.
/// </remarks>
public sealed record WorkspaceGrantRequest(string? Transport);

/// <summary>
/// A minted workspace READ grant (Bug#15): the segment the client puts in a raw workspace URL's PATH, and
/// when the grant stops validating so the client can refresh ahead of it.
/// </summary>
/// <param name="Grant">
/// Under the URL transport, the opaque token itself. Under the cookie transport,
/// <see cref="WorkspaceGrantService.CookieTransportMarker"/> — a constant, public segment, while the token
/// is in the <c>Set-Cookie</c> this response also carries.
/// </param>
/// <param name="ExpiresAt">When the grant stops validating, whichever transport carries it.</param>
/// <param name="Transport">
/// Which transport the server actually used. Echoed rather than assumed: a client that asked for the cookie
/// can tell from the body alone whether it got one.
/// </param>
public sealed record WorkspaceGrantDto(
    string Grant,
    DateTimeOffset ExpiresAt,
    string Transport = WorkspaceGrantTransports.Url
);

/// <summary>The two values <see cref="WorkspaceGrantDto.Transport"/> and <see cref="WorkspaceGrantRequest.Transport"/> take.</summary>
public static class WorkspaceGrantTransports
{
    /// <summary>
    /// The token is in an <c>HttpOnly</c> cookie and the URL carries only the marker. Deliberately the SAME
    /// string as the route segment: one value to read in a log line, and nothing to keep in step.
    /// </summary>
    public const string Cookie = WorkspaceGrantService.CookieTransportMarker;

    /// <summary>The token is the URL path segment. The fallback where a <c>Secure</c> cookie is not accepted.</summary>
    public const string Url = "url";
}
