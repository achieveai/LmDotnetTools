namespace AchieveAi.LmDotnetTools.LmTestUtils.Persistence;

/// <summary>
/// Teardown for a <c>AchieveAi.LmDotnetTools.LmMultiTurn.Persistence.FileConversationStore</c>
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
/// atomic-rename mechanism is used inside <c>LmMultiTurn.Tests</c>' <c>InputAcceptanceStoreTests</c>'
/// vanishing-directory test to arrange that fail-fast deliberately.
/// </para>
/// <para>
/// <b>The root is NEVER deleted in place.</b> An earlier revision fell back to a recursive delete of the
/// still-attached root whenever the rename failed, which voided the invariant in exactly the case it
/// exists for: the roots are GUID-unique, so the destination can never already exist, which leaves a
/// HELD HANDLE as the reachable rename failure — and the handle that matters is a writer still working
/// in the tree, the very scenario the detach protects against. <see cref="Purge"/> now retries the
/// detach and then THROWS, naming the root, so the suite holding it is findable instead of silently
/// getting the unsafe delete. Moving the root somewhere else is not an escape: on Windows a
/// <see cref="Directory.Move(string, string)"/> of an ancestor fails while a descendant handle is open,
/// unless that handle was opened with <see cref="FileShare.Delete"/> — which the store does not do, and
/// which is why the regression test opens with <see cref="FileShare.None"/>.
/// </para>
/// <para>
/// This whole design is Windows-shaped, which is where the .NET suite runs. On POSIX a rename succeeds
/// with descendant handles open, so the refusal below is simply unreachable there rather than wrong.
/// The <c>root</c> is expected to be a normalized path with no trailing separator (every caller
/// passes a <see cref="Path.Combine(string, string)"/> result); a trailing separator would make the
/// detached name a CHILD rather than a sibling.
/// </para>
/// <para>
/// <b>Why public, not <c>internal</c> + <c>InternalsVisibleTo</c>.</b> <c>LmTestUtils</c> is a shipped
/// test-utility package whose entire existing surface is public. An <c>internal</c> helper here would
/// need an <c>InternalsVisibleTo</c> grant edited every time another test assembly picks up a
/// <c>FileConversationStore</c> root — exactly the friction that kept this helper pinned to a single
/// test assembly before this move. A public static helper with no mutable state carries none of the
/// encapsulation risk a grant list exists to contain, so there is nothing to buy by keeping it internal.
/// </para>
/// <para>
/// <b>Coverage — what this helper reaches.</b> Every <c>FileConversationStore</c>-shaped test root
/// across the solution is purged through here. In <c>LmMultiTurn.Tests</c>:
/// <c>FileConversationStoreTests</c> (the fixture root and <c>Constructor_CreatesBaseDirectory</c>'s
/// own), <c>FileRunLedgerStoreTests</c>, <c>ConversationOwnershipTests</c>,
/// <c>FileRunLifecycleStoreTests</c>, <c>InputAcceptanceStoreTests</c>, and
/// <c>ConversationUsageProjectionTests</c>. The one remaining recursive delete of a STORE root in that
/// assembly — <c>SqliteConnectionFactoryTests</c> — backs SQLite, which has no exclusive-create loop, so
/// the window class cannot apply to it. In <c>LmStreaming.Sample.Tests</c>:
/// <c>NotifyWaitDurableRestoreTests</c>, <c>SubAgentScanCoverageCacheCompositionTests</c>,
/// <c>WorkspaceThreadRegistrationCompositionTests</c>, and
/// <c>WorkspaceTranscriptMirrorAttachCompositionTests</c>. In
/// <c>LmStreaming.Sample.Browser.E2E.Tests</c>: <c>BrowserWebAppFactory</c>'s temp-directory teardown —
/// its default delete delegate now calls <see cref="Purge"/>, while its own bounded retry and
/// swallow-and-log-on-final-attempt wrapper (pinned by <c>BrowserWebAppFactoryTempCleanupTests</c>) is
/// unchanged, because that wrapper runs in <c>Dispose</c> after a test's assertions already passed and
/// must not turn a green run red. Some of the swept roots are LIVE writers rather than latent ones, and
/// every live one must have its host DISPOSED before <see cref="Purge"/> runs — not merely at the
/// enclosing method's closing brace, which is after it. The live ones INCLUDE the pool-backed hosts in
/// <c>NotifyWaitDurableRestoreTests</c>, <c>WorkspaceThreadRegistrationCompositionTests</c>, and
/// <c>WorkspaceTranscriptMirrorAttachCompositionTests</c>: each calls <c>pool.GetOrCreateAgent</c>, which
/// is the call that both starts the agent's run task and fires the binding persist, so none of them is
/// latent. Deliberately not a closed count — this list was never derived by enumerating every
/// <c>GetOrCreateAgent</c> caller under a purged root, and the criterion, not the list, is what a new
/// test should be checked against. (<c>SubAgentScanCoverageCacheCompositionTests</c> resolves no pool and
/// is correctly absent.) Since #506 <c>MultiTurnAgentPool.DisposeAsync</c> DOES await both writers: the
/// run task it started itself, and the background work it used to discard with <c>_ =</c> — previously
/// the run task sat in a field no disposal path awaited, and the binding persist had no holder at all, so
/// a store write could outlive the pool. That is what makes disposing the host the teardown's
/// synchronisation point rather than a hopeful one — and equally what makes disposal-after-purge a
/// guaranteed conflict rather than a racy one. Also live is <c>BrowserWebAppFactory</c> (whose own
/// remarks document the race and answer it with a retrying delete, an answer explicitly scoped to "the
/// writer is finishing, not restarting" — which is why its wrapper still retries and swallows around
/// <see cref="Purge"/> rather than letting <see cref="Purge"/>'s own throw reach <c>Dispose</c>
/// directly). Two other recursive deletes are NOT store roots and are unaffected: the detached copy
/// below, and <c>DetachedStoreTeardownTests</c>' own teardown, which must not depend on the helper it
/// tests.
/// </para>
/// </summary>
public static class DetachedStoreTeardown
{
    /// <summary>How many times the detach is attempted before the lock is reported as a failure.</summary>
    private const int DetachAttempts = 10;

