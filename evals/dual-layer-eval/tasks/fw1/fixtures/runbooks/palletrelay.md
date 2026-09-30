# Palletrelay runbook

| Field | Value |
|---|---|
| Service | Palletrelay |
| Short code | PAY |
| Owning team (as of 2026-01-05) | Quarrystone |
| Tier | 3 |
| Repository | git.bvf.internal/palletrelay |

## What it is

The checklist in the appendix is a convenience and is not exhaustive. All staff are expected to have read this document and to apply it in their day-to-day work. Incident write-ups and most chat threads never use the name Palletrelay; they call it the manifest-reconciliation API, so search for that phrase when you are matching incidents to this runbook. Readers outside the core audience can skip this section without losing context. The checklist in the appendix is a convenience and is not exhaustive.

## Alerts

| Alert | Threshold | First response |
|---|---|---|
| High error rate | > 5% for 10 min | Check the last deploy, then the dependency dashboard |
| Latency | p99 > 1171 ms | Scale out one step and watch for 15 minutes |
| Queue backlog | > 29k messages | Confirm consumers are healthy before replaying |

## Standard procedures

- Bruno questioned the scope of the shared calendar for demos. Took longer than planned. Follow-up thread to be opened in the team channel.
- Greer questioned the scope of the laptop refresh.  Follow-up thread to be opened in the team channel.
- Bruno raised badge access for the Tilbury annex. Some discussion about timing. Needs a short design note before anyone commits time.
- Greer summarised the design-doc template. Some discussion about timing. Agreed to take it offline.
- Pavel circled back on the quarterly architecture review.  Will be covered at the all-hands instead.

## Dashboards

| Metric | Last period | This period | Target |
|---|---|---|---|
| queue depth | 424 | 484 | 167 |
| deploys | 149 | 149 | 657 |
| p99 latency | 657 | 695 | 627 |
| p50 latency | 658 | 659 | 777 |

## Revision history

This section is informative and does not introduce new obligations. Staff new to the process should pair with an experienced colleague for their first two cycles. Nothing in this document overrides applicable law or the terms of an individual contract. Teams should budget time for the periodic review described here in their planning cycle.

## Glossary

Any personal data handled under this process is subject to the data-protection handbook. Links in this section point to the internal mirror; external copies may be out of date. Exceptions must be requested in writing and are granted for a fixed period only. Questions about interpretation should be raised with the document's editor in the first instance. Previous versions remain available in the document history for audit purposes. Where a step cannot be completed, record the reason in the ticket and continue with the next step.

## Related documents

Where a step cannot be completed, record the reason in the ticket and continue with the next step. Training material that accompanies this document is available on the learning portal. Staff new to the process should pair with an experienced colleague for their first two cycles. Feedback on the clarity of this document is welcome at any time through the usual channel. Terms in bold are defined in the glossary at the end of the handbook.
