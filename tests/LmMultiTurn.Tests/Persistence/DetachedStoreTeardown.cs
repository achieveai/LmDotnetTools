namespace LmMultiTurn.Tests.Persistence;

/// <summary>
/// Teardown for a <see cref="AchieveAi.LmDotnetTools.LmMultiTurn.Persistence.FileConversationStore"/>
/// backing directory that closes the #477 legal-success-window class.
/// <para>
/// The store's reserve/append path retries an exclusive <c>FileMode.CreateNew</c> in a loop. A plain
/// <see cref="Directory.Delete(string, bool)"/> with <c>recursive: true</c> frees a record's leaf
/// directory BEFORE the parent that holds it, and in that gap the record name is free under a parent
/// that still exists — so an in-flight exclusive create legally WINS, returns a successful admission,
/// and any asserted refusal/cancellation can no longer happen (measured 28/800 under load on #477,
/// 20/20 with a 30 ms widened gap; PR #483 fixed the reference suite the same way).
/// </para>
/// <para>
/// Every file-store suite that uses this helper fully awaits its store calls, so none is in flight at
/// teardown TODAY and the window is latent rather than live. The atomic rename keeps it that way: it
/// moves the whole root to a sibling <c>-detached</c> name before deleting, and a moved root's parent
/// chain is either whole or gone — never half — so a future in-flight creator fails fast with
/// <see cref="DirectoryNotFoundException"/> instead of succeeding into a half-deleted tree. Mirrors the
/// atomic-rename mechanism recorded in InputAcceptanceStoreTests.
/// </para>
/// </summary>
internal static class DetachedStoreTeardown
{
    /// <summary>
    /// Renames <paramref name="root"/> to a sibling <c>-detached</c> name (fail-fast for any in-flight
    /// creator), then recursively deletes the detached copy. Best-effort: a still-locked temp tree left
    /// behind must never fail an otherwise green run.
    /// </summary>
    public static void Purge(string root)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        // Atomically detach BEFORE any delete so an exclusive create in flight sees a vanished parent
        // chain and refuses, rather than winning into a leaf that recursive delete freed early.
        var detached = root + "-detached";
        try
        {
            Directory.Move(root, detached);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Target already exists, root vanished, or a transient lock — delete in place instead.
            detached = root;
        }

        try
        {
            Directory.Delete(detached, recursive: true);
        }
        catch (IOException)
        {
            // A still-locked temp file must not fail an otherwise passing test run.
        }
    }
}
