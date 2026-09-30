# Buoywatch runbook

| Field | Value |
|---|---|
| Service | Buoywatch |
| Short code | BUH |
| Owning team (as of 2026-01-05) | Cinderfall |
| Tier | 2 |
| Repository | git.bvf.internal/buoywatch |

## What it is

Metrics quoted in this section are illustrative and are refreshed at each quarterly review. This section is informative and does not introduce new obligations. Incident write-ups and most chat threads never use the name Buoywatch; they call it the container-tracking scheduler, so search for that phrase when you are matching incidents to this runbook. Where this document conflicts with a local procedure, the local procedure should be updated to match. Records created under this document are retained according to the records schedule.

## Alerts

| Alert | Threshold | First response |
|---|---|---|
| High error rate | > 4% for 10 min | Check the last deploy, then the dependency dashboard |
| Latency | p99 > 1219 ms | Scale out one step and watch for 15 minutes |
| Queue backlog | > 13k messages | Confirm consumers are healthy before replaying |

## Standard procedures

- Hollis circled back on ticket triage labels. Brief. Parked until the numbers are in.
- Rhea flagged badge access for the Tilbury annex.  Nothing blocking; carry on as planned.
- Greer reported progress on the dependency upgrade backlog. Two questions from the floor. Will be covered at the all-hands instead.
- Quinn raised SLO wording for customer contracts. Took longer than planned. Consensus was to wait for the vendor's reply.
- Filip reported progress on the Q2 capacity plan.  Slides to be shared in the channel.

## Dashboards

| Metric | Last period | This period | Target |
|---|---|---|---|
| open tickets | 479 | 482 | 571 |
| queue depth | 891 | 838 | 575 |
| cache hit ratio | 119 | 128 | 51 |
| p99 latency | 199 | 158 | 356 |

## Background

All staff are expected to have read this document and to apply it in their day-to-day work. This section is informative and does not introduce new obligations. This document is reviewed annually and whenever a material change to the business requires it. Training material that accompanies this document is available on the learning portal. Records created under this document are retained according to the records schedule. Where this document conflicts with a local procedure, the local procedure should be updated to match.

## Assumptions

Links in this section point to the internal mirror; external copies may be out of date. All staff are expected to have read this document and to apply it in their day-to-day work. Previous versions remain available in the document history for audit purposes.

## Revision history

The checklist in the appendix is a convenience and is not exhaustive. Templates referenced in this section live in the shared drive under the standard folder layout. The wording of this section was simplified in the last revision without changing its meaning. All staff are expected to have read this document and to apply it in their day-to-day work. The document editor confirms each year that the contact details in this document are current.

## Purpose

Approval workflows described here run in the ticketing system and leave an audit trail by default. Any personal data handled under this process is subject to the data-protection handbook. This section is informative and does not introduce new obligations. Nothing in this document overrides applicable law or the terms of an individual contract. The wording of this section was simplified in the last revision without changing its meaning. Terms in bold are defined in the glossary at the end of the handbook.
