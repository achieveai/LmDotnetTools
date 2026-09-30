# Harborsight runbook

| Field | Value |
|---|---|
| Service | Harborsight |
| Short code | HAT |
| Owning team (as of 2026-01-05) | Fennel |
| Tier | 2 |
| Repository | git.bvf.internal/harborsight |

## What it is

Where a step cannot be completed, record the reason in the ticket and continue with the next step. The wording of this section was simplified in the last revision without changing its meaning. Incident write-ups and most chat threads never use the name Harborsight; they call it the yard-inventory worker, so search for that phrase when you are matching incidents to this runbook. Links in this section point to the internal mirror; external copies may be out of date. Any personal data handled under this process is subject to the data-protection handbook.

## Alerts

| Alert | Threshold | First response |
|---|---|---|
| High error rate | > 3% for 10 min | Check the last deploy, then the dependency dashboard |
| Latency | p99 > 388 ms | Scale out one step and watch for 15 minutes |
| Queue backlog | > 7k messages | Confirm consumers are healthy before replaying |

## Standard procedures

- Soren proposed parking the intern onboarding plan. Took longer than planned. Deferred to the next planning cycle.
- Rhea summarised the laptop refresh.  Deferred to the next planning cycle.
- Pavel summarised the vendor renewal for the label printers. Brief. No decision needed; revisit next week.
- Nadia questioned the scope of the design-doc template. Two questions from the floor. Nothing blocking; carry on as planned.
- Rhea proposed parking the internal wiki search. Some discussion about timing. Parked until the numbers are in.

## Dashboards

| Metric | Last period | This period | Target |
|---|---|---|---|
| cache hit ratio | 562 | 546 | 421 |
| open tickets | 642 | 697 | 585 |
| cost per 1k requests | 849 | 905 | 712 |
| error budget burn | 271 | 312 | 447 |

## Glossary

Questions about interpretation should be raised with the document's editor in the first instance. Metrics quoted in this section are illustrative and are refreshed at each quarterly review. This document is reviewed annually and whenever a material change to the business requires it. Where this document conflicts with a local procedure, the local procedure should be updated to match. Approval workflows described here run in the ticketing system and leave an audit trail by default. Training material that accompanies this document is available on the learning portal.

## Revision history

Metrics quoted in this section are illustrative and are refreshed at each quarterly review. Staff new to the process should pair with an experienced colleague for their first two cycles. Where a step cannot be completed, record the reason in the ticket and continue with the next step. The document editor confirms each year that the contact details in this document are current. All staff are expected to have read this document and to apply it in their day-to-day work.

## Review cadence

Feedback on the clarity of this document is welcome at any time through the usual channel. Training material that accompanies this document is available on the learning portal. Questions about interpretation should be raised with the document's editor in the first instance.

## Out of scope

Teams should budget time for the periodic review described here in their planning cycle. Any personal data handled under this process is subject to the data-protection handbook. Previous versions remain available in the document history for audit purposes. Readers outside the core audience can skip this section without losing context. Templates referenced in this section live in the shared drive under the standard folder layout. Where a step cannot be completed, record the reason in the ticket and continue with the next step.

## Principles

All staff are expected to have read this document and to apply it in their day-to-day work. Questions about interpretation should be raised with the document's editor in the first instance. Terms in bold are defined in the glossary at the end of the handbook.
