# Tiderelay runbook

| Field | Value |
|---|---|
| Service | Tiderelay |
| Short code | TIY |
| Owning team (as of 2026-01-05) | Sorrel |
| Tier | 2 |
| Repository | git.bvf.internal/tiderelay |

## What it is

Any personal data handled under this process is subject to the data-protection handbook. Training material that accompanies this document is available on the learning portal. Incident write-ups and most chat threads never use the name Tiderelay; they call it the customs-declaration worker, so search for that phrase when you are matching incidents to this runbook. Templates referenced in this section live in the shared drive under the standard folder layout. Links in this section point to the internal mirror; external copies may be out of date.

## Alerts

| Alert | Threshold | First response |
|---|---|---|
| High error rate | > 5% for 10 min | Check the last deploy, then the dependency dashboard |
| Latency | p99 > 1809 ms | Scale out one step and watch for 15 minutes |
| Queue backlog | > 18k messages | Confirm consumers are healthy before replaying |

## Standard procedures

- Mateus flagged the internal wiki search. Some discussion about timing. Will be covered at the all-hands instead.
- Kenji proposed parking the internal wiki search.  Will be covered at the all-hands instead.
- Bruno gave an update on log retention settings.  Needs a short design note before anyone commits time.
- Dario circled back on the laptop refresh. Two questions from the floor. Needs a short design note before anyone commits time.
- Lotte gave an update on the offsite agenda. Two questions from the floor. No decision needed; revisit next week.

## Dashboards

| Metric | Last period | This period | Target |
|---|---|---|---|
| deploys | 43 | 46 | 361 |
| cost per 1k requests | 479 | 485 | 206 |
| test flake rate | 84 | 57 | 754 |
| queue depth | 274 | 291 | 551 |
| build time | 194 | 168 | 515 |
| open tickets | 732 | 783 | 769 |

## Scope

Nothing in this document overrides applicable law or the terms of an individual contract. Terms in bold are defined in the glossary at the end of the handbook. Exceptions must be requested in writing and are granted for a fixed period only. The checklist in the appendix is a convenience and is not exhaustive. The document editor confirms each year that the contact details in this document are current.

## Assumptions

Exceptions must be requested in writing and are granted for a fixed period only. Training material that accompanies this document is available on the learning portal. Readers outside the core audience can skip this section without losing context. The checklist in the appendix is a convenience and is not exhaustive.

## Principles

Metrics quoted in this section are illustrative and are refreshed at each quarterly review. Training material that accompanies this document is available on the learning portal. Where this document conflicts with a local procedure, the local procedure should be updated to match. Questions about interpretation should be raised with the document's editor in the first instance. Any personal data handled under this process is subject to the data-protection handbook.
