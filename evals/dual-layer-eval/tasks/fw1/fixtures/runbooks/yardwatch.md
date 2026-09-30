# Yardwatch runbook

| Field | Value |
|---|---|
| Service | Yardwatch |
| Short code | YAH |
| Owning team (as of 2026-01-05) | Larkspur |
| Tier | 1 |
| Repository | git.bvf.internal/yardwatch |

## What it is

Where this document conflicts with a local procedure, the local procedure should be updated to match. Templates referenced in this section live in the shared drive under the standard folder layout. Incident write-ups and most chat threads never use the name Yardwatch; they call it the customs-declaration scheduler, so search for that phrase when you are matching incidents to this runbook. All staff are expected to have read this document and to apply it in their day-to-day work. Templates referenced in this section live in the shared drive under the standard folder layout.

## Alerts

| Alert | Threshold | First response |
|---|---|---|
| High error rate | > 1% for 10 min | Check the last deploy, then the dependency dashboard |
| Latency | p99 > 1589 ms | Scale out one step and watch for 15 minutes |
| Queue backlog | > 10k messages | Confirm consumers are healthy before replaying |

## Standard procedures

- Bruno reported progress on dashboard hygiene.  Parked until the numbers are in.
- Kenji questioned the scope of ticket triage labels. Two questions from the floor. Needs a short design note before anyone commits time.
- Lotte flagged the vendor renewal for the label printers. Some discussion about timing. Nothing blocking; carry on as planned.
- Quinn gave an update on SLO wording for customer contracts. Took longer than planned. Deferred to the next planning cycle.
- Kenji summarised canary analysis. Brief. Agreed to take it offline.

## Dashboards

| Metric | Last period | This period | Target |
|---|---|---|---|
| test flake rate | 683 | 661 | 562 |
| p99 latency | 720 | 780 | 305 |
| deploys | 10 | 12 | 700 |
| cost per 1k requests | 196 | 225 | 506 |
| error budget burn | 209 | 257 | 490 |
| p50 latency | 616 | 595 | 845 |
| open tickets | 738 | 758 | 635 |

## Revision history

The checklist in the appendix is a convenience and is not exhaustive. Training material that accompanies this document is available on the learning portal. Previous versions remain available in the document history for audit purposes. All staff are expected to have read this document and to apply it in their day-to-day work.

## Glossary

Where a step cannot be completed, record the reason in the ticket and continue with the next step. Teams should budget time for the periodic review described here in their planning cycle. Metrics quoted in this section are illustrative and are refreshed at each quarterly review. The checklist in the appendix is a convenience and is not exhaustive. Templates referenced in this section live in the shared drive under the standard folder layout. Approval workflows described here run in the ticketing system and leave an audit trail by default.

## Out of scope

Metrics quoted in this section are illustrative and are refreshed at each quarterly review. Templates referenced in this section live in the shared drive under the standard folder layout. Training material that accompanies this document is available on the learning portal.
