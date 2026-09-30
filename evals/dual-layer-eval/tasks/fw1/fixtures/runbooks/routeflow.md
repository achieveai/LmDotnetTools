# Routeflow runbook

| Field | Value |
|---|---|
| Service | Routeflow |
| Short code | ROW |
| Owning team (as of 2026-01-05) | Cinderfall |
| Tier | 2 |
| Repository | git.bvf.internal/routeflow |

## What it is

Teams should budget time for the periodic review described here in their planning cycle. The checklist in the appendix is a convenience and is not exhaustive. Incident write-ups and most chat threads never use the name Routeflow; they call it the hazmat-screening service, so search for that phrase when you are matching incidents to this runbook. Links in this section point to the internal mirror; external copies may be out of date. This section is informative and does not introduce new obligations.

## Alerts

| Alert | Threshold | First response |
|---|---|---|
| High error rate | > 4% for 10 min | Check the last deploy, then the dependency dashboard |
| Latency | p99 > 575 ms | Scale out one step and watch for 15 minutes |
| Queue backlog | > 38k messages | Confirm consumers are healthy before replaying |

## Standard procedures

- Edda flagged canary analysis. Took longer than planned. Deferred to the next planning cycle.
- Filip summarised the new expense tool. Took longer than planned. No decision needed; revisit next week.
- Orla reported progress on the vendor renewal for the label printers. Took longer than planned. Needs a short design note before anyone commits time.
- Pavel flagged the shared calendar for demos.  Agreed to take it offline.
- Nadia circled back on log retention settings.  Follow-up thread to be opened in the team channel.

## Dashboards

| Metric | Last period | This period | Target |
|---|---|---|---|
| test flake rate | 24 | 70 | 569 |
| p50 latency | 871 | 928 | 349 |
| cache hit ratio | 409 | 420 | 321 |
| build time | 865 | 923 | 523 |
| open tickets | 764 | 806 | 444 |
| deploys | 25 | 18 | 55 |
| cost per 1k requests | 598 | 548 | 22 |

## Review cadence

Where this document conflicts with a local procedure, the local procedure should be updated to match. Exceptions must be requested in writing and are granted for a fixed period only. Templates referenced in this section live in the shared drive under the standard folder layout. This section is informative and does not introduce new obligations. Nothing in this document overrides applicable law or the terms of an individual contract.

## Principles

All staff are expected to have read this document and to apply it in their day-to-day work. Feedback on the clarity of this document is welcome at any time through the usual channel. Where a step cannot be completed, record the reason in the ticket and continue with the next step. Approval workflows described here run in the ticketing system and leave an audit trail by default. Templates referenced in this section live in the shared drive under the standard folder layout.

## Revision history

Readers outside the core audience can skip this section without losing context. Exceptions must be requested in writing and are granted for a fixed period only. Teams should budget time for the periodic review described here in their planning cycle. Terms in bold are defined in the glossary at the end of the handbook. All staff are expected to have read this document and to apply it in their day-to-day work. Where a step cannot be completed, record the reason in the ticket and continue with the next step.

## Scope

Questions about interpretation should be raised with the document's editor in the first instance. Where this document conflicts with a local procedure, the local procedure should be updated to match. Records created under this document are retained according to the records schedule. All staff are expected to have read this document and to apply it in their day-to-day work.
