import { describe, it, expect, afterEach, vi } from 'vitest';
import {
  getConversationContext,
  MAX_COMPACTION_FOCUS_LENGTH,
  requestCompaction,
  supportsManualCompaction,
} from '@/api/contextApi';

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

// Manual compaction: the user (never the model) asks the host to compact a conversation now,
// optionally steering the summary with a focus prompt.
describe('contextApi.requestCompaction (manual compaction)', () => {
  let restore: (() => void) | undefined;
  afterEach(() => restore?.());

  function sentBody(fetchSpy: ReturnType<typeof vi.fn>): unknown {
    const init = fetchSpy.mock.calls[0]?.[1] as RequestInit | undefined;
    return JSON.parse(String(init?.body));
  }

  it('POSTs to the per-conversation compaction route and maps 202 to accepted', async () => {
    const mock = mockFetchOnce(202, { requestId: 'req-1', status: 'queued' });
    restore = mock.restore;

    const result = await requestCompaction('thread/1', 'keep the API decisions');

    expect(result).toEqual({ kind: 'accepted', requestId: 'req-1', status: 'queued' });
    const [url, init] = mock.fetchSpy.mock.calls[0] as unknown as [string, RequestInit];
    expect(url).toBe('/api/conversations/thread%2F1/compaction');
    expect(init.method).toBe('POST');
    expect(sentBody(mock.fetchSpy)).toEqual({ focus: 'keep the API decisions' });
  });

  it('maps a running 202 status through unchanged', async () => {
    const mock = mockFetchOnce(202, { requestId: 'req-2', status: 'running' });
    restore = mock.restore;

    expect(await requestCompaction('thread-1')).toEqual({ kind: 'accepted', requestId: 'req-2', status: 'running' });
  });

  it('trims the focus and sends no focus field when it is blank', async () => {
    const mock = mockFetchOnce(202, { requestId: 'req-3', status: 'queued' });
    restore = mock.restore;

    await requestCompaction('thread-1', '   ');

    expect(sentBody(mock.fetchSpy)).toEqual({});
  });

  it('caps the focus at the server maximum', async () => {
    const mock = mockFetchOnce(202, { requestId: 'req-4', status: 'queued' });
    restore = mock.restore;

    await requestCompaction('thread-1', `  ${'x'.repeat(MAX_COMPACTION_FOCUS_LENGTH + 50)}  `);

    expect((sentBody(mock.fetchSpy) as { focus: string }).focus).toHaveLength(MAX_COMPACTION_FOCUS_LENGTH);
  });

  it.each(['compaction_off', 'provider_owned_session', 'already_pending', 'in_progress', 'nothing_to_compact', 'no_safe_boundary'])(
    'maps 409 %s to a refusal carrying the reason',
    async (reason) => {
      const mock = mockFetchOnce(409, { reason });
      restore = mock.restore;

      expect(await requestCompaction('thread-1')).toEqual({ kind: 'refused', reason });
    }
  );

  it('maps a 409 with an unreadable body to an unknown refusal', async () => {
    const mock = mockFetchOnce(409, 'nope', 'text/plain');
    restore = mock.restore;

    expect(await requestCompaction('thread-1')).toEqual({ kind: 'refused', reason: 'unknown' });
  });

  it('maps 403 to forbidden and 404 to not-found', async () => {
    let mock = mockFetchOnce(403, { error: 'forbidden' });
    expect(await requestCompaction('thread-1')).toEqual({ kind: 'forbidden' });
    mock.restore();

    mock = mockFetchOnce(404, { error: 'unknown_thread', code: 'unknown_thread' });
    restore = mock.restore;
    expect(await requestCompaction('thread-1')).toEqual({ kind: 'not-found' });
  });

  it('throws for any other failure', async () => {
    const mock = mockFetchOnce(500, { error: 'boom' });
    restore = mock.restore;

    await expect(requestCompaction('thread-1')).rejects.toThrow(/Failed to request compaction/);
  });
});

describe('contextApi.supportsManualCompaction', () => {
  let restore: (() => void) | undefined;
  afterEach(() => restore?.());

  it('reads manualCompaction from the host capabilities', async () => {
    const mock = mockFetchOnce(200, { schemaVersion: 1, manualCompaction: true });
    restore = mock.restore;

    expect(await supportsManualCompaction()).toBe(true);
    expect(mock.fetchSpy).toHaveBeenCalledWith('/api/conversations/capabilities');
  });

  it('is false when the flag is absent, false, or the read fails', async () => {
    let mock = mockFetchOnce(200, { schemaVersion: 1 });
    expect(await supportsManualCompaction()).toBe(false);
    mock.restore();

    mock = mockFetchOnce(200, { manualCompaction: false });
    expect(await supportsManualCompaction()).toBe(false);
    mock.restore();

    mock = mockFetchOnce(500, { error: 'boom' });
    expect(await supportsManualCompaction()).toBe(false);
    mock.restore();

    mock = mockFetchOnce(200, '<!doctype html>', 'text/html');
    restore = mock.restore;
    expect(await supportsManualCompaction()).toBe(false);
  });
});
