/**
 * Status of a sub-agent, mirroring the backend `SubAgentSummary.status` values. `persisted` is the
 * marker for a child rebuilt from the conversation store (`SubAgentProvenance.PersistedStatus`) —
 * its parent is no longer in the agent pool, so its live lifecycle can no longer be observed.
 */
export type SubAgentStatus = 'running' | 'completed' | 'error' | 'stopped' | 'persisted';

/**
 * A conversation's sub-agent as summarized by
 * `GET /api/conversations/{parentThreadId}/subagents`. `threadId` is the child's own conversation
 * thread (`subagent-{agentId}`) — pass it to `loadConversationMessages` to load the child's
 * persisted transcript.
 */
export interface SubAgentSummary {
  agentId: string;
  name?: string | null;
  template: string;
  task: string;
  status: SubAgentStatus;
  threadId: string;
  lastActivityUtc?: string | null;
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
