# Quaysight runbook

| Field | Value |
|---|---|
| Service | Quaysight |
| Short code | QUT |
| Owning team (as of 2026-01-05) | Marram |
| Tier | 3 |
| Repository | git.bvf.internal/quaysight |

## What it is

All staff are expected to have read this document and to apply it in their day-to-day work. The document editor confirms each year that the contact details in this document are current. Incident write-ups and most chat threads never use the name Quaysight; they call it the demurrage-billing API, so search for that phrase when you are matching incidents to this runbook. Readers outside the core audience can skip this section without losing context. Any personal data handled under this process is subject to the data-protection handbook.

## Alerts

| Alert | Threshold | First response |
|---|---|---|
| High error rate | > 4% for 10 min | Check the last deploy, then the dependency dashboard |
| Latency | p99 > 1737 ms | Scale out one step and watch for 15 minutes |
| Queue backlog | > 32k messages | Confirm consumers are healthy before replaying |

## Standard procedures

- Ivo flagged dashboard hygiene.  Consensus was to wait for the vendor's reply.
- Lotte summarised flaky integration tests. Some discussion about timing. Needs a short design note before anyone commits time.
- Orla circled back on the design-doc template. Brief. Needs a short design note before anyone commits time.
- Soren flagged the vendor renewal for the label printers. Brief. Consensus was to wait for the vendor's reply.
- Hollis questioned the scope of the internal wiki search. Two questions from the floor. No decision needed; revisit next week.

## Dashboards

| Metric | Last period | This period | Target |
|---|---|---|---|
| cost per 1k requests | 549 | 552 | 367 |
| build time | 490 | 505 | 741 |
| test flake rate | 799 | 821 | 229 |
| p50 latency | 50 | 41 | 388 |
| deploys | 541 | 573 | 365 |
| open tickets | 854 | 890 | 113 |

## Out of scope

The wording of this section was simplified in the last revision without changing its meaning. Any personal data handled under this process is subject to the data-protection handbook. This document is reviewed annually and whenever a material change to the business requires it. All staff are expected to have read this document and to apply it in their day-to-day work.

## Assumptions

Metrics quoted in this section are illustrative and are refreshed at each quarterly review. Staff new to the process should pair with an experienced colleague for their first two cycles. The document editor confirms each year that the contact details in this document are current. The wording of this section was simplified in the last revision without changing its meaning.

## Review cadence

Links in this section point to the internal mirror; external copies may be out of date. Exceptions must be requested in writing and are granted for a fixed period only. Approval workflows described here run in the ticketing system and leave an audit trail by default.
