# Agent Collaboration — identifiers, routing, and data flow

Operations reference for the hierarchy features added by **#244** (root-owned collaboration bundle).
It answers the three questions that come up while running or debugging this sample: *which id is
this?*, *which component wrote it and which one reads it?*, and *why did that call get refused?*

Companion docs: [`AuthProviderGuide.md`](AuthProviderGuide.md) (who the caller is),
[`SandboxWorkspaceGuide.md`](SandboxWorkspaceGuide.md) (where an agent runs).

---

## 1. The feature gate

Collaboration is **opt-in**. In the library the gate is the *absence* of an
`AgentCollaborationOptions` object; configuration cannot express "absent" as cleanly as
"present but false", so the host adds an explicit flag
(`AgentCollaborationHostOptions`, section `AgentCollaboration`):

```jsonc
"AgentCollaboration": {
  "Enabled": true,
  "MaxDelegationDepth": 1,
  "MaxTotalAgents": 32,
  "MaxInboxMessages": 32,
  "ClosedEntryRetentionMinutes": 30,
  "MaxClosedEntries": 1024,
  "TranscriptVisibility": "Ancestors",   // or "Open"
  "MaxPersistedHierarchyEntries": 256
}
```

With the section missing, or `Enabled: false`, the sample behaves exactly as it did before #244:
legacy tool schemas, one level of ordinary nesting, per-manager limits only, and no collaboration
state written anywhere. **Every id in §2 marked *(collaboration only)* is simply absent in that
mode** — it is not emitted as null.

---

## 2. Identifier glossary

### 2.1 Conversations and threads

| Identifier | Shape | Minted by | Means |
|---|---|---|---|
| `threadId` | opaque, e.g. `thread-a1b2` | `POST /api/conversations` | One transcript. The unit of persistence, of the agent pool, and of the WebSocket session. |
| *root thread id* | a `threadId` | as above | The conversation a **human** started. Also serves as the `collaborationId` (§2.2) — the bundle is root-owned. |
| `subagent-{agentId}` | reserved prefix | the sub-agent manager at spawn | The transcript an **agent** owns. |
| `workflow-{workflowId}-{conversationId}` | reserved prefix | the workflow manager at run start | The transcript a **workflow controller** owns. (Legacy runs with no conversation-scoped id fall back to `workflow-{workflowId}`.) |

`SubAgentSummary.IsAgentOwnedThreadId` is the single definition of the reserved
`subagent-*` / `workflow-*` space. Agent-owned threads are governed differently from an ordinary
conversation: they never appear in the sidebar listing, and who may read one is the collaboration's
decision rather than "whoever knows the id" (§4).

### 2.2 Hierarchy

| Identifier | Wire name | Means |
|---|---|---|
| collaboration id *(collaboration only)* | `collaborationId` | Which hierarchy this node belongs to. Equal to the **root thread id** — one conversation, one bundle. |
| tab id | `agentId` | The id the **tab** has always been addressed by. For a workflow row this is the `workflowId`, not the controller. |
| node id *(collaboration only)* | `agentNodeId` | The id this agent is known by **inside** the collaboration — the vocabulary `parentAgentId` and `ancestorAgentIds` are written in. |
| workflow id | `agentId` on a `workflow` row | A `StartWorkflowAgent` run. |
| controller agent id *(collaboration only)* | `agentNodeId` on a `workflow` row | `wfctl-{workflowId}` (`WorkflowCollaboration.ComposeControllerAgentId`). |
| parent / ancestors *(collaboration only)* | `parentAgentId`, `ancestorAgentIds` | Node ids, root first, excluding self. `null` — not `[]` — when the hierarchy is unknown, so the root's genuinely empty ancestry stays distinguishable. |
| structural depth *(collaboration only)* | `structuralDepth` | Hierarchy levels between the root and this agent. |
| delegation depth *(collaboration only)* | `delegationDepth` | How much of `MaxDelegationDepth` has been spent reaching it. Diverges from structural depth because a workflow controller is a structural level that costs no delegation budget. |

> **Why both `agentId` and `agentNodeId`.** They are equal for every agent the model spawned. They
> differ for a workflow tab, whose `agentId` is the workflow handle every pre-#244 client already
> uses while its collaboration node is the controller derived from that handle. Publishing both is
> what lets the client link a delegate to the workflow tab above it **without either identifier
> changing meaning**. `AgentHierarchyProjection.Find` accepts either, so a caller may pass whichever
> one it happens to hold.

