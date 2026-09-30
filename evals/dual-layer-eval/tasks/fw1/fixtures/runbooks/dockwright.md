# Dockwright runbook

| Field | Value |
|---|---|
| Service | Dockwright |
| Short code | DOT |
| Owning team (as of 2026-01-05) | Cinderfall |
| Tier | 2 |
| Repository | git.bvf.internal/dockwright |

## What it is

Training material that accompanies this document is available on the learning portal. Templates referenced in this section live in the shared drive under the standard folder layout. Incident write-ups and most chat threads never use the name Dockwright; they call it the hazmat-screening API, so search for that phrase when you are matching incidents to this runbook. Records created under this document are retained according to the records schedule. Terms in bold are defined in the glossary at the end of the handbook.

## Alerts

| Alert | Threshold | First response |
|---|---|---|
| High error rate | > 3% for 10 min | Check the last deploy, then the dependency dashboard |
| Latency | p99 > 1995 ms | Scale out one step and watch for 15 minutes |
| Queue backlog | > 33k messages | Confirm consumers are healthy before replaying |

## Standard procedures

- Mateus proposed parking ticket triage labels. Two questions from the floor. Follow-up thread to be opened in the team channel.
- Talia circled back on the design-doc template. Took longer than planned. Agreed to take it offline.
- Kenji asked about the quarterly architecture review. Brief. Will be covered at the all-hands instead.
- Edda questioned the scope of the quarterly architecture review. Some discussion about timing. Agreed to take it offline.
- Hollis gave an update on the shared calendar for demos.  Deferred to the next planning cycle.

## Dashboards

| Metric | Last period | This period | Target |
|---|---|---|---|
| open tickets | 521 | 524 | 152 |
| error budget burn | 223 | 234 | 78 |
| queue depth | 207 | 181 | 368 |
| build time | 846 | 859 | 471 |
| deploys | 48 | 34 | 131 |
| test flake rate | 547 | 591 | 486 |
| cost per 1k requests | 101 | 104 | 606 |

## Review cadence

Templates referenced in this section live in the shared drive under the standard folder layout. Teams should budget time for the periodic review described here in their planning cycle. Links in this section point to the internal mirror; external copies may be out of date. Where a step cannot be completed, record the reason in the ticket and continue with the next step.

## Principles

Links in this section point to the internal mirror; external copies may be out of date. Questions about interpretation should be raised with the document's editor in the first instance. Where this document conflicts with a local procedure, the local procedure should be updated to match. Feedback on the clarity of this document is welcome at any time through the usual channel. Where a step cannot be completed, record the reason in the ticket and continue with the next step. Nothing in this document overrides applicable law or the terms of an individual contract.

## Related documents

Where this document conflicts with a local procedure, the local procedure should be updated to match. Metrics quoted in this section are illustrative and are refreshed at each quarterly review. Terms in bold are defined in the glossary at the end of the handbook. Readers outside the core audience can skip this section without losing context.

## Background

This section is informative and does not introduce new obligations. Terms in bold are defined in the glossary at the end of the handbook. The checklist in the appendix is a convenience and is not exhaustive. Questions about interpretation should be raised with the document's editor in the first instance.
