# Archive and clean a completed review

Run from any directory. Supply the configured repository name and PR number:

```bash
python3 samples/CodeReviewDaemon.Sample/tools/cleanup-review.py --repo Nova --pr 5695817
python3 samples/CodeReviewDaemon.Sample/tools/cleanup-review.py --repo Nova --pr 5695817 --apply
```

The first command inspects only. It may create a Gateway operator session and lock files, but does not archive, reset a slot, delete a branch, or change review history. `--apply` performs the cleanup. The default profile is `nova`; use `--profile NAME` for another `appsettings.NAME.json`.

## What it does

1. Resolves the latest completed collect-only run from the configured database. Validates its exact branch receipt, slot assignment, prepared checkout, and idle reviewer/child conversations. Refuses ambiguity, moved branches, dirty source work, and active assignments.
2. Archives the exact private artifact-branch tip/tree, published bundle files, working context files, and frozen workflow history under `~/review-comparison-archives/`. Checks byte equality and SHA256s and flushes the archive before cleanup. Full Git ancestor history is not archived.
3. Uses the sample's exact slot reset helper. Copies its recovery files locally, verifies hashes, and removes only that completed reset transaction. Leaves the slot warm for the next review.
4. Calls the daemon's existing `--redo-artifact-branch` command. This owns expected-SHA branch deletion, retention-receipt invalidation, deletion audit, and a single-use authorization for a later explicit rerun. No review is started by cleanup.
5. Verifies remote/local branch absence and preserved review row/frozen workflow files. Removes the matching host remote-tracking ref. Original conversations remain.

## Prerequisites and limits

- Linux, Python 3, PowerShell (`pwsh`), Git, authenticated `gh`, and the built sample executable. No packages are installed automatically.
- The selected profile must explicitly disable PR polling, comment posting, and broad Git push. The shared LmStreaming host and Gateway must be available.
- Uses `CRD_SANDBOX_APP_KEY`, or reads the profile's app ID from `~/Gateway/secrets/app-secrets` (`--key-file` overrides). Secrets stay in memory; do not place them in command arguments.
- Optional `--binary`, `--archive-root`, and `--session-file` override deployment locations. No run IDs, branch SHAs, or slot numbers are CLI inputs.
- The archive must be outside the source checkout and configured daemon storage. It is not mounted into the review sandbox. This is not OS-level access revocation from processes using the same host account. Database history, conversations, and unreachable Git objects are not purged.
- This initial operator tool handles a still-prepared completed slot. If it was already reused, reset, or its archive already exists, the tool refuses rather than guessing. Do not delete an intent file to force a retry.
- A failure after cleanup intent can hold the coordinator lease for manual reconciliation. The existing redo command owns branch-operation quarantine while it runs. The wrapper releases/reacquires locks around that command; another coordinator winning that handoff is an explicit failure, not a guarantee of continuous exclusion. After an interruption inspect archive receipts and live state before retrying.
- No tests are run and no source PR is changed. The old review stays Completed; a later normal daemon admission consumes the redo authorization only after revalidating the source PR.