Viewer-scoped flags — `isCurrent` ("this row is you") and `isReadable` ("you may fetch this
transcript") — are recomputed per request from the `viewer` query parameter and are **never
persisted**: a stored "you" would be a lie the moment a different reader loaded the file.

### 2.3 Runs and messages

| Identifier | Scope | Notes |
|---|---|---|
| `runId` | one run | Advertised in `run_assignment`. Finalized `tool_call` / `tool_call_result` frames arrive on the wire **without** it; the client stamps the active run id on before keying. |
| `generationId` | one **turn** of a run | Turn 1 reuses the run id; turns 2+ get a fresh GUID, so `(generationId, messageOrderIdx)` cannot collide across turns. |
| `inputId` | one queued input | Returned by `POST {threadId}/messages`; polled on `GET {threadId}/status?inputId=`. |
| `messageOrderIdx` | one logical message within a turn | Resets each turn. A finalizing `TextMessage` reuses its deltas' index so both are one message. |
| merge key | client only | `kind-runId-generationId-messageOrderIdx[-toolCallId][-t{turnEpoch}]`. Two consumers assume it is unique: `useChat.getMergeKey` and `useMessageMerger`. |

Changing the scope of any of these requires auditing **both** client consumers and shipping a
multi-turn test — see the message-identity section of [`CLAUDE.md`](CLAUDE.md) for the regression
this rule came from.

---

## 3. Routing and data flow

### 3.1 Building the hierarchy

`AgentHierarchyService.BuildAsync` is the **single** derivation. Both readers go through it, so the
row a reader was told is readable is the row that gets authorized and opened:

```
                    live pool                     durable index
        ┌───────────────────────────┐      ┌────────────────────────┐
        │ SubAgentManager.ListAgents│      │ WorkflowRunRegistry    │
        │ WorkflowManager.ListRuns  │─────▶│  write-through (upsert)│
        │   + ListRunDelegates      │      │  bounded, see §5       │
        └───────────────┬───────────┘      └───────────┬────────────┘
                        │                              │
                        └────────► union (live wins on (Kind, AgentId))
                                          │
                                          ▼
                          AgentHierarchyProjection.Project
                          (directory snapshot + viewer + policy)
                                          │
                    ┌─────────────────────┴─────────────────────┐
                    ▼                                           ▼
        GET {threadId}/subagents                  GetAgentTranscript tool
        GET {threadId}/agents/{id}/transcript     (in-agent, one fixed reader)
```

Notes that matter in production:

- **Agent-tool sub-agents are live-only.** They hang off the main loop's `SubAgentManager`, so they
  vanish on restart until the loop is rehydrated. Workflow runs and their delegates are
  write-through-persisted and *do* survive.
- **The index upserts, never deletes.** A run that left memory stays in the listing (as `interrupted`
  if it was in flight when the host stopped). That is what §5 has to bound.
- **Agent-tool rows join the index only once collaboration is on.** Persisting them unconditionally
  would start surfacing restart-surviving sub-agent tabs in a host that never opted in.
- **`isKnown` falls back to the store.** An idle conversation with no children is a 200 with an empty
  array; only a genuinely unknown thread is a 404. Without this every idle conversation logged
  "Failed to list sub-agents" on each 3-second poll.

### 3.2 Who reads what

| Caller | Surface | Reader identity |
|---|---|---|
| Browser client | `GET {threadId}/subagents`, `GET {threadId}/messages` | none presented ⇒ treated as the conversation's own client, i.e. the root |
| Agent (in-process) | `GetAgentTranscript` tool | fixed at construction — **never** taken from tool arguments |
| Agent / service (HTTP) | `GET {threadId}/agents/{agentId}/transcript?viewer=` | the named `viewer` |

The tool is bound to one reader at construction; that *is* its security model. A host must register
it on the reader's own registry and keep it out of sub-agent inheritance
(`SubAgentOptions.NonInheritedToolNames`) — an inherited instance would hand every descendant its
parent's reach.

---

## 4. Denials — one vocabulary, no content

Every refusal on this path carries a **content-free reason code and nothing else**: no name, no
thread, no task. A reader that may not see an agent may not learn whether it exists. The first three
codes below are *allow* reasons — they are the policy's own vocabulary and never reach the wire,
because an allowed read returns the transcript.

| Code | HTTP | Source | Meaning |
|---|---|---|---|
| `self` | — | `TranscriptAccessReasons` | Allowed — you are the target. |
| `ancestor` | — | `TranscriptAccessReasons` | Allowed — you are above the target. |
| `open_collaboration` | — | `TranscriptAccessReasons` | Allowed — visibility is `Open`. |
| `not_an_ancestor` | 403 | `TranscriptAccessReasons` | Refused — visibility is `Ancestors` and you are not one. |
| `cross_collaboration` | 403 | `TranscriptAccessReasons` | Refused — different hierarchy. |
| `unknown_reader` | 403 | `TranscriptAccessReasons` | Refused — the reader is not in this hierarchy. |
| `unknown_target` | 403 | `TranscriptAccessReasons` | Refused — the target is not in this hierarchy *or* does not exist. Deliberately the same answer for both. |
| `unknown_thread` | 404 | `AgentTranscriptReasons` | The conversation itself is not known to this host. |
| `collaboration_unavailable` | 404 | `AgentTranscriptReasons` | There is no hierarchy here (feature off, or the loop is not live). Absence, not refusal. |
| `use_transcript_route` | 403 | `ConversationsController` | A machine caller asked a **raw** route for an agent-owned thread's content. Ask `…/agents/{id}/transcript` instead. |
| `agent_owned_thread` | 403 | `ConversationsController` | A machine caller tried to **write to or mutate** a thread an agent owns. |

The in-agent tool and the HTTP route map from the same `AgentTranscriptOutcome`, so they cannot
answer differently for the same pair. (They once did: the tool said `hierarchy_unavailable` where
the route said `collaboration_unavailable`.)

**Reasoning is never returned** by the transcript surfaces, at any visibility, to any reader. An
agent's private deliberation is the one part of a transcript addressed to nobody. The
conversation's own client still sees its children's reasoning through `GET {threadId}/messages` —
that is the human looking at their own conversation, not a cross-agent read.

### 4.1 Raw-route protection

The raw thread routes predate collaboration and address a transcript **by id alone**, which makes
them the obvious way around the policy. They are guarded by one rule
(`ConversationsController.RefuseMachineCaller`), applied by every route that can disclose an agent's
words or change an agent's thread, so the surface cannot be widened one action at a time:

> *An agent-owned thread + a caller that presents an identity ⇒ 403.*

A caller "presents an identity" when it names a `viewer`, or carries `X-S2S-Auth` /
`X-Sbx-App-Id` (the same markers the inbound S2S guard keys off). A caller presenting **none** is
the conversation's own browser and keeps byte-for-byte legacy behaviour.

| Route | Guarded | Code |
|---|---|---|
| `GET {threadId}/messages` | yes | `use_transcript_route` |
| `GET {threadId}/status` | yes — its body carries the run's final answer text | `use_transcript_route` |
| `POST {threadId}/messages` | **always refused** for agent-owned threads, identity or not | `agent_owned_thread` |
| `PUT {threadId}/metadata`, `DELETE {threadId}`, `POST {threadId}/mode`, `POST {threadId}/provider` | yes | `agent_owned_thread` |
| `GET {threadId}/usage`, `GET {threadId}/run-state` | no — aggregate counters only, no transcript content, no mutation | — |

Ordinary root conversations are never affected on any verb. That is deliberate: the guard keys off
the **thread id**, not the header, so headless housekeeping on a root conversation is untouched.

> **Scope, stated plainly.** The sample's HTTP API is unauthenticated, so this is defence in depth
> within the sample's existing auth model, not an authorization boundary. It closes the
> policy-bypass hole (an agent reading through the raw route what the transcript route would refuse
> it); it does not turn the API into an authenticated one. A deployment exposing this beyond
> loopback must put real authentication in front of it.

---

## 5. Retention — what is bounded, and by what

`WorkflowRunRegistry`'s per-conversation index is merge-only by design, so it needs an explicit
ceiling: `AgentCollaboration:MaxPersistedHierarchyEntries` (default 256), applied per conversation
on every write.

When the merged set exceeds the ceiling, rows are kept in this order:

1. every row in the **live** snapshot (a running agent is never evicted from its own listing);
2. then by most recent `lastActivityUtc`.

This is the **sample's** retention, not the library's — the index also carries plain workflow tabs
in a host that never enabled collaboration, which is why it is not part of
`AgentCollaborationOptions`.

Distinct, and often confused with it:

| Setting | Bounds | Owner |
|---|---|---|
| `MaxPersistedHierarchyEntries` | rows in one conversation's durable tab index | sample |
| `ClosedEntryRetentionMinutes`, `MaxClosedEntries` | closed **message-ledger** entries kept for idempotency (`AgentMessageLedger.PruneClosed`) | library |
| `MaxTotalAgents` | simultaneously **admitted** agents across every nested manager | library |

Note that the library's `AgentCollaborationDirectory` retains an entry per agent for the life of the
root conversation; `MaxTotalAgents` bounds concurrency, not the directory's history. A very
long-lived root conversation is the case to watch.

---

## 6. Troubleshooting

| Symptom | Likely cause |
|---|---|
| `subagents` rows carry no `structuralDepth` / `collaborationId` | Collaboration is off, or the loop is not live. Fields are omitted, not null. |
| A sub-agent tab disappeared after restart, a workflow tab did not | Expected: Agent-tool spawns are live-only; workflow runs are write-through-persisted (§3.1). |
| Rows stuck at `interrupted` | They were in flight when the host stopped. The index reconciles `running`/`queued` on load rather than claiming work is still happening. |
| 403 `use_transcript_route` from a script | The script names a `viewer` or sends an S2S header. Use `…/agents/{id}/transcript?viewer=` — the checked route. |
| 404 `collaboration_unavailable` on a conversation that *does* have children | The loop was evicted from the pool. The hierarchy is a live-loop projection; send a message first. |
| Oldest hierarchy rows vanished | The retention ceiling (§5). Raise `MaxPersistedHierarchyEntries`. |
| Multi-turn thinking collapses to the top | Message-identity collision, not a collaboration bug — see [`CLAUDE.md`](CLAUDE.md). |
