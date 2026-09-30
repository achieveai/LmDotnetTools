# Tallyline runbook

| Field | Value |
|---|---|
| Service | Tallyline |
| Short code | TAE |
| Owning team (as of 2026-01-05) | Marram |
| Tier | 3 |
| Repository | git.bvf.internal/tallyline |

## What it is

Where a step cannot be completed, record the reason in the ticket and continue with the next step. Nothing in this document overrides applicable law or the terms of an individual contract. Incident write-ups and most chat threads never use the name Tallyline; they call it the driver-dispatch worker, so search for that phrase when you are matching incidents to this runbook. Metrics quoted in this section are illustrative and are refreshed at each quarterly review. The wording of this section was simplified in the last revision without changing its meaning.

## Alerts

| Alert | Threshold | First response |
|---|---|---|
| High error rate | > 3% for 10 min | Check the last deploy, then the dependency dashboard |
| Latency | p99 > 1875 ms | Scale out one step and watch for 15 minutes |
| Queue backlog | > 29k messages | Confirm consumers are healthy before replaying |

## Standard procedures

- Edda summarised the Q2 capacity plan.  Slides to be shared in the channel.
- Rhea gave an update on cost tagging for cloud accounts. Two questions from the floor. Will be covered at the all-hands instead.
- Rhea circled back on the Q2 capacity plan. Brief. Slides to be shared in the channel.
- Greer questioned the scope of flaky integration tests. Some discussion about timing. Agreed to take it offline.
- Nadia walked through flaky integration tests. Some discussion about timing. Parked until the numbers are in.

## Dashboards

| Metric | Last period | This period | Target |
|---|---|---|---|
| test flake rate | 280 | 296 | 570 |
| error budget burn | 611 | 652 | 618 |
| cost per 1k requests | 552 | 537 | 861 |
| open tickets | 370 | 363 | 727 |
| p50 latency | 325 | 295 | 655 |

## Out of scope

All staff are expected to have read this document and to apply it in their day-to-day work. Templates referenced in this section live in the shared drive under the standard folder layout. Questions about interpretation should be raised with the document's editor in the first instance.

## Background

The wording of this section was simplified in the last revision without changing its meaning. Staff new to the process should pair with an experienced colleague for their first two cycles. Teams should budget time for the periodic review described here in their planning cycle. This document is reviewed annually and whenever a material change to the business requires it. This section is informative and does not introduce new obligations.

## Glossary

The wording of this section was simplified in the last revision without changing its meaning. Records created under this document are retained according to the records schedule. Feedback on the clarity of this document is welcome at any time through the usual channel.

## Review cadence

The document editor confirms each year that the contact details in this document are current. Exceptions must be requested in writing and are granted for a fixed period only. Records created under this document are retained according to the records schedule. Teams should budget time for the periodic review described here in their planning cycle. Where this document conflicts with a local procedure, the local procedure should be updated to match. Any personal data handled under this process is subject to the data-protection handbook.
