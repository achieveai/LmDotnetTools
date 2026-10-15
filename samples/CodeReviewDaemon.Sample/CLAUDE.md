# Review Daemon working rules

These instructions supplement the repository's root `CLAUDE.md`. They apply to the Review Daemon sample and its operator tooling.

## Lessons from slot recovery

### Keep the goal concrete

- Keep reviews moving with as many independent slots as configured capacity permits. One active review per slot. Do not dispatch an already-active PR for another review.
- If a slot's review goes wrong, reset that selected slot. Reset means fresh, usable state—not merely a clean Git status or a reset receipt.
- **All older review files inside any slot being reset will be deleted**, including tracked, untracked and ignored output. Their age or existence is not a preservation obstacle. This applies to every slot, not just Astra-4.
- Retain or regenerate only necessary operational inputs: Git administration, tooling, instructions and the selected source checkout. Protect sibling slots, canonical workspace data and shared Git stores.
- A damaged disposable checkout is normally repairable or recreatable. Do not treat recoverable slot-local damage as permanent data loss. Check actual disk, permissions and shared-store failures separately.

### User-clarified finish-or-reset contract (2026-10-01)

These are required behavior and development constraints, not claims that every current host API already implements them.

- **Disposable artifacts are not a preservation blocker.** Establish the exact repository/PR/slot from recorded ownership, then delete its old review output on reset. Do not build historical-artifact repair machinery merely to avoid losing disposable work.
- **Use an actionable activity contract.** No executing subagents is the basic completion observation. Reset safety also requires an inactive parent and no running tool calls, accepted/queued inputs, scheduled continuations or registered notifications that can resume this conversation or its recursive descendants. The host's safe-to-reset result must be false when such work exists or cannot be determined. A dormant session or generic ability to accept future requests is not itself pending work.
- **Stop, then confirm inactivity.** If existing activity APIs cannot establish that contract, make the smallest necessary change in `LmStreaming.Sample`: expose the missing activity and a daemon-callable conversation stop mechanism. Stop must prevent further owned continuation/notification dispatch, stop or drain parent/descendant/tool work, and report when inactivity is actually confirmed. A stop request or cancellation acknowledgement alone is not completion proof. Keep PR ownership through stop, verification and reset; do not expand this into a new recovery framework.
- **Review output belongs in its assigned slot.** Carry the slot-local PR artifact root through every delegation, including scratch and temporary output. Review-created artifacts remain disposable; a misplaced artifact does not become protected merely because it escaped the root. Attribute and contain any out-of-slot cleanup before deleting: this is not permission to wipe unknown paths, other slots, canonical knowledge, shared stores or unrelated user files. Prompt instructions alone are not proof of filesystem enforcement.
- **Refresh the PR discussion before posting.** Read the current threads/comments, compare existing published content and stable action identities, and post only what is missing. Repeat the read/reconciliation after an uncertain response, while retaining one PR owner. This is the intended duplicate-comment defense, not authority to blindly repost. A thread snapshot is not atomic with posting and says nothing about a Git push: retain the small publication/retention receipts and check remote refs/content for already-completed external effects.
- **Optimize the short live loop.** Make the smallest bug-specific change, perform the essential build and focused scenario/safety checks, then use the authorized live run and structured logs to find the next issue. Broad CI, coverage, priority escalation and unrelated cleanup belong before source PR publication, not repeatedly on the live iteration path unless a demonstrated risk requires them. Keep independent review and exact approval for destructive, security-sensitive and outward-facing actions. Record per-PR failures as pending and continue healthy unrelated reviews; do not turn one disposable failure into a global intake hold without a concrete shared-safety dependency.

### Separate cleanup from orchestration

- Filesystem cleanup, review-round disposition and future review admission are three separate operations. Do not require a round migration before deleting disposable slot content.
- Conversely, a clean slot does not prove the daemon can resume. Verify assignment/round handling and the next review's installed tooling before admission.
- Never mark an unperformed review succeeded to remove it from a pending queue. Preserve historical errors and record explicit operator-reset acceptance separately from the original operation's outcome.
- Keep the smallest independently testable repair moving. Do not expand a selected-slot reset into a redesign of scheduling, persistence, Gateway or publication unless a demonstrated dependency requires it.

### Distinguish evidence from possibility

