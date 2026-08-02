import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { listSubAgents, type SubAgentSummary } from '@/api/subAgentsApi';

describe('subAgentsApi.listSubAgents', () => {
  const originalFetch = globalThis.fetch;

  beforeEach(() => {
    vi.restoreAllMocks();
  });

  afterEach(() => {
    globalThis.fetch = originalFetch;
  });

  it('fetches the subagents endpoint for the parent thread and returns the summaries', async () => {
    const summaries: SubAgentSummary[] = [
      {
        agentId: 'a1',
        name: 'Researcher',
        template: 'research',
        task: 'find things',
        status: 'running',
        threadId: 'subagent-a1',
        lastActivityUtc: '2026-07-19T00:00:00Z',
      },
      {
        agentId: 'a2',
        name: null,
        template: 'code',
        task: 'write code',
        status: 'completed',
        threadId: 'subagent-a2',
        lastActivityUtc: null,
      },
    ];

    const fetchMock = vi.fn().mockResolvedValue({
      ok: true,
      json: async () => summaries,
    });
    globalThis.fetch = fetchMock as unknown as typeof fetch;

    const result = await listSubAgents('parent thread/1');

    expect(fetchMock).toHaveBeenCalledWith('/api/conversations/parent%20thread%2F1/subagents');
    expect(result).toEqual(summaries);
  });

  it('throws when the response is not ok', async () => {
    globalThis.fetch = vi.fn().mockResolvedValue({
      ok: false,
      statusText: 'Internal Server Error',
    }) as unknown as typeof fetch;

    await expect(listSubAgents('parent-1')).rejects.toThrow(/Failed to list sub-agents/);
  });

  // #244: the hierarchy metadata rides on the SAME summaries, camelCase (the API uses the default
  // ASP.NET naming policy), and every field is optional — a pre-#244 row simply omits them.
  it('carries the collaboration hierarchy metadata through unchanged', async () => {
    const summary: SubAgentSummary = {
      agentId: 'a2',
      name: 'reviewer',
      template: 'code-reviewer',
      task: 'review the PR',
      status: 'running',
      threadId: 'subagent-a2',
      lastActivityUtc: null,
      schemaVersion: 1,
      collaborationId: 'thread-root',
      agentNodeId: 'a2',
      agentType: 'code-reviewer',
      agentKind: 'SubAgent',
      role: 'reviews code',
      description: 'contact for review questions',
      parentAgentId: 'a1',
      ancestorAgentIds: ['root', 'a1'],
      structuralDepth: 2,
      delegationDepth: 1,
      isLive: true,
      isCurrent: false,
      isReadable: true,
    };

    globalThis.fetch = vi.fn().mockResolvedValue({
      ok: true,
      json: async () => [summary],
    }) as unknown as typeof fetch;

    const [result] = await listSubAgents('thread-root');

    expect(result).toEqual(summary);
    // The two fields the UI must not confuse: structural position vs. the tab an agent renders in.
    expect(result.agentKind).toBe('SubAgent');
    expect(result.kind, 'a plain sub-agent tab has no explicit kind').toBeUndefined();
    expect(result.ancestorAgentIds).toEqual(['root', 'a1']);
  });

  it('accepts a retained (no longer live) row and a workflow controller row', async () => {
    globalThis.fetch = vi.fn().mockResolvedValue({
      ok: true,
      json: async () => [
        {
          agentId: 'a3',
          template: 'research',
          task: 'find things',
          // A row that was mid-flight when its host stopped comes back as a retained snapshot.
          status: 'interrupted',
          threadId: 'subagent-a3',
          isLive: false,
          isReadable: false,
        },
        {
          agentId: 'w1',
          template: 'workflow',
          task: 'run the workflow',
          status: 'running',
          threadId: 'workflow-w1',
          kind: 'workflow',
          // The tab is keyed by the workflow handle; the node is the controller derived from it.
          agentNodeId: 'w1-controller',
          agentKind: 'WorkflowController',
        },
      ],
    }) as unknown as typeof fetch;

    const [retained, workflow] = await listSubAgents('thread-root');

    expect(retained.status).toBe('interrupted');
    expect(retained.isLive).toBe(false);
    expect(workflow.kind).toBe('workflow');
    expect(workflow.agentKind).toBe('WorkflowController');
    expect(workflow.agentNodeId).not.toBe(workflow.agentId);
  });
});
