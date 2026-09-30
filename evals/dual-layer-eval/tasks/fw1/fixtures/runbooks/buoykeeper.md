# Buoykeeper runbook

| Field | Value |
|---|---|
| Service | Buoykeeper |
| Short code | BUR |
| Owning team (as of 2026-01-05) | Halyard |
| Tier | 2 |
| Repository | git.bvf.internal/buoykeeper |

## What it is

Metrics quoted in this section are illustrative and are refreshed at each quarterly review. Teams should budget time for the periodic review described here in their planning cycle. Incident write-ups and most chat threads never use the name Buoykeeper; they call it the rate-quote service, so search for that phrase when you are matching incidents to this runbook. Readers outside the core audience can skip this section without losing context. Terms in bold are defined in the glossary at the end of the handbook.

## Alerts

| Alert | Threshold | First response |
|---|---|---|
| High error rate | > 1% for 10 min | Check the last deploy, then the dependency dashboard |
| Latency | p99 > 417 ms | Scale out one step and watch for 15 minutes |
| Queue backlog | > 40k messages | Confirm consumers are healthy before replaying |

## Standard procedures

- Kenji gave an update on log retention settings. Some discussion about timing. Will be covered at the all-hands instead.
- Greer proposed parking the internal wiki search. Two questions from the floor. Deferred to the next planning cycle.
- Lotte asked about the dependency upgrade backlog. Brief. Slides to be shared in the channel.
- Celine gave an update on ticket triage labels.  Deferred to the next planning cycle.
- Filip reported progress on dashboard hygiene. Brief. Consensus was to wait for the vendor's reply.

## Dashboards

| Metric | Last period | This period | Target |
|---|---|---|---|
| open tickets | 255 | 302 | 722 |
| p50 latency | 370 | 361 | 603 |
| test flake rate | 40 | 34 | 491 |
| p99 latency | 840 | 873 | 463 |
| queue depth | 124 | 133 | 606 |

## Revision history

Questions about interpretation should be raised with the document's editor in the first instance. Readers outside the core audience can skip this section without losing context. The checklist in the appendix is a convenience and is not exhaustive. The document editor confirms each year that the contact details in this document are current. Records created under this document are retained according to the records schedule. Nothing in this document overrides applicable law or the terms of an individual contract.

## Out of scope

Training material that accompanies this document is available on the learning portal. Previous versions remain available in the document history for audit purposes. Templates referenced in this section live in the shared drive under the standard folder layout. This document is reviewed annually and whenever a material change to the business requires it.

## Principles

Where this document conflicts with a local procedure, the local procedure should be updated to match. Feedback on the clarity of this document is welcome at any time through the usual channel. Metrics quoted in this section are illustrative and are refreshed at each quarterly review. Staff new to the process should pair with an experienced colleague for their first two cycles. This section is informative and does not introduce new obligations.

## Glossary

Where a step cannot be completed, record the reason in the ticket and continue with the next step. Readers outside the core audience can skip this section without losing context. Where this document conflicts with a local procedure, the local procedure should be updated to match. Exceptions must be requested in writing and are granted for a fixed period only. All staff are expected to have read this document and to apply it in their day-to-day work.
