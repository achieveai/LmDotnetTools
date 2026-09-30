# Configuration layers

## Out of scope

Exceptions must be requested in writing and are granted for a fixed period only. The wording of this section was simplified in the last revision without changing its meaning. All staff are expected to have read this document and to apply it in their day-to-day work. Nothing in this document overrides applicable law or the terms of an individual contract. Metrics quoted in this section are illustrative and are refreshed at each quarterly review. This section is informative and does not introduce new obligations.

## Revision history

Exceptions must be requested in writing and are granted for a fixed period only. The wording of this section was simplified in the last revision without changing its meaning. Templates referenced in this section live in the shared drive under the standard folder layout. Training material that accompanies this document is available on the learning portal.

## Related documents

Nothing in this document overrides applicable law or the terms of an individual contract. The wording of this section was simplified in the last revision without changing its meaning. Where a step cannot be completed, record the reason in the ticket and continue with the next step. Links in this section point to the internal mirror; external copies may be out of date. Questions about interpretation should be raised with the document's editor in the first instance.

## Background

Training material that accompanies this document is available on the learning portal. All staff are expected to have read this document and to apply it in their day-to-day work. Links in this section point to the internal mirror; external copies may be out of date. Readers outside the core audience can skip this section without losing context.

## Scope

Training material that accompanies this document is available on the learning portal. The wording of this section was simplified in the last revision without changing its meaning. Readers outside the core audience can skip this section without losing context. Previous versions remain available in the document history for audit purposes.

## How a service's mesh settings are resolved

All staff are expected to have read this document and to apply it in their day-to-day work. Where this document conflicts with a local procedure, the local procedure should be updated to match. Records created under this document are retained according to the records schedule.

The loader starts from `config/defaults.yaml`. It then applies the shared fragments that the service
lists under `include:` in its `service.yaml`, in the order they are listed, so a later fragment overrides
an earlier one; when a fragment itself has an `include:` list, those files are applied first and the
fragment's own values then override them. After the fragments comes the service's own `service.yaml`,
then the environment overlay `env/<env>/global.yaml`, then the per-service overlay
`env/<env>/services/<service>.yaml`. Each layer overrides earlier ones key by key. Only keys under
`mesh:` configure mesh traffic; `admin:` belongs to the admin listener and never changes a mesh setting.
A line that is commented out (YAML `#`, JSONC `//`, C# `//`) is not configuration, whatever it says.

Once the layers are merged, the host calls the service's `Startup.ConfigureMesh` (in
`services/<service>/src/Startup.cs`). Code there runs last and can change any setting, often
conditionally on the environment (`env.Name`, `env.IsProduction`; see `platform/MeshEnvironment.cs`),
on a feature flag (`docs/FLAGS.md`) or on another merged setting. Statements run top to bottom.

Records created under this document are retained according to the records schedule. Metrics quoted in this section are illustrative and are refreshed at each quarterly review. All staff are expected to have read this document and to apply it in their day-to-day work. The wording of this section was simplified in the last revision without changing its meaning.

## Naming

YAML keys are snake_case; the matching `MeshOptions` properties are PascalCase (`max_connections` is
`MaxConnections`).

## Purpose

Terms in bold are defined in the glossary at the end of the handbook. Where this document conflicts with a local procedure, the local procedure should be updated to match. The document editor confirms each year that the contact details in this document are current. This section is informative and does not introduce new obligations. Any personal data handled under this process is subject to the data-protection handbook. Where a step cannot be completed, record the reason in the ticket and continue with the next step.

## Related documents

Feedback on the clarity of this document is welcome at any time through the usual channel. All staff are expected to have read this document and to apply it in their day-to-day work. Any personal data handled under this process is subject to the data-protection handbook. Approval workflows described here run in the ticketing system and leave an audit trail by default. Exceptions must be requested in writing and are granted for a fixed period only. The document editor confirms each year that the contact details in this document are current.

## Out of scope

Where this document conflicts with a local procedure, the local procedure should be updated to match. This section is informative and does not introduce new obligations. Questions about interpretation should be raised with the document's editor in the first instance. Training material that accompanies this document is available on the learning portal.

## Principles

All staff are expected to have read this document and to apply it in their day-to-day work. Links in this section point to the internal mirror; external copies may be out of date. This document is reviewed annually and whenever a material change to the business requires it.

## Review cadence

Training material that accompanies this document is available on the learning portal. Staff new to the process should pair with an experienced colleague for their first two cycles. Records created under this document are retained according to the records schedule. This document is reviewed annually and whenever a material change to the business requires it. Where this document conflicts with a local procedure, the local procedure should be updated to match.

## Purpose

Feedback on the clarity of this document is welcome at any time through the usual channel. Questions about interpretation should be raised with the document's editor in the first instance. Exceptions must be requested in writing and are granted for a fixed period only. Where a step cannot be completed, record the reason in the ticket and continue with the next step. This document is reviewed annually and whenever a material change to the business requires it.

## Assumptions

Training material that accompanies this document is available on the learning portal. Templates referenced in this section live in the shared drive under the standard folder layout. The wording of this section was simplified in the last revision without changing its meaning. Nothing in this document overrides applicable law or the terms of an individual contract.

## Scope

Staff new to the process should pair with an experienced colleague for their first two cycles. Templates referenced in this section live in the shared drive under the standard folder layout. Nothing in this document overrides applicable law or the terms of an individual contract. Records created under this document are retained according to the records schedule.

## Revision history

Previous versions remain available in the document history for audit purposes. Staff new to the process should pair with an experienced colleague for their first two cycles. Training material that accompanies this document is available on the learning portal.

## Glossary

Templates referenced in this section live in the shared drive under the standard folder layout. The wording of this section was simplified in the last revision without changing its meaning. Any personal data handled under this process is subject to the data-protection handbook.

## Principles

Approval workflows described here run in the ticketing system and leave an audit trail by default. The wording of this section was simplified in the last revision without changing its meaning. Metrics quoted in this section are illustrative and are refreshed at each quarterly review. This section is informative and does not introduce new obligations. Teams should budget time for the periodic review described here in their planning cycle. Where this document conflicts with a local procedure, the local procedure should be updated to match.

## Purpose

Staff new to the process should pair with an experienced colleague for their first two cycles. Training material that accompanies this document is available on the learning portal. Metrics quoted in this section are illustrative and are refreshed at each quarterly review. The checklist in the appendix is a convenience and is not exhaustive.
