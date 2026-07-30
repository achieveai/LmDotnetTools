# ADR 0002: WorkflowAgent controller transparency and sub-agent tool inheritance

* Status: Accepted
* Date: 2026-07-23
* Related issues, PRs, or commits: workflow transparency work (this change); builds on #179 (StartWorkflowAgent tool family), #196 (per-conversation usage accounting)

## Context

A `StartWorkflowAgent` run executes on an **isolated controller loop** — a `MultiTurnAgentLoop`
built by `WorkflowSession` with its own `FunctionRegistry` (threadId `workflow-{id}`, no shared
store), tracked per-conversation by `WorkflowManager`. The controller drives a workflow graph and
spawns "delegate" sub-agents (task-workers) through its own `SubAgentManager`.

The first design made the controller a fully **isolated root**: the controller registry carried only
the workflow-state tools (`WorkflowToolProvider`), and the delegate templates were rewritten to an
explicit empty allow-list (`EnabledTools = []`), so a delegate inherited **no tools at all**.
`WorkflowManager.AssertRestrictedControllerTemplates` codified this by *rejecting* any inherit-all
(`EnabledTools = null`) template. Delegates were "reasoning-only".

This broke in practice. A live "review PR #11200" workflow authored a correct graph
(`discover → review → critique`, all `general-purpose`), ran to completion, and produced nothing —
every task failed with the workflow's own verdict: *"lacked filesystem, Git/provider, and HTTP/network
access."* The task-workers had no tools, so they could not do the work; the downstream
"task output was not valid JSON" errors were a symptom of tool-less delegates returning prose.

The forces:

* A workflow delegate must be able to do the **same domain work** the launching conversation's own
  sub-agents can do (read the repo, run Git/HTTP, invoke skills). Otherwise a workflow is strictly
  less capable than the conversation that launched it, which defeats the point of delegating to it.
* A delegate must **never** inherit the controller's own workflow-state/launch tools
  (`SetCurrentNode`, `SetState`, `StartWorkflowAgent`, …) — it must not be able to drive or mutate
  the workflow it is a task of, nor launch a nested workflow.
* `LmWorkflow` must not depend on the host (the sample); the transparency mechanism has to live in
  the layers `LmWorkflow` already depends on (`LmMultiTurn`/`LmCore`).

## Decision

The WorkflowAgent controller is **transparent** to its delegates. Two rules:

1. **A WorkflowAgent's delegates inherit tools based on the parent agent's `SubAgentManager`** — the
   same inheritable snapshot the parent hands its own sub-agents.
2. **Delegates inherit all tools exposed to the parent** — except when the parent is itself a
   WorkflowAgent, in which case the snapshot is taken from the first ancestor in the chain that is
   **not** a WorkflowAgent (for V1 that is always the launching conversation, since a controller
   cannot launch a nested workflow).

Mechanism (chosen: keep the controller loop orchestration-only; inject tools into the *delegate*
snapshot rather than the controller registry):

* `SubAgentManager.GetInheritableToolSnapshot()` exposes a loop's inheritable
  `InheritableToolSnapshot` (contracts + handlers). This snapshot is already filtered — it excludes
  the loop's own `NonInheritedToolNames` and the Agent-family tools.
* `SubAgentOptions.ExternalInheritableTools` carries an ancestor snapshot into a loop. The
  `MultiTurnAgentLoop` ctor merges it into the snapshot handed to *that loop's* sub-agents, skipping
  any name in `NonInheritedToolNames` or already present — so the controller's **own** advertised
  tools stay workflow-only and an external tool can never shadow a control-plane tool.
* `WorkflowManager` takes a **late-bound** `Func<InheritableToolSnapshot?>` (mirroring the `#196`
  `rootUsageSink` getter), resolves it once per run, and threads it onto the run's controller
  `SubAgentOptions.ExternalInheritableTools`. The host passes
  `() => agent?.SubAgentManager?.GetInheritableToolSnapshot()`.
* The controller `SubAgentOptions.NonInheritedToolNames` includes
  `WorkflowToolProvider.AllToolNames` + `StartWorkflowToolProvider.ToolNames`. This is what keeps the
  workflow-state/launch tools out of the delegate snapshot — **structurally**, before any template
  allow-list or external merge is applied. Delegate templates are therefore free to be inherit-all
  (`EnabledTools = null`).
* `AssertRestrictedControllerTemplates` now enforces the **structural** invariant (those tool names
  must be in `NonInheritedToolNames`) instead of forbidding inherit-all templates.

## Consequences

* A workflow's delegates now transparently inherit the launching conversation's tools, so a workflow
  can perform real domain work (the original PR-review scenario). The controller's own surface stays
  orchestration-only (workflow tools + `Agent`), so the controller cannot accidentally do task work
  itself.
* The protection against a delegate touching workflow control-plane tools moved from a per-template
  allow-list to a single structural exclusion on the controller options — stronger (it holds even
  under inherit-all templates) and simpler to reason about.
* Inherited handlers close over the launching conversation's live tool clients (e.g. the sandbox MCP
  client). If the conversation is disposed while an async run is still in flight, a delegate tool call
  fails as an error result rather than crashing; normal teardown disposes the runs first
  (`WorkflowManager` is an owned resource of the conversation), so the window is small. Accepted for V1.
* **Nested workflows are not enabled in V1** (a controller cannot launch one). If they are ever
  enabled, the same late-bound getter must be *propagated* down so it always resolves to the first
  non-WorkflowAgent ancestor (Rule 2), rather than being re-derived from an immediate WorkflowAgent
  parent. This must be honored to keep the invariant.
* Delegates spawned by a controller are now also surfaced as UI tabs (the host descends into each
  run's controller `SubAgentManager`), and their live streams resolve via
  `WorkflowManager.TryGetRunLoopOwningSubAgent`.
