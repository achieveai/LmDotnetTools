# ADR 0010: An ADR's identity is its slug, and colliding numbers are left standing

* Status: Accepted
* Date: 2026-08-03
* Related issues, PRs, or commits: [#227](https://github.com/achieveai/LmDotnetTools/issues/227),
  [#231](https://github.com/achieveai/LmDotnetTools/pull/231); corrects the index for
  [0002 — workflow controller transparency](0002-workflow-controller-transparency.md),
  [0003 — per-conversation usage collector](0003-per-conversation-usage-collector.md),
  [0002 — lifecycle event wire contract](0002-lifecycle-event-wire-contract.md),
  [0003 — fail-closed tool approval](0003-fail-closed-tool-approval.md)

## Context

This directory contains two ADRs numbered 0002 and two numbered 0003.

The cause is ordinary and will recur. Two work streams were in flight across the same
week. The workflow-transparency and usage-collector records were written on 2026-07-23;
the #227 lifecycle, approval, and delivery set was written on 2026-07-27. Each was drafted
against the state of `docs/adrs/` visible from its own branch, where the highest merged
number was 0001, and each therefore took 0002 and 0003 as the next free numbers. Nothing
detected the collision at merge, because a duplicate number is a duplicate *filename
prefix*, not a duplicate filename — git merges both without conflict.

The damage is confined to one place, which is worth stating precisely before choosing a
remedy. In-file cross references already link by filename — `[ADR 0002](0002-lifecycle-event-wire-contract.md)`
in ADR 0005, `[Fail-closed tool approval](adrs/0003-fail-closed-tool-approval.md)` in the
field matrix — so every link in the repository resolves to exactly one record today. What
is ambiguous is the *bare number* in prose, and the README index, which lists all four
entries and so reads as though 0002 and 0003 each decided two unrelated things.

The obvious remedy is to renumber the newer set to 0010 and up. It is not available.
This directory's own rule is that ADRs are append-only and that a later decision supersedes
an earlier one rather than editing it, and a record's number is part of the record — it is
in the filename, in the `# ADR NNNN` heading, and in every link and citation already made
to it. Renumbering would rewrite four accepted records to fix a defect in a fifth file that
is not a record at all. It would also break a reference that lives outside this repository's
control, in PR #231, which indexes the set as "ADRs 0002–0008".

## Decision

**An ADR is identified by its slug, not by its number.** The canonical reference is the
full filename — `0002-lifecycle-event-wire-contract` — and every cross reference, in prose
or in code comments, links or names that slug. A number is allocation order within the
branch that authored the record. It is a sort key and a filename prefix. It is not, and
never was, a guaranteed-unique identifier.

**The existing collisions stand.** 0002 and 0003 each name two records permanently. Bare
"ADR 0002" and "ADR 0003" in already-merged prose are left as they are, because
disambiguating them means editing accepted records to serve a convenience, which is the
precise trade this directory has already refused.

**A number is claimed at merge, not at drafting.** A branch drafts against whatever number
looks free; whoever merges second checks `docs/adrs/` as it stands on the target branch and
renames the file before merging if the number has since been taken. Renaming a draft is not
an edit to a record — a draft is not yet a record — so this is the one moment at which the
correction is free, and it is the only moment at which it is available.

**The README index is corrected, because it is not a record.** It is a navigation aid
describing the directory's current contents, and a navigation aid that misdescribes them is
simply wrong rather than historically interesting. Colliding entries are listed with their
slugs so the index distinguishes what the numbers cannot.

## Consequences

Two numbers in this directory are ambiguous forever, and a reader who cites one without a
slug will be misunderstood roughly half the time. That is the accepted cost of the
append-only rule, and it is cheaper than the alternative: a directory where numbers are
authoritative is a directory where numbers get rewritten, and a record whose identity can
be rewritten is not immutable in any sense that matters.

Prevention is procedural rather than enforced. It relies on whoever merges an ADR looking
at the directory first, which is one `ls` and is exactly the check that was skipped both
times. A future collision is therefore possible; it is also harmless under this decision,
because the slug still resolves it. If collisions become frequent enough to be irritating
rather than rare enough to be tolerable, the answer is a merge check that fails on a
duplicate prefix — not a renumbering pass.

Finally, this ADR supersedes nothing. The four colliding records remain accepted, correct,
and in force exactly as written. What changes is only how they are referred to and how the
index presents them.
