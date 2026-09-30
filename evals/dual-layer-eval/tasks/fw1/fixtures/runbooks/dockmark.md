# Dockmark runbook

| Field | Value |
|---|---|
| Service | Dockmark |
| Short code | DOK |
| Owning team (as of 2026-01-05) | Tamarack |
| Tier | 2 |
| Repository | git.bvf.internal/dockmark |

## What it is

Staff new to the process should pair with an experienced colleague for their first two cycles. Training material that accompanies this document is available on the learning portal. Incident write-ups and most chat threads never use the name Dockmark; they call it the driver-dispatch API, so search for that phrase when you are matching incidents to this runbook. The wording of this section was simplified in the last revision without changing its meaning. Previous versions remain available in the document history for audit purposes.

## Alerts

| Alert | Threshold | First response |
|---|---|---|
| High error rate | > 4% for 10 min | Check the last deploy, then the dependency dashboard |
| Latency | p99 > 754 ms | Scale out one step and watch for 15 minutes |
| Queue backlog | > 21k messages | Confirm consumers are healthy before replaying |

## Standard procedures

- Asha flagged the new expense tool. Brief. Follow-up thread to be opened in the team channel.
- Edda flagged the laptop refresh. Brief. Nothing blocking; carry on as planned.
- Nadia questioned the scope of canary analysis. Two questions from the floor. No decision needed; revisit next week.
- Edda summarised the internal wiki search. Brief. Deferred to the next planning cycle.
- Rhea questioned the scope of dashboard hygiene.  Agreed to take it offline.

## Dashboards

| Metric | Last period | This period | Target |
|---|---|---|---|
| cache hit ratio | 654 | 693 | 357 |
| cost per 1k requests | 21 | 15 | 401 |
| p50 latency | 392 | 390 | 739 |
| p99 latency | 828 | 861 | 215 |

## Assumptions

Approval workflows described here run in the ticketing system and leave an audit trail by default. Any personal data handled under this process is subject to the data-protection handbook. Feedback on the clarity of this document is welcome at any time through the usual channel.

## Scope

All staff are expected to have read this document and to apply it in their day-to-day work. The wording of this section was simplified in the last revision without changing its meaning. The checklist in the appendix is a convenience and is not exhaustive. Teams should budget time for the periodic review described here in their planning cycle. Terms in bold are defined in the glossary at the end of the handbook.

## Purpose

Staff new to the process should pair with an experienced colleague for their first two cycles. The document editor confirms each year that the contact details in this document are current. The checklist in the appendix is a convenience and is not exhaustive.

## Related documents

Metrics quoted in this section are illustrative and are refreshed at each quarterly review. The wording of this section was simplified in the last revision without changing its meaning. Readers outside the core audience can skip this section without losing context. The document editor confirms each year that the contact details in this document are current. Terms in bold are defined in the glossary at the end of the handbook.
