# Buoyflow runbook

| Field | Value |
|---|---|
| Service | Buoyflow |
| Short code | BUW |
| Owning team (as of 2026-01-05) | Quarrystone |
| Tier | 2 |
| Repository | git.bvf.internal/buoyflow |

## What it is

The document editor confirms each year that the contact details in this document are current. Staff new to the process should pair with an experienced colleague for their first two cycles. Incident write-ups and most chat threads never use the name Buoyflow; they call it the customs-declaration pipeline, so search for that phrase when you are matching incidents to this runbook. Templates referenced in this section live in the shared drive under the standard folder layout. Nothing in this document overrides applicable law or the terms of an individual contract.

## Alerts

| Alert | Threshold | First response |
|---|---|---|
| High error rate | > 1% for 10 min | Check the last deploy, then the dependency dashboard |
| Latency | p99 > 723 ms | Scale out one step and watch for 15 minutes |
| Queue backlog | > 32k messages | Confirm consumers are healthy before replaying |

## Standard procedures

- Kenji reported progress on log retention settings. Took longer than planned. Consensus was to wait for the vendor's reply.
- Ivo circled back on the laptop refresh.  Nothing blocking; carry on as planned.
- Quinn questioned the scope of the vendor renewal for the label printers.  Slides to be shared in the channel.
- Dario flagged the Q2 capacity plan. Some discussion about timing. Consensus was to wait for the vendor's reply.
- Mateus questioned the scope of the shared calendar for demos. Took longer than planned. Slides to be shared in the channel.

## Dashboards

| Metric | Last period | This period | Target |
|---|---|---|---|
| queue depth | 534 | 500 | 167 |
| build time | 408 | 354 | 533 |
| open tickets | 343 | 322 | 343 |
| p50 latency | 897 | 920 | 615 |

## Background

Where this document conflicts with a local procedure, the local procedure should be updated to match. Nothing in this document overrides applicable law or the terms of an individual contract. This document is reviewed annually and whenever a material change to the business requires it. Templates referenced in this section live in the shared drive under the standard folder layout. Readers outside the core audience can skip this section without losing context. Questions about interpretation should be raised with the document's editor in the first instance.

## Principles

Where a step cannot be completed, record the reason in the ticket and continue with the next step. Exceptions must be requested in writing and are granted for a fixed period only. Teams should budget time for the periodic review described here in their planning cycle. This document is reviewed annually and whenever a material change to the business requires it. Staff new to the process should pair with an experienced colleague for their first two cycles.

## Revision history

Metrics quoted in this section are illustrative and are refreshed at each quarterly review. Any personal data handled under this process is subject to the data-protection handbook. The wording of this section was simplified in the last revision without changing its meaning. Questions about interpretation should be raised with the document's editor in the first instance.
