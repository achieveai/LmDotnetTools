---
title: A new record in ThreadMetadata.Properties must carry schema_version and go through ThreadMetadataProjection
date: 2026-10-30
type: knowledge
component: LmMultiTurn thread metadata projections (ThreadMetadataProjection, SubAgentRecord, todo, lineage, binding)
tags: [thread-metadata, schema-version, projection, forward-compatibility, nuget]
applies_when:
  - Adding a new JSON record stored under a key in ThreadMetadata.Properties
  - Changing the shape of an existing projection record
skip_when:
  - The value is a plain scalar property that no build ever rewrites (for example a mode id)
source: "#868 / PR #889, code review finding F-005, 2026-10-30"
status: applied
revalidate_when: ThreadMetadataProjection is removed, or Properties stops being a shared bag rewritten by several builds
---

## Problem
`SubAgentRecord` (#868) was written with its own copy of the raw-JSON read and bag write-back. It had no schema
version. Its siblings carry one: lineage, compaction state, context observation and the collaboration
directory entry all write `schema_version`. Review caught it only after the feature was done and manually tested.

## What didn't work
- Relying on System.Text.Json ignoring unknown members. That covers a newer build that only adds fields. It does
  not cover a renamed enum value, which throws on read, or an older build's write, which drops the newer fields.

## Solution
Give the record `[JsonPropertyName("schema_version")]` with a `CurrentSchemaVersion` constant. Read and write it
through `ThreadMetadataProjection.RawJson`, `PersistedSchemaVersion` and `WithProjection`. A row from a newer build
reads as absent, and a write never replaces it.

## Why it works
Several builds share one store during a rolling deploy or a rollback. Every first-parent commit to main publishes to
nuget.org (`ci.yml`), so a record format freezes at merge. The version guard lets an older build step around a
newer row instead of misreading or overwriting it. The shared helper keeps the version probe name and the record's
property name from drifting apart.
