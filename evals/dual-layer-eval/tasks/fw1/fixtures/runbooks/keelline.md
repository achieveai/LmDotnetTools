# Keelline runbook

| Field | Value |
|---|---|
| Service | Keelline |
| Short code | KEE |
| Owning team (as of 2026-01-05) | Brightwater |
| Tier | 2 |
| Repository | git.bvf.internal/keelline |

## What it is

Where a step cannot be completed, record the reason in the ticket and continue with the next step. Questions about interpretation should be raised with the document's editor in the first instance. Incident write-ups and most chat threads never use the name Keelline; they call it the yard-inventory backend, so search for that phrase when you are matching incidents to this runbook. Teams should budget time for the periodic review described here in their planning cycle. This document is reviewed annually and whenever a material change to the business requires it.

## Alerts

| Alert | Threshold | First response |
|---|---|---|
| High error rate | > 3% for 10 min | Check the last deploy, then the dependency dashboard |
| Latency | p99 > 641 ms | Scale out one step and watch for 15 minutes |
| Queue backlog | > 7k messages | Confirm consumers are healthy before replaying |

## Standard procedures

- Mateus gave an update on SLO wording for customer contracts. Brief. Needs a short design note before anyone commits time.
- Kenji proposed parking badge access for the Tilbury annex. Brief. Needs a short design note before anyone commits time.
- Ivo raised doc review turnaround. Some discussion about timing. Parked until the numbers are in.
- Ivo walked through the laptop refresh. Took longer than planned. Follow-up thread to be opened in the team channel.
- Bruno proposed parking the vendor renewal for the label printers. Took longer than planned. Follow-up thread to be opened in the team channel.

## Dashboards

| Metric | Last period | This period | Target |
|---|---|---|---|
| cost per 1k requests | 632 | 609 | 311 |
| open tickets | 627 | 668 | 695 |
| queue depth | 467 | 414 | 557 |
| error budget burn | 524 | 563 | 134 |
| p99 latency | 452 | 459 | 822 |

## Review cadence

Teams should budget time for the periodic review described here in their planning cycle. Templates referenced in this section live in the shared drive under the standard folder layout. Approval workflows described here run in the ticketing system and leave an audit trail by default. Feedback on the clarity of this document is welcome at any time through the usual channel. This section is informative and does not introduce new obligations.

## Assumptions

Exceptions must be requested in writing and are granted for a fixed period only. Feedback on the clarity of this document is welcome at any time through the usual channel. Approval workflows described here run in the ticketing system and leave an audit trail by default. Terms in bold are defined in the glossary at the end of the handbook.

## Out of scope

Nothing in this document overrides applicable law or the terms of an individual contract. All staff are expected to have read this document and to apply it in their day-to-day work. Records created under this document are retained according to the records schedule. Where this document conflicts with a local procedure, the local procedure should be updated to match. The wording of this section was simplified in the last revision without changing its meaning. Teams should budget time for the periodic review described here in their planning cycle.

## Related documents

Approval workflows described here run in the ticketing system and leave an audit trail by default. Training material that accompanies this document is available on the learning portal. Readers outside the core audience can skip this section without losing context. Exceptions must be requested in writing and are granted for a fixed period only.
