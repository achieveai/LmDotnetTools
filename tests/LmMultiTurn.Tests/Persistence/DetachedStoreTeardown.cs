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
/// moves the whole root to a sibling <c>-detached-{guid}</c> name before deleting, and a moved root's
/// parent chain is either whole or gone — never half — so a future in-flight creator fails fast with
/// <see cref="DirectoryNotFoundException"/> instead of succeeding into a half-deleted tree. The same
/// atomic-rename mechanism is used inside <c>InputAcceptanceStoreTests</c>' vanishing-directory test to
/// arrange that fail-fast deliberately.
/// </para>
/// <para>
/// <b>The root is NEVER deleted in place.</b> An earlier revision fell back to a recursive delete of the
/// still-attached root whenever the rename failed, which voided the invariant in exactly the case it
/// exists for: the roots are GUID-unique, so the destination can never already exist, and a held handle
/// is therefore the ONLY reachable rename failure — i.e. a writer still working in the tree, the very
/// scenario the detach protects against. <see cref="Purge"/> now retries the detach and then THROWS,
/// naming the root, so the suite leaving a store operation in flight is findable instead of silently
/// getting the unsafe delete. Moving the root somewhere else is not an escape: on Windows any
/// <see cref="Directory.Move(string, string)"/> fails while a descendant handle is open.
/// </para>
/// <para>
/// <b>Coverage — what this helper does and does not reach.</b> Every
/// <c>FileConversationStore</c> root in THIS assembly is purged through here:
/// <c>FileConversationStoreTests</c> (the fixture root and
/// <c>Constructor_CreatesBaseDirectory</c>'s own), <c>FileRunLedgerStoreTests</c>,
/// <c>ConversationOwnershipTests</c>, <c>FileRunLifecycleStoreTests</c>,
/// <c>InputAcceptanceStoreTests</c>, and <c>ConversationUsageProjectionTests</c>. The one remaining
/// recursive delete in the assembly — <c>SqliteConnectionFactoryTests</c> — backs SQLite, which has no
/// exclusive-create loop, so the window class cannot apply to it.
/// </para>
/// <para>
/// <b>NOT swept, and why.</b> Five further <c>FileConversationStore</c> roots live in OTHER test
/// assemblies and cannot reach this helper at all: it is <c>internal</c> to <c>LmMultiTurn.Tests</c>,
/// no <c>InternalsVisibleTo</c> exists between test assemblies, and there is no shared
/// test-infrastructure project to host it. Hardening them needs that shared home first, so it is a
/// follow-up rather than something this file can fix. They are
/// <c>LmStreaming.Sample.Tests</c>' <c>NotifyWaitDurableRestoreTests</c>,
/// <c>SubAgentScanCoverageCacheCompositionTests</c>, <c>WorkspaceThreadRegistrationCompositionTests</c>
/// and <c>WorkspaceTranscriptMirrorAttachCompositionTests</c>, plus
/// <c>LmStreaming.Sample.Browser.E2E.Tests</c>' <c>BrowserWebAppFactory</c>. Two of those are LIVE
/// writers rather than latent ones, so they are the ones that matter: <c>NotifyWaitDurableRestoreTests</c>
/// (the pool's agent run task is stored but never awaited by any disposal path, and <c>StopAsync</c>
/// both no-ops during pre-loop recovery and only logs on timeout, so store I/O can outlive teardown),
/// and <c>BrowserWebAppFactory</c> (whose own remarks document the race and answer it with a retrying
/// recursive delete, an answer explicitly scoped to "the writer is finishing, not restarting").
/// </para>
/// </summary>
internal static class DetachedStoreTeardown
{
    /// <summary>How many times the detach is attempted before the lock is reported as a failure.</summary>
    private const int DetachAttempts = 10;

    /// <summary>
    /// Backoff step, multiplied by the attempt number: ~1.1 s in total across
    /// <see cref="DetachAttempts"/>. Enough to ride out a virus scanner or search indexer momentarily
    /// holding a freshly written temp file — the transient that is worth absorbing — without turning a
    /// genuinely leaked store handle into a long stall.
    /// </summary>
    private const int DetachRetryDelayMs = 25;

    /// <summary>
    /// Renames <paramref name="root"/> to a sibling <c>-detached-{guid}</c> name (fail-fast for any
    /// in-flight creator), then recursively deletes the detached copy.
    /// <para>
    /// Returns quietly if the root is already gone. Throws <see cref="IOException"/> if the root cannot be
    /// detached after <see cref="DetachAttempts"/> tries, because the only reachable cause is a writer
    /// still holding the tree and deleting it in place would reopen the #477 window.
    /// </para>
    /// <para>
    /// The delete of the DETACHED copy is best-effort, and that swallow is safe for a reason the detach
    /// does not share: the tree no longer occupies the store root, so nothing can create into it and a
    /// leftover locked temp directory is inert. It must never fail an otherwise green run.
    /// </para>
    /// </summary>
    public static void Purge(string root)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        // A fresh suffix per call removes the name-collision failure by construction, which is what lets
        // the catch below attribute any remaining failure to a held handle and act on that.
        var detached = $"{root}-detached-{Guid.NewGuid():N}";

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                // Atomically detach BEFORE any delete so an exclusive create in flight sees a vanished
                // parent chain and refuses, rather than winning into a leaf a recursive delete freed early.
                Directory.Move(root, detached);
                break;
            }
            catch (DirectoryNotFoundException)
            {
                // The root went away between the check above and here. Nothing is attached to protect.
                // Racing that window needs a seam this helper does not have, so this arm is deliberately
                // NOT covered by a test: mutating the return to a throw leaves the suite green. It earns
                // its place on diagnostics — without it a vanished root would exhaust the retries below
                // and report "something is still holding the tree", which would be simply false.
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt >= DetachAttempts)
                {
                    throw new IOException(
                        $"Teardown could not detach the store root '{root}' after {DetachAttempts} attempts: "
                            + "something is still holding the tree. Deleting it in place would reopen the "
                            + "#477 legal-success-window, so teardown refuses instead. Find the test leaving "
                            + "a store operation in flight, or a handle undisposed, under this root.",
                        ex);
                }

                Thread.Sleep(DetachRetryDelayMs * attempt);
            }
        }

        try
        {
            Directory.Delete(detached, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Safe precisely because the tree is DETACHED: the store root name is already free, so no
            // creator can reach what is left here. A still-locked temp tree must not fail a green run.
        }
    }
}
