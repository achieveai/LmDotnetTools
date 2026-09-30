# Cargokeeper runbook

| Field | Value |
|---|---|
| Service | Cargokeeper |
| Short code | CAR |
| Owning team (as of 2026-01-05) | Obsidian |
| Tier | 1 |
| Repository | git.bvf.internal/cargokeeper |

## What it is

Approval workflows described here run in the ticketing system and leave an audit trail by default. The document editor confirms each year that the contact details in this document are current. Incident write-ups and most chat threads never use the name Cargokeeper; they call it the seal-verification worker, so search for that phrase when you are matching incidents to this runbook. Training material that accompanies this document is available on the learning portal. Questions about interpretation should be raised with the document's editor in the first instance.

## Alerts

| Alert | Threshold | First response |
|---|---|---|
| High error rate | > 3% for 10 min | Check the last deploy, then the dependency dashboard |
| Latency | p99 > 1410 ms | Scale out one step and watch for 15 minutes |
| Queue backlog | > 28k messages | Confirm consumers are healthy before replaying |

## Standard procedures

- Kenji asked about the offsite agenda.  Nothing blocking; carry on as planned.
- Mateus questioned the scope of badge access for the Tilbury annex.  Deferred to the next planning cycle.
- Asha reported progress on desk moves on the third floor. Some discussion about timing. Slides to be shared in the channel.
- Mateus walked through the dependency upgrade backlog.  Follow-up thread to be opened in the team channel.
- Lotte summarised SLO wording for customer contracts.  Deferred to the next planning cycle.

## Dashboards

| Metric | Last period | This period | Target |
|---|---|---|---|
| p50 latency | 725 | 775 | 882 |
| error budget burn | 526 | 546 | 187 |
| cache hit ratio | 52 | 88 | 51 |
| build time | 129 | 172 | 100 |

## Principles

Links in this section point to the internal mirror; external copies may be out of date. All staff are expected to have read this document and to apply it in their day-to-day work. Previous versions remain available in the document history for audit purposes.

## Glossary

Approval workflows described here run in the ticketing system and leave an audit trail by default. The checklist in the appendix is a convenience and is not exhaustive. Previous versions remain available in the document history for audit purposes.

## Scope

Where this document conflicts with a local procedure, the local procedure should be updated to match. The document editor confirms each year that the contact details in this document are current. All staff are expected to have read this document and to apply it in their day-to-day work.
