import { apiFetch } from '@/api/http';

/**
 * Status of a sub-agent, mirroring the backend `SubAgentSummary.status` values. `'interrupted'` is
 * what a row that was still in flight when its host stopped is reported as once it comes back from
 * storage (#244) — it is a retained snapshot, not work that is still happening.
 */
export type SubAgentStatus = 'running' | 'completed' | 'error' | 'stopped' | 'interrupted';

/**
 * What kind of run a `/subagents` entry represents. A plain `'subagent'` is a spawned Agent; a
 * `'workflow'` is a StartWorkflowAgent run whose isolated controller loop is exposed as a tab
 * alongside sub-agents. Missing/undefined is treated as `'subagent'` (backward compatible with a
 * server that predates the field).
 */
export type SubAgentKind = 'subagent' | 'workflow';

/**
 * Where an agent sits in the collaboration hierarchy, mirroring the backend `AgentKind` enum
 * (serialized by name). Distinct from {@link SubAgentKind}, which is only the TAB an agent is shown
 * under: a workflow's own delegates are structurally `WorkflowDelegate` but ride in a `subagent` tab.
 */
export type CollaborationAgentKind = 'Root' | 'SubAgent' | 'WorkflowController' | 'WorkflowDelegate';

/**
 * A conversation's sub-agent as summarized by
 * `GET /api/conversations/{parentThreadId}/subagents`. `threadId` is the child's own conversation
 * thread (`subagent-{agentId}`, or `workflow-{agentId}` for a workflow run) — pass it to
 * `loadConversationMessages` to load the child's persisted transcript. Workflow runs arrive in the
 * SAME flat list with `kind: 'workflow'` and `agentId` = the workflowId; the sub-agent WebSocket for
 * that agentId is routed server-side to the workflow's controller loop, so the client streams it with
 * no special transport. Persisted parent/depth/terminal fields are additive and support the separate
 * versioned recursive-tree contract used by the review daemon.
 *
 * Every collaboration member below is ADDITIVE and optional (#244): a server with collaboration
 * switched off, or a row persisted by a pre-#244 build, simply omits them, so nothing here may be
 * required or assumed present.
 */
export interface SubAgentSummary {
  agentId: string;
  name?: string | null;
  template: string;
  task: string;
  status: SubAgentStatus;
  threadId: string;
  lastActivityUtc?: string | null;
  /** `'workflow'` for a workflow run, else `'subagent'`. Absent = `'subagent'`. */
  kind?: SubAgentKind;
  /** Concrete model selected after applying spawn, template, and parent precedence. */
  effectiveModelId?: string | null;
  /** Tier that selected the effective model; absent for non-tier selection. */
  effectiveModelIntelligence?: number | null;
  /** Stable winning input: parent, spawn-model, spawn-tier, template-model, or template-tier. */
  modelSelectionSource?: string | null;
  /** Normalized effort requested before provider capability shaping. */
  requestedReasoningEffort?: string | null;
  /** Provider capability-shaped effort placed on the request; absent when omitted. */
  shapedReasoningEffort?: string | null;
  /** Persisted parent thread id; required for recursive graph nodes. */
  parentThreadId?: string | null;
  /** Distance from the recursive request root. */
  depth?: number | null;
  /** Terminal transition timestamp, when known. */
  terminalAtUtc?: string | null;
  /** Safe machine-readable failure code, when known. */
  failureCode?: string | null;
  /** Version of the persisted node shape; absent/0 means a row written before #244. */
  schemaVersion?: number;
  /** Collaboration this node belongs to; absent when collaboration is not enabled. */
  collaborationId?: string | null;
  /**
   * The id this agent is known by INSIDE the collaboration — the vocabulary `parentAgentId` and
   * `ancestorAgentIds` are expressed in. Equal to `agentId` except for a workflow tab, whose
   * `agentId` is the workflow handle while its node is the controller derived from it.
   */
  agentNodeId?: string | null;
  /** Template the agent was spawned from; always equal to {@link SubAgentSummary.template}. */
  agentType?: string | null;
  /** Structural position in the hierarchy — NOT the tab kind. */
  agentKind?: CollaborationAgentKind | null;
  /** Short statement of what this agent is for. */
  role?: string | null;
  /** Longer guidance on when to contact this agent. */
  description?: string | null;
  /** The agent directly above this one; null for the conversation root. */
  parentAgentId?: string | null;
  /** Every agent above this one, root first, excluding this agent. Absent when unknown. */
  ancestorAgentIds?: string[] | null;
  /** How many hierarchy levels lie between the root and this agent. */
  structuralDepth?: number | null;
  /** How much delegation budget has been spent reaching this agent. */
  delegationDepth?: number | null;
  /** Whether the agent is still addressable; false for a row retained after it left memory. */
  isLive?: boolean | null;
  /** Whether this row is the agent the reader is currently looking at (viewer-scoped). */
  isCurrent?: boolean;
  /** Whether the reader may fetch this agent's transcript (viewer-scoped). */
  isReadable?: boolean;
}

/**
 * Lists the sub-agents spawned within a parent conversation. Mirrors the conversationsApi fetch/DTO
 * style: GETs the REST endpoint and throws on a non-ok response.
 */
export async function listSubAgents(parentThreadId: string): Promise<SubAgentSummary[]> {
  const response = await apiFetch(`/api/conversations/${encodeURIComponent(parentThreadId)}/subagents`);
  if (!response.ok) {
    throw new Error(`Failed to list sub-agents: ${response.statusText}`);
  }
  return response.json();
}
