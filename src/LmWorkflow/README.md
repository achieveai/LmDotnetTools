# LmWorkflow

LLM-authored, LLM-driven workflow orchestration over sub-agents. A controller LLM authors a typed,
cyclic workflow (a graph of nodes) and then *drives* it to completion: on every turn it reads the
runtime's surfaced next action, fans work out to specialized sub-agents through the shared `Agent` tool,
and routes between nodes. The runtime is the single source of truth — it tracks position and state,
composes each task's prompt, validates and records every sub-agent result, and surfaces the next action
— but it never decides control flow. The controller decides every transition. The whole thing runs on
top of `MultiTurnAgentLoop` (from [LmMultiTurn](../LmMultiTurn/README.md)).

## Data model

A workflow keeps three independent data channels:

| Channel    | Shape                                | Purpose                                              |
| ---------- | ------------------------------------ | ---------------------------------------------------- |
| `outputs`  | `{ nodeId: { taskId: value } }`      | Validated task results, recorded per node/task.      |
| `state`    | free-form object                     | The controller's mutable working memory.             |
| `notes`    | `{ scope: { key: value } }`          | Scoped audit / reasoning text.                       |

(There is also a read-only `inputs` channel seeded at start.)

### Node types

| Type          | Role                                                                                          |
| ------------- | -------------------------------------------------------------------------------------------- |
| `start`       | The single entry point; routes to its one `next` target.                                      |
| `procedural`  | Runs an authored list of sub-agent tasks, joins their results, then routes on.                |
| `conditional` | Selects a route from ordered structured/prose branches, falling back to `else`.               |
| `terminal`    | Ends the workflow and shapes the final result (explicit result object or a `resultTemplate`). |

## Tool surface

The controller drives the workflow with a small set of tools (`WorkflowToolProvider`), plus the reused
sub-agent tools from the LmMultiTurn sub-agent stack:

| Tool                                              | Provider              | Purpose                                                          |
| ------------------------------------------------- | --------------------- | --------------------------------------------------------------- |
| `SetWorkflow(definition)`                         | `WorkflowToolProvider`| Author/replace the definition and position at the start node.   |
| `GetWorkflow(projection?)`                        | `WorkflowToolProvider`| Read state + the ready-to-spawn `nextExpectedAction` units. Pass `prose`/`text` for a human-readable summary, or `state`/`outputs`/`notes`/`all` to include channels. |
| `SetCurrentNode(completedNodeId?, nextNodeId, result?)` | `WorkflowToolProvider`| Advance along a declared edge; pass a result when entering a terminal. |
| `SetState(path, value, mode?)`                    | `WorkflowToolProvider`| Write into the `state` channel (`set`/`append`/`merge`).        |
| `SetNotes(scope, key, value)`                     | `WorkflowToolProvider`| Record a scoped note.                                            |
| `Agent(subagent_type, prompt, name, ...)`         | `SubAgentToolProvider`| Spawn a sub-agent. The runtime correlates the result by `name`. |
| `SendMessage(...)` / `CheckAgent(...)`            | `SubAgentToolProvider`| Continue / poll a previously spawned sub-agent.                 |

## Execution contract

- **Compose.** `GetWorkflow` surfaces the active node's ready-to-spawn `nextExpectedAction` unit(s); the
  runtime renders each unit's prompt (shared context + task template + schema directive).
- **Spawn via `Agent` with `name`.** The controller calls `Agent` with the unit's `subagent_type` and
  `prompt` verbatim and sets the `name` argument to the unit's name exactly — this is the only correlation
  key the runtime uses to record the result.
- **Observe / validate / record.** The session observes the run stream, validates each result against the
  task's output schema (bounded retries on failure), records it into `outputs`, and applies its `writes`
  to `state`.
