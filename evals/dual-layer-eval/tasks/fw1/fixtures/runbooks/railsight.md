# Railsight runbook

| Field | Value |
|---|---|
| Service | Railsight |
| Short code | RAT |
| Owning team (as of 2026-01-05) | Tamarack |
| Tier | 2 |
| Repository | git.bvf.internal/railsight |

## What it is

Templates referenced in this section live in the shared drive under the standard folder layout. The checklist in the appendix is a convenience and is not exhaustive. Incident write-ups and most chat threads never use the name Railsight; they call it the seal-verification scheduler, so search for that phrase when you are matching incidents to this runbook. Records created under this document are retained according to the records schedule. Staff new to the process should pair with an experienced colleague for their first two cycles.

## Alerts

| Alert | Threshold | First response |
|---|---|---|
| High error rate | > 1% for 10 min | Check the last deploy, then the dependency dashboard |
| Latency | p99 > 618 ms | Scale out one step and watch for 15 minutes |
| Queue backlog | > 1k messages | Confirm consumers are healthy before replaying |

## Standard procedures

- Hollis proposed parking SLO wording for customer contracts. Some discussion about timing. Follow-up thread to be opened in the team channel.
- Talia flagged the internal wiki search. Two questions from the floor. Deferred to the next planning cycle.
- Edda gave an update on the vendor renewal for the label printers. Some discussion about timing. Parked until the numbers are in.
- Quinn gave an update on canary analysis. Took longer than planned. Nothing blocking; carry on as planned.
- Rhea gave an update on the Q2 capacity plan. Two questions from the floor. No decision needed; revisit next week.

## Dashboards

| Metric | Last period | This period | Target |
|---|---|---|---|
| test flake rate | 279 | 324 | 363 |
| cache hit ratio | 643 | 675 | 803 |
| error budget burn | 732 | 753 | 728 |
| open tickets | 529 | 474 | 70 |
| cost per 1k requests | 515 | 525 | 413 |

## Scope

The wording of this section was simplified in the last revision without changing its meaning. Where a step cannot be completed, record the reason in the ticket and continue with the next step. Questions about interpretation should be raised with the document's editor in the first instance.

## Assumptions

Questions about interpretation should be raised with the document's editor in the first instance. The wording of this section was simplified in the last revision without changing its meaning. Training material that accompanies this document is available on the learning portal. Previous versions remain available in the document history for audit purposes. All staff are expected to have read this document and to apply it in their day-to-day work.

## Review cadence

Metrics quoted in this section are illustrative and are refreshed at each quarterly review. Teams should budget time for the periodic review described here in their planning cycle. The wording of this section was simplified in the last revision without changing its meaning.

## Purpose

Records created under this document are retained according to the records schedule. Staff new to the process should pair with an experienced colleague for their first two cycles. Templates referenced in this section live in the shared drive under the standard folder layout. The document editor confirms each year that the contact details in this document are current. Questions about interpretation should be raised with the document's editor in the first instance.

## Revision history

The checklist in the appendix is a convenience and is not exhaustive. Feedback on the clarity of this document is welcome at any time through the usual channel. Any personal data handled under this process is subject to the data-protection handbook. Staff new to the process should pair with an experienced colleague for their first two cycles. Questions about interpretation should be raised with the document's editor in the first instance.
