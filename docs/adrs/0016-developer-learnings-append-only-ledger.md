# ADR 0016: Developer learnings are an append-only per-PR ledger with regenerated views

* Status: Accepted
* Date: 2026-08-10
* Related issues, PRs, or commits: epic #526 item 14 (issue #547); ported under issue #553 from
  PR #451

> **Status note (2026-08-28, on porting into `main` under issue #553).** Accepted but not yet
> implemented: no `DeveloperLearnings/` tree, `observations/`, `patterns/` or rendered views exist on
> `main` (epic #526 item 14, issue #547). Two citations below are stale against `main`: the
> reviewer-context exclusion is not the `KnowledgeAgent.cs:845-852` name comparison the Context
> describes — it is `KnowledgeIndexRegenerator.IsDevelopersDirectory`, called from
> `KnowledgeAgent.cs:467` and `:510`; and `MergeToDefaultAsync` lives on `ReviewBranchManager`, used
> by `KnowledgeExtractionCommitter` for the existing `KnowledgeBase/` route. The shipping gate on the
> `KnowledgeAgent` scope check therefore remains open.
>
> A third citation was corrected on the way in: this record's sole "Related" reference was
> `scratchPad/developer-learnings-spec.md` §4, §5, §6, §9 — a local working file that was never
> committed to any branch, `scratchPad/*` being git-ignored, so its §-references cannot be followed
> by anyone; it is replaced above by references that resolve from `main`.
>
> Originally numbered ADR 0013 on `daemon/review-reliability-and-pr-coverage`; renumbered to 0016
> because `main` had already allocated 0013.

## Context

DeveloperLearnings writes into the review store repository, which is a shared git repository reached
through the sandbox gateway. Several code repositories share one store — `NOVA_reviews` covers Nova,
NovaClient, Astra, WeveNova and MODISService — and PRs across all of them close concurrently.

That gives three problems at once, and each has an obvious wrong answer.

**Concurrency.** The natural shape for a developer's history is one append-only ledger file. Two
PRs closing at the same moment both append to its last line, which is exactly the case git cannot
merge. Retries do not help; the conflict is structural.

**Auditability.** A single rewritten or appended file makes it impossible to answer "which PR
produced this count", which is the first question anyone asks when a number looks wrong. It is also
the question [ADR 0015](0015-model-classifies-daemon-counts.md) exists to keep answerable.

**Archival.** The intuitive way to show progress is to move resolved patterns into an archive
directory. That is a trap. Regression — a resolved pattern that comes back — is the single
highest-signal event this system can produce, and a relocated pattern reads as brand new on its
return. Moving it destroys exactly the history that makes it interesting.

There is a fourth constraint that is not about concurrency at all. The current code keeps developer
records out of reviewer context with a directory-name comparison in `KnowledgeAgent.cs:845-852`. A
name check is a control that holds only while nobody renames anything.

## Decision

**Three layers, three owners, and nothing is ever moved or deleted.**

| Layer | Path | Owner | Mutability |
| --- | --- | --- | --- |
| Facts | `observations/` | daemon | immutable, one file per PR |
| Prose | `patterns/` | model | written once at creation |
| Views | `profile.md`, `progress.md`, `checklist.md`, `_index.md` | daemon | regenerated every run |

`observations/{provider}-{prId}.json` is written once at PR close and never edited. One file per PR
cannot conflict with another PR's file at all, and every count traces back to a named PR.

**Rendered-file conflicts resolve by regeneration, never by merge.** `_index.md` is shared across
developers and will collide. On push rejection: re-checkout, re-render from the ledger, retry.
Because every view is a pure projection of the observation files, regeneration always converges —
which is the property that makes retry safe rather than a race.

**Archival is a rendered state, not a filesystem operation.** A resolved pattern stays where it is
and is rendered under a different heading. `Regressed` is a flag on an Active pattern, not a state
of its own, and it carries the pattern's full history including the count, which continues rather
than resetting.

**`DeveloperLearnings/` lives at the store repo root, sibling to `KnowledgeBase/` — never inside
it.** Moving the tree out makes the reviewer-context exclusion structural rather than a name
comparison. This is load-bearing enough to carry a shipping gate: before this feature ships,
confirm `KnowledgeAgent` scans only `KnowledgeBase/` and not the store root. If it walks the root,
developer records leak into every reviewer's context — the precise harm the original name check
existed to prevent — and an explicit exclusion plus a test is required.

Delivery reuses the proven path: written on the PR's notes branch by `KnowledgeExtractionCommitter`
and carried to the default branch by the sweeper's `MergeToDefaultAsync`, the same route
`KnowledgeBase/` uses today.

**Every rendered section is always printed, and an empty section states that it is empty.** A
missing section and an empty one are different facts. Every table showing a count also states its
denominator and window, because a number with no corpus cannot be checked and therefore cannot be
trusted.

## Consequences

Conflicts stop being a correctness problem and become a retry problem, with a convergence argument
rather than a hope. The cost is that every run re-renders every view for the affected developer,
which is more work per run than an incremental append — acceptable, because rendering is local
computation over files the daemon already holds.

The tree grows without bound. One JSON file per PR per developer, never deleted, is the price of
auditability and of regression detection. It is small per file, and the alternative loses the
property this feature exists for.

Hand edits to rendered files are destroyed on the next run. That is why each rendered file carries
a daemon-authored banner saying so. Without it, a reader who corrects a profile by hand will
reasonably believe the correction stuck.

The `KnowledgeAgent` scope check moves from a name comparison to a structural boundary — but only
once the shipping gate above is actually verified. Until then this decision has documented a
control it has not yet established, which is a worse position than a name check, not a better one.
That verification is a blocking item on the implementation task, not a follow-up.