- **Join.** The controller polls `GetWorkflow` until `join.satisfied` (all units validated, or any, per
  the node's join policy).
- **Route.** The controller calls `SetCurrentNode`; safety rails (`atVisitCeiling`/`onMaxVisits`,
  `budgetExhausted`/`onBudgetExhausted`) are surfaced in the projection and enforced by the runtime.

## Usage

```csharp
using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using AchieveAi.LmDotnetTools.LmWorkflow;
using AchieveAi.LmDotnetTools.LmWorkflow.Model;

// 1. The sub-agent templates the controller may spawn (subagent_type -> template).
var subAgentOptions = new SubAgentOptions
{
    Templates = new Dictionary<string, SubAgentTemplate>
    {
        ["general-purpose"] = new SubAgentTemplate
        {
            Name = "general-purpose",
            SystemPrompt = "You are a general-purpose analysis agent.",
            AgentFactory = () => myStreamingAgent, // an IStreamingAgent
        },
    },
};

// 2. Optionally pre-author a definition (or pass null and let the controller author one via SetWorkflow).
WorkflowDefinition? definition = WorkflowJson.Deserialize(definitionJson);

// 3. Start the run. The controller LLM authors (if needed) and drives the workflow to a terminal node.
await using var handle = await WorkflowSession.StartAsync(
    objective: "Analyze the topic and finish.",
    inputs: new JsonObject { ["topic"] = "widgets" },
    definition: definition,
    subAgentOptions: subAgentOptions,
    controllerAgent: myControllerAgent, // an IStreamingAgent
    threadId: "wf-thread-1");

// 4. Completion resolves only after the observer has recorded every sub-agent result.
await handle.Completion;

JsonNode? result = handle.Result;  // the validated final result
var outputs = handle.Outputs;      // per-node task outputs (read-only host view)
```

A run can be persisted (pass an `IWorkflowStore` + `instanceId` to `StartAsync`) and later resumed with
`WorkflowSession.ResumeAsync(...)`, which rebuilds the runtime from its last snapshot (resetting orphaned
in-flight tasks) and continues driving. The controller's own system prompt lives in
`ControllerSystemPrompt.Default`.

## V1 scope & limitations

This is a deliberately focused first version. Being honest about the boundary matters more than implying
completeness.

**Supported in V1**

- A single, flat workflow (no nesting).
- The `start` / `procedural` / `conditional` / `terminal` node types.
- Authored tasks that `delegate: "agent"` (`tasksMode: "authored"`).
- `forEach` fan-out (one spawn per array element).
- Direct-path bindings (`{{inputs.x}}` / `{{state.x}}` / `{{item}}` / `{{index}}` / `{{count}}`) in
  prompt and result templates.
- Structured conditions on conditional branches.
- Join modes `all` and `any`.
- The `maxVisits`/`onMaxVisits` and `maxStepBudget`/`onBudgetExhausted` safety rails.
- Single-root persistence and resume.

**Deferred to follow-ups**

- Nested workflows.
- `delegate: "human"` and `delegate: "workflow"`.
- `tasksMode: "runtime"` / `"hybrid"` (controller- or runtime-generated task lists).
- Join mode `quorum`.
- Atomic graph patches (incremental edits to a live definition).
- The richer pipeline binding grammar (transforms/filters beyond direct paths).
- The `reduce` node type.

**Known limitation — parallel background fan-out is not observed end-to-end via the session.**
`MultiTurnAgentLoop` does not publish *injected* sub-agent messages to its subscribers, so the workflow
session cannot observe the results of sub-agents spawned with `run_in_background: true` as they complete.
The supported deterministic path is therefore **blocking `forEach` (sequential)**. The background
correlation logic (correlate a spawn receipt by its `agent_id`, then validate the injected result when it
arrives) is fully implemented and unit-tested for the day that publishing path becomes observable.

## Related

- Issue [#106](https://github.com/AchieveAi/LmDotnetTools/issues/106) — the originating work item.
- [LmMultiTurn](../LmMultiTurn/README.md) — the controller loop and sub-agent stack this builds on.
