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
public sealed record DirectoryListingDto(string WorkspaceId, string Path, IReadOnlyList<FileEntryDto> Entries, int MoreCount);

/// <summary>The structured "no sandbox session yet" state a listing returns with HTTP 200 (actions return 409 instead).</summary>
public sealed record NoSessionStateDto(string State, string? WorkspaceId)
{
    public const string StateValue = "no_session_yet";

    public static NoSessionStateDto For(string? workspaceId) => new(StateValue, workspaceId);
}

/// <summary>A text-preview result. When <see cref="Previewable"/> is false, <see cref="Reason"/> explains why (binary/too_large/not_utf8/not_a_file).</summary>
public sealed record PreviewResultDto(bool Previewable, string? Reason, string? Text, int? LineCount);

/// <summary>The per-file upload outcome (one file per request). <see cref="Name"/> echoes the relative path when the upload carried one, otherwise the base file name.</summary>
public sealed record UploadResultDto(string Name, long Size);

/// <summary>The JSON body of a create-directory request: the new folder's base name (a single path component).</summary>
public sealed record CreateDirectoryRequest(string Name);

/// <summary>The create-directory outcome: the resolved server path of the created (or already-existing) directory.</summary>
public sealed record CreateDirectoryResultDto(string Path);
