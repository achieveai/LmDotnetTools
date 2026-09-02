import { describe, it, expect, afterEach, vi } from 'vitest';
import { getConversationContext } from '@/api/contextApi';

function mockFetchOnce(status: number, body: unknown, contentType = 'application/json') {
  const original = globalThis.fetch;
  const fetchSpy = vi.fn(
    async () =>
      new Response(typeof body === 'string' ? body : JSON.stringify(body), {
        status,
        headers: { 'Content-Type': contentType },
      })
  );
  globalThis.fetch = fetchSpy as unknown as typeof fetch;
  return { fetchSpy, restore: () => (globalThis.fetch = original) };
}

function report() {
  return {
    rootThreadId: 'thread-1',
    schemaVersion: 1,
    generatedAtUtc: '2026-09-02T10:00:00Z',
    agents: [
      {
        agentId: 'root',
        threadId: 'thread-1',
        parentAgentId: null,
        executionKind: 'Primary',
        observation: null,
        freshness: 'None',
        cacheTemperature: 'Unknown',
        compaction: { state: 'None' },
        usage: null,
      },
    ],
    total: {
      inputTokens: 0,
      outputTokens: 0,
      cacheReadTokens: 0,
      cacheWriteTokens: 0,
      reasoningTokens: 0,
      totalTokens: 0,
      preferredCostMicros: null,
      costProvenance: 'Unavailable',
      costCompleteness: 'Unavailable',
      usageCompleteness: null,
    },
  };
}

// #685: the context report is the authoritative source for the context/cost panel — read on load,
// reconnect, conversation switch and run completion; live `context_pressure` frames only enrich it.
describe('contextApi.getConversationContext (#685)', () => {
  let restore: (() => void) | undefined;
  afterEach(() => restore?.());

  it('returns the report on success, from the per-conversation context route', async () => {
    const mock = mockFetchOnce(200, report());
    restore = mock.restore;

    const result = await getConversationContext('thread-1');

    expect(result?.rootThreadId).toBe('thread-1');
    expect(result?.agents[0].agentId).toBe('root');
    expect(mock.fetchSpy).toHaveBeenCalledWith('/api/conversations/thread-1/context');
  });

  it('encodes the thread id in the path', async () => {
    const mock = mockFetchOnce(200, report());
    restore = mock.restore;

    await getConversationContext('thread/with space');

    expect(mock.fetchSpy).toHaveBeenCalledWith('/api/conversations/thread%2Fwith%20space/context');
  });

  it('returns null for an unknown thread (404) — nothing to show', async () => {
    const mock = mockFetchOnce(404, { error: 'unknown_thread' });
    restore = mock.restore;

    expect(await getConversationContext('thread-1')).toBeNull();
  });

  it('returns the SAME null for a thread the caller may not read (403): no metadata leaks', async () => {
    const mock = mockFetchOnce(403, { error: 'forbidden', code: 'not_owner' });
    restore = mock.restore;

    expect(await getConversationContext('thread-1')).toBeNull();
  });

  it('returns null for a non-JSON body (a dev server answering with index.html)', async () => {
    const mock = mockFetchOnce(200, '<!doctype html><html></html>', 'text/html');
    restore = mock.restore;

    expect(await getConversationContext('thread-1')).toBeNull();
  });

  it('throws for any other failure so a broken route stays distinguishable in the logs', async () => {
    const mock = mockFetchOnce(500, { error: 'boom' });
    restore = mock.restore;

    await expect(getConversationContext('thread-1')).rejects.toThrow(/Failed to fetch context/);
  });
});
