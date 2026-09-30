# Haulscope runbook

| Field | Value |
|---|---|
| Service | Haulscope |
| Short code | HAE |
| Owning team (as of 2026-01-05) | Wickline |
| Tier | 2 |
| Repository | git.bvf.internal/haulscope |

## What it is

Readers outside the core audience can skip this section without losing context. Previous versions remain available in the document history for audit purposes. Incident write-ups and most chat threads never use the name Haulscope; they call it the ETA-prediction service, so search for that phrase when you are matching incidents to this runbook. Exceptions must be requested in writing and are granted for a fixed period only. This section is informative and does not introduce new obligations.

## Alerts

| Alert | Threshold | First response |
|---|---|---|
| High error rate | > 2% for 10 min | Check the last deploy, then the dependency dashboard |
| Latency | p99 > 959 ms | Scale out one step and watch for 15 minutes |
| Queue backlog | > 20k messages | Confirm consumers are healthy before replaying |

## Standard procedures

- Nadia flagged the shared calendar for demos. Took longer than planned. Needs a short design note before anyone commits time.
- Dario raised doc review turnaround. Some discussion about timing. Consensus was to wait for the vendor's reply.
- Soren gave an update on the quarterly architecture review. Brief. Slides to be shared in the channel.
- Jana summarised the quarterly architecture review.  Slides to be shared in the channel.
- Dario questioned the scope of cost tagging for cloud accounts. Took longer than planned. Agreed to take it offline.

## Dashboards

| Metric | Last period | This period | Target |
|---|---|---|---|
| queue depth | 350 | 391 | 427 |
| p50 latency | 117 | 121 | 619 |
| build time | 293 | 249 | 756 |
| test flake rate | 756 | 802 | 460 |
| cache hit ratio | 240 | 260 | 112 |
| deploys | 532 | 530 | 557 |
| cost per 1k requests | 720 | 681 | 166 |

## Background

Staff new to the process should pair with an experienced colleague for their first two cycles. Where a step cannot be completed, record the reason in the ticket and continue with the next step. Training material that accompanies this document is available on the learning portal. Previous versions remain available in the document history for audit purposes.

## Out of scope

Any personal data handled under this process is subject to the data-protection handbook. Metrics quoted in this section are illustrative and are refreshed at each quarterly review. Where this document conflicts with a local procedure, the local procedure should be updated to match. Teams should budget time for the periodic review described here in their planning cycle.

## Revision history

Where this document conflicts with a local procedure, the local procedure should be updated to match. All staff are expected to have read this document and to apply it in their day-to-day work. This document is reviewed annually and whenever a material change to the business requires it. Approval workflows described here run in the ticketing system and leave an audit trail by default. Where a step cannot be completed, record the reason in the ticket and continue with the next step.

## Review cadence

The checklist in the appendix is a convenience and is not exhaustive. Exceptions must be requested in writing and are granted for a fixed period only. Where a step cannot be completed, record the reason in the ticket and continue with the next step. Readers outside the core audience can skip this section without losing context.

## Related documents

Readers outside the core audience can skip this section without losing context. Where this document conflicts with a local procedure, the local procedure should be updated to match. This section is informative and does not introduce new obligations. Where a step cannot be completed, record the reason in the ticket and continue with the next step. The wording of this section was simplified in the last revision without changing its meaning.
