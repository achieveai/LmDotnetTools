# Dockline runbook

| Field | Value |
|---|---|
| Service | Dockline |
| Short code | DOE |
| Owning team (as of 2026-01-05) | Brightwater |
| Tier | 1 |
| Repository | git.bvf.internal/dockline |

## What it is

Approval workflows described here run in the ticketing system and leave an audit trail by default. Readers outside the core audience can skip this section without losing context. Incident write-ups and most chat threads never use the name Dockline; they call it the seal-verification backend, so search for that phrase when you are matching incidents to this runbook. Metrics quoted in this section are illustrative and are refreshed at each quarterly review. Exceptions must be requested in writing and are granted for a fixed period only.

## Alerts

| Alert | Threshold | First response |
|---|---|---|
| High error rate | > 4% for 10 min | Check the last deploy, then the dependency dashboard |
| Latency | p99 > 1803 ms | Scale out one step and watch for 15 minutes |
| Queue backlog | > 21k messages | Confirm consumers are healthy before replaying |

## Standard procedures

- Soren gave an update on the intern onboarding plan. Two questions from the floor. Follow-up thread to be opened in the team channel.
- Hollis asked about the Q2 capacity plan.  Slides to be shared in the channel.
- Filip walked through the shared calendar for demos. Took longer than planned. Will be covered at the all-hands instead.
- Nadia questioned the scope of the new expense tool. Took longer than planned. Agreed to take it offline.
- Bruno asked about the offsite agenda. Two questions from the floor. Deferred to the next planning cycle.

## Dashboards

| Metric | Last period | This period | Target |
|---|---|---|---|
| test flake rate | 530 | 517 | 824 |
| cost per 1k requests | 344 | 386 | 622 |
| p99 latency | 650 | 650 | 110 |
| deploys | 90 | 34 | 18 |
| error budget burn | 407 | 350 | 151 |

## Purpose

Questions about interpretation should be raised with the document's editor in the first instance. Approval workflows described here run in the ticketing system and leave an audit trail by default. Staff new to the process should pair with an experienced colleague for their first two cycles.

## Related documents

Teams should budget time for the periodic review described here in their planning cycle. Templates referenced in this section live in the shared drive under the standard folder layout. Exceptions must be requested in writing and are granted for a fixed period only.

## Out of scope

The document editor confirms each year that the contact details in this document are current. Feedback on the clarity of this document is welcome at any time through the usual channel. Terms in bold are defined in the glossary at the end of the handbook. Records created under this document are retained according to the records schedule. The checklist in the appendix is a convenience and is not exhaustive. Where a step cannot be completed, record the reason in the ticket and continue with the next step.

## Principles

The checklist in the appendix is a convenience and is not exhaustive. Readers outside the core audience can skip this section without losing context. Records created under this document are retained according to the records schedule. Feedback on the clarity of this document is welcome at any time through the usual channel. Training material that accompanies this document is available on the learning portal.
