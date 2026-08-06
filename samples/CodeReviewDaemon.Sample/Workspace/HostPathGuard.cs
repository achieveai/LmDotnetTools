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
    /// a re-clone instead, which wipes the whole store without following the link.
    /// </para>
    /// <para>
    /// This check does not see a HARD link, and that is an accepted residual rather than a proof of safety. On
    /// NTFS a hard link is a second NAME for one file record: deleting one name does leave the other whole, but
    /// clearing the read-only bit through one clears it on the record, so a hard link planted under a store
    /// strips protection from the file outside it. The planter to picture is not a generic insider — it is the
    /// review agent, which writes into this store and takes its instructions from the reviewed repo's own
    /// CLAUDE.md and AGENTS.md as read at the PR head, so what it does is attacker-influenced by construction.
    /// Containing that is why these walks check anything at all. What the strip costs is a write brake, not a
    /// read boundary: read-only is an attribute, not an ACL, so clearing it grants nobody access they lacked and
    /// instead removes the last thing standing between the daemon account and a file it can already open for
    /// writing. Detecting one costs a link-count query per entry on every walk, so it is written down here
    /// rather than paid for.
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
