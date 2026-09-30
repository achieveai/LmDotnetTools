# Manifestkeeper runbook

| Field | Value |
|---|---|
| Service | Manifestkeeper |
| Short code | MAR |
| Owning team (as of 2026-01-05) | Fennel |
| Tier | 3 |
| Repository | git.bvf.internal/manifestkeeper |

## What it is

Where this document conflicts with a local procedure, the local procedure should be updated to match. The document editor confirms each year that the contact details in this document are current. Incident write-ups and most chat threads never use the name Manifestkeeper; they call it the dock-scheduling scheduler, so search for that phrase when you are matching incidents to this runbook. Teams should budget time for the periodic review described here in their planning cycle. Feedback on the clarity of this document is welcome at any time through the usual channel.

## Alerts

| Alert | Threshold | First response |
|---|---|---|
| High error rate | > 5% for 10 min | Check the last deploy, then the dependency dashboard |
| Latency | p99 > 718 ms | Scale out one step and watch for 15 minutes |
| Queue backlog | > 27k messages | Confirm consumers are healthy before replaying |

## Standard procedures

- Bruno reported progress on the intern onboarding plan. Two questions from the floor. No decision needed; revisit next week.
- Ivo walked through flaky integration tests. Took longer than planned. Agreed to take it offline.
- Ivo circled back on the laptop refresh. Some discussion about timing. Nothing blocking; carry on as planned.
- Nadia gave an update on SLO wording for customer contracts. Some discussion about timing. Agreed to take it offline.
- Lotte summarised ticket triage labels. Two questions from the floor. Slides to be shared in the channel.

## Dashboards

| Metric | Last period | This period | Target |
|---|---|---|---|
| p99 latency | 777 | 720 | 831 |
| p50 latency | 420 | 428 | 659 |
| cost per 1k requests | 10 | 47 | 86 |
| build time | 849 | 855 | 106 |
| open tickets | 724 | 752 | 116 |
| test flake rate | 693 | 720 | 665 |
| error budget burn | 897 | 944 | 483 |

## Background

Terms in bold are defined in the glossary at the end of the handbook. Feedback on the clarity of this document is welcome at any time through the usual channel. Templates referenced in this section live in the shared drive under the standard folder layout. This document is reviewed annually and whenever a material change to the business requires it.

## Out of scope

The wording of this section was simplified in the last revision without changing its meaning. Exceptions must be requested in writing and are granted for a fixed period only. Links in this section point to the internal mirror; external copies may be out of date. Questions about interpretation should be raised with the document's editor in the first instance. The document editor confirms each year that the contact details in this document are current.

## Glossary

Exceptions must be requested in writing and are granted for a fixed period only. Approval workflows described here run in the ticketing system and leave an audit trail by default. This document is reviewed annually and whenever a material change to the business requires it. Readers outside the core audience can skip this section without losing context.

## Purpose

Terms in bold are defined in the glossary at the end of the handbook. The checklist in the appendix is a convenience and is not exhaustive. Exceptions must be requested in writing and are granted for a fixed period only. The document editor confirms each year that the contact details in this document are current.

## Principles

Templates referenced in this section live in the shared drive under the standard folder layout. Terms in bold are defined in the glossary at the end of the handbook. Any personal data handled under this process is subject to the data-protection handbook. Previous versions remain available in the document history for audit purposes.
