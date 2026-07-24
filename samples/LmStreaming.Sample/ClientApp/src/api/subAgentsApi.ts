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
 * SAME list with `kind: 'workflow'` and `agentId` = the workflowId; the sub-agent WebSocket for that
 * agentId is routed server-side to the workflow's controller loop, so the client streams it with no
 * special transport.
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
