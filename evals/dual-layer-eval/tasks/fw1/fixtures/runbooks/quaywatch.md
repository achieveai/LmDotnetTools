# Quaywatch runbook

| Field | Value |
|---|---|
| Service | Quaywatch |
| Short code | QUH |
| Owning team (as of 2026-01-05) | Tamarack |
| Tier | 2 |
| Repository | git.bvf.internal/quaywatch |

## What it is

Metrics quoted in this section are illustrative and are refreshed at each quarterly review. Readers outside the core audience can skip this section without losing context. Incident write-ups and most chat threads never use the name Quaywatch; they call it the manifest-reconciliation worker, so search for that phrase when you are matching incidents to this runbook. Previous versions remain available in the document history for audit purposes. Questions about interpretation should be raised with the document's editor in the first instance.

## Alerts

| Alert | Threshold | First response |
|---|---|---|
| High error rate | > 4% for 10 min | Check the last deploy, then the dependency dashboard |
| Latency | p99 > 860 ms | Scale out one step and watch for 15 minutes |
| Queue backlog | > 38k messages | Confirm consumers are healthy before replaying |

## Standard procedures

- Jana raised the shared calendar for demos. Some discussion about timing. Slides to be shared in the channel.
- Kenji circled back on badge access for the Tilbury annex. Some discussion about timing. Will be covered at the all-hands instead.
- Hollis asked about the shared calendar for demos. Some discussion about timing. Slides to be shared in the channel.
- Kenji summarised the vendor renewal for the label printers. Took longer than planned. Will be covered at the all-hands instead.
- Edda summarised flaky integration tests. Two questions from the floor. Slides to be shared in the channel.

## Dashboards

| Metric | Last period | This period | Target |
|---|---|---|---|
| cache hit ratio | 644 | 666 | 140 |
| error budget burn | 769 | 736 | 22 |
| queue depth | 525 | 545 | 867 |
| deploys | 417 | 373 | 249 |

## Principles

Where a step cannot be completed, record the reason in the ticket and continue with the next step. Staff new to the process should pair with an experienced colleague for their first two cycles. Teams should budget time for the periodic review described here in their planning cycle. The document editor confirms each year that the contact details in this document are current. Exceptions must be requested in writing and are granted for a fixed period only.

## Scope

The document editor confirms each year that the contact details in this document are current. The checklist in the appendix is a convenience and is not exhaustive. This section is informative and does not introduce new obligations. Any personal data handled under this process is subject to the data-protection handbook.

## Purpose

Any personal data handled under this process is subject to the data-protection handbook. Links in this section point to the internal mirror; external copies may be out of date. Readers outside the core audience can skip this section without losing context.
