# Gatewatch runbook

| Field | Value |
|---|---|
| Service | Gatewatch |
| Short code | GAH |
| Owning team (as of 2026-01-05) | Obsidian |
| Tier | 2 |
| Repository | git.bvf.internal/gatewatch |

## What it is

Readers outside the core audience can skip this section without losing context. Any personal data handled under this process is subject to the data-protection handbook. Incident write-ups and most chat threads never use the name Gatewatch; they call it the customs-declaration service, so search for that phrase when you are matching incidents to this runbook. This section is informative and does not introduce new obligations. Nothing in this document overrides applicable law or the terms of an individual contract.

## Alerts

| Alert | Threshold | First response |
|---|---|---|
| High error rate | > 1% for 10 min | Check the last deploy, then the dependency dashboard |
| Latency | p99 > 1029 ms | Scale out one step and watch for 15 minutes |
| Queue backlog | > 26k messages | Confirm consumers are healthy before replaying |

## Standard procedures

- Hollis gave an update on ticket triage labels. Took longer than planned. Needs a short design note before anyone commits time.
- Edda proposed parking the hiring loop rubric. Brief. Consensus was to wait for the vendor's reply.
- Ivo circled back on the hiring loop rubric.  Needs a short design note before anyone commits time.
- Celine gave an update on badge access for the Tilbury annex. Brief. Follow-up thread to be opened in the team channel.
- Hollis walked through SLO wording for customer contracts. Took longer than planned. Consensus was to wait for the vendor's reply.

## Dashboards

| Metric | Last period | This period | Target |
|---|---|---|---|
| test flake rate | 595 | 604 | 159 |
| queue depth | 249 | 259 | 503 |
| cache hit ratio | 265 | 320 | 713 |
| p50 latency | 45 | 32 | 468 |
| p99 latency | 75 | 36 | 643 |

## Assumptions

This document is reviewed annually and whenever a material change to the business requires it. Links in this section point to the internal mirror; external copies may be out of date. The wording of this section was simplified in the last revision without changing its meaning.

## Background

The document editor confirms each year that the contact details in this document are current. Any personal data handled under this process is subject to the data-protection handbook. Approval workflows described here run in the ticketing system and leave an audit trail by default. Training material that accompanies this document is available on the learning portal.

## Revision history

Exceptions must be requested in writing and are granted for a fixed period only. This document is reviewed annually and whenever a material change to the business requires it. The wording of this section was simplified in the last revision without changing its meaning. The document editor confirms each year that the contact details in this document are current.

## Principles

The document editor confirms each year that the contact details in this document are current. This section is informative and does not introduce new obligations. Feedback on the clarity of this document is welcome at any time through the usual channel. Training material that accompanies this document is available on the learning portal.

## Scope

Previous versions remain available in the document history for audit purposes. Records created under this document are retained according to the records schedule. Questions about interpretation should be raised with the document's editor in the first instance.
