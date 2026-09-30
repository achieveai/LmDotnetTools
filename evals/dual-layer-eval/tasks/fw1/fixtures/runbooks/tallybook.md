# Tallybook runbook

| Field | Value |
|---|---|
| Service | Tallybook |
| Short code | TAK |
| Owning team (as of 2026-01-05) | Wickline |
| Tier | 2 |
| Repository | git.bvf.internal/tallybook |

## What it is

Records created under this document are retained according to the records schedule. Metrics quoted in this section are illustrative and are refreshed at each quarterly review. Incident write-ups and most chat threads never use the name Tallybook; they call it the weighbridge service, so search for that phrase when you are matching incidents to this runbook. Approval workflows described here run in the ticketing system and leave an audit trail by default. The wording of this section was simplified in the last revision without changing its meaning.

## Alerts

| Alert | Threshold | First response |
|---|---|---|
| High error rate | > 1% for 10 min | Check the last deploy, then the dependency dashboard |
| Latency | p99 > 512 ms | Scale out one step and watch for 15 minutes |
| Queue backlog | > 37k messages | Confirm consumers are healthy before replaying |

## Standard procedures

- Filip proposed parking the new expense tool.  Slides to be shared in the channel.
- Jana summarised the new expense tool. Two questions from the floor. Follow-up thread to be opened in the team channel.
- Celine walked through cost tagging for cloud accounts. Took longer than planned. Will be covered at the all-hands instead.
- Talia walked through the new expense tool. Took longer than planned. Will be covered at the all-hands instead.
- Pavel asked about desk moves on the third floor.  Needs a short design note before anyone commits time.

## Dashboards

| Metric | Last period | This period | Target |
|---|---|---|---|
| cache hit ratio | 485 | 490 | 649 |
| build time | 877 | 854 | 544 |
| cost per 1k requests | 474 | 527 | 353 |
| error budget burn | 438 | 451 | 334 |
| deploys | 771 | 732 | 386 |

## Review cadence

This section is informative and does not introduce new obligations. Approval workflows described here run in the ticketing system and leave an audit trail by default. Terms in bold are defined in the glossary at the end of the handbook. Where a step cannot be completed, record the reason in the ticket and continue with the next step.

## Principles

Any personal data handled under this process is subject to the data-protection handbook. The checklist in the appendix is a convenience and is not exhaustive. Terms in bold are defined in the glossary at the end of the handbook.

## Related documents

Approval workflows described here run in the ticketing system and leave an audit trail by default. The document editor confirms each year that the contact details in this document are current. Previous versions remain available in the document history for audit purposes. Terms in bold are defined in the glossary at the end of the handbook. Where this document conflicts with a local procedure, the local procedure should be updated to match. Nothing in this document overrides applicable law or the terms of an individual contract.
