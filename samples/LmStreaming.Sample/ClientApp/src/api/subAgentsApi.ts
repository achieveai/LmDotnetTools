/**
 * Status of a sub-agent, mirroring the backend `SubAgentSummary.status` values. `unknown` is the
 * marker for a child rebuilt from the conversation store whose lifecycle status was never stamped
 * (e.g. metadata written before status stamping existed, or a parent no longer in the agent pool
 * that never observed a live snapshot) — see `SubAgentProvenance.UnknownStatus` on the backend.
 */
export type SubAgentStatus = 'running' | 'completed' | 'error' | 'stopped' | 'unknown';

/**
 * A conversation's sub-agent as summarized by
 * `GET /api/conversations/{parentThreadId}/subagents`. `threadId` is the child's own conversation
 * thread (`subagent-{agentId}`) — pass it to `loadConversationMessages` to load the child's
 * persisted transcript.
 *
 * The `parentThreadId`, `depth`, `terminalAtUtc`, and `failureCode` fields are additive: they are
 * only populated by the recursive descendant graph (`?recursive=true`, see
 * `SubAgentTreeResponse` on the backend) and are `undefined`/absent on the flat listing.
 */
export interface SubAgentSummary {
  agentId: string;
  name?: string | null;
  template: string;
  task: string;
  status: SubAgentStatus;
  threadId: string;
  lastActivityUtc?: string | null;
  parentThreadId?: string | null;
  depth?: number | null;
  terminalAtUtc?: string | null;
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