    /// <summary>
    /// Backoff step, multiplied by the attempt number: ~1.1 s in total across
    /// <see cref="DetachAttempts"/> (the sleep fires on attempts 1-9; the tenth throws first).
    /// <para>
    /// The retry exists because not every held handle belongs to a store writer: a virus scanner or a
    /// search indexer touching a freshly written temp file holds one briefly, and so does a pooled SQLite
    /// connection between <c>ClearAllPools</c> and the handle actually closing. Those are the transients
    /// worth absorbing, and waiting for the condition is what a fixed sleep cannot do — see
    /// <c>ConversationOwnershipTests.DisposeAsync</c>, which still guesses with a flat 50 ms delay before
    /// purging. A genuinely leaked store handle outlives the budget and is reported.
    /// </para>
    /// </summary>
    private const int DetachRetryDelayMs = 25;

    /// <summary>
    /// Renames <paramref name="root"/> to a sibling <c>-detached-{guid}</c> name (fail-fast for any
    /// in-flight creator), then recursively deletes the detached copy.
    /// <para>
    /// Returns quietly if the root is already gone. Throws <see cref="IOException"/> if the root cannot be
    /// detached after <see cref="DetachAttempts"/> tries: the cause worth finding is a writer still
    /// holding the tree, and deleting it in place would reopen the #477 window. The thrown message names
    /// the root and defers to the inner exception rather than asserting which handle was to blame.
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
                // its place on diagnostics — without it a vanished root would fall into the retry arm
                // below, burn the whole budget, and then fail the run reporting a root it could not
                // detach, when in fact there was nothing left to detach.
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt >= DetachAttempts)
                {
                    // The message names candidate causes rather than asserting one: a leaked store
                    // handle is the cause worth finding, but a pooled SQLite connection, a scanner, or
                    // an ACL/path-length problem land in this same arm and must not be misdiagnosed.
                    throw new IOException(
                        $"Teardown could not detach the store root '{root}' after {DetachAttempts} attempts "
                            + $"(~{DetachRetryDelayMs * DetachAttempts * (DetachAttempts - 1) / 2} ms). "
                            + "Deleting it in place would reopen the #477 legal-success-window, so teardown "
                            + "refuses instead. Most likely a test left a store operation in flight or a "
                            + "handle undisposed under this root; see the inner exception, which also covers "
                            + "a pooled connection not yet released and a plain access failure.",
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
