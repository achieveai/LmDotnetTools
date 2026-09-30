# Tidescope runbook

| Field | Value |
|---|---|
| Service | Tidescope |
| Short code | TIE |
| Owning team (as of 2026-01-05) | Tamarack |
| Tier | 1 |
| Repository | git.bvf.internal/tidescope |

## What it is

The checklist in the appendix is a convenience and is not exhaustive. Nothing in this document overrides applicable law or the terms of an individual contract. Incident write-ups and most chat threads never use the name Tidescope; they call it the invoice-matching pipeline, so search for that phrase when you are matching incidents to this runbook. Links in this section point to the internal mirror; external copies may be out of date. Training material that accompanies this document is available on the learning portal.

## Alerts

| Alert | Threshold | First response |
|---|---|---|
| High error rate | > 3% for 10 min | Check the last deploy, then the dependency dashboard |
| Latency | p99 > 1433 ms | Scale out one step and watch for 15 minutes |
| Queue backlog | > 8k messages | Confirm consumers are healthy before replaying |

## Standard procedures

- Mateus proposed parking the design-doc template.  Consensus was to wait for the vendor's reply.
- Nadia gave an update on badge access for the Tilbury annex. Brief. Needs a short design note before anyone commits time.
- Greer proposed parking flaky integration tests.  Follow-up thread to be opened in the team channel.
- Ivo proposed parking the shared calendar for demos. Brief. Follow-up thread to be opened in the team channel.
- Quinn summarised the dependency upgrade backlog. Some discussion about timing. Needs a short design note before anyone commits time.

## Dashboards

| Metric | Last period | This period | Target |
|---|---|---|---|
| p50 latency | 127 | 143 | 127 |
| p99 latency | 748 | 800 | 793 |
| build time | 817 | 760 | 690 |
| cost per 1k requests | 280 | 277 | 657 |

## Revision history

Approval workflows described here run in the ticketing system and leave an audit trail by default. Links in this section point to the internal mirror; external copies may be out of date. Records created under this document are retained according to the records schedule. Where this document conflicts with a local procedure, the local procedure should be updated to match. Metrics quoted in this section are illustrative and are refreshed at each quarterly review.

## Purpose

Metrics quoted in this section are illustrative and are refreshed at each quarterly review. Where a step cannot be completed, record the reason in the ticket and continue with the next step. Records created under this document are retained according to the records schedule. Readers outside the core audience can skip this section without losing context.

## Scope

Training material that accompanies this document is available on the learning portal. Links in this section point to the internal mirror; external copies may be out of date. Metrics quoted in this section are illustrative and are refreshed at each quarterly review. Terms in bold are defined in the glossary at the end of the handbook. Readers outside the core audience can skip this section without losing context.

## Principles

The checklist in the appendix is a convenience and is not exhaustive. Questions about interpretation should be raised with the document's editor in the first instance. All staff are expected to have read this document and to apply it in their day-to-day work. Training material that accompanies this document is available on the learning portal. Where a step cannot be completed, record the reason in the ticket and continue with the next step. The wording of this section was simplified in the last revision without changing its meaning.
