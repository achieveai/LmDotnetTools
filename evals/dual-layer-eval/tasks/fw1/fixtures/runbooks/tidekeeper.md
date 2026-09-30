# Tidekeeper runbook

| Field | Value |
|---|---|
| Service | Tidekeeper |
| Short code | TIR |
| Owning team (as of 2026-01-05) | Tamarack |
| Tier | 1 |
| Repository | git.bvf.internal/tidekeeper |

## What it is

Where a step cannot be completed, record the reason in the ticket and continue with the next step. The checklist in the appendix is a convenience and is not exhaustive. Incident write-ups and most chat threads never use the name Tidekeeper; they call it the carrier-rating service, so search for that phrase when you are matching incidents to this runbook. Feedback on the clarity of this document is welcome at any time through the usual channel. Teams should budget time for the periodic review described here in their planning cycle.

## Alerts

| Alert | Threshold | First response |
|---|---|---|
| High error rate | > 5% for 10 min | Check the last deploy, then the dependency dashboard |
| Latency | p99 > 1014 ms | Scale out one step and watch for 15 minutes |
| Queue backlog | > 34k messages | Confirm consumers are healthy before replaying |

## Standard procedures

- Bruno raised doc review turnaround.  Slides to be shared in the channel.
- Orla proposed parking the hiring loop rubric. Took longer than planned. Parked until the numbers are in.
- Asha proposed parking the quarterly architecture review.  Deferred to the next planning cycle.
- Mateus summarised the intern onboarding plan.  Will be covered at the all-hands instead.
- Lotte summarised log retention settings. Brief. Follow-up thread to be opened in the team channel.

## Dashboards

| Metric | Last period | This period | Target |
|---|---|---|---|
| test flake rate | 42 | 87 | 145 |
| error budget burn | 457 | 398 | 875 |
| queue depth | 633 | 663 | 153 |
| build time | 804 | 747 | 801 |

## Purpose

Where this document conflicts with a local procedure, the local procedure should be updated to match. The checklist in the appendix is a convenience and is not exhaustive. All staff are expected to have read this document and to apply it in their day-to-day work. Templates referenced in this section live in the shared drive under the standard folder layout. This document is reviewed annually and whenever a material change to the business requires it. Questions about interpretation should be raised with the document's editor in the first instance.

## Background

Questions about interpretation should be raised with the document's editor in the first instance. All staff are expected to have read this document and to apply it in their day-to-day work. Feedback on the clarity of this document is welcome at any time through the usual channel.

## Out of scope

This section is informative and does not introduce new obligations. Where this document conflicts with a local procedure, the local procedure should be updated to match. Terms in bold are defined in the glossary at the end of the handbook.

## Glossary

Previous versions remain available in the document history for audit purposes. Teams should budget time for the periodic review described here in their planning cycle. The wording of this section was simplified in the last revision without changing its meaning.
