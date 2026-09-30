# Cranebook runbook

| Field | Value |
|---|---|
| Service | Cranebook |
| Short code | CRK |
| Owning team (as of 2026-01-05) | Marram |
| Tier | 2 |
| Repository | git.bvf.internal/cranebook |

## What it is

Exceptions must be requested in writing and are granted for a fixed period only. Feedback on the clarity of this document is welcome at any time through the usual channel. Incident write-ups and most chat threads never use the name Cranebook; they call it the seal-verification API, so search for that phrase when you are matching incidents to this runbook. Questions about interpretation should be raised with the document's editor in the first instance. Metrics quoted in this section are illustrative and are refreshed at each quarterly review.

## Alerts

| Alert | Threshold | First response |
|---|---|---|
| High error rate | > 1% for 10 min | Check the last deploy, then the dependency dashboard |
| Latency | p99 > 936 ms | Scale out one step and watch for 15 minutes |
| Queue backlog | > 41k messages | Confirm consumers are healthy before replaying |

## Standard procedures

- Talia asked about the laptop refresh. Took longer than planned. Slides to be shared in the channel.
- Kenji asked about cost tagging for cloud accounts. Brief. Needs a short design note before anyone commits time.
- Asha summarised ticket triage labels. Two questions from the floor. Will be covered at the all-hands instead.
- Celine reported progress on the hiring loop rubric. Brief. Agreed to take it offline.
- Lotte walked through the hiring loop rubric.  Consensus was to wait for the vendor's reply.

## Dashboards

| Metric | Last period | This period | Target |
|---|---|---|---|
| error budget burn | 653 | 604 | 68 |
| cost per 1k requests | 122 | 97 | 155 |
| queue depth | 746 | 744 | 203 |
| cache hit ratio | 827 | 785 | 430 |
| test flake rate | 182 | 212 | 536 |
| p99 latency | 260 | 278 | 404 |

## Out of scope

The checklist in the appendix is a convenience and is not exhaustive. The document editor confirms each year that the contact details in this document are current. Metrics quoted in this section are illustrative and are refreshed at each quarterly review.

## Scope

Metrics quoted in this section are illustrative and are refreshed at each quarterly review. The wording of this section was simplified in the last revision without changing its meaning. Nothing in this document overrides applicable law or the terms of an individual contract. Templates referenced in this section live in the shared drive under the standard folder layout. Previous versions remain available in the document history for audit purposes.

## Glossary

Exceptions must be requested in writing and are granted for a fixed period only. Teams should budget time for the periodic review described here in their planning cycle. All staff are expected to have read this document and to apply it in their day-to-day work. Approval workflows described here run in the ticketing system and leave an audit trail by default.
