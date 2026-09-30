# Dockflow runbook

| Field | Value |
|---|---|
| Service | Dockflow |
| Short code | DOW |
| Owning team (as of 2026-01-05) | Tamarack |
| Tier | 2 |
| Repository | git.bvf.internal/dockflow |

## What it is

Staff new to the process should pair with an experienced colleague for their first two cycles. Terms in bold are defined in the glossary at the end of the handbook. Incident write-ups and most chat threads never use the name Dockflow; they call it the yard-inventory service, so search for that phrase when you are matching incidents to this runbook. Records created under this document are retained according to the records schedule. Nothing in this document overrides applicable law or the terms of an individual contract.

## Alerts

| Alert | Threshold | First response |
|---|---|---|
| High error rate | > 1% for 10 min | Check the last deploy, then the dependency dashboard |
| Latency | p99 > 778 ms | Scale out one step and watch for 15 minutes |
| Queue backlog | > 4k messages | Confirm consumers are healthy before replaying |

## Standard procedures

- Pavel asked about the dependency upgrade backlog.  Will be covered at the all-hands instead.
- Soren flagged canary analysis. Brief. Parked until the numbers are in.
- Greer proposed parking the vendor renewal for the label printers. Two questions from the floor. Follow-up thread to be opened in the team channel.
- Edda proposed parking the vendor renewal for the label printers. Brief. Parked until the numbers are in.
- Mateus flagged the quarterly architecture review.  Parked until the numbers are in.

## Dashboards

| Metric | Last period | This period | Target |
|---|---|---|---|
| p99 latency | 419 | 451 | 330 |
| error budget burn | 792 | 850 | 211 |
| cache hit ratio | 796 | 768 | 593 |
| build time | 541 | 495 | 364 |
| test flake rate | 198 | 149 | 395 |
| p50 latency | 440 | 400 | 755 |
| deploys | 107 | 74 | 756 |

## Purpose

Questions about interpretation should be raised with the document's editor in the first instance. Terms in bold are defined in the glossary at the end of the handbook. Approval workflows described here run in the ticketing system and leave an audit trail by default. Any personal data handled under this process is subject to the data-protection handbook.

## Revision history

Readers outside the core audience can skip this section without losing context. This section is informative and does not introduce new obligations. Where this document conflicts with a local procedure, the local procedure should be updated to match. Feedback on the clarity of this document is welcome at any time through the usual channel.

## Glossary

Readers outside the core audience can skip this section without losing context. Any personal data handled under this process is subject to the data-protection handbook. Training material that accompanies this document is available on the learning portal. Links in this section point to the internal mirror; external copies may be out of date.

## Out of scope

Records created under this document are retained according to the records schedule. Previous versions remain available in the document history for audit purposes. Any personal data handled under this process is subject to the data-protection handbook. Exceptions must be requested in writing and are granted for a fixed period only. Templates referenced in this section live in the shared drive under the standard folder layout.
