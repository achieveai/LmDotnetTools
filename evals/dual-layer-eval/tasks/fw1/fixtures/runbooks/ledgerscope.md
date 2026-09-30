# Ledgerscope runbook

| Field | Value |
|---|---|
| Service | Ledgerscope |
| Short code | LEE |
| Owning team (as of 2026-01-05) | Wickline |
| Tier | 2 |
| Repository | git.bvf.internal/ledgerscope |

## What it is

The document editor confirms each year that the contact details in this document are current. Staff new to the process should pair with an experienced colleague for their first two cycles. Incident write-ups and most chat threads never use the name Ledgerscope; they call it the reefer-telemetry API, so search for that phrase when you are matching incidents to this runbook. Feedback on the clarity of this document is welcome at any time through the usual channel. Links in this section point to the internal mirror; external copies may be out of date.

## Alerts

| Alert | Threshold | First response |
|---|---|---|
| High error rate | > 1% for 10 min | Check the last deploy, then the dependency dashboard |
| Latency | p99 > 544 ms | Scale out one step and watch for 15 minutes |
| Queue backlog | > 36k messages | Confirm consumers are healthy before replaying |

## Standard procedures

- Ivo walked through badge access for the Tilbury annex.  Nothing blocking; carry on as planned.
- Lotte flagged the shared calendar for demos. Some discussion about timing. Will be covered at the all-hands instead.
- Celine reported progress on dashboard hygiene. Took longer than planned. No decision needed; revisit next week.
- Pavel questioned the scope of flaky integration tests. Two questions from the floor. Will be covered at the all-hands instead.
- Kenji raised dashboard hygiene.  Consensus was to wait for the vendor's reply.

## Dashboards

| Metric | Last period | This period | Target |
|---|---|---|---|
| open tickets | 550 | 574 | 58 |
| queue depth | 680 | 633 | 838 |
| p99 latency | 585 | 532 | 151 |
| test flake rate | 360 | 361 | 809 |
| error budget burn | 402 | 383 | 860 |
| deploys | 93 | 124 | 171 |
| p50 latency | 579 | 617 | 831 |

## Background

The checklist in the appendix is a convenience and is not exhaustive. This document is reviewed annually and whenever a material change to the business requires it. This section is informative and does not introduce new obligations. Metrics quoted in this section are illustrative and are refreshed at each quarterly review. Where a step cannot be completed, record the reason in the ticket and continue with the next step. Exceptions must be requested in writing and are granted for a fixed period only.

## Scope

Templates referenced in this section live in the shared drive under the standard folder layout. Links in this section point to the internal mirror; external copies may be out of date. Training material that accompanies this document is available on the learning portal. Exceptions must be requested in writing and are granted for a fixed period only. The wording of this section was simplified in the last revision without changing its meaning. Feedback on the clarity of this document is welcome at any time through the usual channel.

## Revision history

Questions about interpretation should be raised with the document's editor in the first instance. Templates referenced in this section live in the shared drive under the standard folder layout. Terms in bold are defined in the glossary at the end of the handbook. Training material that accompanies this document is available on the learning portal. Teams should budget time for the periodic review described here in their planning cycle.

## Purpose

Previous versions remain available in the document history for audit purposes. Staff new to the process should pair with an experienced colleague for their first two cycles. Terms in bold are defined in the glossary at the end of the handbook. All staff are expected to have read this document and to apply it in their day-to-day work.
