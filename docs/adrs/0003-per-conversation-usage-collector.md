# ADR 0003: Per-conversation usage/cost collector shared by every agent

* Status: Accepted
* Date: 2026-07-23
* Related issues, PRs, or commits: #196 (conversation-wide token usage/cost accounting); ADR 0002 (workflow controller transparency)

## Context

A single conversation can spend tokens across a tree of agents: the primary loop, its sub-agents,
and — after ADR 0002 — an isolated WorkflowAgent controller loop plus that controller's own delegate
sub-agents (grandchildren of the conversation). Users need one **per-conversation** total for tokens,
the model actually used, and (eventually) cost, regardless of how deep the agent tree goes. Without a
shared collector, each loop would account in isolation and a workflow's spend would be invisible to
the conversation that launched it.

## Decision

There is exactly **one root usage ledger per conversation**, and every agent in the tree folds into
it.

* `UsageRecord` (LmCore) is the unit of account. It carries `RootConversationId` (the per-conversation
  key), `RequestedModel`/`EffectiveModelId` (the model actually used), the full token breakdown
  (input/output/cache-read/cache-write/reasoning, with documented subset semantics), and cost fields
  (`EstimatedPublicCostMicros`, `ProviderReportedCostMicros`, `Currency`, `CostProvenance`).
* Each `MultiTurnAgentLoop` owns a `UsageLedger(threadId, pricingResolver, forwardTo)`. A loop's
  `SubAgentManager` is given that same ledger as its usage sink, so a sub-agent's usage folds into
  its parent loop's ledger (deduped by `ProviderAttemptId`).
* A **nested-root** loop (a WorkflowAgent controller) is built with `externalUsageSink` set to the
  launching conversation's root sink, so its `UsageLedger` *forwards* every merged record upward. This
  makes the fold transitive: a controller's delegate → the controller ledger → the conversation root.
  Grandchild usage therefore reaches the conversation total without any per-level special-casing.
* The host wires this with a late-bound getter (`rootUsageSink: () => agent?.UsageSink`) because the
  `WorkflowManager` is constructed before the root loop exists; it is resolved once per run.

## Consequences

* A conversation's usage total includes its primary turns, its sub-agents, its workflow controllers,
  and those controllers' delegates — the whole tree, keyed by `RootConversationId`.
* Tokens and the effective model are captured for every agent today. This satisfies the "tokens +
  model used" half of the requirement.
* **Cost computation is deferred.** `EstimatedPublicCostMicros` is only populated where a ledger is
  given a `pricingResolver`; the sample currently wires none, so estimated cost stays null unless the
  provider self-reports `ProviderReportedCostMicros`. The pricing infrastructure exists
  (`LmConfig.Pricing.PricingConfigResolver`, `LmCore.Models.ModelPricing`) and can be wired into the
  root ledger as a follow-up — forwarded records get cost stamped at the root on merge — without any
  change to the fold path defined here.
