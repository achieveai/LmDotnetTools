namespace AchieveAi.LmDotnetTools.Sandbox;

/// <summary>
/// The kind of a workspace directory entry as reported by the gateway's direct directories API
/// (ADR 0031 / issue #119). The gateway reports symlinks as a distinct kind and never follows them.
/// </summary>
public enum SandboxEntryType
{
    /// <summary>A regular file.</summary>
    File,

    /// <summary>A directory.</summary>
    Directory,

    /// <summary>A symbolic link (reported, never followed by the gateway).</summary>
    Symlink,
}

/// <summary>
/// One entry of a workspace directory listing, carrying the metadata the gateway's direct
/// directories API returns for each child: its non-recursive <see cref="Name"/>, its
/// <see cref="Type"/>, its <see cref="Size"/> (files only), and whether its name was lossy-decoded.
/// </summary>
/// <remarks>
/// <para>
/// This is the rich counterpart to <see cref="SandboxClient.ListDirectoryAsync"/>'s names-only
/// projection: <see cref="SandboxClient.ListDirectoryEntriesAsync"/> returns these so a caller can
/// distinguish files from directories/symlinks, show sizes, and detect un-addressable names without a
/// second round-trip. It is a client-facing model, not a wire DTO — it is never round-tripped to JSON.
/// </para>
/// <para>
/// <see cref="NameLossy"/> is <c>true</c> when the gateway had to substitute replacement characters to
/// render the raw on-disk name as text. A lossy name cannot round-trip back to the exact bytes, so it
/// is not safely addressable for navigate/preview/download/delete; the gateway remains authoritative
/// for this flag (a valid file whose name legitimately contains U+FFFD is reported with
/// <see cref="NameLossy"/> <c>false</c> and stays addressable).
/// </para>
/// </remarks>
/// <param name="Name">The entry's non-recursive name (not a full path), excluding <c>.</c> and <c>..</c>.</param>
/// <param name="Type">The entry kind.</param>
/// <param name="Size">The file size in bytes, or <c>null</c> for a directory/symlink or when absent.</param>
/// <param name="NameLossy">Whether the name was lossy-decoded and therefore not safely addressable.</param>
public sealed record SandboxDirectoryEntry(string Name, SandboxEntryType Type, long? Size, bool NameLossy);
