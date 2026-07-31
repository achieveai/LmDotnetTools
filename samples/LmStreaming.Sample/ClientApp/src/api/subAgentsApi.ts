/**
 * Status of a sub-agent, mirroring the backend `SubAgentSummary.status` values.
 */
export type SubAgentStatus = 'running' | 'completed' | 'error' | 'stopped';

/**
 * What kind of run a `/subagents` entry represents. A plain `'subagent'` is a spawned Agent; a
 * `'workflow'` is a StartWorkflowAgent run whose isolated controller loop is exposed as a tab
 * alongside sub-agents. Missing/undefined is treated as `'subagent'` (backward compatible with a
 * server that predates the field).
 */
export type SubAgentKind = 'subagent' | 'workflow';

/**
 * A conversation's sub-agent as summarized by
 * `GET /api/conversations/{parentThreadId}/subagents`. `threadId` is the child's own conversation
 * thread (`subagent-{agentId}`, or `workflow-{agentId}` for a workflow run) — pass it to
 * `loadConversationMessages` to load the child's persisted transcript. Workflow runs arrive in the
 * SAME flat list with `kind: 'workflow'`. Persisted parent/depth/terminal fields are additive and
 * required by the separate versioned recursive-tree contract used by the review daemon.
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
  /** Persisted parent thread id; required for recursive graph nodes. */
  parentThreadId?: string | null;
  /** Distance from the recursive request root. */
  depth?: number | null;
  /** Terminal transition timestamp, when known. */
  terminalAtUtc?: string | null;
  /** Safe machine-readable failure code, when known. */
  failureCode?: string | null;
}

/**
 * Lists the sub-agents spawned within a parent conversation. Mirrors the conversationsApi fetch/DTO
 * style: GETs the REST endpoint and throws on a non-ok response.
 */
export async function listSubAgents(parentThreadId: string): Promise<SubAgentSummary[]> {
  const response = await fetch(`/api/conversations/${encodeURIComponent(parentThreadId)}/subagents`);
  if (!response.ok) {
    throw new Error(`Failed to list sub-agents: ${response.statusText}`);
  }
  return response.json();
}
