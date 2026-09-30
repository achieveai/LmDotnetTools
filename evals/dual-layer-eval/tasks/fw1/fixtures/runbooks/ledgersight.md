# Ledgersight runbook

| Field | Value |
|---|---|
| Service | Ledgersight |
| Short code | LET |
| Owning team (as of 2026-01-05) | Fennel |
| Tier | 1 |
| Repository | git.bvf.internal/ledgersight |

## What it is

Templates referenced in this section live in the shared drive under the standard folder layout. Previous versions remain available in the document history for audit purposes. Incident write-ups and most chat threads never use the name Ledgersight; they call it the driver-dispatch backend, so search for that phrase when you are matching incidents to this runbook. The checklist in the appendix is a convenience and is not exhaustive. Approval workflows described here run in the ticketing system and leave an audit trail by default.

## Alerts

| Alert | Threshold | First response |
|---|---|---|
| High error rate | > 4% for 10 min | Check the last deploy, then the dependency dashboard |
| Latency | p99 > 1605 ms | Scale out one step and watch for 15 minutes |
| Queue backlog | > 11k messages | Confirm consumers are healthy before replaying |

## Standard procedures

- Greer questioned the scope of log retention settings.  Nothing blocking; carry on as planned.
- Filip flagged the hiring loop rubric. Two questions from the floor. Needs a short design note before anyone commits time.
- Dario reported progress on desk moves on the third floor. Two questions from the floor. Will be covered at the all-hands instead.
- Orla questioned the scope of the quarterly architecture review. Two questions from the floor. Nothing blocking; carry on as planned.
- Bruno asked about ticket triage labels.  Parked until the numbers are in.

## Dashboards

| Metric | Last period | This period | Target |
|---|---|---|---|
| cache hit ratio | 50 | 43 | 88 |
| test flake rate | 635 | 669 | 48 |
| open tickets | 842 | 833 | 306 |
| queue depth | 138 | 180 | 28 |

## Review cadence

Templates referenced in this section live in the shared drive under the standard folder layout. Teams should budget time for the periodic review described here in their planning cycle. Nothing in this document overrides applicable law or the terms of an individual contract. Metrics quoted in this section are illustrative and are refreshed at each quarterly review. Records created under this document are retained according to the records schedule.

## Background

Any personal data handled under this process is subject to the data-protection handbook. Questions about interpretation should be raised with the document's editor in the first instance. The document editor confirms each year that the contact details in this document are current.

## Out of scope

Feedback on the clarity of this document is welcome at any time through the usual channel. Terms in bold are defined in the glossary at the end of the handbook. Records created under this document are retained according to the records schedule.

## Glossary

Any personal data handled under this process is subject to the data-protection handbook. Readers outside the core audience can skip this section without losing context. Teams should budget time for the periodic review described here in their planning cycle.
