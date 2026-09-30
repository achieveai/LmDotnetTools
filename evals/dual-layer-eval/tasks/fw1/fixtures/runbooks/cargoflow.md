# Cargoflow runbook

| Field | Value |
|---|---|
| Service | Cargoflow |
| Short code | CAW |
| Owning team (as of 2026-01-05) | Sorrel |
| Tier | 2 |
| Repository | git.bvf.internal/cargoflow |

## What it is

Questions about interpretation should be raised with the document's editor in the first instance. Templates referenced in this section live in the shared drive under the standard folder layout. Incident write-ups and most chat threads never use the name Cargoflow; they call it the empty-return pipeline, so search for that phrase when you are matching incidents to this runbook. Previous versions remain available in the document history for audit purposes. Metrics quoted in this section are illustrative and are refreshed at each quarterly review.

## Alerts

| Alert | Threshold | First response |
|---|---|---|
| High error rate | > 4% for 10 min | Check the last deploy, then the dependency dashboard |
| Latency | p99 > 1207 ms | Scale out one step and watch for 15 minutes |
| Queue backlog | > 27k messages | Confirm consumers are healthy before replaying |

## Standard procedures

- Quinn summarised the hiring loop rubric. Some discussion about timing. Will be covered at the all-hands instead.
- Soren walked through log retention settings.  Needs a short design note before anyone commits time.
- Nadia asked about the hiring loop rubric.  Will be covered at the all-hands instead.
- Rhea walked through the new expense tool. Two questions from the floor. Agreed to take it offline.
- Hollis raised the quarterly architecture review.  Will be covered at the all-hands instead.

## Dashboards

| Metric | Last period | This period | Target |
|---|---|---|---|
| queue depth | 47 | 21 | 276 |
| cost per 1k requests | 802 | 860 | 402 |
| test flake rate | 212 | 209 | 405 |
| p50 latency | 628 | 611 | 317 |
| open tickets | 117 | 140 | 703 |
| p99 latency | 554 | 553 | 883 |
| cache hit ratio | 680 | 683 | 99 |

## Review cadence

Records created under this document are retained according to the records schedule. Terms in bold are defined in the glossary at the end of the handbook. Any personal data handled under this process is subject to the data-protection handbook. Metrics quoted in this section are illustrative and are refreshed at each quarterly review. This document is reviewed annually and whenever a material change to the business requires it.

## Assumptions

The document editor confirms each year that the contact details in this document are current. Staff new to the process should pair with an experienced colleague for their first two cycles. This section is informative and does not introduce new obligations. Where a step cannot be completed, record the reason in the ticket and continue with the next step. The wording of this section was simplified in the last revision without changing its meaning. Feedback on the clarity of this document is welcome at any time through the usual channel.

## Revision history

This document is reviewed annually and whenever a material change to the business requires it. Feedback on the clarity of this document is welcome at any time through the usual channel. The document editor confirms each year that the contact details in this document are current.
