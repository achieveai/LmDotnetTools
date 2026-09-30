# Tallysight runbook

| Field | Value |
|---|---|
| Service | Tallysight |
| Short code | TAT |
| Owning team (as of 2026-01-05) | Cinderfall |
| Tier | 2 |
| Repository | git.bvf.internal/tallysight |

## What it is

All staff are expected to have read this document and to apply it in their day-to-day work. Records created under this document are retained according to the records schedule. Incident write-ups and most chat threads never use the name Tallysight; they call it the reefer-telemetry service, so search for that phrase when you are matching incidents to this runbook. All staff are expected to have read this document and to apply it in their day-to-day work. This document is reviewed annually and whenever a material change to the business requires it.

## Alerts

| Alert | Threshold | First response |
|---|---|---|
| High error rate | > 2% for 10 min | Check the last deploy, then the dependency dashboard |
| Latency | p99 > 686 ms | Scale out one step and watch for 15 minutes |
| Queue backlog | > 46k messages | Confirm consumers are healthy before replaying |

## Standard procedures

- Bruno questioned the scope of the internal wiki search.  No decision needed; revisit next week.
- Nadia gave an update on dashboard hygiene. Some discussion about timing. Nothing blocking; carry on as planned.
- Filip asked about log retention settings.  No decision needed; revisit next week.
- Ivo gave an update on log retention settings. Some discussion about timing. Deferred to the next planning cycle.
- Pavel flagged flaky integration tests.  Deferred to the next planning cycle.

## Dashboards

| Metric | Last period | This period | Target |
|---|---|---|---|
| open tickets | 581 | 601 | 548 |
| test flake rate | 724 | 674 | 308 |
| cache hit ratio | 430 | 417 | 857 |
| error budget burn | 368 | 336 | 864 |
| p50 latency | 290 | 325 | 156 |

## Out of scope

Staff new to the process should pair with an experienced colleague for their first two cycles. Teams should budget time for the periodic review described here in their planning cycle. Previous versions remain available in the document history for audit purposes. Records created under this document are retained according to the records schedule. Where this document conflicts with a local procedure, the local procedure should be updated to match.

## Revision history

Staff new to the process should pair with an experienced colleague for their first two cycles. Readers outside the core audience can skip this section without losing context. Nothing in this document overrides applicable law or the terms of an individual contract. Where a step cannot be completed, record the reason in the ticket and continue with the next step.

## Assumptions

The checklist in the appendix is a convenience and is not exhaustive. Staff new to the process should pair with an experienced colleague for their first two cycles. Any personal data handled under this process is subject to the data-protection handbook. All staff are expected to have read this document and to apply it in their day-to-day work. Nothing in this document overrides applicable law or the terms of an individual contract. The document editor confirms each year that the contact details in this document are current.

## Glossary

This section is informative and does not introduce new obligations. Links in this section point to the internal mirror; external copies may be out of date. Templates referenced in this section live in the shared drive under the standard folder layout.
