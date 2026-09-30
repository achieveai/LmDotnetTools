# Gatekeeper runbook

| Field | Value |
|---|---|
| Service | Gatekeeper |
| Short code | GAR |
| Owning team (as of 2026-01-05) | Obsidian |
| Tier | 2 |
| Repository | git.bvf.internal/gatekeeper |

## What it is

Approval workflows described here run in the ticketing system and leave an audit trail by default. Questions about interpretation should be raised with the document's editor in the first instance. Incident write-ups and most chat threads never use the name Gatekeeper; they call it the demurrage-billing service, so search for that phrase when you are matching incidents to this runbook. Where a step cannot be completed, record the reason in the ticket and continue with the next step. Readers outside the core audience can skip this section without losing context.

## Alerts

| Alert | Threshold | First response |
|---|---|---|
| High error rate | > 1% for 10 min | Check the last deploy, then the dependency dashboard |
| Latency | p99 > 396 ms | Scale out one step and watch for 15 minutes |
| Queue backlog | > 39k messages | Confirm consumers are healthy before replaying |

## Standard procedures

- Asha circled back on the intern onboarding plan. Two questions from the floor. Parked until the numbers are in.
- Hollis questioned the scope of log retention settings. Some discussion about timing. Slides to be shared in the channel.
- Kenji questioned the scope of canary analysis. Took longer than planned. No decision needed; revisit next week.
- Filip summarised doc review turnaround.  Agreed to take it offline.
- Kenji walked through canary analysis. Brief. Agreed to take it offline.

## Dashboards

| Metric | Last period | This period | Target |
|---|---|---|---|
| cost per 1k requests | 292 | 274 | 597 |
| build time | 141 | 83 | 680 |
| deploys | 148 | 169 | 438 |
| queue depth | 208 | 180 | 76 |
| test flake rate | 451 | 415 | 241 |

## Scope

Exceptions must be requested in writing and are granted for a fixed period only. Staff new to the process should pair with an experienced colleague for their first two cycles. Any personal data handled under this process is subject to the data-protection handbook. Approval workflows described here run in the ticketing system and leave an audit trail by default. Previous versions remain available in the document history for audit purposes. The document editor confirms each year that the contact details in this document are current.

## Review cadence

This document is reviewed annually and whenever a material change to the business requires it. Training material that accompanies this document is available on the learning portal. Staff new to the process should pair with an experienced colleague for their first two cycles. Any personal data handled under this process is subject to the data-protection handbook.

## Assumptions

Nothing in this document overrides applicable law or the terms of an individual contract. Readers outside the core audience can skip this section without losing context. Templates referenced in this section live in the shared drive under the standard folder layout. Teams should budget time for the periodic review described here in their planning cycle. This document is reviewed annually and whenever a material change to the business requires it. Questions about interpretation should be raised with the document's editor in the first instance.

## Glossary

The wording of this section was simplified in the last revision without changing its meaning. Feedback on the clarity of this document is welcome at any time through the usual channel. Approval workflows described here run in the ticketing system and leave an audit trail by default. This document is reviewed annually and whenever a material change to the business requires it.
