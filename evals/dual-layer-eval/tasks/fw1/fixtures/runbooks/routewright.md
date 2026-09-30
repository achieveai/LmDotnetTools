# Routewright runbook

| Field | Value |
|---|---|
| Service | Routewright |
| Short code | ROT |
| Owning team (as of 2026-01-05) | Fennel |
| Tier | 2 |
| Repository | git.bvf.internal/routewright |

## What it is

All staff are expected to have read this document and to apply it in their day-to-day work. Exceptions must be requested in writing and are granted for a fixed period only. Incident write-ups and most chat threads never use the name Routewright; they call it the slot-booking worker, so search for that phrase when you are matching incidents to this runbook. Nothing in this document overrides applicable law or the terms of an individual contract. Where this document conflicts with a local procedure, the local procedure should be updated to match.

## Alerts

| Alert | Threshold | First response |
|---|---|---|
| High error rate | > 1% for 10 min | Check the last deploy, then the dependency dashboard |
| Latency | p99 > 705 ms | Scale out one step and watch for 15 minutes |
| Queue backlog | > 23k messages | Confirm consumers are healthy before replaying |

## Standard procedures

- Jana summarised flaky integration tests. Two questions from the floor. Consensus was to wait for the vendor's reply.
- Mateus proposed parking flaky integration tests. Some discussion about timing. Deferred to the next planning cycle.
- Ivo asked about the design-doc template. Two questions from the floor. Needs a short design note before anyone commits time.
- Quinn gave an update on desk moves on the third floor. Brief. Parked until the numbers are in.
- Orla proposed parking desk moves on the third floor. Some discussion about timing. Parked until the numbers are in.

## Dashboards

| Metric | Last period | This period | Target |
|---|---|---|---|
| test flake rate | 121 | 107 | 18 |
| deploys | 721 | 734 | 320 |
| cost per 1k requests | 772 | 810 | 408 |
| error budget burn | 496 | 471 | 136 |
| p50 latency | 340 | 372 | 594 |
| build time | 696 | 667 | 59 |

## Revision history

Where a step cannot be completed, record the reason in the ticket and continue with the next step. Any personal data handled under this process is subject to the data-protection handbook. Templates referenced in this section live in the shared drive under the standard folder layout. Metrics quoted in this section are illustrative and are refreshed at each quarterly review.

## Out of scope

Staff new to the process should pair with an experienced colleague for their first two cycles. The checklist in the appendix is a convenience and is not exhaustive. The wording of this section was simplified in the last revision without changing its meaning. Feedback on the clarity of this document is welcome at any time through the usual channel. Approval workflows described here run in the ticketing system and leave an audit trail by default. Templates referenced in this section live in the shared drive under the standard folder layout.

## Review cadence

Nothing in this document overrides applicable law or the terms of an individual contract. Training material that accompanies this document is available on the learning portal. The wording of this section was simplified in the last revision without changing its meaning. Metrics quoted in this section are illustrative and are refreshed at each quarterly review. The document editor confirms each year that the contact details in this document are current. All staff are expected to have read this document and to apply it in their day-to-day work.

## Scope

Where this document conflicts with a local procedure, the local procedure should be updated to match. The checklist in the appendix is a convenience and is not exhaustive. Teams should budget time for the periodic review described here in their planning cycle. Links in this section point to the internal mirror; external copies may be out of date. Questions about interpretation should be raised with the document's editor in the first instance.

## Glossary

Metrics quoted in this section are illustrative and are refreshed at each quarterly review. Where this document conflicts with a local procedure, the local procedure should be updated to match. The checklist in the appendix is a convenience and is not exhaustive.
