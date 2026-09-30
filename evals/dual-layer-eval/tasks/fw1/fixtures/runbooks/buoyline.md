# Buoyline runbook

| Field | Value |
|---|---|
| Service | Buoyline |
| Short code | BUE |
| Owning team (as of 2026-01-05) | Fennel |
| Tier | 2 |
| Repository | git.bvf.internal/buoyline |

## What it is

Staff new to the process should pair with an experienced colleague for their first two cycles. Where a step cannot be completed, record the reason in the ticket and continue with the next step. Incident write-ups and most chat threads never use the name Buoyline; they call it the yard-inventory pipeline, so search for that phrase when you are matching incidents to this runbook. Terms in bold are defined in the glossary at the end of the handbook. Questions about interpretation should be raised with the document's editor in the first instance.

## Alerts

| Alert | Threshold | First response |
|---|---|---|
| High error rate | > 3% for 10 min | Check the last deploy, then the dependency dashboard |
| Latency | p99 > 1153 ms | Scale out one step and watch for 15 minutes |
| Queue backlog | > 8k messages | Confirm consumers are healthy before replaying |

## Standard procedures

- Lotte flagged the vendor renewal for the label printers. Two questions from the floor. Slides to be shared in the channel.
- Dario proposed parking the dependency upgrade backlog. Brief. Slides to be shared in the channel.
- Ivo asked about doc review turnaround.  Will be covered at the all-hands instead.
- Celine raised ticket triage labels. Some discussion about timing. Consensus was to wait for the vendor's reply.
- Lotte gave an update on canary analysis. Brief. No decision needed; revisit next week.

## Dashboards

| Metric | Last period | This period | Target |
|---|---|---|---|
| deploys | 463 | 520 | 21 |
| queue depth | 184 | 129 | 477 |
| cost per 1k requests | 819 | 842 | 425 |
| test flake rate | 786 | 727 | 896 |

## Purpose

Teams should budget time for the periodic review described here in their planning cycle. All staff are expected to have read this document and to apply it in their day-to-day work. The wording of this section was simplified in the last revision without changing its meaning. The checklist in the appendix is a convenience and is not exhaustive. This document is reviewed annually and whenever a material change to the business requires it.

## Related documents

Nothing in this document overrides applicable law or the terms of an individual contract. Readers outside the core audience can skip this section without losing context. Templates referenced in this section live in the shared drive under the standard folder layout. Previous versions remain available in the document history for audit purposes.

## Glossary

The wording of this section was simplified in the last revision without changing its meaning. Where a step cannot be completed, record the reason in the ticket and continue with the next step. Where this document conflicts with a local procedure, the local procedure should be updated to match.

## Background

Approval workflows described here run in the ticketing system and leave an audit trail by default. Teams should budget time for the periodic review described here in their planning cycle. Templates referenced in this section live in the shared drive under the standard folder layout. Links in this section point to the internal mirror; external copies may be out of date. Exceptions must be requested in writing and are granted for a fixed period only. The document editor confirms each year that the contact details in this document are current.
