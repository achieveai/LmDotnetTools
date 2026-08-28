using AchieveAi.LmDotnetTools.LmTestUtils.Persistence;

namespace AchieveAi.LmDotnetTools.LmTestUtils.Tests.Persistence;

/// <summary>
/// <see cref="DetachedStoreTeardown"/> exists to keep a teardown from ever recursive-deleting a store root
/// that a writer might still be creating into. These pin that it actually does that — including in the one
/// case where it is hardest, and where the original implementation did the opposite.
/// </summary>
public sealed class DetachedStoreTeardownTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"DetachedStoreTeardownTests_{Guid.NewGuid():N}");

    /// <summary>
    /// The detach is what makes teardown safe, and a HELD HANDLE is the only reachable way it can fail: the
    /// roots are GUID-unique so the destination can never already exist, which leaves a writer keeping the
    /// tree busy — precisely the scenario the helper exists to protect against. Falling back to a recursive
    /// delete of the still-ATTACHED root there would void the invariant exactly when it matters: a recursive
    /// delete frees a record's leaf before its parent, and in that gap an in-flight exclusive
    /// <c>FileMode.CreateNew</c> legally wins into a half-deleted tree (the #477 class).
    /// <para>
    /// So the contract is: refuse, loudly, naming the root. The sibling record below is the observable — it
    /// is untouched by the lock and would be the FIRST casualty of an in-place recursive delete, so its
    /// survival is what separates "refused" from "deleted anyway, quietly".
    /// </para>
    /// </summary>
    [WindowsOnlyFact("only Windows refuses to rename a directory whose descendant file is open; POSIX rename succeeds")]
    public void Purge_WhenTheRootCannotBeDetached_RefusesInsteadOfDeletingTheAttachedRootInPlace()
    {
        var survivor = Path.Combine(_root, "record-survivor");
        _ = Directory.CreateDirectory(survivor);
        var survivorFile = Path.Combine(survivor, "record.json");
        File.WriteAllText(survivorFile, "{}");

        var busy = Path.Combine(_root, "record-busy");
        _ = Directory.CreateDirectory(busy);
        var busyFile = Path.Combine(busy, "record.json");
        File.WriteAllText(busyFile, "{}");

        // FileShare.None keeps the whole tree un-renameable on Windows for as long as this handle lives.
        using var handle = new FileStream(busyFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var thrown = Record.Exception(() => DetachedStoreTeardown.Purge(_root));

        // Asserted BEFORE the exception: this is the defect itself. A fallback that recursive-deletes the
        // attached root frees the unlocked sibling first and only then trips over the lock, so a missing
        // survivor says "deleted around the writer" even when nothing was thrown.
        Assert.True(
            File.Exists(survivorFile),
            "an in-place recursive delete frees sibling records first, so this file is the canary for one"
        );
        Assert.True(
            Directory.Exists(_root),
            "the attached root must be left exactly as found when it could not be detached"
        );

        // A teardown that cannot detach the root must surface the writer holding it, not delete around it.
        var refusal = Assert.IsType<IOException>(thrown);
        // The offending suite is only findable if the failure names the root it could not detach.
        Assert.Contains(_root, refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The ordinary path: nothing holds the tree, so the root is detached and the detached copy removed.
    /// <para>
    /// Scope, stated honestly: both assertions here would also be satisfied by a plain recursive delete that
    /// never detached at all, so this case does NOT pin the rename — the held-handle case above is the only
    /// one that discriminates. What this pins is that the detached copy is actually deleted rather than
    /// merely renamed out of the way, which would otherwise leave a growing pile of <c>-detached</c>
    /// siblings in the temp directory and still look like it worked.
    /// </para>
    /// </summary>
    [Fact]
    public void Purge_OnAQuiescentRoot_RemovesItAndLeavesNoDetachedResidue()
    {
        _ = Directory.CreateDirectory(Path.Combine(_root, "record"));
        File.WriteAllText(Path.Combine(_root, "record", "record.json"), "{}");

        DetachedStoreTeardown.Purge(_root);

        Assert.False(Directory.Exists(_root));
        // The detached copy is deleted, not merely renamed out of the way.
        Assert.Empty(
            Directory.EnumerateDirectories(Path.GetDirectoryName(_root)!, Path.GetFileName(_root) + "-detached*")
        );
    }

    /// <summary>A root that was never created — or that a previous purge already took — is not a failure.</summary>
    [Fact]
    public void Purge_OnAMissingRoot_IsANoOp()
    {
        Assert.False(Directory.Exists(_root)); // precondition: nothing created this root

        var purgeNeverCreated = Record.Exception(() => DetachedStoreTeardown.Purge(_root));
        Assert.Null(purgeNeverCreated);

        // The second half of the claim: a root a previous purge already took is equally not a failure.
        _ = Directory.CreateDirectory(_root);
        DetachedStoreTeardown.Purge(_root);
        // Teardown runs per test, and the second call must be inert.
        var purgeAgain = Record.Exception(() => DetachedStoreTeardown.Purge(_root));
        Assert.Null(purgeAgain);
    }

    public void Dispose()
    {
        // Deliberately NOT DetachedStoreTeardown.Purge: this suite's whole job is to decide whether that
        // helper behaves, so its teardown must not depend on the thing under test. A plain recursive
        // delete is safe HERE for the reason the helper cannot assume anywhere else — the only handles
        // ever opened under these roots are this suite's own, and they are disposed by now.
        string[] leftovers;
        try
        {
            // Materialized before deleting: the loop mutates the directory being walked, and a lazy
            // enumerator over a changing directory may silently skip entries. The enumeration can also
            // throw on its own if another process removes a temp entry mid-walk, which must not fail
            // teardown either.
            leftovers = Directory.GetDirectories(Path.GetDirectoryName(_root)!, Path.GetFileName(_root) + "*");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var directory in leftovers)
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A temp directory the OS has not finished releasing must not fail a green run.
            }
        }
    }
}
