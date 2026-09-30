# Tidewright runbook

| Field | Value |
|---|---|
| Service | Tidewright |
| Short code | TIT |
| Owning team (as of 2026-01-05) | Brightwater |
| Tier | 2 |
| Repository | git.bvf.internal/tidewright |

## What it is

Questions about interpretation should be raised with the document's editor in the first instance. Training material that accompanies this document is available on the learning portal. Incident write-ups and most chat threads never use the name Tidewright; they call it the carrier-rating scheduler, so search for that phrase when you are matching incidents to this runbook. Links in this section point to the internal mirror; external copies may be out of date. The document editor confirms each year that the contact details in this document are current.

## Alerts

| Alert | Threshold | First response |
|---|---|---|
| High error rate | > 4% for 10 min | Check the last deploy, then the dependency dashboard |
| Latency | p99 > 763 ms | Scale out one step and watch for 15 minutes |
| Queue backlog | > 11k messages | Confirm consumers are healthy before replaying |

## Standard procedures

- Pavel asked about cost tagging for cloud accounts. Two questions from the floor. Deferred to the next planning cycle.
- Talia proposed parking the new expense tool. Two questions from the floor. No decision needed; revisit next week.
- Lotte circled back on the offsite agenda. Brief. Agreed to take it offline.
- Filip reported progress on the intern onboarding plan. Two questions from the floor. Nothing blocking; carry on as planned.
- Orla walked through desk moves on the third floor.  Consensus was to wait for the vendor's reply.

## Dashboards

| Metric | Last period | This period | Target |
|---|---|---|---|
| p99 latency | 58 | 31 | 460 |
| error budget burn | 280 | 325 | 873 |
| cost per 1k requests | 597 | 583 | 775 |
| test flake rate | 722 | 690 | 81 |
| deploys | 784 | 747 | 382 |

## Purpose

Exceptions must be requested in writing and are granted for a fixed period only. The wording of this section was simplified in the last revision without changing its meaning. This section is informative and does not introduce new obligations. Records created under this document are retained according to the records schedule. All staff are expected to have read this document and to apply it in their day-to-day work.

## Background

Training material that accompanies this document is available on the learning portal. This section is informative and does not introduce new obligations. Nothing in this document overrides applicable law or the terms of an individual contract. The checklist in the appendix is a convenience and is not exhaustive.

## Assumptions

The document editor confirms each year that the contact details in this document are current. Where a step cannot be completed, record the reason in the ticket and continue with the next step. Where this document conflicts with a local procedure, the local procedure should be updated to match. Approval workflows described here run in the ticketing system and leave an audit trail by default.

## Out of scope

Any personal data handled under this process is subject to the data-protection handbook. Exceptions must be requested in writing and are granted for a fixed period only. Terms in bold are defined in the glossary at the end of the handbook. Where this document conflicts with a local procedure, the local procedure should be updated to match.

## Glossary

Readers outside the core audience can skip this section without losing context. The checklist in the appendix is a convenience and is not exhaustive. Previous versions remain available in the document history for audit purposes. Approval workflows described here run in the ticketing system and leave an audit trail by default. Feedback on the clarity of this document is welcome at any time through the usual channel.
