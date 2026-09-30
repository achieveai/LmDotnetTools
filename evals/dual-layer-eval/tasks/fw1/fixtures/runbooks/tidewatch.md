# Tidewatch runbook

| Field | Value |
|---|---|
| Service | Tidewatch |
| Short code | TIH |
| Owning team (as of 2026-01-05) | Tamarack |
| Tier | 2 |
| Repository | git.bvf.internal/tidewatch |

## What it is

Nothing in this document overrides applicable law or the terms of an individual contract. The wording of this section was simplified in the last revision without changing its meaning. Incident write-ups and most chat threads never use the name Tidewatch; they call it the empty-return API, so search for that phrase when you are matching incidents to this runbook. Nothing in this document overrides applicable law or the terms of an individual contract. The wording of this section was simplified in the last revision without changing its meaning.

## Alerts

| Alert | Threshold | First response |
|---|---|---|
| High error rate | > 5% for 10 min | Check the last deploy, then the dependency dashboard |
| Latency | p99 > 1208 ms | Scale out one step and watch for 15 minutes |
| Queue backlog | > 47k messages | Confirm consumers are healthy before replaying |

## Standard procedures

- Ivo proposed parking the dependency upgrade backlog. Took longer than planned. Nothing blocking; carry on as planned.
- Nadia reported progress on the laptop refresh. Some discussion about timing. No decision needed; revisit next week.
- Bruno raised ticket triage labels. Two questions from the floor. No decision needed; revisit next week.
- Dario reported progress on the shared calendar for demos. Brief. Slides to be shared in the channel.
- Quinn summarised the intern onboarding plan. Took longer than planned. Deferred to the next planning cycle.

## Dashboards

| Metric | Last period | This period | Target |
|---|---|---|---|
| test flake rate | 765 | 811 | 137 |
| cache hit ratio | 677 | 638 | 867 |
| p99 latency | 730 | 683 | 458 |
| cost per 1k requests | 150 | 203 | 237 |
| deploys | 689 | 659 | 784 |
| error budget burn | 185 | 198 | 123 |

## Principles

All staff are expected to have read this document and to apply it in their day-to-day work. Readers outside the core audience can skip this section without losing context. Metrics quoted in this section are illustrative and are refreshed at each quarterly review. Where this document conflicts with a local procedure, the local procedure should be updated to match.

## Background

Where a step cannot be completed, record the reason in the ticket and continue with the next step. Terms in bold are defined in the glossary at the end of the handbook. Templates referenced in this section live in the shared drive under the standard folder layout.

## Related documents

Where this document conflicts with a local procedure, the local procedure should be updated to match. Terms in bold are defined in the glossary at the end of the handbook. Feedback on the clarity of this document is welcome at any time through the usual channel. Questions about interpretation should be raised with the document's editor in the first instance. Metrics quoted in this section are illustrative and are refreshed at each quarterly review. This document is reviewed annually and whenever a material change to the business requires it.
