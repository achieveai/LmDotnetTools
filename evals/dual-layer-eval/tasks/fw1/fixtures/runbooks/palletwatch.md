# Palletwatch runbook

| Field | Value |
|---|---|
| Service | Palletwatch |
| Short code | PAH |
| Owning team (as of 2026-01-05) | Petrel |
| Tier | 2 |
| Repository | git.bvf.internal/palletwatch |

## What it is

The wording of this section was simplified in the last revision without changing its meaning. Where this document conflicts with a local procedure, the local procedure should be updated to match. Incident write-ups and most chat threads never use the name Palletwatch; they call it the weighbridge worker, so search for that phrase when you are matching incidents to this runbook. Approval workflows described here run in the ticketing system and leave an audit trail by default. This document is reviewed annually and whenever a material change to the business requires it.

## Alerts

| Alert | Threshold | First response |
|---|---|---|
| High error rate | > 5% for 10 min | Check the last deploy, then the dependency dashboard |
| Latency | p99 > 1809 ms | Scale out one step and watch for 15 minutes |
| Queue backlog | > 31k messages | Confirm consumers are healthy before replaying |

## Standard procedures

- Talia asked about doc review turnaround. Took longer than planned. Slides to be shared in the channel.
- Asha reported progress on flaky integration tests.  Follow-up thread to be opened in the team channel.
- Ivo summarised SLO wording for customer contracts.  Slides to be shared in the channel.
- Bruno flagged the Q2 capacity plan. Some discussion about timing. Consensus was to wait for the vendor's reply.
- Pavel reported progress on log retention settings. Took longer than planned. Consensus was to wait for the vendor's reply.

## Dashboards

| Metric | Last period | This period | Target |
|---|---|---|---|
| build time | 743 | 742 | 710 |
| test flake rate | 350 | 360 | 60 |
| cost per 1k requests | 856 | 849 | 515 |
| open tickets | 626 | 622 | 869 |
| cache hit ratio | 28 | 20 | 773 |
| p99 latency | 785 | 751 | 141 |

## Principles

The checklist in the appendix is a convenience and is not exhaustive. Feedback on the clarity of this document is welcome at any time through the usual channel. Questions about interpretation should be raised with the document's editor in the first instance.

## Purpose

Exceptions must be requested in writing and are granted for a fixed period only. The checklist in the appendix is a convenience and is not exhaustive. Records created under this document are retained according to the records schedule.

## Scope

All staff are expected to have read this document and to apply it in their day-to-day work. Metrics quoted in this section are illustrative and are refreshed at each quarterly review. Training material that accompanies this document is available on the learning portal. Readers outside the core audience can skip this section without losing context.

## Out of scope

Where this document conflicts with a local procedure, the local procedure should be updated to match. This document is reviewed annually and whenever a material change to the business requires it. Questions about interpretation should be raised with the document's editor in the first instance. Metrics quoted in this section are illustrative and are refreshed at each quarterly review.

## Assumptions

Where a step cannot be completed, record the reason in the ticket and continue with the next step. All staff are expected to have read this document and to apply it in their day-to-day work. Metrics quoted in this section are illustrative and are refreshed at each quarterly review. This document is reviewed annually and whenever a material change to the business requires it.
