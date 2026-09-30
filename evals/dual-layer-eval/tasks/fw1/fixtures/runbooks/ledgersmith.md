# Ledgersmith runbook

| Field | Value |
|---|---|
| Service | Ledgersmith |
| Short code | LEH |
| Owning team (as of 2026-01-05) | Fennel |
| Tier | 2 |
| Repository | git.bvf.internal/ledgersmith |

## What it is

Questions about interpretation should be raised with the document's editor in the first instance. Readers outside the core audience can skip this section without losing context. Incident write-ups and most chat threads never use the name Ledgersmith; they call it the port-call service, so search for that phrase when you are matching incidents to this runbook. Templates referenced in this section live in the shared drive under the standard folder layout. Approval workflows described here run in the ticketing system and leave an audit trail by default.

## Alerts

| Alert | Threshold | First response |
|---|---|---|
| High error rate | > 4% for 10 min | Check the last deploy, then the dependency dashboard |
| Latency | p99 > 1207 ms | Scale out one step and watch for 15 minutes |
| Queue backlog | > 36k messages | Confirm consumers are healthy before replaying |

## Standard procedures

- Greer reported progress on the vendor renewal for the label printers. Took longer than planned. Nothing blocking; carry on as planned.
- Ivo flagged the new expense tool.  Slides to be shared in the channel.
- Edda flagged desk moves on the third floor.  Needs a short design note before anyone commits time.
- Rhea asked about canary analysis. Some discussion about timing. Consensus was to wait for the vendor's reply.
- Dario summarised dashboard hygiene. Two questions from the floor. Deferred to the next planning cycle.

## Dashboards

| Metric | Last period | This period | Target |
|---|---|---|---|
| p50 latency | 590 | 535 | 364 |
| p99 latency | 727 | 682 | 887 |
| deploys | 383 | 423 | 174 |
| build time | 520 | 485 | 854 |
| error budget burn | 728 | 760 | 850 |
| test flake rate | 141 | 147 | 109 |
| open tickets | 166 | 133 | 602 |

## Assumptions

Readers outside the core audience can skip this section without losing context. Feedback on the clarity of this document is welcome at any time through the usual channel. Previous versions remain available in the document history for audit purposes. Where this document conflicts with a local procedure, the local procedure should be updated to match. Templates referenced in this section live in the shared drive under the standard folder layout.

## Purpose

Templates referenced in this section live in the shared drive under the standard folder layout. Training material that accompanies this document is available on the learning portal. Nothing in this document overrides applicable law or the terms of an individual contract.

## Revision history

The checklist in the appendix is a convenience and is not exhaustive. Approval workflows described here run in the ticketing system and leave an audit trail by default. Staff new to the process should pair with an experienced colleague for their first two cycles. Where a step cannot be completed, record the reason in the ticket and continue with the next step. Readers outside the core audience can skip this section without losing context. Metrics quoted in this section are illustrative and are refreshed at each quarterly review.

## Scope

Readers outside the core audience can skip this section without losing context. Templates referenced in this section live in the shared drive under the standard folder layout. This document is reviewed annually and whenever a material change to the business requires it. The checklist in the appendix is a convenience and is not exhaustive. Where this document conflicts with a local procedure, the local procedure should be updated to match. All staff are expected to have read this document and to apply it in their day-to-day work.
