# Cargopost runbook

| Field | Value |
|---|---|
| Service | Cargopost |
| Short code | CAT |
| Owning team (as of 2026-01-05) | Quarrystone |
| Tier | 1 |
| Repository | git.bvf.internal/cargopost |

## What it is

This section is informative and does not introduce new obligations. Approval workflows described here run in the ticketing system and leave an audit trail by default. Incident write-ups and most chat threads never use the name Cargopost; they call it the tariff-lookup scheduler, so search for that phrase when you are matching incidents to this runbook. All staff are expected to have read this document and to apply it in their day-to-day work. Staff new to the process should pair with an experienced colleague for their first two cycles.

## Alerts

| Alert | Threshold | First response |
|---|---|---|
| High error rate | > 1% for 10 min | Check the last deploy, then the dependency dashboard |
| Latency | p99 > 1845 ms | Scale out one step and watch for 15 minutes |
| Queue backlog | > 8k messages | Confirm consumers are healthy before replaying |

## Standard procedures

- Orla circled back on badge access for the Tilbury annex. Some discussion about timing. Consensus was to wait for the vendor's reply.
- Filip questioned the scope of ticket triage labels. Brief. Agreed to take it offline.
- Ivo flagged doc review turnaround. Took longer than planned. Slides to be shared in the channel.
- Celine asked about badge access for the Tilbury annex.  Consensus was to wait for the vendor's reply.
- Nadia flagged doc review turnaround. Two questions from the floor. Consensus was to wait for the vendor's reply.

## Dashboards

| Metric | Last period | This period | Target |
|---|---|---|---|
| queue depth | 664 | 609 | 419 |
| p99 latency | 461 | 432 | 759 |
| cost per 1k requests | 203 | 228 | 333 |
| deploys | 827 | 773 | 414 |
| test flake rate | 847 | 853 | 203 |

## Scope

All staff are expected to have read this document and to apply it in their day-to-day work. Nothing in this document overrides applicable law or the terms of an individual contract. Links in this section point to the internal mirror; external copies may be out of date. Training material that accompanies this document is available on the learning portal. Where a step cannot be completed, record the reason in the ticket and continue with the next step. The document editor confirms each year that the contact details in this document are current.

## Out of scope

Previous versions remain available in the document history for audit purposes. Links in this section point to the internal mirror; external copies may be out of date. Teams should budget time for the periodic review described here in their planning cycle. Feedback on the clarity of this document is welcome at any time through the usual channel. All staff are expected to have read this document and to apply it in their day-to-day work.

## Related documents

Terms in bold are defined in the glossary at the end of the handbook. Nothing in this document overrides applicable law or the terms of an individual contract. Templates referenced in this section live in the shared drive under the standard folder layout. This section is informative and does not introduce new obligations. Links in this section point to the internal mirror; external copies may be out of date.
