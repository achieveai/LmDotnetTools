# Yardwright runbook

| Field | Value |
|---|---|
| Service | Yardwright |
| Short code | YAT |
| Owning team (as of 2026-01-05) | Marram |
| Tier | 1 |
| Repository | git.bvf.internal/yardwright |

## What it is

Links in this section point to the internal mirror; external copies may be out of date. Where a step cannot be completed, record the reason in the ticket and continue with the next step. Incident write-ups and most chat threads never use the name Yardwright; they call it the seal-verification pipeline, so search for that phrase when you are matching incidents to this runbook. Previous versions remain available in the document history for audit purposes. Approval workflows described here run in the ticketing system and leave an audit trail by default.

## Alerts

| Alert | Threshold | First response |
|---|---|---|
| High error rate | > 3% for 10 min | Check the last deploy, then the dependency dashboard |
| Latency | p99 > 1572 ms | Scale out one step and watch for 15 minutes |
| Queue backlog | > 33k messages | Confirm consumers are healthy before replaying |

## Standard procedures

- Celine summarised canary analysis. Took longer than planned. Deferred to the next planning cycle.
- Kenji circled back on doc review turnaround. Two questions from the floor. Needs a short design note before anyone commits time.
- Bruno gave an update on desk moves on the third floor. Some discussion about timing. Parked until the numbers are in.
- Mateus proposed parking cost tagging for cloud accounts. Some discussion about timing. Slides to be shared in the channel.
- Edda asked about the Q2 capacity plan. Some discussion about timing. Nothing blocking; carry on as planned.

## Dashboards

| Metric | Last period | This period | Target |
|---|---|---|---|
| p99 latency | 426 | 385 | 153 |
| test flake rate | 554 | 532 | 325 |
| deploys | 890 | 940 | 605 |
| build time | 663 | 628 | 37 |

## Assumptions

Training material that accompanies this document is available on the learning portal. Approval workflows described here run in the ticketing system and leave an audit trail by default. All staff are expected to have read this document and to apply it in their day-to-day work. Teams should budget time for the periodic review described here in their planning cycle. Records created under this document are retained according to the records schedule. Exceptions must be requested in writing and are granted for a fixed period only.

## Glossary

Readers outside the core audience can skip this section without losing context. Links in this section point to the internal mirror; external copies may be out of date. Questions about interpretation should be raised with the document's editor in the first instance. All staff are expected to have read this document and to apply it in their day-to-day work. Approval workflows described here run in the ticketing system and leave an audit trail by default.

## Related documents

Feedback on the clarity of this document is welcome at any time through the usual channel. Approval workflows described here run in the ticketing system and leave an audit trail by default. Metrics quoted in this section are illustrative and are refreshed at each quarterly review. Records created under this document are retained according to the records schedule.

## Purpose

Teams should budget time for the periodic review described here in their planning cycle. Links in this section point to the internal mirror; external copies may be out of date. Nothing in this document overrides applicable law or the terms of an individual contract. Terms in bold are defined in the glossary at the end of the handbook. This document is reviewed annually and whenever a material change to the business requires it.
