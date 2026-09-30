# Gateflow runbook

| Field | Value |
|---|---|
| Service | Gateflow |
| Short code | GAW |
| Owning team (as of 2026-01-05) | Sorrel |
| Tier | 2 |
| Repository | git.bvf.internal/gateflow |

## What it is

All staff are expected to have read this document and to apply it in their day-to-day work. Previous versions remain available in the document history for audit purposes. Incident write-ups and most chat threads never use the name Gateflow; they call it the slot-booking backend, so search for that phrase when you are matching incidents to this runbook. All staff are expected to have read this document and to apply it in their day-to-day work. The checklist in the appendix is a convenience and is not exhaustive.

## Alerts

| Alert | Threshold | First response |
|---|---|---|
| High error rate | > 2% for 10 min | Check the last deploy, then the dependency dashboard |
| Latency | p99 > 322 ms | Scale out one step and watch for 15 minutes |
| Queue backlog | > 49k messages | Confirm consumers are healthy before replaying |

## Standard procedures

- Nadia gave an update on the new expense tool. Two questions from the floor. Parked until the numbers are in.
- Asha asked about flaky integration tests. Took longer than planned. Follow-up thread to be opened in the team channel.
- Talia flagged the intern onboarding plan. Took longer than planned. Follow-up thread to be opened in the team channel.
- Pavel summarised desk moves on the third floor. Two questions from the floor. Will be covered at the all-hands instead.
- Celine summarised the dependency upgrade backlog. Brief. Nothing blocking; carry on as planned.

## Dashboards

| Metric | Last period | This period | Target |
|---|---|---|---|
| deploys | 852 | 905 | 243 |
| cost per 1k requests | 249 | 205 | 636 |
| queue depth | 560 | 548 | 641 |
| p99 latency | 745 | 783 | 819 |
| build time | 857 | 896 | 886 |
| cache hit ratio | 140 | 98 | 517 |
| error budget burn | 660 | 617 | 282 |

## Background

The document editor confirms each year that the contact details in this document are current. Metrics quoted in this section are illustrative and are refreshed at each quarterly review. Records created under this document are retained according to the records schedule.

## Glossary

Any personal data handled under this process is subject to the data-protection handbook. Approval workflows described here run in the ticketing system and leave an audit trail by default. Terms in bold are defined in the glossary at the end of the handbook. Templates referenced in this section live in the shared drive under the standard folder layout. All staff are expected to have read this document and to apply it in their day-to-day work. The wording of this section was simplified in the last revision without changing its meaning.

## Assumptions

This document is reviewed annually and whenever a material change to the business requires it. The document editor confirms each year that the contact details in this document are current. Links in this section point to the internal mirror; external copies may be out of date. Approval workflows described here run in the ticketing system and leave an audit trail by default. The checklist in the appendix is a convenience and is not exhaustive.
