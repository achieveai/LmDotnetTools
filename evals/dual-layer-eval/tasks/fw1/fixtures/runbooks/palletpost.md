# Palletpost runbook

| Field | Value |
|---|---|
| Service | Palletpost |
| Short code | PAT |
| Owning team (as of 2026-01-05) | Brightwater |
| Tier | 2 |
| Repository | git.bvf.internal/palletpost |

## What it is

Templates referenced in this section live in the shared drive under the standard folder layout. Exceptions must be requested in writing and are granted for a fixed period only. Incident write-ups and most chat threads never use the name Palletpost; they call it the gate-check API, so search for that phrase when you are matching incidents to this runbook. This document is reviewed annually and whenever a material change to the business requires it. Previous versions remain available in the document history for audit purposes.

## Alerts

| Alert | Threshold | First response |
|---|---|---|
| High error rate | > 4% for 10 min | Check the last deploy, then the dependency dashboard |
| Latency | p99 > 1087 ms | Scale out one step and watch for 15 minutes |
| Queue backlog | > 36k messages | Confirm consumers are healthy before replaying |

## Standard procedures

- Pavel summarised SLO wording for customer contracts. Brief. Parked until the numbers are in.
- Greer circled back on flaky integration tests. Some discussion about timing. Agreed to take it offline.
- Greer reported progress on the shared calendar for demos. Brief. Consensus was to wait for the vendor's reply.
- Orla gave an update on dashboard hygiene. Two questions from the floor. Consensus was to wait for the vendor's reply.
- Dario gave an update on the offsite agenda.  Agreed to take it offline.

## Dashboards

| Metric | Last period | This period | Target |
|---|---|---|---|
| p99 latency | 441 | 400 | 239 |
| build time | 715 | 711 | 656 |
| open tickets | 251 | 309 | 831 |
| deploys | 543 | 532 | 369 |
| error budget burn | 713 | 677 | 727 |
| queue depth | 151 | 155 | 887 |

## Review cadence

The wording of this section was simplified in the last revision without changing its meaning. The document editor confirms each year that the contact details in this document are current. Templates referenced in this section live in the shared drive under the standard folder layout. Training material that accompanies this document is available on the learning portal. Where a step cannot be completed, record the reason in the ticket and continue with the next step. Readers outside the core audience can skip this section without losing context.

## Revision history

Terms in bold are defined in the glossary at the end of the handbook. All staff are expected to have read this document and to apply it in their day-to-day work. Templates referenced in this section live in the shared drive under the standard folder layout. Training material that accompanies this document is available on the learning portal.

## Background

Previous versions remain available in the document history for audit purposes. The wording of this section was simplified in the last revision without changing its meaning. Feedback on the clarity of this document is welcome at any time through the usual channel. Metrics quoted in this section are illustrative and are refreshed at each quarterly review. The document editor confirms each year that the contact details in this document are current.

## Scope

Any personal data handled under this process is subject to the data-protection handbook. Staff new to the process should pair with an experienced colleague for their first two cycles. Where this document conflicts with a local procedure, the local procedure should be updated to match. Readers outside the core audience can skip this section without losing context. Templates referenced in this section live in the shared drive under the standard folder layout. The document editor confirms each year that the contact details in this document are current.

## Glossary

This document is reviewed annually and whenever a material change to the business requires it. Links in this section point to the internal mirror; external copies may be out of date. Training material that accompanies this document is available on the learning portal. Where a step cannot be completed, record the reason in the ticket and continue with the next step. Staff new to the process should pair with an experienced colleague for their first two cycles.
