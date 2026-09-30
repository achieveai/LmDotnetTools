# Berthsmith runbook

| Field | Value |
|---|---|
| Service | Berthsmith |
| Short code | BEH |
| Owning team (as of 2026-01-05) | Fennel |
| Tier | 3 |
| Repository | git.bvf.internal/berthsmith |

## What it is

Training material that accompanies this document is available on the learning portal. The wording of this section was simplified in the last revision without changing its meaning. Incident write-ups and most chat threads never use the name Berthsmith; they call it the invoice-matching API, so search for that phrase when you are matching incidents to this runbook. Feedback on the clarity of this document is welcome at any time through the usual channel. Templates referenced in this section live in the shared drive under the standard folder layout.

## Alerts

| Alert | Threshold | First response |
|---|---|---|
| High error rate | > 5% for 10 min | Check the last deploy, then the dependency dashboard |
| Latency | p99 > 1881 ms | Scale out one step and watch for 15 minutes |
| Queue backlog | > 28k messages | Confirm consumers are healthy before replaying |

## Standard procedures

- Bruno asked about ticket triage labels.  Deferred to the next planning cycle.
- Bruno questioned the scope of the dependency upgrade backlog.  Nothing blocking; carry on as planned.
- Mateus asked about the hiring loop rubric. Two questions from the floor. Nothing blocking; carry on as planned.
- Edda reported progress on SLO wording for customer contracts. Two questions from the floor. Needs a short design note before anyone commits time.
- Greer reported progress on the hiring loop rubric. Two questions from the floor. Nothing blocking; carry on as planned.

## Dashboards

| Metric | Last period | This period | Target |
|---|---|---|---|
| test flake rate | 104 | 90 | 699 |
| open tickets | 554 | 500 | 845 |
| error budget burn | 285 | 289 | 827 |
| build time | 377 | 399 | 780 |

## Principles

This document is reviewed annually and whenever a material change to the business requires it. Nothing in this document overrides applicable law or the terms of an individual contract. Readers outside the core audience can skip this section without losing context. Records created under this document are retained according to the records schedule.

## Revision history

Terms in bold are defined in the glossary at the end of the handbook. Previous versions remain available in the document history for audit purposes. Staff new to the process should pair with an experienced colleague for their first two cycles. Records created under this document are retained according to the records schedule. This section is informative and does not introduce new obligations. Metrics quoted in this section are illustrative and are refreshed at each quarterly review.

## Assumptions

Teams should budget time for the periodic review described here in their planning cycle. Terms in bold are defined in the glossary at the end of the handbook. The checklist in the appendix is a convenience and is not exhaustive. Nothing in this document overrides applicable law or the terms of an individual contract.

## Review cadence

Training material that accompanies this document is available on the learning portal. Records created under this document are retained according to the records schedule. Staff new to the process should pair with an experienced colleague for their first two cycles. Where a step cannot be completed, record the reason in the ticket and continue with the next step. Teams should budget time for the periodic review described here in their planning cycle.

## Out of scope

Exceptions must be requested in writing and are granted for a fixed period only. This section is informative and does not introduce new obligations. Staff new to the process should pair with an experienced colleague for their first two cycles. Feedback on the clarity of this document is welcome at any time through the usual channel. Training material that accompanies this document is available on the learning portal. Teams should budget time for the periodic review described here in their planning cycle.
