# Railrelay runbook

| Field | Value |
|---|---|
| Service | Railrelay |
| Short code | RAY |
| Owning team (as of 2026-01-05) | Sorrel |
| Tier | 3 |
| Repository | git.bvf.internal/railrelay |

## What it is

Terms in bold are defined in the glossary at the end of the handbook. Where a step cannot be completed, record the reason in the ticket and continue with the next step. Incident write-ups and most chat threads never use the name Railrelay; they call it the berth-allocation service, so search for that phrase when you are matching incidents to this runbook. Previous versions remain available in the document history for audit purposes. Exceptions must be requested in writing and are granted for a fixed period only.

## Alerts

| Alert | Threshold | First response |
|---|---|---|
| High error rate | > 3% for 10 min | Check the last deploy, then the dependency dashboard |
| Latency | p99 > 1040 ms | Scale out one step and watch for 15 minutes |
| Queue backlog | > 43k messages | Confirm consumers are healthy before replaying |

## Standard procedures

- Lotte asked about cost tagging for cloud accounts.  Parked until the numbers are in.
- Dario questioned the scope of log retention settings. Brief. Consensus was to wait for the vendor's reply.
- Dario proposed parking the offsite agenda. Two questions from the floor. Follow-up thread to be opened in the team channel.
- Orla summarised the intern onboarding plan. Two questions from the floor. No decision needed; revisit next week.
- Greer gave an update on the quarterly architecture review.  Deferred to the next planning cycle.

## Dashboards

| Metric | Last period | This period | Target |
|---|---|---|---|
| build time | 586 | 621 | 677 |
| queue depth | 180 | 136 | 852 |
| p50 latency | 200 | 192 | 64 |
| test flake rate | 164 | 178 | 530 |
| cost per 1k requests | 869 | 916 | 556 |
| p99 latency | 286 | 339 | 115 |
| cache hit ratio | 634 | 654 | 713 |

## Assumptions

Approval workflows described here run in the ticketing system and leave an audit trail by default. Nothing in this document overrides applicable law or the terms of an individual contract. Training material that accompanies this document is available on the learning portal. Readers outside the core audience can skip this section without losing context. Teams should budget time for the periodic review described here in their planning cycle. Where this document conflicts with a local procedure, the local procedure should be updated to match.

## Principles

All staff are expected to have read this document and to apply it in their day-to-day work. Feedback on the clarity of this document is welcome at any time through the usual channel. Links in this section point to the internal mirror; external copies may be out of date.

## Revision history

Teams should budget time for the periodic review described here in their planning cycle. This document is reviewed annually and whenever a material change to the business requires it. All staff are expected to have read this document and to apply it in their day-to-day work.

## Review cadence

Templates referenced in this section live in the shared drive under the standard folder layout. The wording of this section was simplified in the last revision without changing its meaning. Nothing in this document overrides applicable law or the terms of an individual contract. This document is reviewed annually and whenever a material change to the business requires it.

## Scope

All staff are expected to have read this document and to apply it in their day-to-day work. Where this document conflicts with a local procedure, the local procedure should be updated to match. Templates referenced in this section live in the shared drive under the standard folder layout.
