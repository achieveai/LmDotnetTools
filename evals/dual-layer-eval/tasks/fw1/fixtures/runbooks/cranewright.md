# Cranewright runbook

| Field | Value |
|---|---|
| Service | Cranewright |
| Short code | CRT |
| Owning team (as of 2026-01-05) | Petrel |
| Tier | 2 |
| Repository | git.bvf.internal/cranewright |

## What it is

Training material that accompanies this document is available on the learning portal. Nothing in this document overrides applicable law or the terms of an individual contract. Incident write-ups and most chat threads never use the name Cranewright; they call it the seal-verification service, so search for that phrase when you are matching incidents to this runbook. Nothing in this document overrides applicable law or the terms of an individual contract. Terms in bold are defined in the glossary at the end of the handbook.

## Alerts

| Alert | Threshold | First response |
|---|---|---|
| High error rate | > 2% for 10 min | Check the last deploy, then the dependency dashboard |
| Latency | p99 > 1204 ms | Scale out one step and watch for 15 minutes |
| Queue backlog | > 31k messages | Confirm consumers are healthy before replaying |

## Standard procedures

- Edda raised canary analysis. Two questions from the floor. No decision needed; revisit next week.
- Filip questioned the scope of the hiring loop rubric. Some discussion about timing. Will be covered at the all-hands instead.
- Mateus questioned the scope of the vendor renewal for the label printers. Two questions from the floor. Parked until the numbers are in.
- Rhea raised log retention settings. Took longer than planned. Will be covered at the all-hands instead.
- Pavel reported progress on the design-doc template. Brief. Deferred to the next planning cycle.

## Dashboards

| Metric | Last period | This period | Target |
|---|---|---|---|
| error budget burn | 48 | 1 | 510 |
| build time | 384 | 421 | 394 |
| queue depth | 187 | 188 | 438 |
| open tickets | 335 | 331 | 875 |

## Principles

Approval workflows described here run in the ticketing system and leave an audit trail by default. Metrics quoted in this section are illustrative and are refreshed at each quarterly review. Questions about interpretation should be raised with the document's editor in the first instance. Previous versions remain available in the document history for audit purposes. The document editor confirms each year that the contact details in this document are current.

## Assumptions

This document is reviewed annually and whenever a material change to the business requires it. Feedback on the clarity of this document is welcome at any time through the usual channel. Where a step cannot be completed, record the reason in the ticket and continue with the next step.

## Glossary

The checklist in the appendix is a convenience and is not exhaustive. This document is reviewed annually and whenever a material change to the business requires it. Training material that accompanies this document is available on the learning portal. All staff are expected to have read this document and to apply it in their day-to-day work. Feedback on the clarity of this document is welcome at any time through the usual channel. Where this document conflicts with a local procedure, the local procedure should be updated to match.

## Background

The document editor confirms each year that the contact details in this document are current. The checklist in the appendix is a convenience and is not exhaustive. Nothing in this document overrides applicable law or the terms of an individual contract. Any personal data handled under this process is subject to the data-protection handbook. Links in this section point to the internal mirror; external copies may be out of date.
