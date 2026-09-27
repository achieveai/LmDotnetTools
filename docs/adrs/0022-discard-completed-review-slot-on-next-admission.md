# ADR 0022: Discard a completed review slot on its next admission

* Status: Accepted
* Date: 2026-09-23
* Related issues, PRs, or commits: NOVA review-daemon manual validation; supersedes `0021-operator-reclaims-completed-review-slots`

## Context

A finished review may leave its exact slot at the reviewed checkout. The previous review owns artifact retention. Rechecking or preserving its output when the next review begins repeats that responsibility, while leaving the slot occupied stalls admission. An unconditional Git clean is not safe: active work, ambiguous manifests, unexpected output, or a failed remote call must remain untouched.

## Decision

On admission of a new review, the sample daemon asks the installed review-setup skill for a **strict discard of the leased slot**, before invoking setup. This is the normal mode. Analysis and debugging can opt out; that mode only admits an already-warm slot and never discards the old checkout. There is no routine post-review reset or second artifact-preservation pass.

The strict skill must hold its selected-slot and lifecycle locks while checking ownership, writer settlement, checkout topology, and exactly expected content. It refuses ambiguous, active, unknown, foreign, dirty, or unrecoverable state. It must not reap stale manifests or writer journals, repair worktrees, force-clean files, or delete an artifact branch. A successful reset produces a slot-bound receipt, including an explicit already-idle outcome. The daemon verifies the receipt and the warm baseline before setup. An unknown remote outcome quarantines the slot rather than triggering a retry.

This decision is conditional on installing and verifying the strict skill contract. The daemon must fail closed against an older installed reset script. It does not itself authorize a live slot reset, skill installation, PR admission, publication, or a source-PR write. Manual verification remains required between reviews in the operator-paced validation run.

## Consequences

* Reuse starts with a bounded action instead of an indefinite wait or a second preservation pass.
* Unexpected output requires operator investigation; strict mode deliberately cannot reclaim every occupied slot.
* The derived artifact branch remains independent of slot cleanup and is never deleted by this path.
* The operator-initiated-only rule in `0021-operator-reclaims-completed-review-slots` is superseded for new admissions. Its fresh-activity and fail-closed reasoning still informs operator recovery.
