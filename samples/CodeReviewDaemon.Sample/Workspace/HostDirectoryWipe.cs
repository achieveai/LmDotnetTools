namespace CodeReviewDaemon.Sample.Workspace;

/// <summary>
/// The daemon's one guarded recursive delete of a host directory.
/// </summary>
internal static class HostDirectoryWipe
{
    /// <summary>
    /// Recursively deletes a host directory, clearing the read-only attribute first: a git store is full of
    /// read-only pack/object files that <see cref="Directory.Delete(string, bool)"/> otherwise refuses.
    /// <para>
    /// The attribute pass walks by hand rather than with <c>SearchOption.AllDirectories</c>, which follows a
    /// symlinked or junctioned directory without saying so. Enumerating through a link would clear the read-only
    /// bit on whatever it aims at, on the daemon host, under the daemon's own account — and read-only is the last
    /// write brake standing between that account and a file it can already open. The strip is the damage on its
    /// own: it lands even when the delete that follows then fails.
    /// </para>
    /// <para>
    /// Each link is unlinked as the walk meets it, which is also what keeps the wipe survivable:
    /// <see cref="Directory.Delete(string, bool)"/>'s own recursion throws
    /// <see cref="UnauthorizedAccessException"/> on a Windows junction, so leaving one for it would turn a
    /// condemned store into an unrecoverable slot. A non-recursive delete removes the link alone and leaves
    /// whatever it points at untouched.
    /// </para>
    /// <para>
    /// <paramref name="root"/> itself is refused rather than wiped, because it is the one entry the per-child
    /// check can never see: everything below is reached THROUGH it, so a redirected root aimed the entire walk
    /// outside the workspace at once. The refusal fails closed both ways, matching <see cref="SlotHygiene"/> —
    /// the link is not followed and it is not removed either, since unlinking is itself a write chosen by
    /// whoever planted it, and re-creating the directory afterwards (which <c>ReviewSlotPreparer</c>'s scratch
    /// clear does) only hands the next one a fresh target. It is raised as
    /// <see cref="SlotAddressUnusableException"/>, the type that means exactly this, and NOT as one of the two
    /// re-clone types: those drive the recovery ladder into a wipe, and this wipe is what already refused. An
    /// earlier note here defended throwing an untyped <see cref="InvalidOperationException"/> on that same "not
    /// one of the re-clone types" reasoning, which mistook the absence of a wrong type for the absence of a right
    /// one and left a security refusal indistinguishable from a failed <c>git</c> invocation at every handler
    /// above.
    /// </para>
    /// <para>
    /// Being a distinguishable type is what lets a caller ACT on the refusal, and the two callers act
    /// differently, which is why the decision is theirs and not this method's. The pooled preparer retires the
    /// slot: the refusal is a property of the address, not of the attempt, so a slot returned to the pool's free
    /// stack comes straight back out as the next index and refuses again, forever, on a store no run can ever
    /// prepare — and the entry that stops this walk is a DESCENDANT of the three paths
    /// <c>ReviewSlotPool.LeaseAsync</c> guards, so a later lease cannot see it and the pool only learns the
    /// address is spent if this refusal is legible on the way out. <c>ReviewSessionProvisioner</c>'s per-run
    /// teardown has nothing to retire, because the next run gets a fresh <c>review-run-{id}</c> name rather than
    /// a recycled one; it logs the refusal as an error and leaves the directory standing. Both are fail-closed.
    /// What neither may do is treat the refusal as an ordinary I/O error, which is the one shape a security
    /// refusal must not arrive in.
    /// </para>
    /// <para>
    /// The containment check sits ABOVE the existence check below it, and the order is load-bearing. Every
    /// redirected DIRECTORY still reports <see cref="Directory.Exists(string)"/> as true — a junction and a
    /// directory symlink both do, whether or not their target is still there — so checking containment second
    /// would keep catching all of those and read as a free simplification. The root it would miss is one that
    /// is not a directory at all: a FILE symlink standing where the store or the scratch path should be reads
    /// as ABSENT, so a guard below the existence check never runs on it, and the wipe returns as though there
    /// were nothing there — leaving the caller to clone or provision onto a name that resolves outside the
    /// workspace.
    /// </para>
    /// <para>
    /// Two costs in this shape are deliberate, and both read as waste. <see cref="ChildrenOf"/> materializes
    /// each directory's entries into an array rather than streaming them, because the walk MUTATES the
    /// directory it is enumerating: <see cref="Unlink"/> removes a redirected entry as the walk meets it, and
    /// a lazy enumeration's results are unspecified once the directory changes underneath it. And the closing
    /// <see cref="Directory.Delete(string, bool)"/> re-walks a tree this method has already walked. Replacing
    /// it with a hand-rolled post-order delete would save that pass and buy back the decision the guarded walk
    /// exists to remove — what is safe to recurse into, taken per entry, on the untrusted side of the store.
    /// One redundant traversal of a tree that is being deleted anyway is the cheaper half of that trade.
    /// </para>
    /// <para>
    /// Two residuals are accepted rather than chased. Ancestors ABOVE <paramref name="root"/> are not checked:
    /// they are the operator's own configured workspace path, and refusing there would refuse every deployment
    /// that deliberately puts the pool behind a junction. And an entry can be swapped between the check and the
    /// delete — an unprivileged local race against the daemon's own workspace root, with no cheap
    /// file-identity primitive available here to close it.
    /// </para>
    /// </summary>
    public static void Delete(string root)
    {
        if (HostPathGuard.Check(root) is { } rootRefusal)
        {
            throw Refuse(root, rootRefusal);
        }

        if (!Directory.Exists(root))
        {
            return;
        }

        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var entry in ChildrenOf(directory))
            {
                if (HostPathGuard.Check(entry) is { } refusal)
                {
                    // A redirected entry is unlinked — that removes the NAME inside the store and leaves the
                    // target alone, which is the whole point. An unreadable one cannot be unlinked on the same
                    // reasoning, because the reasoning depends on knowing what it is; the wipe stops instead.
                    if (refusal.Verdict == HostPathVerdict.Redirected)
                    {
                        Unlink(root, entry);
                        continue;
                    }

                    throw Refuse(root, refusal);
                }

                if (Directory.Exists(entry))
                {
                    pending.Push(entry);
                    continue;
                }

                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReadOnly) != 0)
                {
                    File.SetAttributes(entry, attributes & ~FileAttributes.ReadOnly);
                }
            }
        }

        Directory.Delete(root, recursive: true);
    }

    /// <summary>The one refusal message the wipe raises, whether the entry that stopped it is the root it was
    /// handed or one it found on the way down. <paramref name="cause"/> is carried when the refusal came from a
    /// failed call rather than from a verdict, so the log still shows whether the listing was denied or the
    /// device failed — the two produce the same refusal but not the same operator response.</summary>
    private static SlotAddressUnusableException Refuse(
        string root, HostPathRefusal refusal, Exception? cause = null) =>
        new(
            $"Refusing to wipe host directory '{root}': '{refusal.Path}' — {refusal.Reason}. Not following it, "
                + "and not removing it either.",
            cause);

    /// <summary>
    /// One directory's entries, or a refusal when it could not be enumerated.
    /// <para>
    /// A directory this walk cannot list is the same condition <see cref="HostPathGuard"/> reports as
    /// <see cref="HostPathVerdict.Unreadable"/> one level down, met at a different call, and it gets the same
    /// answer: the wipe is about to delete everything under here, and it cannot decide what to do with entries
    /// it cannot name. Letting the raw <see cref="UnauthorizedAccessException"/> out instead was fail-closed —
    /// nothing was deleted and nothing was cloned — but it arrived at the caller as an ordinary I/O error,
    /// which is the one thing this walk's refusals must not look like, since the pooled caller retires the slot
    /// on a refusal and merely retries on an error.
    /// </para>
    /// </summary>
    private static string[] ChildrenOf(string directory)
    {
        try
        {
            return Directory.GetFileSystemEntries(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw Refuse(directory, new HostPathRefusal(directory, HostPathVerdict.Unreadable), ex);
        }
    }

    /// <summary>
    /// Removes a symlink or junction itself, never its target — or refuses the wipe when it is not permitted to.
    /// <para>
    /// <see cref="Directory.Exists(string)"/> is the right question here, and it is worth writing down what it
    /// actually answers, because the obvious reading is wrong. It reports on the LINK, not on what the link aims
    /// at, so a DANGLING directory link — one whose target has since been deleted — still answers true and still
    /// takes the <see cref="Directory.Delete(string)"/> branch. Measured on Windows 11, junction and directory
    /// symlink alike:
    /// </para>
    /// <code>
    ///                         Directory.Exists  File.Exists  Attributes               File.Delete  Directory.Delete
    ///   live junction         True              False        Directory, ReparsePoint  throws       unlinks
    ///   DANGLING junction     True              False        Directory, ReparsePoint  throws       unlinks
    ///   DANGLING dir symlink  True              False        Directory, ReparsePoint  throws       unlinks
    /// </code>
    /// <para>
    /// The <see cref="File.Delete(string)"/> branch is therefore never reached for a directory link of any kind.
    /// It is there for a FILE symlink, the one redirected shape that reads as absent — the same shape
    /// <see cref="Delete"/>'s check order exists to catch. A review proposed replacing the test with the
    /// reparse-point type read from metadata, on the theory that a dangling link reports
    /// <see cref="Directory.Exists(string)"/> as false, falls into <see cref="File.Delete(string)"/>, and throws.
    /// The table is that theory measured, and it does not happen. The replacement would also be strictly worse:
    /// <see cref="FileSystemInfo.Attributes"/> returns <c>(FileAttributes)(-1)</c> for an entry that is absent or
    /// cannot be read — every bit set, <see cref="FileAttributes.ReparsePoint"/> among them — so branching on it
    /// treats "I could not look" as "it is a link", which is the exact conflation
    /// <see cref="HostPathGuard"/> already spends a special case to keep out.
    /// </para>
    /// <para>
    /// What does fail here is PERMISSION, and it gets the same answer <see cref="ChildrenOf"/> gives one call
    /// up. An entry the daemon is not allowed to unlink leaves the walk with no move it may make: it will not
    /// follow the link, it cannot remove it, and it must not delete the tree around it and call the store
    /// cleared. Letting the raw exception out was fail-closed but arrived untyped, and the pooled caller routes
    /// on TYPE — only <see cref="SlotAddressUnusableException"/> retires the slot, everything else returns it to
    /// a free list that is a STACK. A denied unlink is not transient the way a busy file is: it is a property of
    /// the entry, so the next run takes the same index and is denied again, forever. Returning the slot is what
    /// makes that loop, and the refusal type is what breaks it.
    /// </para>
    /// <para>
    /// The catch is WIDER than the case that motivates it, deliberately. A denial is a property of the entry, but
    /// an <see cref="IOException"/> here can equally be a sharing violation — an antivirus handle, an indexer
    /// mid-scan — and nothing available at this point tells the two apart. They get the same treatment on purpose,
    /// which means a transient lock now retires a slot that would have recovered on its next lease. That cost is
    /// accepted: it is pool capacity down by one until restart, weighed against re-leasing an address that is
    /// denied again on every single lease, forever. It stays cheap because of WHERE this runs — only on an entry
    /// <see cref="HostPathGuard"/> has already ruled <see cref="HostPathVerdict.Redirected"/>, and a reparse point
    /// inside a git store is anomalous before this method is reached at all. A transient lock on one is rare on
    /// top of rare, while the denial it is being confused with is the shape someone plants on purpose.
    /// </para>
    /// <para>
    /// The verdict is reused rather than re-derived. <see cref="Delete"/> established
    /// <see cref="HostPathVerdict.Redirected"/> for this entry one line above; asking again here would re-read a
    /// path that has already proven hostile, and open a second window between the two answers.
    /// </para>
    /// </summary>
    private static void Unlink(string root, string entry)
    {
        try
        {
            if (Directory.Exists(entry))
            {
                Directory.Delete(entry);
            }
            else
            {
                File.Delete(entry);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // BOTH types, and the pair is measured rather than defensive: a deny ACE on a junction surfaces as
            // IOException ("Access to the path ... is denied"), not the UnauthorizedAccessException the
            // File.Delete shape leads you to expect. Narrowing this to UnauthorizedAccessException would let the
            // common case straight back out untyped.
            throw Refuse(root, new HostPathRefusal(entry, HostPathVerdict.Redirected), ex);
        }
    }
}