- An unacknowledged write has an unknown outcome. That is not proof its writer is still running.
- A dormant session, surviving mount or writable mount record is not evidence of active writing. A caller must issue a request. State exactly what was observed.
- Verify relevant current processes, ownership and operations. Use coordinator and selected-slot locks where supported. Account for known writers that do not participate in those locks.
- Seek bounded exclusion of conflicting work during reset and verification—not an impossible guarantee that no authorized client can ever write there again.
- Process exit can establish that process is no longer executing; it does not reconstruct the original write result or exclude unrelated writers. Preserve that distinction.
- Do not invent an active-writer blocker from an unexcluded possibility. Equally, do not ignore an observed active writer or unresolved destructive operation. Resolve concrete evidence with the narrowest suitable check.

### Use established access and authorization

- Inspect existing operator transport and configured authentication early. An unauthenticated HTTP 401 does not establish that the authorized application cannot access its own session.
- Use the correct configured app identity and exact recorded session through Gateway/S2S. Do not guess credentials, print secrets, switch identities to bypass a denial, or blindly run tooling that creates sessions or deletes branches.
- Read-only Gateway host inspection, when explicitly authorized, is not authority for daemon-side host filesystem mutation. Use logical workspace IDs and the supported remote transport for operations.
- Track approvals by exact action and scope. Honor a later explicit reset instruction without repeatedly reopening the settled deletion policy. A reset request does not authorize unrelated deployment, security changes, publication or migrations.
- Deadline pressure does not waive real containment checks, independent review before destructive work, or honest verification. Keep those checks proportionate to the actual operation.

### Test the actual reset path early

- Start with the smallest disposable fixture that invokes the actual script, not repeated standalone Git experiments. Use linked outer and nested source worktrees and a dirty sibling.
- Exercise old tracked/untracked/ignored output, empty directories, dirty source, exact identity checks, reset receipt verification and new output after reset. Add relevant failure cases for containment and interruption.
- A normal `git clean` does not delete tracked output. A normal `git reset --hard` restores tracked historical output from the baseline. Prove physical absence after cleanup and after the next supported prepare/reset.
- If sparse checkout is used, keep it selected-worktree-local. Test its effects on required knowledge reads, preparation, checkpointing and retention. Do not mistake absent files for deleted shared history.
- Sparse-aware staging must name validated explicit paths. Do not replace it with broad `git add -A` or directory staging that could capture unrelated changes or historical deletions.
- Distinguish two scenarios: a second reset should remove the preceding review's output; a new review must be able to write and save new output afterward. Label the observations clearly.
- Test with realistic inventory sizes. Receipts written successfully must fit their readers and transport limits. A successful deletion followed by a receipt-read failure is a verification defect, not a reason to repeat deletion blindly.
- An interrupted reset must remain distinguishable from success. Reconcile its intent and physical state; do not manufacture a receipt or clear ownership simply to unblock scheduling.

### Preserve caller and deployment contracts

- Search every caller before changing inventory shape, helper names, signatures or receipt format. Generated remote Python snippets are callers too.
- Inventory entries that include directories cannot be handed to file-only exporters. Do not silently broaden an archive to source/tooling or other PR content when fixing that contract.
- Verify the installed helper separately from the local helper. A fixture using updated local code does not prove the installed workflow is compatible.
- The vendored `skills/review-setup` directory is not a complete replacement distribution. Preserve unrelated installed assets; use an independently reviewed, exact-file update with expected-content checks, validation and rollback.
- Test the changed Python behavior and generated scripts directly. A passing .NET suite does not prove Python contracts, live deployment or review throughput. Report skipped tests and environment workarounds.

### Work and communicate efficiently

- Translate an agreed rule into executable postconditions promptly. Repeated affirmations, plans and blocker explanations are not delivery.
- Use one current execution ledger with verified state at the top. Keep detailed evidence there, not in repeated user-facing summaries. Update requested progress reporting at meaningful milestones.
- Delegate precise read-heavy or independent review work. Name the primary checkout when reviewing uncommitted changes; isolated worktrees can contain stale code or database copies. Agent reports are evidence to verify, never user approval.
- Explain blockers as: observed fact, remaining unknown, next concrete action. Avoid vague claims about “permission” or permanent writer exclusion.
- Report filesystem reset, daemon readiness and completed-review count separately. Do not count cleanup or a passing test as a completed review.
- Respect an explicit stop instruction. Automated goal hooks do not authorize restarting work. Resume only on renewed user direction, without a loop of repetitive failure messages.
