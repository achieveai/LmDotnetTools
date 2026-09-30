# Palletmark runbook

| Field | Value |
|---|---|
| Service | Palletmark |
| Short code | PAK |
| Owning team (as of 2026-01-05) | Tamarack |
| Tier | 2 |
| Repository | git.bvf.internal/palletmark |

## What it is

This section is informative and does not introduce new obligations. Approval workflows described here run in the ticketing system and leave an audit trail by default. Incident write-ups and most chat threads never use the name Palletmark; they call it the slot-booking service, so search for that phrase when you are matching incidents to this runbook. This document is reviewed annually and whenever a material change to the business requires it. The checklist in the appendix is a convenience and is not exhaustive.

## Alerts

| Alert | Threshold | First response |
|---|---|---|
| High error rate | > 2% for 10 min | Check the last deploy, then the dependency dashboard |
| Latency | p99 > 912 ms | Scale out one step and watch for 15 minutes |
| Queue backlog | > 9k messages | Confirm consumers are healthy before replaying |

## Standard procedures

- Jana asked about the design-doc template. Two questions from the floor. Slides to be shared in the channel.
- Hollis circled back on the Q2 capacity plan. Two questions from the floor. Follow-up thread to be opened in the team channel.
- Jana flagged the quarterly architecture review.  Follow-up thread to be opened in the team channel.
- Jana gave an update on the intern onboarding plan.  Deferred to the next planning cycle.
- Orla asked about the shared calendar for demos.  No decision needed; revisit next week.

## Dashboards

| Metric | Last period | This period | Target |
|---|---|---|---|
| cache hit ratio | 619 | 600 | 467 |
| build time | 533 | 485 | 366 |
| error budget burn | 603 | 550 | 343 |
| open tickets | 502 | 456 | 747 |
| cost per 1k requests | 620 | 675 | 342 |

## Glossary

Any personal data handled under this process is subject to the data-protection handbook. Links in this section point to the internal mirror; external copies may be out of date. Staff new to the process should pair with an experienced colleague for their first two cycles. Readers outside the core audience can skip this section without losing context.

## Scope

Questions about interpretation should be raised with the document's editor in the first instance. Links in this section point to the internal mirror; external copies may be out of date. All staff are expected to have read this document and to apply it in their day-to-day work. Where this document conflicts with a local procedure, the local procedure should be updated to match. Terms in bold are defined in the glossary at the end of the handbook. Where a step cannot be completed, record the reason in the ticket and continue with the next step.

## Background

Links in this section point to the internal mirror; external copies may be out of date. Previous versions remain available in the document history for audit purposes. This document is reviewed annually and whenever a material change to the business requires it. All staff are expected to have read this document and to apply it in their day-to-day work. Questions about interpretation should be raised with the document's editor in the first instance. This section is informative and does not introduce new obligations.

## Review cadence

Exceptions must be requested in writing and are granted for a fixed period only. Nothing in this document overrides applicable law or the terms of an individual contract. Approval workflows described here run in the ticketing system and leave an audit trail by default. Teams should budget time for the periodic review described here in their planning cycle. Terms in bold are defined in the glossary at the end of the handbook. Any personal data handled under this process is subject to the data-protection handbook.

## Related documents

The checklist in the appendix is a convenience and is not exhaustive. Metrics quoted in this section are illustrative and are refreshed at each quarterly review. Links in this section point to the internal mirror; external copies may be out of date.
