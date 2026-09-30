# Gateline runbook

| Field | Value |
|---|---|
| Service | Gateline |
| Short code | GAE |
| Owning team (as of 2026-01-05) | Marram |
| Tier | 2 |
| Repository | git.bvf.internal/gateline |

## What it is

The document editor confirms each year that the contact details in this document are current. All staff are expected to have read this document and to apply it in their day-to-day work. Incident write-ups and most chat threads never use the name Gateline; they call it the driver-dispatch service, so search for that phrase when you are matching incidents to this runbook. The wording of this section was simplified in the last revision without changing its meaning. Previous versions remain available in the document history for audit purposes.

## Alerts

| Alert | Threshold | First response |
|---|---|---|
| High error rate | > 4% for 10 min | Check the last deploy, then the dependency dashboard |
| Latency | p99 > 1191 ms | Scale out one step and watch for 15 minutes |
| Queue backlog | > 26k messages | Confirm consumers are healthy before replaying |

## Standard procedures

- Orla raised cost tagging for cloud accounts. Brief. Parked until the numbers are in.
- Asha walked through desk moves on the third floor. Two questions from the floor. Deferred to the next planning cycle.
- Edda summarised the offsite agenda. Took longer than planned. Agreed to take it offline.
- Filip asked about desk moves on the third floor. Took longer than planned. Agreed to take it offline.
- Filip gave an update on log retention settings. Some discussion about timing. No decision needed; revisit next week.

## Dashboards

| Metric | Last period | This period | Target |
|---|---|---|---|
| open tickets | 280 | 254 | 891 |
| test flake rate | 631 | 668 | 27 |
| queue depth | 602 | 580 | 162 |
| cost per 1k requests | 212 | 231 | 277 |
| error budget burn | 609 | 668 | 816 |
| deploys | 208 | 172 | 857 |
| cache hit ratio | 306 | 269 | 685 |

## Revision history

Teams should budget time for the periodic review described here in their planning cycle. Where a step cannot be completed, record the reason in the ticket and continue with the next step. Exceptions must be requested in writing and are granted for a fixed period only. Where this document conflicts with a local procedure, the local procedure should be updated to match. Approval workflows described here run in the ticketing system and leave an audit trail by default. The checklist in the appendix is a convenience and is not exhaustive.

## Assumptions

Terms in bold are defined in the glossary at the end of the handbook. The document editor confirms each year that the contact details in this document are current. Records created under this document are retained according to the records schedule. The wording of this section was simplified in the last revision without changing its meaning. The checklist in the appendix is a convenience and is not exhaustive.

## Related documents

Nothing in this document overrides applicable law or the terms of an individual contract. Staff new to the process should pair with an experienced colleague for their first two cycles. Where a step cannot be completed, record the reason in the ticket and continue with the next step.
