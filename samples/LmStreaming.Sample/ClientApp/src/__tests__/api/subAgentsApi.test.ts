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
});
