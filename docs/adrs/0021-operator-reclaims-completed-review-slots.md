# ADR 0021: The operator reclaims completed review slots after a fresh activity check

* Status: Accepted
* Date: 2026-09-23
* Related issues, PRs, or commits: NOVA six-review manual validation; `docs/plans/2026-07-12-contextready-slot-durability-design.md`

## Context

A completed NOVA review can leave its slot at the reviewed checkout. The next manual review needs a clean slot, but the daemon must not reset completed slots automatically. During the six-review rerun, Nova-0 remained occupied by a completed review even though its assignment and reviewer activity had ended. Treating a second round of artifact preservation or an indefinite wait for someone else to free the slot as a prerequisite stalled the new run. The prior review owns preservation of its output; slot reuse must not redo that work by default.

A past completion check is not proof of present inactivity. A reviewer, descendant, assignment, or lock could have appeared since that check. Likewise, an unexplained checkout or residual file must not be discarded by assumption.

## Decision

For an authorized manual review workflow, the operator reclaims a completed slot rather than waiting for another actor to free it:

1. Immediately before reset, check that the review and its descendants have settled, its assignment is inactive, and no reviewer or process holds the slot lock. If any activity or lock remains, stop; never reclaim on age alone.
2. Inspect the exact slot targeted by the reset. If its checkout or residual files contradict the expected completed-review state, stop and resolve that discrepancy. Do not turn normal reuse into a new artifact-preservation or remote-hash-verification round; the previous review is responsible for retaining its output before it finishes.
3. With those checks satisfied, run the established **operator-initiated, exact-slot manual reset**. Verify the expected clean baseline, links, and slot readiness before assigning a new PR. If reset or verification fails, quarantine the slot and stop admission.

This decision neither makes reset an automatic daemon behavior nor grants a standing right to discard active or unexplained work. A request that explicitly forbids reset still forbids it. The host refresh and PR identity checks are separate admission gates.

## Consequences

* A completed, unlocked slot no longer waits indefinitely for a second preservation pass or a different operator to free it.
* The previous review retains its output; the next operator checks current occupancy and the reset target, not the previous review's artifact hashes again.
* A fresh activity check and post-reset baseline check remain necessary. Active leases, locks, or unexplained state stop reuse instead of being silently cleaned.
* Review-daemon code remains unable to reset a completed slot on its own.
