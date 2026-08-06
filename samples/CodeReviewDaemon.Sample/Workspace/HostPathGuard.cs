namespace CodeReviewDaemon.Sample.Workspace;

/// <summary>
/// The containment check the daemon's host-side tree walks run before they touch an entry.
/// </summary>
internal static class HostPathGuard
{
    /// <summary>
    /// True when <paramref name="path"/> is a symlink or a Windows junction — an entry whose NAME is inside the
    /// pooled store but whose CONTENT is somewhere else entirely.
    /// <para>
    /// Two host walks recurse over a leased slot's store and then write: <see cref="SlotHygiene"/>'s stale-state
    /// sweep deletes every <c>*.lock</c> it reaches, and the re-clone wipe clears the read-only attribute on
    /// every file it reaches. Both used .NET's recursive enumeration, which follows a redirected DIRECTORY
    /// without saying so, so one link planted anywhere under a store aimed either walk at an arbitrary path on
    /// the daemon host under the daemon's own account. Checking only the leaf does not help: the leaf is reached
    /// THROUGH its ancestors, and it is an ancestor that does the redirecting — so callers must check every
    /// entry before descending into it.
    /// </para>
    /// <para>
    /// The response is to refuse, never to repair. Removing the link is a write chosen by whoever planted it,
    /// and re-creating the directory afterwards only hands the next one a fresh target; the slot is condemned to
    /// a re-clone instead, which wipes the whole store without following the link. A hard link needs no separate
    /// check here — both walks only ever delete a file or clear its read-only bit, and unlinking one name leaves
    /// the other name and the content untouched. That reasoning does NOT carry to a walk that writes THROUGH a
    /// path, which is why it is written down rather than assumed.
    /// </para>
    /// </summary>
    public static bool IsRedirected(string path)
    {
        try
        {
            FileSystemInfo entry = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
            return entry.Exists && entry.Attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch (IOException)
        {
            return false; // Unreadable is not redirected; the caller's own delete will fail the same way.
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
