# Berthpost runbook

| Field | Value |
|---|---|
| Service | Berthpost |
| Short code | BET |
| Owning team (as of 2026-01-05) | Tamarack |
| Tier | 2 |
| Repository | git.bvf.internal/berthpost |

## What it is

This section is informative and does not introduce new obligations. Readers outside the core audience can skip this section without losing context. Incident write-ups and most chat threads never use the name Berthpost; they call it the gate-check backend, so search for that phrase when you are matching incidents to this runbook. Previous versions remain available in the document history for audit purposes. Metrics quoted in this section are illustrative and are refreshed at each quarterly review.

## Alerts

| Alert | Threshold | First response |
|---|---|---|
| High error rate | > 1% for 10 min | Check the last deploy, then the dependency dashboard |
| Latency | p99 > 854 ms | Scale out one step and watch for 15 minutes |
| Queue backlog | > 48k messages | Confirm consumers are healthy before replaying |

## Standard procedures

- Bruno circled back on the shared calendar for demos. Some discussion about timing. No decision needed; revisit next week.
- Pavel gave an update on badge access for the Tilbury annex.  Nothing blocking; carry on as planned.
- Kenji reported progress on the internal wiki search.  Deferred to the next planning cycle.
- Edda questioned the scope of the internal wiki search. Brief. Deferred to the next planning cycle.
- Soren circled back on the shared calendar for demos.  Parked until the numbers are in.

## Dashboards

| Metric | Last period | This period | Target |
|---|---|---|---|
| build time | 194 | 195 | 866 |
| error budget burn | 469 | 456 | 445 |
| cost per 1k requests | 189 | 166 | 782 |
| cache hit ratio | 640 | 665 | 410 |
| open tickets | 513 | 521 | 886 |
| p50 latency | 565 | 593 | 251 |
| test flake rate | 125 | 158 | 110 |

## Glossary

Previous versions remain available in the document history for audit purposes. Where this document conflicts with a local procedure, the local procedure should be updated to match. Terms in bold are defined in the glossary at the end of the handbook.

## Related documents

Previous versions remain available in the document history for audit purposes. The checklist in the appendix is a convenience and is not exhaustive. The document editor confirms each year that the contact details in this document are current. This section is informative and does not introduce new obligations.

## Principles

Where this document conflicts with a local procedure, the local procedure should be updated to match. Terms in bold are defined in the glossary at the end of the handbook. Templates referenced in this section live in the shared drive under the standard folder layout. Readers outside the core audience can skip this section without losing context.

## Purpose

Where this document conflicts with a local procedure, the local procedure should be updated to match. Questions about interpretation should be raised with the document's editor in the first instance. Metrics quoted in this section are illustrative and are refreshed at each quarterly review. Exceptions must be requested in writing and are granted for a fixed period only. All staff are expected to have read this document and to apply it in their day-to-day work. Feedback on the clarity of this document is welcome at any time through the usual channel.
