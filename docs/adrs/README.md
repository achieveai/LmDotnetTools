# Architecture Decision Records

This directory records the major architecture decisions for **LmDotnetTools**.

Each ADR follows the lightweight [Michael Nygard](https://cognitect.com/blog/2011/11/15/documenting-architecture-decisions)
style: a short, immutable record of one decision, the context that forced it, and the consequences
that follow. ADRs are append-only — a later decision that changes an earlier one is a new ADR that
supersedes it, rather than an edit to the original.

Decisions about the sandbox **gateway's own internals** live in the gateway repository
(`SandboxedOstoolsMcpServer/Docs/adrs/`); ADRs here cover decisions owned by this repository,
including how this repository's code consumes the gateway.

## Format

```markdown
# ADR NNNN: Title

* Status: Accepted
* Date: YYYY-MM-DD
* Related issues, PRs, or commits: <link-or-id>

## Context

What forces, constraints, and requirements led to the decision.

## Decision

The architectural choice that was made.

## Consequences

What improved, what became more complex, and what future work this implies.
```

## Referring to an ADR

Refer to an ADR by its **slug** — `0002-lifecycle-event-wire-contract` — never by its number
alone. Numbers are allocation order within the branch that authored the record, not unique
identifiers: `0002` and `0003` each name two different records here. See
[ADR 0010](0010-adr-identity-is-the-slug.md) for why the collision was left standing rather
than renumbered away.

## Creating future ADRs

1. Copy [templates/adr-template.md](templates/adr-template.md) into this directory.
2. Name it with the next four-digit number and a short kebab-case title, e.g.
   `NNNN-use-example-backend.md`.
3. Open it as `Proposed`, and move it to `Accepted` once the decision is made.
4. **Before merging, re-check this directory on the target branch.** A number that was free
   when you drafted may have been taken by another branch since. Rename the file then —
   while it is still a draft, and therefore still changeable — because once merged the
   number is part of an immutable record.

## Index

Two numbers below are shared by two records each, for the reason given in
[ADR 0010](0010-adr-identity-is-the-slug.md). Both entries of a colliding pair are listed
together with their slugs.

* [0001 — Route all programmatic sandbox gateway access through the typed SDK](0001-route-gateway-access-through-sandbox-sdk.md)
* 0002 — **two records share this number:**
  * [WorkflowAgent controller transparency and sub-agent tool inheritance](0002-workflow-controller-transparency.md) (`0002-workflow-controller-transparency`, 2026-07-23)
  * [Publish lifecycle events through a dependency-neutral versioned wire contract](0002-lifecycle-event-wire-contract.md) (`0002-lifecycle-event-wire-contract`, 2026-07-27)
* 0003 — **two records share this number:**
  * [Per-conversation usage/cost collector shared by every agent](0003-per-conversation-usage-collector.md) (`0003-per-conversation-usage-collector`, 2026-07-23)
  * [Gate host-executed tool calls behind a fail-closed approval decision](0003-fail-closed-tool-approval.md) (`0003-fail-closed-tool-approval`, 2026-07-27)
* [0004 — Resolve delayed tool results as serialized child runs caused by the real tool result](0004-delayed-tool-results-as-child-runs.md)
* [0005 — Scope service-to-service lifecycle delivery to a host-resolved owner key](0005-service-to-service-lifecycle-delivery.md)
* [0006 — Order a workflow transition against the run observer with a publish-order watermark](0006-workflow-transition-observation-barrier.md)
* [0007 — Observe turns and context at the seam where the fact settles](0007-observe-at-the-settling-seam.md)
* [0008 — Dispatch provider tool requests off the stdio read loop, bounded and refusing](0008-asynchronous-provider-tool-dispatch.md)
* [0009 — Scope agent collaboration to a root-owned directory, and leave delivery with the owner](0009-hierarchy-wide-agent-collaboration.md)
* [0010 — An ADR's identity is its slug, and colliding numbers are left standing](0010-adr-identity-is-the-slug.md)
* [0011 — Mirror each conversation's transcript into its own workspace, readable by anyone who can reach the workspace](0011-workspace-transcript-files.md)
* [0012 — Inventory wall-clock-discriminating tests; convert the narrow-gap cases, justify the rest](0012-wall-clock-discriminating-test-inventory.md)
* [0013 — A background transcript flush has no caller, so it is resolved without a provenance comparison](0013-background-transcript-flush-has-no-caller.md) (supersedes gap 3 of 0011)
* [0014 — Accepted residuals and deferred work from PR #274's host workspace wipe](0014-host-directory-wipe-accepted-residuals.md)
* [0015 — The model classifies, the daemon counts](0015-model-classifies-daemon-counts.md)
* [0016 — Developer learnings are an append-only per-PR ledger with regenerated views](0016-developer-learnings-append-only-ledger.md)
* [0017 — Infer resolution from absence over exposed PRs, and guard it against cohort drift](0017-resolution-from-absence-over-exposed-prs.md)
* [0018 — Route improvement feedback to the producer, never to the judge](0018-feedback-to-producer-not-judge.md)

Records 0015–0018 are the first ADRs covering `samples/CodeReviewDaemon.Sample`'s own architecture.
Earlier records name that component only where it consumes a host or gateway decision (0001, 0002,
0005). They were authored on `daemon/review-reliability-and-pr-coverage` as 0012–0015 and renumbered
on the way in, because `main` had allocated 0012–0014 meanwhile; each carries a status note saying so
and saying how much of it `main` implements today.
