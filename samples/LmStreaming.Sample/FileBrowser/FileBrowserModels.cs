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
/// <see cref="Rows"/> of cell strings. <see cref="Truncated"/> is true when rows or columns were dropped.</summary>
public sealed record SheetPreviewDto(string Name, IReadOnlyList<IReadOnlyList<string>> Rows, bool Truncated);

/// <summary>A tabular preview: one entry per worksheet, in workbook order. A delimited text file has exactly one.
/// <see cref="Truncated"/> is true when whole sheets past the sheet cap were dropped — a per-sheet
/// <see cref="SheetPreviewDto.Truncated"/> cannot say that, and dropping them silently would be a lie.</summary>
public sealed record TablePreviewDto(IReadOnlyList<SheetPreviewDto> Sheets, bool Truncated = false);

/// <summary>A file-preview result. When <see cref="Previewable"/> is false, <see cref="Reason"/> explains why (binary/too_large/not_utf8/not_a_file/excluded/corrupt_spreadsheet).</summary>
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
/// A minted workspace READ grant (Bug#15): the opaque token the client puts in a raw workspace URL's PATH,
/// and when it stops validating so the client can refresh ahead of it.
/// </summary>
public sealed record WorkspaceGrantDto(string Grant, DateTimeOffset ExpiresAt);
