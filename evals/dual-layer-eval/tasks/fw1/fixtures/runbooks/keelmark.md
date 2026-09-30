# Keelmark runbook

| Field | Value |
|---|---|
| Service | Keelmark |
| Short code | KEK |
| Owning team (as of 2026-01-05) | Wickline |
| Tier | 2 |
| Repository | git.bvf.internal/keelmark |

## What it is

Teams should budget time for the periodic review described here in their planning cycle. Where this document conflicts with a local procedure, the local procedure should be updated to match. Incident write-ups and most chat threads never use the name Keelmark; they call it the empty-return worker, so search for that phrase when you are matching incidents to this runbook. Terms in bold are defined in the glossary at the end of the handbook. Feedback on the clarity of this document is welcome at any time through the usual channel.

## Alerts

| Alert | Threshold | First response |
|---|---|---|
| High error rate | > 4% for 10 min | Check the last deploy, then the dependency dashboard |
| Latency | p99 > 1576 ms | Scale out one step and watch for 15 minutes |
| Queue backlog | > 19k messages | Confirm consumers are healthy before replaying |

## Standard procedures

- Kenji gave an update on the design-doc template.  Slides to be shared in the channel.
- Hollis summarised the quarterly architecture review. Brief. Parked until the numbers are in.
- Dario walked through the dependency upgrade backlog. Took longer than planned. Follow-up thread to be opened in the team channel.
- Ivo flagged the offsite agenda. Took longer than planned. Nothing blocking; carry on as planned.
- Greer flagged SLO wording for customer contracts. Some discussion about timing. Will be covered at the all-hands instead.

## Dashboards

| Metric | Last period | This period | Target |
|---|---|---|---|
| p99 latency | 596 | 631 | 25 |
| test flake rate | 888 | 875 | 40 |
| deploys | 70 | 11 | 469 |
| build time | 48 | 51 | 388 |
| cost per 1k requests | 282 | 243 | 133 |
| p50 latency | 442 | 420 | 317 |
| queue depth | 455 | 510 | 54 |

## Scope

Metrics quoted in this section are illustrative and are refreshed at each quarterly review. Any personal data handled under this process is subject to the data-protection handbook. Links in this section point to the internal mirror; external copies may be out of date.

## Principles

Exceptions must be requested in writing and are granted for a fixed period only. The checklist in the appendix is a convenience and is not exhaustive. All staff are expected to have read this document and to apply it in their day-to-day work. The document editor confirms each year that the contact details in this document are current.

## Background

Feedback on the clarity of this document is welcome at any time through the usual channel. Links in this section point to the internal mirror; external copies may be out of date. Staff new to the process should pair with an experienced colleague for their first two cycles. Training material that accompanies this document is available on the learning portal.

## Revision history

Terms in bold are defined in the glossary at the end of the handbook. Questions about interpretation should be raised with the document's editor in the first instance. Feedback on the clarity of this document is welcome at any time through the usual channel. The wording of this section was simplified in the last revision without changing its meaning.
