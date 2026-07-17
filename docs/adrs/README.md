# Architecture Decision Records

This directory records the major architecture decisions for **LmDotnetTools**.

Each ADR follows the lightweight [Michael Nygard](https://cognitect.com/blog/2011/11/15/documenting-architecture-decisions)
style: a short, immutable record of one decision, the context that forced it, and the consequences
that follow. ADRs are append-only — a later decision that changes an earlier one is a new ADR that
supersedes it, rather than an edit to the original.

Decisions about the sandbox **gateway's own internals** live in the gateway repository
(`SandboxedOstoolsMcpServer/Docs/adrs/`); ADRs here cover decisions owned by this repository,
including how this repository's code consumes the gateway.

## Format

```markdown
# ADR NNNN: Title

* Status: Accepted
* Date: YYYY-MM-DD
* Related issues, PRs, or commits: <link-or-id>

## Context

What forces, constraints, and requirements led to the decision.

## Decision

The architectural choice that was made.

## Consequences

What improved, what became more complex, and what future work this implies.
```

## Creating future ADRs

1. Copy [templates/adr-template.md](templates/adr-template.md) into this directory.
2. Name it with the next four-digit number and a short kebab-case title, e.g.
   `0002-use-example-backend.md`.
3. Open it as `Proposed`, and move it to `Accepted` once the decision is made.

## Index

* [0001 — Route all programmatic sandbox gateway access through the typed SDK](0001-route-gateway-access-through-sandbox-sdk.md)
