namespace CodeReviewDaemon.Sample.Workspace;

/// <summary>What a host walk learned when it asked whether one entry is safe to touch.</summary>
internal enum HostPathVerdict
{
    /// <summary>
    /// The entry's NAME and its CONTENT are the same place — or there is nothing at that name at all. The only
    /// verdict that lets a walk write to the entry or descend into it.
    /// </summary>
    Contained,

    /// <summary>A symlink or a Windows junction: the name is inside the store, the content is not.</summary>
    Redirected,

    /// <summary>
    /// The entry could not be read well enough to tell. Refused for the same reason <see cref="Redirected"/>
    /// is: the walk's whole job is to establish containment, and "I could not look" is not an establishment.
    /// </summary>
    Unreadable,
}

/// <summary>An entry a host walk refused to cross, and the verdict that stopped it.</summary>
internal readonly record struct HostPathRefusal(string Path, HostPathVerdict Verdict)
{
    /// <summary>
    /// The clause each caller drops into its own message. It is derived from the verdict rather than written at
    /// the throw site because the two verdicts are refused identically and would otherwise both be reported as
    /// "it is a symlink or junction" — a reason that is false for half of them, and a false reason in a refusal
    /// message sends the next reader looking for a link that was never there.
    /// </summary>
    public string Reason =>
        Verdict == HostPathVerdict.Redirected
            ? "it is a symlink or junction, so a walk through it would reach outside the store"
            : "its attributes cannot be read, so whether a walk through it stays inside the store is unknowable";
}

/// <summary>
/// The containment check the daemon's host-side tree walks run before they touch an entry.
/// </summary>
internal static class HostPathGuard
{
    /// <summary>
    /// The attributes of a path with nothing at it. <see cref="FileSystemInfo.Attributes"/> reports this rather
    /// than throwing when the entry is genuinely absent, and it must be recognised before the reparse-point test
    /// because every bit is set in it — including <see cref="FileAttributes.ReparsePoint"/>.
    /// </summary>
    private const FileAttributes Absent = (FileAttributes)(-1);

    /// <summary>
    /// The refusal that stops a walk at <paramref name="path"/>, or <c>null</c> when the entry may be touched.
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
    /// That rule is about the SWEEP, and the re-clone WIPE is not an exception to it even though the wipe does
    /// remove a redirected name. The difference is what happens next. The sweep leaves the store in use, so
    /// unlinking and carrying on repairs one link and hands the planter a fresh directory to aim the next one
    /// at, on a store the daemon is about to keep working in — which is why it stops and condemns instead. The
    /// wipe is destroying that store outright, so removing the name is part of the deletion rather than a
    /// repair: there is no continued use for the removal to make safe, and nothing is re-created for the next
    /// link to land in. Neither one ever follows the link — the wipe unlinks the name and leaves the target
    /// untouched, and an entry it cannot read it still refuses, because the reasoning that makes the unlink safe
    /// depends on knowing what the entry is.
    /// </para>
    /// <para>
    /// An entry whose attributes cannot be read is refused too, and reaching that case takes some care. The
    /// obvious spelling — <c>entry.Exists &amp;&amp; entry.Attributes.HasFlag(ReparsePoint)</c> — never reaches
    /// it: <see cref="FileSystemInfo.Exists"/> swallows the error and reports FALSE for an entry it could not
    /// read, exactly as it does for one that is not there, so the <c>&amp;&amp;</c> short-circuits and the guard
    /// answers "nothing to worry about" for a path it never managed to look at. The two cases are only
    /// distinguishable at <see cref="FileSystemInfo.Attributes"/>, which throws for the first and returns
    /// <see cref="Absent"/> for the second — which is why the attributes are read first and the existence
    /// question answered from them, rather than the other way round.
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
    public static HostPathRefusal? Check(string path)
    {
        FileAttributes attributes;
        try
        {
            FileSystemInfo entry = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
            attributes = entry.Attributes;
        }
        catch (IOException)
        {
            return new HostPathRefusal(path, HostPathVerdict.Unreadable);
        }
        catch (UnauthorizedAccessException)
        {
            return new HostPathRefusal(path, HostPathVerdict.Unreadable);
        }

        if (attributes == Absent || !attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            return null;
        }

        return new HostPathRefusal(path, HostPathVerdict.Redirected);
    }
}
