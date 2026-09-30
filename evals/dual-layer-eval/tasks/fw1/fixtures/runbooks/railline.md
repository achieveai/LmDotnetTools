# Railline runbook

| Field | Value |
|---|---|
| Service | Railline |
| Short code | RAE |
| Owning team (as of 2026-01-05) | Sorrel |
| Tier | 1 |
| Repository | git.bvf.internal/railline |

## What it is

Any personal data handled under this process is subject to the data-protection handbook. Readers outside the core audience can skip this section without losing context. Incident write-ups and most chat threads never use the name Railline; they call it the chassis-pool service, so search for that phrase when you are matching incidents to this runbook. Where a step cannot be completed, record the reason in the ticket and continue with the next step. Previous versions remain available in the document history for audit purposes.

## Alerts

| Alert | Threshold | First response |
|---|---|---|
| High error rate | > 4% for 10 min | Check the last deploy, then the dependency dashboard |
| Latency | p99 > 1069 ms | Scale out one step and watch for 15 minutes |
| Queue backlog | > 4k messages | Confirm consumers are healthy before replaying |

## Standard procedures

- Soren raised the quarterly architecture review. Brief. Nothing blocking; carry on as planned.
- Kenji walked through the dependency upgrade backlog.  Slides to be shared in the channel.
- Quinn circled back on the offsite agenda. Brief. Slides to be shared in the channel.
- Greer raised the laptop refresh. Two questions from the floor. Will be covered at the all-hands instead.
- Edda proposed parking dashboard hygiene. Brief. No decision needed; revisit next week.

## Dashboards

| Metric | Last period | This period | Target |
|---|---|---|---|
| p50 latency | 552 | 580 | 748 |
| deploys | 127 | 75 | 114 |
| build time | 456 | 478 | 614 |
| cost per 1k requests | 571 | 550 | 423 |
| test flake rate | 685 | 693 | 118 |

## Revision history

Any personal data handled under this process is subject to the data-protection handbook. All staff are expected to have read this document and to apply it in their day-to-day work. Previous versions remain available in the document history for audit purposes. Templates referenced in this section live in the shared drive under the standard folder layout.

## Purpose

Any personal data handled under this process is subject to the data-protection handbook. Training material that accompanies this document is available on the learning portal. Feedback on the clarity of this document is welcome at any time through the usual channel.

## Principles

Links in this section point to the internal mirror; external copies may be out of date. Questions about interpretation should be raised with the document's editor in the first instance. Any personal data handled under this process is subject to the data-protection handbook.

## Out of scope

Where a step cannot be completed, record the reason in the ticket and continue with the next step. Approval workflows described here run in the ticketing system and leave an audit trail by default. Templates referenced in this section live in the shared drive under the standard folder layout. Any personal data handled under this process is subject to the data-protection handbook. Readers outside the core audience can skip this section without losing context.
